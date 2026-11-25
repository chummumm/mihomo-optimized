//! Independent evidence for adopting a legacy installer record. Old Windows
//! Uninstall keys can inherit write grants to Terminal Server User; that record
//! alone cannot authorize elevated cleanup of a filesystem path.

use crate::{
    error::{AppError, Result},
    installation, windows,
};
use std::{
    fs,
    io::Read,
    os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle},
    path::{Path, PathBuf},
};
use windows_api::{
    core::Interface,
    Win32::{
        Foundation::RPC_E_CHANGED_MODE,
        System::Com::{
            CoCreateInstance, CoInitializeEx, CoTaskMemFree, CoUninitialize, IPersistStream,
            CLSCTX_INPROC_SERVER, COINIT_MULTITHREADED,
        },
        UI::Shell::{
            FOLDERID_CommonPrograms, IShellLinkW, SHCreateMemStream, SHGetKnownFolderPath,
            ShellLink, KF_FLAG_DEFAULT, SLGP_RAWPATH,
        },
    },
};
use windows_service::{
    service::ServiceAccess,
    service_manager::{ServiceManager, ServiceManagerAccess},
};
use windows_sys::Win32::{
    Foundation::INVALID_HANDLE_VALUE,
    Storage::FileSystem::{
        CreateFileW, GetFileInformationByHandle, BY_HANDLE_FILE_INFORMATION,
        FILE_ATTRIBUTE_DIRECTORY, FILE_ATTRIBUTE_REPARSE_POINT, FILE_FLAG_BACKUP_SEMANTICS,
        FILE_FLAG_OPEN_REPARSE_POINT, FILE_LIST_DIRECTORY, FILE_READ_ATTRIBUTES, FILE_READ_DATA,
        FILE_SHARE_READ, OPEN_EXISTING, READ_CONTROL,
    },
};

pub(super) fn require_anchor(directory: &Path) -> Result<()> {
    // An existing foreign or writable SCM service is a conflict, not absence.
    if service_anchor(directory)? {
        return Ok(());
    }
    let reason = match shortcut_anchor(directory) {
        Ok(true) => return Ok(()),
        Ok(false) => String::new(),
        Err(error) => format!("（公共快捷方式检查：{error}）"),
    };
    // A missing or unusable optional shortcut still permits the caller to check
    // its independent complete protected-layout and build-manifest evidence.
    Err(AppError::new(
        "service_legacy_identity_missing",
        format!("无法从受保护的本产品服务或公共开始菜单快捷方式确认旧安装位置，未采用不安全的旧安装记录{reason}"),
    ))
}

fn service_anchor(directory: &Path) -> Result<bool> {
    let manager = ServiceManager::local_computer(None::<&str>, ServiceManagerAccess::CONNECT)
        .map_err(super::service_error)?;
    let service = match manager.open_service(
        super::SERVICE_NAME,
        ServiceAccess::QUERY_CONFIG | ServiceAccess::READ_CONTROL,
    ) {
        Ok(service) => service,
        Err(error)
            if super::has_service_error(&error, 1060) || super::has_service_error(&error, 1072) =>
        {
            return Ok(false)
        }
        Err(error) => return Err(super::service_error(error)),
    };
    windows::require_admin_service(service.raw_handle())?;
    super::require_owned_service(&service, directory)?;
    Ok(true)
}

fn com_error(error: windows_api::core::Error) -> AppError {
    AppError::new(
        "service_legacy_shortcut_error",
        format!(
            "无法读取受保护的旧安装快捷方式（HRESULT 0x{:08X}）",
            error.code().0 as u32
        ),
    )
}

struct ComApartment(bool);
impl Drop for ComApartment {
    fn drop(&mut self) {
        if self.0 {
            unsafe { CoUninitialize() };
        }
    }
}

fn common_shortcut() -> Result<PathBuf> {
    unsafe {
        let raw = SHGetKnownFolderPath(&FOLDERID_CommonPrograms, KF_FLAG_DEFAULT, None)
            .map_err(com_error)?;
        let value = raw.to_string().map_err(|_| {
            AppError::new("service_legacy_shortcut_error", "公共开始菜单目录编码无效")
        });
        CoTaskMemFree(Some(raw.0.cast()));
        Ok(PathBuf::from(value?).join("Mihomo Optimized.lnk"))
    }
}

const MAX_SHORTCUT_BYTES: u64 = 1024 * 1024;

fn shortcut_error_at(error: AppError, path: &Path) -> AppError {
    AppError::new(
        &error.code,
        format!("{}：{}", error.message, path.display()),
    )
}

fn snapshot_shortcut(shortcut: &Path) -> Result<Vec<u8>> {
    let mut paths: Vec<_> = shortcut.ancestors().collect();
    paths.reverse();
    let mut handles = Vec::with_capacity(paths.len());
    let mut parent_security: Option<windows::ShortcutSecurity> = None;
    for path in paths {
        let is_shortcut = path == shortcut;
        let raw = unsafe {
            CreateFileW(
                windows::wide(path).as_ptr(),
                FILE_READ_ATTRIBUTES
                    | READ_CONTROL
                    | if is_shortcut {
                        FILE_READ_DATA
                    } else {
                        FILE_LIST_DIRECTORY
                    },
                FILE_SHARE_READ,
                std::ptr::null(),
                OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
                std::ptr::null_mut(),
            )
        };
        if raw == INVALID_HANDLE_VALUE {
            return Err(shortcut_error_at(
                windows::last_error("无法锁定旧安装快捷方式"),
                path,
            ));
        }
        let handle = unsafe { OwnedHandle::from_raw_handle(raw) };
        let mut information: BY_HANDLE_FILE_INFORMATION = unsafe { std::mem::zeroed() };
        if unsafe { GetFileInformationByHandle(handle.as_raw_handle(), &mut information) } == 0 {
            return Err(shortcut_error_at(
                windows::last_error("无法检查旧安装快捷方式"),
                path,
            ));
        }
        let is_directory = information.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY != 0;
        if information.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT != 0
            || is_directory == is_shortcut
            || (is_shortcut && information.nNumberOfLinks != 1)
        {
            return Err(shortcut_error_at(
                AppError::new(
                    "service_legacy_shortcut_unsafe",
                    "旧安装快捷方式及其目录不能使用链接或异常文件类型",
                ),
                path,
            ));
        }
        let security = windows::shortcut_object_security(handle.as_raw_handle(), is_directory)
            .map_err(|error| shortcut_error_at(error, path))?;
        if let Some(parent) = parent_security {
            parent
                .require_child(security)
                .map_err(|error| shortcut_error_at(error, path))?;
        }
        parent_security = Some(security);
        if is_shortcut {
            let length =
                ((information.nFileSizeHigh as u64) << 32) | information.nFileSizeLow as u64;
            if length == 0 || length > MAX_SHORTCUT_BYTES {
                return Err(AppError::new(
                    "service_legacy_shortcut_unsafe",
                    "旧安装快捷方式大小异常",
                ));
            }
            let mut file = fs::File::from(handle);
            let mut bytes = Vec::with_capacity(length as usize);
            (&mut file)
                .take(MAX_SHORTCUT_BYTES + 1)
                .read_to_end(&mut bytes)
                .map_err(|error| AppError::io("无法读取旧安装快捷方式内容", error))?;
            if bytes.len() as u64 != length {
                return Err(AppError::new(
                    "service_legacy_shortcut_unsafe",
                    "旧安装快捷方式读取长度不一致",
                ));
            }
            // The checked data-read handle denies write/delete sharing. COM
            // receives only this bounded snapshot and never reopens the path.
            return Ok(bytes);
        }
        handles.push(handle);
    }
    Err(AppError::new(
        "service_legacy_shortcut_unsafe",
        "旧安装快捷方式路径无效",
    ))
}

fn shortcut_anchor(directory: &Path) -> Result<bool> {
    let shortcut = installation::supported_local_path(&common_shortcut()?)?;
    let bytes = snapshot_shortcut(&shortcut)?;
    let initialized = unsafe { CoInitializeEx(None, COINIT_MULTITHREADED) };
    if initialized.is_err() && initialized != RPC_E_CHANGED_MODE {
        return Err(com_error(initialized.into()));
    }
    let _apartment = ComApartment(initialized.is_ok());
    let link: IShellLinkW =
        unsafe { CoCreateInstance(&ShellLink, None, CLSCTX_INPROC_SERVER) }.map_err(com_error)?;
    let persistent: IPersistStream = link.cast().map_err(com_error)?;
    let stream = unsafe { SHCreateMemStream(Some(&bytes)) }.ok_or_else(|| {
        AppError::new(
            "service_legacy_shortcut_error",
            "无法分配旧安装快捷方式读取缓冲区",
        )
    })?;
    unsafe { persistent.Load(&stream) }.map_err(com_error)?;
    let mut target = vec![0_u16; 32768];
    // Never Resolve, launch, load an icon, or expand an arbitrary user location.
    // The stored target remains useful even when old program files are missing.
    unsafe { link.GetPath(&mut target, std::ptr::null_mut(), SLGP_RAWPATH.0 as u32) }
        .map_err(com_error)?;
    let end = target
        .iter()
        .position(|value| *value == 0)
        .ok_or_else(|| AppError::new("service_legacy_shortcut_error", "旧安装快捷方式目标过长"))?;
    let target = String::from_utf16(&target[..end]).map_err(|_| {
        AppError::new(
            "service_legacy_shortcut_error",
            "旧安装快捷方式目标编码无效",
        )
    })?;
    Ok(super::service_image_matches(
        Path::new(&target),
        &directory.join("mihomo-optimized.exe"),
    ))
}
