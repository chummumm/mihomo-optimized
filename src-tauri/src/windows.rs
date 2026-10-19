use crate::error::{AppError, Result};
use std::{
    ffi::OsStr,
    fs,
    os::windows::{
        ffi::OsStrExt,
        io::{AsRawHandle, FromRawHandle, OwnedHandle},
    },
    path::{Path, PathBuf},
    process::Child,
};
use windows_sys::Win32::{
    Foundation::{GetLastError, LocalFree, HANDLE},
    Security::{
        Authorization::{
            ConvertSidToStringSidW, ConvertStringSecurityDescriptorToSecurityDescriptorW,
            ConvertStringSidToSidW, GetSecurityInfo, SetEntriesInAclW, SetSecurityInfo,
            EXPLICIT_ACCESS_W, GRANT_ACCESS, NO_MULTIPLE_TRUSTEE,
            SE_KERNEL_OBJECT, TRUSTEE_IS_SID, TRUSTEE_IS_USER, TRUSTEE_W,
        },
        GetTokenInformation, IsValidAcl, SetFileSecurityW, TokenUser, DACL_SECURITY_INFORMATION,
        NO_INHERITANCE,
        PROTECTED_DACL_SECURITY_INFORMATION, TOKEN_QUERY, TOKEN_USER,
    },
    System::{
        Com::CoTaskMemFree,
        JobObjects::{
            AssignProcessToJobObject, CreateJobObjectW, JobObjectExtendedLimitInformation,
            SetInformationJobObject, JOBOBJECT_EXTENDED_LIMIT_INFORMATION,
            JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
        },
        Threading::{
            GetCurrentProcess, OpenProcess, OpenProcessToken, QueryFullProcessImageNameW,
            PROCESS_QUERY_LIMITED_INFORMATION,
        },
    },
    UI::Shell::{
        FOLDERID_LocalAppData, FOLDERID_ProgramData, FOLDERID_ProgramFiles, SHGetKnownFolderPath,
        ShellExecuteExW, SEE_MASK_NOCLOSEPROCESS, SHELLEXECUTEINFOW,
    },
};

pub fn wide(value: impl AsRef<OsStr>) -> Vec<u16> {
    value.as_ref().encode_wide().chain(Some(0)).collect()
}

pub unsafe fn read_wide(value: *const u16) -> String {
    if value.is_null() {
        return String::new();
    }
    let mut length = 0;
    while length < 32768 && *value.add(length) != 0 {
        length += 1;
    }
    String::from_utf16_lossy(std::slice::from_raw_parts(value, length))
}

pub fn last_error(operation: &str) -> AppError {
    AppError::io(operation, std::io::Error::last_os_error())
}

pub fn current_sid() -> Result<String> {
    unsafe { process_sid(GetCurrentProcess()) }
}

unsafe fn process_sid(process: HANDLE) -> Result<String> {
    let mut raw_token = std::ptr::null_mut();
    if OpenProcessToken(process, TOKEN_QUERY, &mut raw_token) == 0 {
        return Err(last_error("无法读取用户身份"));
    }
    let token = OwnedHandle::from_raw_handle(raw_token);
    let mut length = 0_u32;
    GetTokenInformation(
        token.as_raw_handle(),
        TokenUser,
        std::ptr::null_mut(),
        0,
        &mut length,
    );
    let mut buffer = vec![0_usize; (length as usize).div_ceil(std::mem::size_of::<usize>())];
    if GetTokenInformation(
        token.as_raw_handle(),
        TokenUser,
        buffer.as_mut_ptr().cast(),
        length,
        &mut length,
    ) == 0
    {
        return Err(last_error("无法读取用户身份"));
    }
    let user = &*buffer.as_ptr().cast::<TOKEN_USER>();
    let mut string_sid = std::ptr::null_mut();
    if ConvertSidToStringSidW(user.User.Sid, &mut string_sid) == 0 {
        return Err(last_error("无法转换用户身份"));
    }
    let value = read_wide(string_sid);
    LocalFree(string_sid.cast());
    Ok(value)
}

pub fn process_identity(pid: u32) -> Result<(PathBuf, String)> {
    unsafe {
        let process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
        if process.is_null() {
            return Err(last_error("无法验证本地进程"));
        }
        let process = OwnedHandle::from_raw_handle(process);
        process_handle_identity(&process)
    }
}

pub fn process_handle_identity(process: &OwnedHandle) -> Result<(PathBuf, String)> {
    unsafe {
        let mut buffer = vec![0_u16; 32768];
        let mut size = buffer.len() as u32;
        if QueryFullProcessImageNameW(process.as_raw_handle(), 0, buffer.as_mut_ptr(), &mut size)
            == 0
        {
            return Err(last_error("无法验证本地进程路径"));
        }
        let path = PathBuf::from(String::from_utf16_lossy(&buffer[..size as usize]));
        Ok((path, process_sid(process.as_raw_handle())?))
    }
}

pub fn process_path(pid: u32) -> Result<PathBuf> {
    unsafe {
        let raw = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
        if raw.is_null() {
            return Err(last_error("无法验证服务进程"));
        }
        let process = OwnedHandle::from_raw_handle(raw);
        let mut buffer = vec![0_u16; 32768];
        let mut size = buffer.len() as u32;
        if QueryFullProcessImageNameW(process.as_raw_handle(), 0, buffer.as_mut_ptr(), &mut size)
            == 0
        {
            return Err(last_error("无法验证服务路径"));
        }
        Ok(PathBuf::from(String::from_utf16_lossy(
            &buffer[..size as usize],
        )))
    }
}

/// Let the configured desktop user authenticate this SYSTEM service's image path.
/// Merge only limited process-query access; retain the existing DACL entries,
/// owner, token permissions and integrity label. Call before accepting clients.
pub fn allow_service_identity_query(owner_sid: &str) -> Result<()> {
    unsafe {
        let mut sid = std::ptr::null_mut();
        if ConvertStringSidToSidW(wide(owner_sid).as_ptr(), &mut sid) == 0 {
            return Err(last_error("无法读取服务查询用户"));
        }
        let mut descriptor = std::ptr::null_mut();
        let mut old_acl = std::ptr::null_mut();
        let mut new_acl = std::ptr::null_mut();
        let result = (|| {
            // GetCurrentProcess returns a pseudo handle; it must not be closed.
            let process = GetCurrentProcess();
            let code = GetSecurityInfo(
                process,
                SE_KERNEL_OBJECT,
                DACL_SECURITY_INFORMATION,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
                &mut old_acl,
                std::ptr::null_mut(),
                &mut descriptor,
            );
            if code != 0 {
                return Err(AppError::io(
                    "无法读取服务进程访问控制",
                    std::io::Error::from_raw_os_error(code as i32),
                ));
            }
            if old_acl.is_null() || IsValidAcl(old_acl) == 0 || (*old_acl).AceCount == 0 {
                return Err(AppError::new(
                    "service_identity_acl_invalid",
                    "服务进程访问控制无效，已停止启动",
                ));
            }
            let entry = EXPLICIT_ACCESS_W {
                grfAccessPermissions: PROCESS_QUERY_LIMITED_INFORMATION,
                grfAccessMode: GRANT_ACCESS,
                grfInheritance: NO_INHERITANCE,
                Trustee: TRUSTEE_W {
                    pMultipleTrustee: std::ptr::null_mut(),
                    MultipleTrusteeOperation: NO_MULTIPLE_TRUSTEE,
                    TrusteeForm: TRUSTEE_IS_SID,
                    TrusteeType: TRUSTEE_IS_USER,
                    ptstrName: sid.cast(),
                },
            };
            let code = SetEntriesInAclW(1, &entry, old_acl, &mut new_acl);
            if code != 0 {
                return Err(AppError::io(
                    "无法合并服务进程查询权限",
                    std::io::Error::from_raw_os_error(code as i32),
                ));
            }
            // A null DACL would grant full access to everyone. Never pass one.
            if new_acl.is_null() || IsValidAcl(new_acl) == 0 {
                return Err(AppError::new(
                    "service_identity_acl_invalid",
                    "服务进程查询权限无效，已停止启动",
                ));
            }
            let code = SetSecurityInfo(
                process,
                SE_KERNEL_OBJECT,
                DACL_SECURITY_INFORMATION,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
                new_acl,
                std::ptr::null_mut(),
            );
            if code != 0 {
                return Err(AppError::io(
                    "无法设置服务进程查询权限",
                    std::io::Error::from_raw_os_error(code as i32),
                ));
            }
            Ok(())
        })();
        // old_acl is owned by descriptor, not a separate allocation.
        LocalFree(new_acl.cast());
        LocalFree(descriptor);
        LocalFree(sid);
        result
    }
}

fn known_folder(id: &windows_sys::core::GUID) -> Result<PathBuf> {
    unsafe {
        let mut path = std::ptr::null_mut();
        if SHGetKnownFolderPath(id, 0, std::ptr::null_mut(), &mut path) < 0 {
            return Err(AppError::new(
                "folder_unavailable",
                "无法定位 Windows 数据目录",
            ));
        }
        let value = read_wide(path);
        CoTaskMemFree(path.cast());
        Ok(PathBuf::from(value))
    }
}

pub fn user_data_dir() -> Result<PathBuf> {
    Ok(known_folder(&FOLDERID_LocalAppData)?.join("Mihomo Optimized"))
}
pub fn service_data_dir() -> Result<PathBuf> {
    Ok(known_folder(&FOLDERID_ProgramData)?
        .join("Mihomo Optimized")
        .join("service"))
}

pub fn configure_tun_firewall(directory: &Path, install: bool) -> Result<()> {
    let executable = directory.join("mihomo-core.exe");
    reject_reparse_path(&executable)?;
    reject_hard_links(&executable)?;
    require_admin_protected(&executable)?;
    let canonical = executable
        .canonicalize()
        .map_err(|error| AppError::io("无法确认防火墙程序路径", error))?;
    let program = canonical
        .to_str()
        .ok_or_else(|| AppError::new("firewall_path_invalid", "防火墙程序路径无法编码"))?;
    // Windows Firewall filters require a normal DOS executable path, not the
    // extended-length spelling returned by Rust's canonicalize().
    let program = program.strip_prefix(r"\\?\").unwrap_or(program);
    let bytes = program.as_bytes();
    if bytes.len() < 3 || !bytes[0].is_ascii_alphabetic() || bytes[1..3] != *b":\\" {
        return Err(AppError::new(
            "firewall_path_invalid",
            "TUN 防火墙规则需要本地安装程序路径",
        ));
    }
    crate::firewall::configure(program, install)
}

pub fn protected_install_dir() -> Result<PathBuf> {
    let executable = std::env::current_exe().map_err(|e| AppError::io("无法定位程序", e))?;
    reject_reparse_path(&executable)?;
    let directory = executable
        .parent()
        .ok_or_else(|| AppError::new("service_install_required", "请先安装桌面版以使用 TUN"))?
        .canonicalize()
        .map_err(|e| AppError::io("无法定位安装目录", e))?;
    let program_files = known_folder(&FOLDERID_ProgramFiles)?
        .canonicalize()
        .map_err(|e| AppError::io("无法定位受保护目录", e))?;
    if !directory.starts_with(&program_files) || directory == program_files {
        return Err(AppError::new(
            "service_install_required",
            "TUN 服务需要安装版；请运行安装包将程序安装到 Program Files",
        ));
    }
    for ancestor in directory
        .ancestors()
        .take_while(|path| path.starts_with(&program_files))
    {
        require_admin_protected(ancestor)?;
    }
    for filename in [
        "mihomo-core.exe",
        "mihomo-optimized.exe",
        "mihomo-desktop-service.exe",
    ] {
        reject_reparse_path(&directory.join(filename))?;
        let candidate = directory.join(filename).canonicalize().map_err(|_| {
            AppError::new("service_binary_missing", "安装目录缺少配套程序，请重新安装")
        })?;
        if candidate.parent() != Some(directory.as_path()) {
            return Err(AppError::new(
                "service_untrusted_path",
                "服务程序不能通过链接指向安装目录以外",
            ));
        }
        reject_hard_links(&candidate)?;
        require_admin_protected(&candidate)?;
    }
    let wintun = directory.join("wintun.dll");
    if wintun.exists() {
        reject_reparse_path(&wintun)?;
        reject_hard_links(&wintun)?;
        require_admin_protected(&wintun)?;
    }
    Ok(directory)
}

pub fn secure_directory(path: &Path, owner_sid: Option<&str>) -> Result<()> {
    reject_reparse_path(path)?;
    fs::create_dir_all(path).map_err(|e| AppError::io("无法创建安全目录", e))?;
    reject_reparse_path(path)?;
    let sddl = match owner_sid {
        Some(sid) => format!("D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;{sid})"),
        None => "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)".into(),
    };
    apply_dacl(path, &sddl)
}

pub fn reject_reparse_path(path: &Path) -> Result<()> {
    use std::os::windows::fs::MetadataExt;
    use windows_sys::Win32::Storage::FileSystem::FILE_ATTRIBUTE_REPARSE_POINT;
    for component in path.ancestors() {
        match fs::symlink_metadata(component) {
            Ok(metadata) if metadata.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0 => {
                return Err(AppError::new(
                    "unsafe_directory",
                    "受保护的程序和运行目录不能包含符号链接或目录联接",
                ))
            }
            Ok(_) => {}
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
            Err(error) => return Err(AppError::io("无法检查安全目录", error)),
        }
    }
    Ok(())
}

pub fn apply_dacl(path: &Path, sddl: &str) -> Result<()> {
    unsafe {
        let mut descriptor = std::ptr::null_mut();
        if ConvertStringSecurityDescriptorToSecurityDescriptorW(
            wide(sddl).as_ptr(),
            1,
            &mut descriptor,
            std::ptr::null_mut(),
        ) == 0
        {
            return Err(last_error("无法生成目录权限"));
        }
        // Apply one object at a time. Automatic recursive ACL propagation would
        // traverse existing children before their links have been checked.
        let owner = if sddl.starts_with("O:") {
            windows_sys::Win32::Security::OWNER_SECURITY_INFORMATION
        } else {
            0
        };
        let success = SetFileSecurityW(
            wide(path).as_ptr(),
            owner | DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION,
            descriptor,
        );
        let error = if success == 0 {
            Some(last_error("无法保护运行目录"))
        } else {
            None
        };
        LocalFree(descriptor);
        error.map_or(Ok(()), Err)
    }
}

pub fn reject_hard_links(path: &Path) -> Result<()> {
    use windows_sys::Win32::Storage::FileSystem::{
        GetFileInformationByHandle, BY_HANDLE_FILE_INFORMATION,
    };
    let file = fs::File::open(path).map_err(|e| AppError::io("无法检查受保护文件", e))?;
    let mut information: BY_HANDLE_FILE_INFORMATION = unsafe { std::mem::zeroed() };
    if unsafe { GetFileInformationByHandle(file.as_raw_handle(), &mut information) } == 0 {
        return Err(last_error("无法检查文件链接"));
    }
    if information.nNumberOfLinks != 1 {
        return Err(AppError::new(
            "unsafe_file",
            "受保护的程序和运行文件不能使用硬链接",
        ));
    }
    Ok(())
}

/// A protected file's owner must also be trusted: removing a user's write ACE
/// alone does not remove an owner's ability to grant write access again.
pub fn require_admin_protected(path: &Path) -> Result<()> {
    use windows_sys::Win32::{
        Foundation::{GENERIC_ALL, GENERIC_WRITE},
        Security::{
            Authorization::{GetNamedSecurityInfoW, SE_FILE_OBJECT},
            GetAce, ACCESS_ALLOWED_ACE, ACE_HEADER, INHERIT_ONLY_ACE, OWNER_SECURITY_INFORMATION,
        },
        Storage::FileSystem::{
            DELETE, FILE_APPEND_DATA, FILE_DELETE_CHILD, FILE_WRITE_ATTRIBUTES, FILE_WRITE_DATA,
            FILE_WRITE_EA, WRITE_DAC, WRITE_OWNER,
        },
    };
    fn trusted(sid: &str) -> bool {
        matches!(
            sid,
            "S-1-5-18"
                | "S-1-5-32-544"
                | "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"
        )
    }
    unsafe fn sid_text(sid: *mut core::ffi::c_void) -> Result<String> {
        let mut text = std::ptr::null_mut();
        if sid.is_null() || ConvertSidToStringSidW(sid, &mut text) == 0 {
            return Err(last_error("无法验证文件所有者"));
        }
        let value = read_wide(text);
        LocalFree(text.cast());
        Ok(value)
    }
    let unsafe_acl = || {
        AppError::new("service_untrusted_permissions", "TUN 服务需要管理员保护的安装目录。请通过安装包重新安装到 Program Files，勿从普通用户可修改的目录启动服务")
    };
    unsafe {
        let mut owner = std::ptr::null_mut();
        let mut dacl = std::ptr::null_mut();
        let mut descriptor = std::ptr::null_mut();
        let error = GetNamedSecurityInfoW(
            wide(path).as_ptr(),
            SE_FILE_OBJECT,
            OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
            &mut owner,
            std::ptr::null_mut(),
            &mut dacl,
            std::ptr::null_mut(),
            &mut descriptor,
        );
        if error != 0 {
            return Err(AppError::io(
                "无法读取安装目录权限",
                std::io::Error::from_raw_os_error(error as i32),
            ));
        }
        let result = (|| {
            if !trusted(&sid_text(owner)?) || dacl.is_null() {
                return Err(unsafe_acl());
            }
            let write_mask = GENERIC_ALL
                | GENERIC_WRITE
                | DELETE
                | WRITE_DAC
                | WRITE_OWNER
                | FILE_WRITE_DATA
                | FILE_APPEND_DATA
                | FILE_WRITE_EA
                | FILE_WRITE_ATTRIBUTES
                | FILE_DELETE_CHILD;
            for index in 0..(*dacl).AceCount as u32 {
                let mut raw = std::ptr::null_mut();
                if GetAce(dacl, index, &mut raw) == 0 {
                    return Err(last_error("无法验证目录权限项"));
                }
                let header = &*raw.cast::<ACE_HEADER>();
                if header.AceFlags as u32 & INHERIT_ONLY_ACE != 0 {
                    continue;
                }
                // Deny ACEs cannot grant a write permission. Object/callback grants
                // are conservatively refused when they carry any write bit.
                if [1_u8, 6, 10, 12].contains(&header.AceType) {
                    continue;
                }
                if header.AceSize < 8 {
                    return Err(unsafe_acl());
                }
                let ace = &*raw.cast::<ACCESS_ALLOWED_ACE>();
                if ace.Mask & write_mask == 0 {
                    continue;
                }
                if header.AceType != 0
                    || !trusted(&sid_text(
                        std::ptr::addr_of!(ace.SidStart).cast_mut().cast(),
                    )?)
                {
                    return Err(unsafe_acl());
                }
            }
            Ok(())
        })();
        LocalFree(descriptor);
        result
    }
}

pub fn secure_admin_tree(path: &Path, read_sid: Option<&str>) -> Result<()> {
    fn protect(path: &Path, read_sid: Option<&str>, depth: usize) -> Result<()> {
        if depth > 64 {
            return Err(AppError::new(
                "unsafe_directory",
                "服务运行目录层级异常，请重新安装服务",
            ));
        }
        reject_reparse_path(path)?;
        let metadata =
            fs::symlink_metadata(path).map_err(|e| AppError::io("无法检查服务数据", e))?;
        if metadata.is_dir() {
            let sddl = match read_sid {
                Some(sid) => {
                    format!("O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;GRGX;;;{sid})")
                }
                None => "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)".into(),
            };
            apply_dacl(path, &sddl)?;
            for item in fs::read_dir(path).map_err(|e| AppError::io("无法检查服务数据目录", e))?
            {
                protect(
                    &item
                        .map_err(|e| AppError::io("无法检查服务数据文件", e))?
                        .path(),
                    read_sid,
                    depth + 1,
                )?;
            }
        } else {
            reject_hard_links(path)?;
            let sddl = match read_sid {
                Some(sid) => format!("O:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GR;;;{sid})"),
                None => "O:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)".into(),
            };
            apply_dacl(path, &sddl)?;
        }
        Ok(())
    }
    protect(path, read_sid, 0)
}

pub fn create_kill_job(child: &Child) -> Result<OwnedHandle> {
    unsafe {
        let raw = CreateJobObjectW(std::ptr::null(), std::ptr::null());
        if raw.is_null() {
            return Err(last_error("无法创建内核进程组"));
        }
        let job = OwnedHandle::from_raw_handle(raw);
        let mut limits: JOBOBJECT_EXTENDED_LIMIT_INFORMATION = std::mem::zeroed();
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if SetInformationJobObject(
            job.as_raw_handle(),
            JobObjectExtendedLimitInformation,
            (&limits as *const JOBOBJECT_EXTENDED_LIMIT_INFORMATION).cast(),
            std::mem::size_of_val(&limits) as u32,
        ) == 0
        {
            return Err(last_error("无法限制内核生命周期"));
        }
        if AssignProcessToJobObject(job.as_raw_handle(), child.as_raw_handle()) == 0 {
            return Err(last_error("无法接管内核进程"));
        }
        Ok(job)
    }
}

pub fn shell_open(target: &str) -> Result<()> {
    let verb = wide("open");
    let target = wide(target);
    unsafe {
        let mut info: SHELLEXECUTEINFOW = std::mem::zeroed();
        info.cbSize = std::mem::size_of_val(&info) as u32;
        info.lpVerb = verb.as_ptr();
        info.lpFile = target.as_ptr();
        info.nShow = 1;
        if ShellExecuteExW(&mut info) == 0 {
            return Err(last_error("无法打开目标"));
        }
    }
    Ok(())
}

pub fn run_elevated_service_action(action: &str) -> Result<()> {
    use windows_sys::Win32::System::Threading::{GetExitCodeProcess, WaitForSingleObject};
    let directory = protected_install_dir()?;
    let service = wide(directory.join("mihomo-desktop-service.exe"));
    let sid = current_sid()?;
    let parameters = match action {
        "install" => format!("--install --owner-sid {sid}"),
        "uninstall" => "--uninstall".into(),
        _ => return Err(AppError::new("invalid_service_action", "不支持的服务操作")),
    };
    let parameters = wide(parameters);
    let verb = wide("runas");
    unsafe {
        let mut info: SHELLEXECUTEINFOW = std::mem::zeroed();
        info.cbSize = std::mem::size_of_val(&info) as u32;
        info.fMask = SEE_MASK_NOCLOSEPROCESS;
        info.lpVerb = verb.as_ptr();
        info.lpFile = service.as_ptr();
        info.lpParameters = parameters.as_ptr();
        info.nShow = 0;
        if ShellExecuteExW(&mut info) == 0 {
            return Err(AppError::new(
                if GetLastError() == 1223 {
                    "service_elevation_cancelled"
                } else {
                    "service_elevation_failed"
                },
                "管理员授权未完成，服务设置没有更改",
            ));
        }
        if info.hProcess.is_null() {
            return Err(AppError::new(
                "service_elevation_failed",
                "无法等待服务安装结果",
            ));
        }
        let process = OwnedHandle::from_raw_handle(info.hProcess);
        if WaitForSingleObject(process.as_raw_handle(), 60_000) != 0 {
            return Err(AppError::new(
                "service_install_timeout",
                "服务安装仍未完成，请稍后检查服务状态",
            ));
        }
        let mut code = 1;
        if GetExitCodeProcess(process.as_raw_handle(), &mut code) == 0 || code != 0 {
            return Err(AppError::new(
                "service_install_failed",
                "服务操作失败，请检查安装目录权限后重试",
            ));
        }
    }
    Ok(())
}

pub fn autostart_enabled() -> Result<bool> {
    let key = winreg::RegKey::predef(winreg::enums::HKEY_CURRENT_USER)
        .open_subkey("Software\\Microsoft\\Windows\\CurrentVersion\\Run");
    let value: String = match key.and_then(|key| key.get_value("MihomoOptimized")) {
        Ok(value) => value,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(false),
        Err(error) => return Err(AppError::io("无法读取登录启动设置", error)),
    };
    Ok(value == autostart_value()?)
}

fn autostart_value() -> Result<String> {
    let path = std::env::current_exe().map_err(|e| AppError::io("无法定位启动程序", e))?;
    Ok(format!("\"{}\" --tray", path.display()))
}

pub fn set_autostart(enabled: bool) -> Result<()> {
    let (key, _) = winreg::RegKey::predef(winreg::enums::HKEY_CURRENT_USER)
        .create_subkey("Software\\Microsoft\\Windows\\CurrentVersion\\Run")
        .map_err(|e| AppError::io("无法打开登录启动设置", e))?;
    if enabled {
        key.set_value("MihomoOptimized", &autostart_value()?)
            .map_err(|e| AppError::io("无法启用登录启动", e))?;
    } else if autostart_enabled()? {
        key.delete_value("MihomoOptimized")
            .map_err(|e| AppError::io("无法关闭登录启动", e))?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temporary_directory() -> PathBuf {
        let mut nonce = [0_u8; 12];
        getrandom::getrandom(&mut nonce).unwrap();
        let path = std::env::temp_dir().join(format!(
            "mihomo-windows-security-test-{}",
            hex::encode(nonce)
        ));
        fs::create_dir(&path).unwrap();
        path
    }

    #[test]
    fn protected_file_rejects_hard_links() {
        let directory = temporary_directory();
        let file = directory.join("original.txt");
        fs::write(&file, b"fixture").unwrap();
        assert!(reject_hard_links(&file).is_ok());
        fs::hard_link(&file, directory.join("linked.txt")).unwrap();
        assert_eq!(reject_hard_links(&file).unwrap_err().code, "unsafe_file");
        fs::remove_dir_all(directory).unwrap();
    }

    #[test]
    fn owner_that_can_regrant_write_access_is_rejected() {
        let sid = current_sid().unwrap();
        if sid == "S-1-5-18" {
            return;
        }
        let directory = temporary_directory();
        let file = directory.join("owned.txt");
        fs::write(&file, b"fixture").unwrap();
        apply_dacl(
            &file,
            &format!("O:{sid}D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GR;;;{sid})"),
        )
        .unwrap();
        let result = require_admin_protected(&file);
        // The owner has no write ACE, but Windows still allows it to change the DACL.
        apply_dacl(&file, &format!("O:{sid}D:P(A;;FA;;;SY)(A;;FA;;;{sid})")).unwrap();
        fs::remove_dir_all(directory).unwrap();
        assert_eq!(result.unwrap_err().code, "service_untrusted_permissions");
    }
}
