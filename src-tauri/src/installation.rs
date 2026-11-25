//! Windows installation identity and filesystem protection.
//!
//! The administrator-owned installer record, not a particular drive letter,
//! identifies an installation. Runtime checks never repair permissions or
//! reinterpret a damaged installation as a writable portable copy. Installer
//! and removal operations have separate contracts because removal must work
//! even when application files have already disappeared.
use crate::{
    error::{AppError, Result},
    windows,
};
use std::{
    fs,
    os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle},
    path::{Path, PathBuf},
};
use windows_sys::Win32::{
    Foundation::{GetLastError, LocalFree, ERROR_ALREADY_EXISTS, INVALID_HANDLE_VALUE},
    Security::{
        Authorization::ConvertStringSecurityDescriptorToSecurityDescriptorW, SECURITY_ATTRIBUTES,
    },
    Storage::FileSystem::{
        CreateDirectoryW, CreateFileW, FileDispositionInfo, GetDriveTypeW,
        GetFileInformationByHandle, GetVolumeInformationW, QueryDosDeviceW,
        SetFileInformationByHandle, BY_HANDLE_FILE_INFORMATION, DELETE, FILE_ATTRIBUTE_DIRECTORY,
        FILE_ATTRIBUTE_REPARSE_POINT, FILE_DISPOSITION_INFO, FILE_FLAG_BACKUP_SEMANTICS,
        FILE_FLAG_OPEN_REPARSE_POINT, FILE_READ_ATTRIBUTES, FILE_SHARE_READ, OPEN_EXISTING,
        READ_CONTROL,
    },
};
use winreg::{
    enums::{HKEY_LOCAL_MACHINE, KEY_READ, KEY_WOW64_32KEY, KEY_WOW64_64KEY},
    RegKey,
};

const UNINSTALL_KEY: &str = r"Software\Microsoft\Windows\CurrentVersion\Uninstall\Mihomo Optimized";
const PRODUCT_DIRECTORY_SDDL: &str = "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;GRGX;;;BU)";
const MAX_TREE_DEPTH: usize = 64;

fn registration_error() -> AppError {
    AppError::new(
        "service_install_mismatch",
        "安装目录与 Windows 安装记录不一致，请使用完整安装包修复",
    )
}

fn read_registration(view: u32) -> Result<Option<PathBuf>> {
    let key = match RegKey::predef(HKEY_LOCAL_MACHINE)
        .open_subkey_with_flags(UNINSTALL_KEY, KEY_READ | view)
    {
        Ok(key) => key,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(error) => return Err(AppError::io("无法读取 Windows 安装记录", error)),
    };
    windows::require_admin_registry_key(key.raw_handle())?;
    let binary: String = key
        .get_value("MainBinaryName")
        .map_err(|_| registration_error())?;
    if binary != windows::APPLICATION_EXE {
        return Err(registration_error());
    }
    let location: String = key
        .get_value("InstallLocation")
        .map_err(|_| registration_error())?;
    // Earlier Tauri installers stored literal surrounding quotes.
    let location = location.trim();
    let location = location
        .strip_prefix('"')
        .and_then(|value| value.strip_suffix('"'))
        .unwrap_or(location);
    Ok(Some(normalize_directory(Path::new(location))?))
}

fn registered_directory() -> Result<Option<PathBuf>> {
    let native = read_registration(KEY_WOW64_64KEY)?;
    let legacy = read_registration(KEY_WOW64_32KEY)?;
    match (native, legacy) {
        (Some(native), Some(legacy)) if !same_location(&native, &legacy)? => {
            Err(registration_error())
        }
        (Some(directory), _) | (None, Some(directory)) => Ok(Some(directory)),
        (None, None) => Ok(None),
    }
}

/// Produce an unambiguous local DOS path without requiring its files to exist.
/// Device namespaces, UNC paths, alternate streams, and Win32 name aliases are
/// not valid installation identities.
fn normalize_directory(path: &Path) -> Result<PathBuf> {
    let invalid = || {
        AppError::new(
            "service_install_path",
            "请选择本地磁盘上的绝对安装目录，勿使用网络路径或特殊路径别名",
        )
    };
    let original = path.to_str().ok_or_else(invalid)?;
    let original = original.strip_prefix(r"\\?\").unwrap_or(original);
    let text = original.replace('/', "\\");
    let bytes = text.as_bytes();
    if bytes.len() < 4 || !bytes[0].is_ascii_alphabetic() || bytes[1..3] != *b":\\" {
        return Err(invalid());
    }
    let mut parts = Vec::new();
    for part in text[3..].split('\\').filter(|part| !part.is_empty()) {
        if part == "."
            || part == ".."
            || part.ends_with(' ')
            || part.ends_with('.')
            || part
                .chars()
                .any(|character| character < ' ' || ":\"<>|?*".contains(character))
        {
            return Err(invalid());
        }
        let basename = part.split('.').next().unwrap_or(part).to_ascii_uppercase();
        if ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$"].contains(&basename.as_str())
            || (basename.len() == 4
                && (basename.starts_with("COM") || basename.starts_with("LPT"))
                && matches!(basename.as_bytes()[3], b'1'..=b'9'))
            || ["COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³"].contains(&basename.as_str())
        {
            return Err(invalid());
        }
        parts.push(part);
    }
    if parts.is_empty() {
        return Err(invalid());
    }
    Ok(PathBuf::from(format!(
        "{}:\\{}",
        (bytes[0] as char).to_ascii_uppercase(),
        parts.join("\\")
    )))
}

fn same_location(left: &Path, right: &Path) -> Result<bool> {
    let spelling = |path: &Path| -> Result<PathBuf> {
        match path.canonicalize() {
            Ok(canonical) => normalize_directory(&canonical),
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => normalize_directory(path),
            Err(error) => Err(AppError::io("无法确认安装目录身份", error)),
        }
    };
    let left = spelling(left)?;
    let right = spelling(right)?;
    Ok(left
        .as_os_str()
        .to_string_lossy()
        .eq_ignore_ascii_case(&right.as_os_str().to_string_lossy()))
}

fn require_supported_volume(directory: &Path) -> Result<()> {
    let text = directory.to_str().ok_or_else(registration_error)?;
    let root = windows::wide(&text[..3]);
    let unsupported = || {
        AppError::new("service_install_volume", "安装目录需要本地固定磁盘和支持访问权限的文件系统（如 NTFS）；请勿使用网络盘、临时映射盘或不支持权限的磁盘")
    };
    // GetDriveType: DRIVE_FIXED = 3. GetVolumeInformation: FILE_PERSISTENT_ACLS = 8.
    if unsafe { GetDriveTypeW(root.as_ptr()) } != 3 {
        return Err(unsupported());
    }
    let mut flags = 0;
    if unsafe {
        GetVolumeInformationW(
            root.as_ptr(),
            std::ptr::null_mut(),
            0,
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            &mut flags,
            std::ptr::null_mut(),
            0,
        )
    } == 0
    {
        return Err(windows::last_error("无法检查安装磁盘"));
    }
    if flags & 8 == 0 {
        return Err(unsupported());
    }
    // A per-user SUBST drive can look fixed but resolve differently to SYSTEM.
    let mut device = vec![0u16; 32768];
    if unsafe {
        QueryDosDeviceW(
            windows::wide(&text[..2]).as_ptr(),
            device.as_mut_ptr(),
            device.len() as u32,
        )
    } == 0
    {
        return Err(windows::last_error("无法检查安装磁盘映射"));
    }
    if !unsafe { windows::read_wide(device.as_ptr()) }.starts_with(r"\Device\") {
        return Err(unsupported());
    }
    Ok(())
}

fn require_parent_chain(directory: &Path) -> Result<()> {
    for parent in directory.ancestors().skip(1) {
        match fs::symlink_metadata(parent) {
            Ok(metadata) if metadata.is_dir() => windows::require_admin_parent(parent)?,
            Ok(_) => {
                return Err(AppError::new(
                    "service_install_path",
                    "安装目录的上级路径不是文件夹",
                ))
            }
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => (),
            Err(error) => return Err(AppError::io("无法检查安装目录的上级路径", error)),
        }
    }
    Ok(())
}

fn require_registered_directory(directory: &Path) -> Result<PathBuf> {
    let requested = normalize_directory(directory)?;
    let registered = registered_directory()?.ok_or_else(registration_error)?;
    if !same_location(&requested, &registered)? {
        return Err(registration_error());
    }
    windows::reject_reparse_path(&requested)?;
    windows::reject_reparse_path(&registered)?;
    existing_directory_spelling(&requested)
}

// SCM and firewall identities should use the real DOS spelling, even when the
// installer was given an 8.3 alias. Cleanup still accepts a missing directory.
fn existing_directory_spelling(directory: &Path) -> Result<PathBuf> {
    match directory.canonicalize() {
        Ok(canonical) => normalize_directory(&canonical),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            normalize_directory(directory)
        }
        Err(error) => Err(AppError::io("无法确认安装目录路径", error)),
    }
}

fn protected_directory(directory: &Path) -> Result<PathBuf> {
    let directory = require_registered_directory(directory)?;
    require_supported_volume(&directory)?;
    require_parent_chain(&directory)?;
    windows::require_admin_protected(&directory)?;
    if !directory.is_dir() {
        return Err(AppError::new(
            "service_install_missing",
            "已登记的安装目录不存在，请使用完整安装包修复",
        ));
    }
    directory
        .canonicalize()
        .map_err(|error| AppError::io("无法定位安装目录", error))
}

/// A portable copy may coexist with an installation elsewhere. Once this
/// executable matches the registered location, every protection error is fatal.
/// The core may be temporarily absent during an interrupted update; callers
/// that execute it additionally validate/repair the bundled files.
pub fn directory_for_application(application: &Path) -> Result<Option<PathBuf>> {
    let Some(registered) = registered_directory()? else {
        return Ok(None);
    };
    let directory = application.parent().ok_or_else(registration_error)?;
    if !same_location(directory, &registered)? {
        return Ok(None);
    }
    windows::reject_reparse_path(application)?;
    windows::reject_hard_links(application)?;
    windows::require_admin_protected(application)?;
    Ok(Some(protected_directory(directory)?))
}

/// Read-only check before an elevated installer may ask an old UI to exit.
pub fn validate_existing_directory(directory: &Path) -> Result<()> {
    let directory = protected_directory(directory)?;
    validate_runtime_tree(&directory, 0)?;
    windows::validate_installation_files(&directory, false)
}

fn require_existing_directory_boundary(directory: &Path) -> Result<()> {
    windows::require_admin_protected(directory).map_err(|error| {
        if error.code == "service_untrusted_permissions" {
            AppError::new("service_install_recreate_required",
                "原安装文件夹的权限不安全，无法直接接管。请先用新版安装包移除旧安装，再安装到新的或空的程序文件夹；用户配置会保留")
        } else { error }
    })
}

fn directory_is_empty(directory: &Path) -> Result<bool> {
    let mut entries =
        fs::read_dir(directory).map_err(|error| AppError::io("无法检查安装目录", error))?;
    match entries.next() {
        None => Ok(true),
        Some(Ok(_)) => Ok(false),
        Some(Err(error)) => Err(AppError::io("无法检查安装目录", error)),
    }
}

/// Read-only preflight. An existing nonempty directory belongs to the product
/// only when the administrator-owned installer record says so, except the
/// narrowly identified, protected helper residue of an old uninstall. Shared
/// parents are inspected but never modified, even during a legacy-install repair.
pub fn preflight_directory(directory: &Path) -> Result<PathBuf> {
    let directory = normalize_directory(directory)?;
    windows::reject_reparse_path(&directory)?;
    require_supported_volume(&directory)?;
    require_parent_chain(&directory)?;
    let registered = registered_directory()?;
    if let Some(registered) = &registered {
        if !same_location(&directory, registered)? {
            return Err(registration_error());
        }
    }
    if directory.exists() {
        if !directory.is_dir() {
            return Err(AppError::new("service_install_path", "安装路径不是文件夹"));
        }
        let empty = directory_is_empty(&directory)?;
        if registered.is_none() && !empty && !is_legacy_helper_residue(&directory)? {
            return Err(AppError::new(
                "service_install_nonempty",
                "首次安装请选择新的或空的程序文件夹，现有文件不会被覆盖或更改权限",
            ));
        }
        // Empty unsafe leaves are replaced as objects during preparation.
        // Nonempty user-controlled directories cannot be made trusted by ACL
        // changes: an already opened WRITE_DAC handle retains its authority.
        if !empty {
            inspect_tree(&directory, 0)?;
        }
    }
    existing_directory_spelling(&directory)
}

fn is_legacy_helper_residue(directory: &Path) -> Result<bool> {
    let entries = fs::read_dir(directory)
        .map_err(|error| AppError::io("无法检查旧安装残留", error))?
        .collect::<std::io::Result<Vec<_>>>()
        .map_err(|error| AppError::io("无法检查旧安装残留", error))?;
    if entries.len() != 1 || entries[0].file_name() != "mihomo-desktop-service.exe" {
        return Ok(false);
    }
    // Published 0.1.1 could remove its registration but leave a locked helper.
    // Do not execute or trust its contents: the installer replaces it from its
    // own package. Requiring the old admin boundary avoids adopting user data.
    let helper = entries[0].path();
    if !helper.is_file() {
        return Ok(false);
    }
    windows::require_admin_protected(directory)?;
    windows::reject_reparse_path(&helper)?;
    windows::reject_hard_links(&helper)?;
    windows::require_admin_protected(&helper)?;
    Ok(true)
}

fn inspect_tree(path: &Path, depth: usize) -> Result<()> {
    if depth > MAX_TREE_DEPTH {
        return Err(AppError::new("unsafe_directory", "安装目录层级异常"));
    }
    windows::reject_reparse_path(path)?;
    let metadata =
        fs::symlink_metadata(path).map_err(|error| AppError::io("无法检查安装文件", error))?;
    if metadata.is_dir() {
        require_existing_directory_boundary(path)?;
        for entry in
            fs::read_dir(path).map_err(|error| AppError::io("无法检查安装文件夹", error))?
        {
            inspect_tree(
                &entry
                    .map_err(|error| AppError::io("无法检查安装文件", error))?
                    .path(),
                depth + 1,
            )?;
        }
    } else if metadata.is_file() {
        windows::reject_hard_links(path)?;
    } else {
        return Err(AppError::new(
            "unsafe_file",
            "安装目录只能包含普通文件和文件夹",
        ));
    }
    Ok(())
}

fn validate_runtime_tree(path: &Path, depth: usize) -> Result<()> {
    if depth > MAX_TREE_DEPTH {
        return Err(AppError::new("unsafe_directory", "安装目录层级异常"));
    }
    windows::reject_reparse_path(path)?;
    let metadata =
        fs::symlink_metadata(path).map_err(|error| AppError::io("无法验证安装文件", error))?;
    if metadata.is_dir() {
        windows::require_admin_protected(path)?;
        for entry in
            fs::read_dir(path).map_err(|error| AppError::io("无法验证安装文件夹", error))?
        {
            validate_runtime_tree(
                &entry
                    .map_err(|error| AppError::io("无法验证安装文件", error))?
                    .path(),
                depth + 1,
            )?;
        }
    } else if metadata.is_file() {
        windows::reject_hard_links(path)?;
        let extension = path
            .extension()
            .and_then(|value| value.to_str())
            .unwrap_or("")
            .to_ascii_lowercase();
        if matches!(
            extension.as_str(),
            "exe" | "dll" | "json" | "config" | "manifest"
        ) {
            windows::require_admin_protected(path)?;
        }
    } else {
        return Err(AppError::new("unsafe_file", "安装目录存在不支持的文件类型"));
    }
    Ok(())
}

/// Pins path components while a maintenance operation acts on an old, possibly
/// writable installation. No write/delete sharing prevents pre-existing writer
/// handles and path replacement from being mistaken for safe cleanup targets.
pub struct DirectoryGuard {
    directory: PathBuf,
    handles: Vec<(PathBuf, OwnedHandle)>,
    removal: bool,
}

impl DirectoryGuard {
    fn parents(directory: PathBuf, removal: bool) -> Result<Self> {
        let mut guard = Self {
            directory,
            handles: Vec::new(),
            removal,
        };
        let parents: Vec<_> = guard
            .directory
            .ancestors()
            .skip(1)
            .map(Path::to_path_buf)
            .collect();
        for parent in parents.into_iter().rev() {
            match fs::symlink_metadata(&parent) {
                Ok(_) => guard.pin(&parent)?,
                Err(error) if error.kind() == std::io::ErrorKind::NotFound => (),
                Err(error) => return Err(AppError::io("无法锁定安装路径", error)),
            }
        }
        Ok(guard)
    }

    fn new(directory: PathBuf, removal: bool) -> Result<Self> {
        let mut guard = Self::parents(directory, removal)?;
        let root = guard.directory.clone();
        match fs::symlink_metadata(&root) {
            Ok(_) => guard.pin(&root)?,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => (),
            Err(error) => return Err(AppError::io("无法锁定安装目录", error)),
        }
        Ok(guard)
    }

    fn pin(&mut self, path: &Path) -> Result<()> {
        if self.handles.iter().any(|(existing, _)| existing == path) {
            return Ok(());
        }
        let (handle, information) =
            open_protected_object(path, self.removal && path.starts_with(&self.directory))?;
        if information.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY == 0 {
            return Err(AppError::new(
                "unsafe_directory",
                "安装路径中存在非文件夹对象",
            ));
        }
        self.handles.push((path.to_path_buf(), handle));
        Ok(())
    }

    pub fn directory(&self) -> &Path {
        &self.directory
    }

    pub fn directory_exists(&self) -> bool {
        self.handles.iter().any(|(path, _)| path == &self.directory)
    }

    /// Only installation descendants can be added to the retained cleanup set.
    pub fn lock_directory(&mut self, path: &Path) -> Result<()> {
        let path = normalize_directory(path)?;
        if !path.starts_with(&self.directory) {
            return Err(AppError::new(
                "unsafe_directory",
                "清理路径不能超出安装目录",
            ));
        }
        windows::reject_reparse_path(&path)?;
        let mut descendants: Vec<_> = path
            .ancestors()
            .take_while(|ancestor| ancestor.starts_with(&self.directory))
            .map(Path::to_path_buf)
            .collect();
        descendants.reverse();
        for directory in descendants {
            self.pin(&directory)?;
        }
        Ok(())
    }

    /// Remove the exact pinned empty object while retaining its parent chain.
    /// Nonempty directories containing preserved user files remain in place.
    pub fn remove_empty_directory(&mut self, path: &Path) -> Result<bool> {
        if !self.removal || !path.starts_with(&self.directory) {
            return Err(AppError::new("unsafe_directory", "未授权移除此维护目录"));
        }
        let Some(index) = self
            .handles
            .iter()
            .position(|(existing, _)| existing == path)
        else {
            // Never adopt a replacement created after an absent target was checked.
            return Ok(true);
        };
        let removed = mark_empty_directory_deleted(&self.handles[index].1)?;
        if removed {
            self.handles.remove(index);
        }
        Ok(removed)
    }
}

fn open_protected_object(
    path: &Path,
    removal: bool,
) -> Result<(OwnedHandle, BY_HANDLE_FILE_INFORMATION)> {
    let raw = unsafe {
        CreateFileW(
            windows::wide(path).as_ptr(),
            FILE_READ_ATTRIBUTES | READ_CONTROL | if removal { DELETE } else { 0 },
            FILE_SHARE_READ,
            std::ptr::null(),
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
            std::ptr::null_mut(),
        )
    };
    if raw == INVALID_HANDLE_VALUE {
        return Err(windows::last_error(
            "无法锁定安装对象，请退出占用程序后重试",
        ));
    }
    let handle = unsafe { OwnedHandle::from_raw_handle(raw) };
    let mut information: BY_HANDLE_FILE_INFORMATION = unsafe { std::mem::zeroed() };
    if unsafe { GetFileInformationByHandle(handle.as_raw_handle(), &mut information) } == 0 {
        return Err(windows::last_error("无法验证安装对象"));
    }
    if information.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT != 0 {
        return Err(AppError::new(
            "unsafe_directory",
            "安装对象不能使用符号链接或目录联接",
        ));
    }
    if information.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY == 0
        && information.nNumberOfLinks != 1
    {
        return Err(AppError::new("unsafe_file", "安装文件不能使用硬链接"));
    }
    Ok((handle, information))
}

fn mark_empty_directory_deleted(handle: &OwnedHandle) -> Result<bool> {
    let disposition = FILE_DISPOSITION_INFO { DeleteFile: true };
    if unsafe {
        SetFileInformationByHandle(
            handle.as_raw_handle(),
            FileDispositionInfo,
            (&disposition as *const FILE_DISPOSITION_INFO).cast(),
            std::mem::size_of::<FILE_DISPOSITION_INFO>() as u32,
        )
    } == 0
    {
        let error = std::io::Error::last_os_error();
        if error.raw_os_error() == Some(145) {
            return Ok(false);
        }
        return Err(AppError::io("无法移除空安装目录", error));
    }
    Ok(true)
}

fn create_product_directory(path: &Path) -> Result<()> {
    unsafe {
        let mut descriptor = std::ptr::null_mut();
        if ConvertStringSecurityDescriptorToSecurityDescriptorW(
            windows::wide(PRODUCT_DIRECTORY_SDDL).as_ptr(),
            1,
            &mut descriptor,
            std::ptr::null_mut(),
        ) == 0
        {
            return Err(windows::last_error("无法生成安装目录权限"));
        }
        let attributes = SECURITY_ATTRIBUTES {
            nLength: std::mem::size_of::<SECURITY_ATTRIBUTES>() as u32,
            lpSecurityDescriptor: descriptor,
            bInheritHandle: 0,
        };
        let success = CreateDirectoryW(windows::wide(path).as_ptr(), &attributes);
        let error = if success == 0 {
            Some(GetLastError())
        } else {
            None
        };
        LocalFree(descriptor);
        if let Some(error) = error {
            // A concurrent creation is never adopted as an installer-owned path.
            return Err(AppError::io(
                if error == ERROR_ALREADY_EXISTS {
                    "安装目录已被其他进程创建，请重试"
                } else {
                    "无法创建安装目录"
                },
                std::io::Error::from_raw_os_error(error as i32),
            ));
        }
        Ok(())
    }
}

/// Called only by the freshly packaged, elevated installer maintenance helper.
/// Missing ancestors are created with the final ACL in the creation call; no
/// existing directory object is promoted by changing its owner or ACL.
pub fn prepare_directory(directory: &Path) -> Result<PathBuf> {
    let directory = preflight_directory(directory)?;
    let mut guard = DirectoryGuard::parents(directory.clone(), false)?;
    // Recheck after retaining the already trusted parent chain. An unsafe empty
    // leaf is removed by its exact handle, never by a second path lookup.
    preflight_directory(&directory)?;
    if directory.exists() {
        match windows::require_admin_protected(&directory) {
            Ok(()) => (),
            Err(error) if error.code == "service_untrusted_permissions" => {
                let (handle, information) = open_protected_object(&directory, true)?;
                if information.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY == 0
                    || !directory_is_empty(&directory)?
                    || !mark_empty_directory_deleted(&handle)?
                {
                    return Err(AppError::new(
                        "service_install_recreate_required",
                        "原目录已包含文件，无法安全重建；请先移除旧安装或选择新的程序文件夹",
                    ));
                }
                drop(handle);
                // A retained handle may defer removal. CreateDirectory then
                // fails safely; never reuse the old object or a racing creator.
                create_product_directory(&directory)?;
            }
            Err(error) => return Err(error),
        }
        guard.pin(&directory)?;
    }
    let missing: Vec<_> = directory
        .ancestors()
        .take_while(|path| !path.exists())
        .map(Path::to_path_buf)
        .collect();
    for path in missing.into_iter().rev() {
        create_product_directory(&path)?;
        guard.pin(&path)?;
    }
    inspect_tree(&directory, 0)?;
    require_parent_chain(&directory)?;
    existing_directory_spelling(&directory)
}

/// The installer writes its trusted HKLM record after replacing every shipped
/// file, then calls this before starting a service from the new installation.
pub fn finalize_directory(directory: &Path) -> Result<PathBuf> {
    let directory = require_registered_directory(directory)?;
    require_supported_volume(&directory)?;
    require_parent_chain(&directory)?;
    let _guard = DirectoryGuard::new(directory.clone(), false)?;
    validate_existing_directory(&directory)?;
    Ok(directory)
}

/// Cleanup authenticates the installation record but intentionally does not
/// require complete binaries or the runtime permission boundary. Nothing from
/// this path may be executed by a caller using this weaker cleanup contract.
pub fn maintenance_directory(directory: &Path) -> Result<PathBuf> {
    require_registered_directory(directory)
}

pub fn maintenance_guard(directory: &Path) -> Result<DirectoryGuard> {
    DirectoryGuard::new(maintenance_directory(directory)?, true)
}

/// Used for exact-manifest replacement after preparation, including a fresh
/// installation whose HKLM registration is written only after files are copied.
pub fn prepared_guard(directory: &Path) -> Result<DirectoryGuard> {
    let directory = preflight_directory(directory)?;
    windows::require_admin_protected(&directory)?;
    DirectoryGuard::new(directory, false)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn installation_paths_accept_drives_spaces_and_unicode() {
        assert_eq!(
            normalize_directory(Path::new(r"d:\应用 程序\Mihomo Optimized")).unwrap(),
            PathBuf::from(r"D:\应用 程序\Mihomo Optimized")
        );
        assert_eq!(
            normalize_directory(Path::new(r"\\?\D:\应用 程序\Mihomo Optimized\")).unwrap(),
            PathBuf::from(r"D:\应用 程序\Mihomo Optimized")
        );
    }

    #[test]
    fn installation_paths_reject_ambiguous_or_shared_roots() {
        for path in [
            r"D:\",
            r"D:relative",
            r"\folder",
            r"\\server\share\app",
            r"\\.\C:\app",
            r"\\?\GLOBALROOT\Device\app",
            r"C:\app\..\other",
            r"D:\app:stream",
            r"D:\app.\child",
            "D:\\app ",
            r"D:\NUL\app",
            r"D:\COM1.txt",
        ] {
            assert!(
                normalize_directory(Path::new(path)).is_err(),
                "accepted {path}"
            );
        }
    }
}
