//! Ownership and reclamation of downloaded desktop updates.
//!
//! A workspace keeps its namespace and a shared lease open from the first
//! download until its last owner (GUI or monitor) finishes. The monitor attaches
//! before acknowledging the handoff, so there is no unowned interval. Cleanup
//! also claims every payload for exclusive write/delete access: a crashed
//! monitor must not make an installer that is still running look idle.
//!
//! Only this flat, private cache is inspected. Unknown entries and links are
//! retained; cleanup never recursively follows a path or modifies file bytes.

use crate::error::{AppError, Result};
use semver::Version;
use std::{
    collections::BTreeSet,
    ffi::{OsStr, OsString},
    fs::{self, File, OpenOptions},
    io,
    os::windows::{
        ffi::OsStrExt,
        fs::OpenOptionsExt,
        io::{AsRawHandle, FromRawHandle},
    },
    path::{Component, Path, PathBuf},
    sync::atomic::{AtomicBool, Ordering},
    thread,
    time::{Duration, Instant},
};
use windows_sys::Wdk::{
    Foundation::OBJECT_ATTRIBUTES,
    Storage::FileSystem::{
        NtCreateFile, FILE_CREATE, FILE_DIRECTORY_FILE, FILE_NON_DIRECTORY_FILE, FILE_OPEN,
        FILE_OPEN_IF, FILE_OPEN_REPARSE_POINT, FILE_SYNCHRONOUS_IO_NONALERT,
    },
};
use windows_sys::Win32::{
    Foundation::{
        LocalFree, RtlNtStatusToDosError, GENERIC_READ, GENERIC_WRITE, OBJ_CASE_INSENSITIVE,
        OBJ_DONT_REPARSE, STATUS_REPARSE_POINT_ENCOUNTERED, UNICODE_STRING,
    },
    Security::{
        Authorization::ConvertStringSecurityDescriptorToSecurityDescriptorW, SECURITY_DESCRIPTOR,
    },
    Storage::FileSystem::{
        FileDispositionInfo, GetFileInformationByHandle, SetFileInformationByHandle,
        BY_HANDLE_FILE_INFORMATION, DELETE, FILE_ATTRIBUTE_DIRECTORY, FILE_ATTRIBUTE_NORMAL,
        FILE_ATTRIBUTE_REPARSE_POINT, FILE_DISPOSITION_INFO, FILE_FLAG_BACKUP_SEMANTICS,
        FILE_FLAG_OPEN_REPARSE_POINT, FILE_LIST_DIRECTORY, FILE_READ_ATTRIBUTES, FILE_SHARE_READ,
        FILE_SHARE_WRITE, SYNCHRONIZE,
    },
    System::IO::IO_STATUS_BLOCK,
};

const GATE_NAME: &str = "cleanup.lock";
const LEASE_NAME: &str = "active.lock";
const MONITOR_NAME: &str = "desktop-update-monitor.exe";
const CONFIG_DIRECTORY: &str = "gh-config";
const MAX_ENTRIES: usize = 16;
const GATE_WAIT: Duration = Duration::from_secs(2);
static CLEANUP_STARTED: AtomicBool = AtomicBool::new(false);

#[derive(Debug)]
enum CacheError {
    Unsafe,
    Io(io::Error),
}

type CacheResult<T> = std::result::Result<T, CacheError>;

impl From<io::Error> for CacheError {
    fn from(error: io::Error) -> Self {
        Self::Io(error)
    }
}

impl CacheError {
    fn into_app(self) -> AppError {
        match self {
            Self::Unsafe => AppError::new(
                "update_cache_unsafe",
                "更新缓存包含不安全的路径或链接，已停止使用该目录",
            ),
            Self::Io(error) => AppError::new(
                "update_cache_unavailable",
                AppError::io("无法安全使用更新缓存", error).message,
            ),
        }
    }

    fn retryable(&self) -> bool {
        matches!(self, Self::Io(error) if error.kind() != io::ErrorKind::NotFound)
    }
}

/// The path is useful only while this owner is alive. `attach` must finish
/// before the previous owner releases its workspace during a process handoff.
pub(crate) struct UpdateWorkspace {
    // Release the lease before the namespace, so an intervening collector must
    // still wait for the directory handles to close.
    _lease: File,
    _namespace: Vec<File>,
    path: PathBuf,
}

impl UpdateWorkspace {
    pub(crate) fn create() -> Result<Self> {
        let root = crate::windows::user_data_dir()?.join("updates");
        Self::create_in(&root).map_err(CacheError::into_app)
    }

    fn create_in(root_path: &Path) -> CacheResult<Self> {
        let root = CacheRoot::open(root_path, true)?;
        let gate = root.gate(GATE_WAIT)?;
        for _ in 0..4 {
            let mut nonce = [0_u8; 16];
            getrandom::getrandom(&mut nonce)
                .map_err(|_| io::Error::other("system randomness unavailable"))?;
            let name = hex::encode(nonce);
            let path = root.path.join(&name);
            let directory = match create_private_directory(root.handle(), OsStr::new(&name)) {
                Ok(directory) => directory,
                Err(CacheError::Io(error)) if error.kind() == io::ErrorKind::AlreadyExists => {
                    continue;
                }
                Err(error) => return Err(error),
            };
            let lease = open_child(
                &directory,
                OsStr::new(LEASE_NAME),
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                FILE_CREATE,
                FILE_NON_DIRECTORY_FILE,
                std::ptr::null(),
            )?;
            require_regular_file(&lease)?;
            let mut namespace = root.namespace;
            namespace.push(directory);
            // The root gate covers the otherwise vulnerable mkdir -> lease
            // interval. The workspace already owns both before releasing it.
            drop(gate);
            return Ok(Self {
                _lease: lease,
                _namespace: namespace,
                path,
            });
        }
        Err(io::Error::new(io::ErrorKind::AlreadyExists, "update cache collision").into())
    }

    pub(crate) fn attach(directory: &Path) -> Result<Self> {
        let root = crate::windows::user_data_dir()?.join("updates");
        Self::attach_in(&root, directory).map_err(CacheError::into_app)
    }

    fn attach_in(root_path: &Path, directory: &Path) -> CacheResult<Self> {
        let name = directory
            .file_name()
            .and_then(|name| name.to_str())
            .filter(|name| is_attempt_name(name))
            .ok_or(CacheError::Unsafe)?;
        let root = CacheRoot::open(root_path, false)?;
        // The caller's spelling is never used for mutation. Canonicalization
        // here only checks containment; all actual opens use the locked root.
        if directory
            .parent()
            .ok_or(CacheError::Unsafe)?
            .canonicalize()?
            != root.path.canonicalize()?
        {
            return Err(CacheError::Unsafe);
        }
        let gate = root.gate(GATE_WAIT)?;
        let path = root.path.join(name);
        let directory = open_directory_child(root.handle(), OsStr::new(name), false)?;
        let lease = open_child(
            &directory,
            OsStr::new(LEASE_NAME),
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            FILE_OPEN,
            FILE_NON_DIRECTORY_FILE,
            std::ptr::null(),
        )?;
        require_regular_file(&lease)?;
        let mut namespace = root.namespace;
        namespace.push(directory);
        drop(gate);
        Ok(Self {
            _lease: lease,
            _namespace: namespace,
            path,
        })
    }

    pub(crate) fn path(&self) -> &Path {
        &self.path
    }

    /// Cleanup is deliberately secondary to the installation outcome. A busy
    /// monitor image or antivirus handle is retried by the GUI's collector.
    pub(crate) fn finish(self) {
        let path = self.path.clone();
        drop(self);
        if let Some(parent) = path.parent() {
            if let Ok(root) = CacheRoot::open(parent, false) {
                if let Some(name) = path.file_name().and_then(|name| name.to_str()) {
                    let _ = reclaim(&root, name);
                }
            }
        }
    }
}

struct CacheRoot {
    path: PathBuf,
    namespace: Vec<File>,
}

impl CacheRoot {
    fn open(path: &Path, create: bool) -> CacheResult<Self> {
        if !path.is_absolute()
            || path
                .components()
                .any(|part| matches!(part, Component::ParentDir | Component::CurDir))
        {
            return Err(CacheError::Unsafe);
        }
        let parent = path.parent().ok_or(CacheError::Unsafe)?;
        let mut namespace = Vec::new();
        // Only the volume/share root is opened by an absolute path. Every
        // descendant is resolved from its already opened parent, with reparse
        // processing disabled in the same kernel operation as the open.
        for ancestor in parent.ancestors().collect::<Vec<_>>().into_iter().rev() {
            let directory = match namespace.last() {
                Some(parent) => open_directory_child(
                    parent,
                    ancestor.file_name().ok_or(CacheError::Unsafe)?,
                    false,
                )?,
                None => open_volume_root(ancestor)?,
            };
            namespace.push(directory);
        }
        let name = path.file_name().ok_or(CacheError::Unsafe)?;
        let parent = namespace.last().ok_or(CacheError::Unsafe)?;
        let directory = if create {
            match create_private_directory(parent, name) {
                Ok(directory) => directory,
                Err(CacheError::Io(error)) if error.kind() == io::ErrorKind::AlreadyExists => {
                    open_directory_child(parent, name, false)?
                }
                Err(error) => return Err(error),
            }
        } else {
            open_directory_child(parent, name, false)?
        };
        namespace.push(directory);
        Ok(Self {
            path: path.to_owned(),
            namespace,
        })
    }

    fn handle(&self) -> &File {
        self.namespace
            .last()
            .expect("cache root has an open handle")
    }

    fn gate(&self, wait: Duration) -> CacheResult<File> {
        let started = Instant::now();
        loop {
            match open_child(
                self.handle(),
                OsStr::new(GATE_NAME),
                GENERIC_READ | GENERIC_WRITE,
                0,
                FILE_OPEN_IF,
                FILE_NON_DIRECTORY_FILE,
                std::ptr::null(),
            ) {
                Ok(file) => {
                    require_regular_file(&file)?;
                    return Ok(file);
                }
                Err(CacheError::Io(error))
                    if matches!(error.raw_os_error(), Some(32 | 33))
                        && started.elapsed() < wait =>
                {
                    thread::sleep(Duration::from_millis(20));
                }
                Err(error) => return Err(error),
            }
        }
    }
}

fn create_private_directory(parent: &File, name: &OsStr) -> CacheResult<File> {
    let sid = crate::windows::current_sid().map_err(|_| {
        io::Error::new(io::ErrorKind::PermissionDenied, "user identity unavailable")
    })?;
    let sddl = crate::windows::wide(format!("D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;{sid})"));
    let mut descriptor = std::ptr::null_mut();
    if unsafe {
        ConvertStringSecurityDescriptorToSecurityDescriptorW(
            sddl.as_ptr(),
            1,
            &mut descriptor,
            std::ptr::null_mut(),
        )
    } == 0
    {
        return Err(io::Error::last_os_error().into());
    }
    let result = open_child(
        parent,
        name,
        FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        FILE_CREATE,
        FILE_DIRECTORY_FILE,
        descriptor.cast(),
    );
    unsafe { LocalFree(descriptor) };
    let directory = result?;
    require_directory(&directory)?;
    Ok(directory)
}

/// Open exactly one child of an existing directory capability. The name is not
/// an absolute path and cannot traverse another directory. In particular, a
/// parent changed into a junction cannot redirect CREATE to an outside folder.
fn open_child(
    parent: &File,
    name: &OsStr,
    access: u32,
    sharing: u32,
    disposition: u32,
    kind: u32,
    security: *const SECURITY_DESCRIPTOR,
) -> CacheResult<File> {
    let mut units: Vec<u16> = name.encode_wide().collect();
    if units.is_empty()
        || name == "."
        || name == ".."
        || units.iter().any(|unit| matches!(*unit, 0 | 47 | 58 | 92))
        || units.len() > (u16::MAX as usize / 2)
    {
        return Err(CacheError::Unsafe);
    }
    require_directory(parent)?;
    let name = UNICODE_STRING {
        Length: (units.len() * 2) as u16,
        MaximumLength: (units.len() * 2) as u16,
        Buffer: units.as_mut_ptr(),
    };
    let attributes = OBJECT_ATTRIBUTES {
        Length: std::mem::size_of::<OBJECT_ATTRIBUTES>() as u32,
        RootDirectory: parent.as_raw_handle(),
        ObjectName: &name,
        Attributes: OBJ_CASE_INSENSITIVE | OBJ_DONT_REPARSE,
        SecurityDescriptor: security,
        SecurityQualityOfService: std::ptr::null(),
    };
    let mut handle = std::ptr::null_mut();
    let mut status_block: IO_STATUS_BLOCK = unsafe { std::mem::zeroed() };
    let status = unsafe {
        NtCreateFile(
            &mut handle,
            access | SYNCHRONIZE,
            &attributes,
            &mut status_block,
            std::ptr::null(),
            FILE_ATTRIBUTE_NORMAL,
            sharing,
            disposition,
            kind | FILE_OPEN_REPARSE_POINT | FILE_SYNCHRONOUS_IO_NONALERT,
            std::ptr::null(),
            0,
        )
    };
    if status == STATUS_REPARSE_POINT_ENCOUNTERED {
        return Err(CacheError::Unsafe);
    }
    if status < 0 {
        return Err(
            io::Error::from_raw_os_error(unsafe { RtlNtStatusToDosError(status) } as i32).into(),
        );
    }
    let file = unsafe { File::from_raw_handle(handle) };
    // Once a new child is open, its denied DELETE sharing pins that child and
    // keeps the parent nonempty. Do not return a workspace whose parent became
    // a reparse point during this operation, even when the open was harmless.
    require_directory(parent)?;
    Ok(file)
}

fn information(file: &File) -> CacheResult<BY_HANDLE_FILE_INFORMATION> {
    let mut info = unsafe { std::mem::zeroed() };
    if unsafe { GetFileInformationByHandle(file.as_raw_handle(), &mut info) } == 0 {
        return Err(io::Error::last_os_error().into());
    }
    Ok(info)
}

fn open_volume_root(path: &Path) -> CacheResult<File> {
    let file = OpenOptions::new()
        .access_mode(FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES)
        .share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE)
        .custom_flags(FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT)
        .open(path)?;
    require_directory(&file)?;
    Ok(file)
}

fn open_directory_child(parent: &File, name: &OsStr, deleting: bool) -> CacheResult<File> {
    let file = open_child(
        parent,
        name,
        FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | if deleting { DELETE } else { 0 },
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        FILE_OPEN,
        FILE_DIRECTORY_FILE,
        std::ptr::null(),
    )?;
    require_directory(&file)?;
    Ok(file)
}

fn require_directory(file: &File) -> CacheResult<()> {
    let info = information(file)?;
    if info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY == 0
        || info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT != 0
    {
        return Err(CacheError::Unsafe);
    }
    Ok(())
}

fn require_regular_file(file: &File) -> CacheResult<()> {
    let info = information(file)?;
    if info.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT) != 0
        || info.nNumberOfLinks != 1
    {
        return Err(CacheError::Unsafe);
    }
    Ok(())
}

fn open_payload(parent: &File, name: &OsStr) -> CacheResult<File> {
    let file = open_child(
        parent,
        name,
        GENERIC_READ | GENERIC_WRITE | DELETE,
        0,
        FILE_OPEN,
        FILE_NON_DIRECTORY_FILE,
        std::ptr::null(),
    )?;
    require_regular_file(&file)?;
    Ok(file)
}

fn is_attempt_name(name: &str) -> bool {
    name.len() == 32
        && name
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
}

fn is_payload_name(name: &str) -> bool {
    if matches!(name, LEASE_NAME | MONITOR_NAME | "verification.json") {
        return true;
    }
    let installer = name.strip_suffix(".sigstore.json").unwrap_or(name);
    let Some(version) = installer
        .strip_prefix("mihomo-desktop-")
        .and_then(|value| value.strip_suffix("-windows-x64-setup.exe"))
    else {
        return false;
    };
    Version::parse(version).is_ok_and(|parsed| {
        parsed.pre.is_empty() && parsed.build.is_empty() && parsed.to_string() == version
    })
}

fn entries(directory: &Path) -> CacheResult<BTreeSet<OsString>> {
    let mut result = BTreeSet::new();
    for entry in fs::read_dir(directory)? {
        let name = entry?.file_name();
        if result.len() >= MAX_ENTRIES
            || !name
                .to_str()
                .is_some_and(|value| is_payload_name(value) || value == CONFIG_DIRECTORY)
        {
            return Err(CacheError::Unsafe);
        }
        result.insert(name);
    }
    Ok(result)
}

fn require_empty_directory(path: &Path) -> CacheResult<()> {
    match fs::read_dir(path)?.next() {
        None => Ok(()),
        Some(Ok(_)) => Err(CacheError::Unsafe),
        Some(Err(error)) => Err(error.into()),
    }
}

fn delete_on_close(file: File) -> CacheResult<()> {
    let disposition = FILE_DISPOSITION_INFO {
        DeleteFile: true.into(),
    };
    if unsafe {
        SetFileInformationByHandle(
            file.as_raw_handle(),
            FileDispositionInfo,
            (&disposition as *const FILE_DISPOSITION_INFO).cast(),
            std::mem::size_of::<FILE_DISPOSITION_INFO>() as u32,
        )
    } == 0
    {
        return Err(io::Error::last_os_error().into());
    }
    // Close each marked child before marking its parent. Otherwise Windows may
    // still count a delete-pending child when it checks directory emptiness.
    drop(file);
    Ok(())
}

fn reclaim(root: &CacheRoot, name: &str) -> CacheResult<()> {
    if !is_attempt_name(name) {
        return Err(CacheError::Unsafe);
    }
    let gate = root.gate(Duration::ZERO)?;
    let path = root.path.join(name);
    // DELETE access plus no DELETE sharing makes this the sole collector. It
    // also conflicts with a live workspace's namespace handles. No marker is
    // ever created in a legacy directory.
    let directory = open_directory_child(root.handle(), OsStr::new(name), true)?;
    let lease = match open_payload(&directory, OsStr::new(LEASE_NAME)) {
        Ok(file) => Some(file),
        Err(CacheError::Io(error)) if error.kind() == io::ErrorKind::NotFound => None,
        Err(error) => return Err(error),
    };
    drop(gate);

    let names = entries(&path)?;
    let mut files = Vec::new();
    let mut config_directory = None;
    for name in &names {
        if name == LEASE_NAME && lease.is_some() {
            continue;
        }
        if name == CONFIG_DIRECTORY {
            let handle = open_directory_child(&directory, name, true)?;
            require_empty_directory(&path.join(name))?;
            config_directory = Some(handle);
        } else {
            files.push(open_payload(&directory, name)?);
        }
    }
    // No deletion happens until every entry has been recognized and claimed.
    // A newly appearing unknown entry retains the directory. An addition after
    // this check can only make final directory deletion fail; it is never
    // traversed or removed recursively.
    if entries(&path)? != names {
        return Err(io::Error::new(io::ErrorKind::WouldBlock, "cache entries changed").into());
    }
    if config_directory.is_some() {
        require_empty_directory(&path.join(CONFIG_DIRECTORY))?;
    }
    require_directory(&directory)?;
    if let Some(file) = &config_directory {
        require_directory(file)?;
    }
    for file in files {
        delete_on_close(file)?;
    }
    if let Some(file) = config_directory {
        delete_on_close(file)?;
    }
    if let Some(file) = lease {
        delete_on_close(file)?;
    }
    require_directory(&directory)?;
    delete_on_close(directory)
}

fn cleanup_pass(root_path: &Path) -> bool {
    let root = match CacheRoot::open(root_path, false) {
        Ok(root) => root,
        Err(error) => return error.retryable(),
    };
    let children = match fs::read_dir(&root.path) {
        Ok(children) => children,
        Err(_) => return true,
    };
    let mut retry = false;
    for child in children {
        let name = match child {
            Ok(child) => child.file_name(),
            Err(_) => {
                retry = true;
                continue;
            }
        };
        let Some(name) = name.to_str().filter(|name| is_attempt_name(name)) else {
            continue;
        };
        if let Err(error) = reclaim(&root, name) {
            retry |= error.retryable();
        }
    }
    retry
}

/// Start only from normal GUI setup, never from the short-lived monitor. The
/// worker has no HostState access and cannot delay core startup or shutdown.
pub(crate) fn start_cleanup_worker() {
    if CLEANUP_STARTED.swap(true, Ordering::AcqRel) {
        return;
    }
    if thread::Builder::new()
        .name("desktop-update-cleanup".into())
        .spawn(|| loop {
            let retry = crate::windows::user_data_dir()
                .map(|directory| cleanup_pass(&directory.join("updates")))
                .unwrap_or(false);
            thread::sleep(Duration::from_secs(if retry { 2 } else { 30 }));
        })
        .is_err()
    {
        CLEANUP_STARTED.store(false, Ordering::Release);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::process::{Child, Command, Stdio};

    const INSTALLER: &str = "mihomo-desktop-0.1.6-windows-x64-setup.exe";

    struct TestRoot(PathBuf);

    impl TestRoot {
        fn new() -> Self {
            let mut nonce = [0_u8; 16];
            getrandom::getrandom(&mut nonce).unwrap();
            let directory = std::env::temp_dir()
                .join(format!("mihomo-update-cache-test-{}", hex::encode(nonce)));
            fs::create_dir(&directory).unwrap();
            Self(directory)
        }

        fn cache(&self) -> PathBuf {
            self.0.join("updates")
        }

        fn legacy(&self) -> PathBuf {
            let root = CacheRoot::open(&self.cache(), true).unwrap();
            let path = root.path.join("0123456789abcdef0123456789abcdef");
            drop(create_private_directory(root.handle(), path.file_name().unwrap()).unwrap());
            fs::write(path.join(INSTALLER), b"installer").unwrap();
            fs::write(path.join("verification.json"), b"{}").unwrap();
            path
        }
    }

    impl Drop for TestRoot {
        fn drop(&mut self) {
            // Every test owns this randomly named directory; production never
            // uses recursive removal.
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    #[test]
    fn cache_policy_recognizes_only_exact_owned_names() {
        assert!(is_attempt_name("0123456789abcdef0123456789abcdef"));
        for invalid in ["..", "0123456789abcdef", "0123456789ABCDEF0123456789ABCDEF"] {
            assert!(!is_attempt_name(invalid));
        }
        assert!(is_payload_name(INSTALLER));
        assert!(is_payload_name(&format!("{INSTALLER}.sigstore.json")));
        assert!(is_payload_name(MONITOR_NAME));
        for invalid in [
            "config.yaml",
            "../desktop-update-monitor.exe",
            "desktop-update-monitor.exe:stream",
            "mihomo-desktop-01.2.3-windows-x64-setup.exe",
            "mihomo-desktop-1.2.3-beta-windows-x64-setup.exe",
            "mihomo-desktop-1.2.3-windows-arm64-setup.exe",
        ] {
            assert!(!is_payload_name(invalid));
        }
    }

    #[test]
    fn shared_workspace_lease_survives_gui_to_monitor_handoff() {
        let fixture = TestRoot::new();
        let gui = UpdateWorkspace::create_in(&fixture.cache()).unwrap();
        let path = gui.path().to_owned();
        fs::write(path.join(INSTALLER), b"installer").unwrap();
        let monitor = UpdateWorkspace::attach_in(&fixture.cache(), &path).unwrap();
        assert!(cleanup_pass(&fixture.cache()));
        drop(gui);
        assert!(cleanup_pass(&fixture.cache()));
        assert_eq!(fs::read(path.join(INSTALLER)).unwrap(), b"installer");
        monitor.finish();
        assert!(!path.exists());
    }

    struct OwnedTestChild(Child);

    impl Drop for OwnedTestChild {
        fn drop(&mut self) {
            if !matches!(self.0.try_wait(), Ok(Some(_))) {
                let _ = self.0.kill();
                let _ = self.0.wait();
            }
        }
    }

    #[test]
    #[ignore = "spawned only by the workspace lease process test"]
    fn workspace_lease_process() {
        assert_eq!(std::env::var("MIHOMO_CACHE_LEASE_TEST").as_deref(), Ok("1"));
        let root = PathBuf::from(std::env::var_os("MIHOMO_CACHE_LEASE_ROOT").unwrap());
        let path = PathBuf::from(std::env::var_os("MIHOMO_CACHE_LEASE_PATH").unwrap());
        let ready = PathBuf::from(std::env::var_os("MIHOMO_CACHE_LEASE_READY").unwrap());
        let _workspace = UpdateWorkspace::attach_in(&root, &path).unwrap();
        fs::write(ready, b"lease acquired").unwrap();
        thread::sleep(Duration::from_secs(30));
    }

    #[test]
    fn process_handoff_and_crash_release_the_kernel_lease() {
        let fixture = TestRoot::new();
        let workspace = UpdateWorkspace::create_in(&fixture.cache()).unwrap();
        let path = workspace.path().to_owned();
        fs::write(path.join(INSTALLER), b"installer").unwrap();
        let ready = fixture.0.join("child-ready");
        let mut child = OwnedTestChild(
            Command::new(std::env::current_exe().unwrap())
                .args([
                    "--ignored",
                    "--exact",
                    "update_cache::tests::workspace_lease_process",
                ])
                .env("MIHOMO_CACHE_LEASE_TEST", "1")
                .env("MIHOMO_CACHE_LEASE_ROOT", fixture.cache())
                .env("MIHOMO_CACHE_LEASE_PATH", &path)
                .env("MIHOMO_CACHE_LEASE_READY", &ready)
                .stdin(Stdio::null())
                .stdout(Stdio::null())
                .stderr(Stdio::null())
                .spawn()
                .unwrap(),
        );
        let started = Instant::now();
        while !ready.exists() {
            assert!(child.0.try_wait().unwrap().is_none(), "lease child exited");
            assert!(
                started.elapsed() < Duration::from_secs(10),
                "lease child not ready"
            );
            thread::sleep(Duration::from_millis(20));
        }
        drop(workspace);
        assert!(cleanup_pass(&fixture.cache()));
        assert_eq!(fs::read(path.join(INSTALLER)).unwrap(), b"installer");
        child.0.kill().unwrap();
        child.0.wait().unwrap();
        assert!(!cleanup_pass(&fixture.cache()));
        assert!(!path.exists());
    }

    #[test]
    fn lease_alone_protects_an_attempt_until_its_last_owner_closes() {
        let fixture = TestRoot::new();
        let workspace = UpdateWorkspace::create_in(&fixture.cache()).unwrap();
        let path = workspace.path().to_owned();
        let lease = OpenOptions::new()
            .read(true)
            .write(true)
            .share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE)
            .open(path.join(LEASE_NAME))
            .unwrap();
        drop(workspace);
        assert!(cleanup_pass(&fixture.cache()));
        assert!(path.exists());
        drop(lease);
        assert!(!cleanup_pass(&fixture.cache()));
        assert!(!path.exists());
    }

    #[test]
    fn orphaned_workspace_still_checks_installer_occupancy() {
        let fixture = TestRoot::new();
        let workspace = UpdateWorkspace::create_in(&fixture.cache()).unwrap();
        let path = workspace.path().to_owned();
        fs::write(path.join(INSTALLER), b"installer").unwrap();
        fs::write(path.join("verification.json"), b"{}").unwrap();
        let installer = OpenOptions::new()
            .read(true)
            .share_mode(FILE_SHARE_READ)
            .open(path.join(INSTALLER))
            .unwrap();
        // Simulate a dead monitor while its separately elevated installer is
        // still using the payload. The abandoned lease alone is not enough.
        drop(workspace);
        assert!(cleanup_pass(&fixture.cache()));
        assert!(path.join(LEASE_NAME).exists());
        assert!(path.join("verification.json").exists());
        drop(installer);
        assert!(!cleanup_pass(&fixture.cache()));
        assert!(!path.exists());
    }

    #[test]
    fn root_gate_serializes_directory_creation_and_collection() {
        let fixture = TestRoot::new();
        let path = fixture.legacy();
        let root = CacheRoot::open(&fixture.cache(), false).unwrap();
        let gate = root.gate(Duration::ZERO).unwrap();
        assert!(cleanup_pass(&fixture.cache()));
        assert!(path.join(INSTALLER).exists());
        drop(gate);
        assert!(!cleanup_pass(&fixture.cache()));
        assert!(!path.exists());
    }

    #[test]
    fn busy_legacy_payload_prevents_partial_deletion() {
        let fixture = TestRoot::new();
        let path = fixture.legacy();
        let occupied = OpenOptions::new()
            .read(true)
            .share_mode(FILE_SHARE_READ)
            .open(path.join(INSTALLER))
            .unwrap();
        assert!(cleanup_pass(&fixture.cache()));
        assert!(path.join("verification.json").exists());
        drop(occupied);
        assert!(!cleanup_pass(&fixture.cache()));
        assert!(!path.exists());
    }

    #[test]
    fn unknown_entry_preserves_entire_legacy_directory() {
        let fixture = TestRoot::new();
        let path = fixture.legacy();
        fs::write(path.join("user.txt"), b"do not delete").unwrap();
        assert!(!cleanup_pass(&fixture.cache()));
        assert!(path.join(INSTALLER).exists());
        assert_eq!(fs::read(path.join("user.txt")).unwrap(), b"do not delete");
        assert!(!path.join(LEASE_NAME).exists());
    }

    #[test]
    fn hard_link_preserves_payload_and_external_file() {
        let fixture = TestRoot::new();
        let path = fixture.legacy();
        let outside = fixture.0.join("external.txt");
        fs::hard_link(path.join(INSTALLER), &outside).unwrap();
        assert!(!cleanup_pass(&fixture.cache()));
        assert!(path.join("verification.json").exists());
        assert_eq!(fs::read(&outside).unwrap(), b"installer");
    }

    #[test]
    fn verifier_directory_is_removed_only_when_empty() {
        let fixture = TestRoot::new();
        let path = fixture.legacy();
        fs::create_dir(path.join(CONFIG_DIRECTORY)).unwrap();
        fs::write(path.join(CONFIG_DIRECTORY).join("hosts.yml"), b"retain").unwrap();
        assert!(!cleanup_pass(&fixture.cache()));
        assert!(path.join(INSTALLER).exists());
        fs::remove_file(path.join(CONFIG_DIRECTORY).join("hosts.yml")).unwrap();
        assert!(!cleanup_pass(&fixture.cache()));
        assert!(!path.exists());
    }

    #[test]
    fn live_workspace_pins_its_namespace_against_rename() {
        let fixture = TestRoot::new();
        let workspace = UpdateWorkspace::create_in(&fixture.cache()).unwrap();
        let original = workspace.path().to_owned();
        let moved = fixture.0.join("moved");
        assert!(fs::rename(&original, &moved).is_err());
        assert!(fs::rename(fixture.cache(), fixture.0.join("moved-root")).is_err());
        assert!(UpdateWorkspace::attach_in(&fixture.cache(), &moved).is_err());
        workspace.finish();
        assert!(!original.exists());
    }

    // A mount-point reparse buffer creates a directory junction without
    // invoking a shell. Both its target and link belong to this fixture.
    fn junction(link: &Path, target: &Path) {
        fs::create_dir(link).unwrap();
        set_junction(link, target);
    }

    fn set_junction(link: &Path, target: &Path) {
        use windows_sys::Win32::Storage::FileSystem::FILE_SHARE_DELETE;
        use windows_sys::Win32::System::IO::DeviceIoControl;

        let target = target.canonicalize().unwrap();
        let target = target.to_str().unwrap().strip_prefix(r"\\?\").unwrap();
        let substitute: Vec<u16> = format!(r"\??\{target}").encode_utf16().collect();
        let printed: Vec<u16> = target.encode_utf16().collect();
        let mut buffer = Vec::new();
        // IO_REPARSE_TAG_MOUNT_POINT, followed by REPARSE_DATA_BUFFER's
        // MountPointReparseBuffer (all offsets and lengths are in bytes).
        buffer.extend_from_slice(&0xa000_0003_u32.to_le_bytes());
        buffer.extend_from_slice(
            &(8_u16 + ((substitute.len() + printed.len() + 2) * 2) as u16).to_le_bytes(),
        );
        buffer.extend_from_slice(&0_u16.to_le_bytes());
        buffer.extend_from_slice(&0_u16.to_le_bytes());
        buffer.extend_from_slice(&((substitute.len() * 2) as u16).to_le_bytes());
        buffer.extend_from_slice(&(((substitute.len() + 1) * 2) as u16).to_le_bytes());
        buffer.extend_from_slice(&((printed.len() * 2) as u16).to_le_bytes());
        for unit in substitute
            .into_iter()
            .chain(Some(0))
            .chain(printed)
            .chain(Some(0))
        {
            buffer.extend_from_slice(&unit.to_le_bytes());
        }
        let directory = OpenOptions::new()
            .read(true)
            .write(true)
            .share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE)
            .custom_flags(FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT)
            .open(link)
            .unwrap();
        let mut returned = 0;
        let success = unsafe {
            DeviceIoControl(
                directory.as_raw_handle(),
                0x0009_00a4, // FSCTL_SET_REPARSE_POINT
                buffer.as_ptr().cast(),
                buffer.len() as u32,
                std::ptr::null_mut(),
                0,
                &mut returned,
                std::ptr::null_mut(),
            )
        };
        assert_ne!(success, 0, "{}", io::Error::last_os_error());
    }

    #[test]
    fn opened_empty_directory_cannot_redirect_child_open_or_creation() {
        let fixture = TestRoot::new();
        let root = CacheRoot::open(&fixture.cache(), true).unwrap();
        let name = "0123456789abcdef0123456789abcdef";
        let path = root.path.join(name);
        let directory = create_private_directory(root.handle(), OsStr::new(name)).unwrap();
        require_directory(&directory).unwrap();

        let outside = fixture.0.join("outside");
        fs::create_dir(&outside).unwrap();
        fs::write(outside.join(INSTALLER), b"outside installer").unwrap();
        let original_entries = entries(&outside).unwrap();

        // Retaining a directory handle without DELETE sharing prevents rename,
        // but an empty directory can still become a junction. Simulate that
        // exact change after the collector/creator's initial plain-dir check.
        set_junction(&path, &outside);
        assert!(matches!(
            open_child(
                &directory,
                OsStr::new(LEASE_NAME),
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                FILE_CREATE,
                FILE_NON_DIRECTORY_FILE,
                std::ptr::null(),
            ),
            Err(CacheError::Unsafe)
        ));
        assert!(matches!(
            open_payload(&directory, OsStr::new(INSTALLER)),
            Err(CacheError::Unsafe)
        ));
        assert_eq!(entries(&outside).unwrap(), original_entries);
        assert_eq!(
            fs::read(outside.join(INSTALLER)).unwrap(),
            b"outside installer"
        );
        assert!(!outside.join(LEASE_NAME).exists());

        drop(directory);
        assert!(!cleanup_pass(&fixture.cache()));
        assert!(fs::symlink_metadata(&path).is_ok());
        assert_eq!(entries(&outside).unwrap(), original_entries);
        assert_eq!(
            fs::read(outside.join(INSTALLER)).unwrap(),
            b"outside installer"
        );
        fs::remove_dir(path).unwrap();
    }

    #[test]
    fn junction_entries_and_ancestors_never_redirect_collection() {
        let fixture = TestRoot::new();
        drop(CacheRoot::open(&fixture.cache(), true).unwrap());
        let outside = fixture.0.join("outside");
        fs::create_dir(&outside).unwrap();
        fs::write(outside.join(INSTALLER), b"outside installer").unwrap();
        let link = fixture.cache().join("0123456789abcdef0123456789abcdef");
        junction(&link, &outside);
        assert!(!cleanup_pass(&fixture.cache()));
        assert!(fs::symlink_metadata(&link).is_ok());
        assert_eq!(
            fs::read(outside.join(INSTALLER)).unwrap(),
            b"outside installer"
        );

        let root_alias = fixture.0.join("root-alias");
        junction(&root_alias, &fixture.cache());
        assert!(!cleanup_pass(&root_alias));
        assert_eq!(
            fs::read(outside.join(INSTALLER)).unwrap(),
            b"outside installer"
        );
        fs::remove_dir(root_alias).unwrap();
        fs::remove_dir(link).unwrap();
    }
}
