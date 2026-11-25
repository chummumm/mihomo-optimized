//! Installer-owned file maintenance. The allowlist is extracted beside the
//! fresh helper into a private administrator directory, never read from the
//! installation being repaired. Directory handles pin every path component
//! while elevated deletion operates on a damaged installation.

use super::{installer_check, uninstall_directory};
use crate::{
    error::{AppError, Result},
    installation, windows,
};
use serde::Deserialize;
use std::{
    collections::BTreeSet,
    fs,
    path::{Component, Path, PathBuf},
    thread,
    time::{Duration, Instant},
};

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Manifest {
    files: Vec<String>,
}

pub(super) fn manifest(path: &Path) -> Result<Vec<PathBuf>> {
    let executable =
        std::env::current_exe().map_err(|error| AppError::io("无法定位安装维护程序", error))?;
    let directory = executable
        .parent()
        .ok_or_else(|| invalid_manifest("无法定位安装维护程序目录"))?;
    let same_parent = path
        .parent()
        .and_then(|parent| parent.canonicalize().ok())
        .zip(directory.canonicalize().ok())
        .is_some_and(|(actual, expected)| super::service_image_matches(&actual, &expected));
    if path.file_name().and_then(|name| name.to_str()) != Some("installer-files.json")
        || !same_parent
    {
        return Err(invalid_manifest("文件清单必须与新安装维护程序一起解包"));
    }
    for candidate in [directory, executable.as_path(), path] {
        windows::reject_reparse_path(candidate)?;
        windows::require_admin_protected(candidate)?;
    }
    windows::reject_hard_links(path)?;
    let metadata =
        fs::metadata(path).map_err(|error| AppError::io("无法读取安装文件清单", error))?;
    if metadata.len() > 1024 * 1024 {
        return Err(invalid_manifest("安装文件清单超出大小限制"));
    }
    let value: Manifest = serde_json::from_slice(
        &fs::read(path).map_err(|error| AppError::io("无法读取安装文件清单", error))?,
    )
    .map_err(|_| invalid_manifest("安装文件清单无效"))?;
    validate_entries(value.files)
}

fn invalid_manifest(message: &str) -> AppError {
    AppError::new("installer_manifest_invalid", message)
}

fn validate_entries(entries: Vec<String>) -> Result<Vec<PathBuf>> {
    if entries.is_empty() || entries.len() > 4096 {
        return Err(invalid_manifest("安装文件清单数量无效"));
    }
    let mut names = BTreeSet::new();
    let mut result = Vec::with_capacity(entries.len());
    for entry in entries {
        let path = PathBuf::from(&entry);
        if entry.is_empty()
            || entry.len() > 1024
            || entry.contains([':', '\0', '\r', '\n', '"'])
            || path.is_absolute()
            || path
                .components()
                .any(|part| !matches!(part, Component::Normal(_)))
            || entry
                .split(['/', '\\'])
                .any(|part| part.is_empty() || part.ends_with([' ', '.']))
            || !names.insert(entry.replace('\\', "/").to_lowercase())
        {
            return Err(invalid_manifest("安装文件清单包含无效或重复的相对路径"));
        }
        result.push(path);
    }
    for required in [
        "mihomo-optimized.exe",
        "mihomo-desktop-host.exe",
        "mihomo-desktop-service.exe",
        "mihomo-core.exe",
        "uninstall.exe",
    ] {
        if !names.contains(required) {
            return Err(invalid_manifest("安装文件清单缺少必要程序"));
        }
    }
    Ok(result)
}

pub(super) fn check_payload(target: &Path, entries: &[PathBuf]) -> Result<()> {
    if !target.exists() {
        return Ok(());
    }
    let known: BTreeSet<String> = entries
        .iter()
        .map(|path| path.to_string_lossy().replace('\\', "/").to_lowercase())
        .collect();
    let mut pending = vec![target.to_path_buf()];
    let mut visited = 0;
    while let Some(directory) = pending.pop() {
        windows::reject_reparse_path(&directory)?;
        for entry in
            fs::read_dir(&directory).map_err(|error| AppError::io("无法检查原安装文件", error))?
        {
            let entry = entry.map_err(|error| AppError::io("无法检查原安装文件", error))?;
            visited += 1;
            if visited > 16384 {
                return Err(AppError::new(
                    "installer_directory_large",
                    "安装目录包含过多额外文件，请使用专用程序目录",
                ));
            }
            let path = entry.path();
            windows::reject_reparse_path(&path)?;
            let kind = entry
                .file_type()
                .map_err(|error| AppError::io("无法检查原安装文件", error))?;
            if kind.is_dir() {
                pending.push(path);
            } else if kind.is_file() {
                let relative = path
                    .strip_prefix(target)
                    .map_err(|_| invalid_manifest("安装文件路径越界"))?;
                let name = relative.to_string_lossy().replace('\\', "/").to_lowercase();
                let extension = path
                    .extension()
                    .and_then(|value| value.to_str())
                    .unwrap_or("")
                    .to_lowercase();
                if !known.contains(&name)
                    && matches!(
                        extension.as_str(),
                        "exe" | "dll" | "json" | "config" | "manifest"
                    )
                {
                    return Err(AppError::new(
                        "installer_unknown_runtime_file",
                        format!(
                            "安装目录包含非本安装包的程序或加载配置：{}；未修改该文件",
                            relative.display()
                        ),
                    ));
                }
            } else {
                return Err(AppError::new(
                    "installer_unsafe_file",
                    "安装目录包含不支持的文件类型",
                ));
            }
        }
    }
    Ok(())
}

pub(super) fn check(target: &Path, list: &Path) -> Result<()> {
    let entries = manifest(list)?;
    installer_check(target)?;
    check_payload(target, &entries)
}

pub(super) fn replace_files(target: &Path, list: &Path) -> Result<()> {
    let entries = manifest(list)?;
    check(target, list)?;
    super::installer_prepare(target)?;
    let mut guard = installation::prepared_guard(target)?;
    // Recheck while the installer pins the protected directory; a file added
    // after preflight must not be promoted into trusted runtime.
    check_payload(guard.directory(), &entries)?;
    remove_files(&mut guard, &entries, false, false)
}

pub(super) fn remove_installation(target: &Path, list: &Path, parent_pid: u32) -> Result<()> {
    let entries = manifest(list)?;
    // Keep the protected registration object until cleanup succeeds.
    let registration = installation::maintenance_registration(target)?;
    let mut guard = installation::maintenance_guard(registration.directory())?;
    let directory = guard.directory().to_path_buf();
    let directory_existed = guard.directory_exists();
    // NSIS normally runs an extracted uninstaller. Its documented _?= test/
    // maintenance form runs in place; only that exact live image may remain
    // for its caller to remove after exit, never an arbitrary locked binary.
    let keep_uninstaller = windows::process_path(parent_pid)
        .is_ok_and(|image| super::service_image_matches(&image, &directory.join("uninstall.exe")));
    if directory_existed {
        pin_file_directories(&mut guard, &entries)?;
    }
    uninstall_directory(&directory)?;
    if directory_existed {
        remove_files(&mut guard, &entries, keep_uninstaller, true)?;
        // Delete the pinned directory object itself so its parent cannot swap
        // the path between unlock and deletion.
        guard.remove_empty_directory(&directory)?;
    }
    registration.remove()
}

fn pin_file_directories(
    guard: &mut installation::DirectoryGuard,
    entries: &[PathBuf],
) -> Result<Vec<PathBuf>> {
    let root = guard.directory().to_path_buf();
    let mut paths = BTreeSet::new();
    for entry in entries {
        for parent in entry
            .ancestors()
            .skip(1)
            .filter(|path| !path.as_os_str().is_empty())
        {
            paths.insert(root.join(parent));
        }
    }
    let mut paths: Vec<_> = paths.into_iter().collect();
    paths.sort_by_key(|path| path.components().count());
    let mut pinned = Vec::new();
    for path in paths {
        match fs::symlink_metadata(&path) {
            Ok(_) => {
                guard.lock_directory(&path)?;
                pinned.push(path);
            }
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => (),
            Err(error) => return Err(AppError::io("无法锁定待清理目录", error)),
        }
    }
    Ok(pinned)
}

fn remove_files(
    guard: &mut installation::DirectoryGuard,
    entries: &[PathBuf],
    keep_uninstaller: bool,
    remove_directories: bool,
) -> Result<()> {
    let root = guard.directory().to_path_buf();
    let directories = pin_file_directories(guard, entries)?;
    let started = Instant::now();
    for relative in entries {
        if keep_uninstaller && relative == Path::new("uninstall.exe") {
            continue;
        }
        let path = root.join(relative);
        // A parent that was absent when locks were acquired must stay out of
        // this operation. Otherwise an unprivileged owner of a damaged root
        // could insert a junction after the check and redirect later deletion.
        if path.parent().is_some_and(|parent| {
            parent != root && !directories.iter().any(|pinned| pinned == parent)
        }) {
            continue;
        }
        match fs::symlink_metadata(&path) {
            Ok(metadata) => {
                if !metadata.is_file() {
                    return Err(AppError::new(
                        "installer_unsafe_file",
                        "待清理的程序路径不是普通文件",
                    ));
                }
                windows::reject_reparse_path(&path)?;
                windows::reject_hard_links(&path)?;
            }
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => continue,
            Err(error) => return Err(AppError::io("无法检查待清理程序", error)),
        }
        loop {
            match fs::remove_file(&path) {
                Ok(()) => break,
                Err(error) if error.kind() == std::io::ErrorKind::NotFound => break,
                Err(error)
                    if matches!(error.raw_os_error(), Some(5 | 32))
                        && started.elapsed() < Duration::from_secs(40) =>
                {
                    thread::sleep(Duration::from_millis(100))
                }
                Err(error) => {
                    return Err(AppError::io(
                        &format!("无法移除程序文件 {}", relative.display()),
                        error,
                    ))
                }
            }
        }
    }
    if remove_directories {
        for directory in directories.into_iter().rev() {
            guard.remove_empty_directory(&directory)?;
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn manifest_rejects_traversal_aliases_and_duplicates() {
        let baseline = || {
            [
                "mihomo-optimized.exe",
                "mihomo-desktop-host.exe",
                "mihomo-desktop-service.exe",
                "mihomo-core.exe",
                "uninstall.exe",
            ]
            .into_iter()
            .map(str::to_owned)
            .collect::<Vec<_>>()
        };
        assert!(validate_entries(baseline()).is_ok());
        for entry in [
            "../outside.exe",
            "C:\\outside.exe",
            "resources/../outside.dll",
            "resources/x.dll:stream",
            "resources//x.dll",
            "resources/x.dll.",
        ] {
            let mut entries = baseline();
            entries.push(entry.into());
            assert!(validate_entries(entries).is_err(), "{entry}");
        }
        let mut duplicate = baseline();
        duplicate.push("MIHOMO-CORE.EXE".into());
        assert!(validate_entries(duplicate).is_err());
        let mut unicode = baseline();
        unicode.push("resources/字体 文件.json".into());
        assert!(validate_entries(unicode).is_ok());
    }
}
