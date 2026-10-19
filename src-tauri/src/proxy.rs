use crate::{
    config,
    error::{AppError, Result},
    windows,
};
use serde::{Deserialize, Serialize};
use std::{
    fs,
    io::{Read, Write},
    os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle},
    path::PathBuf,
    process::Child,
    thread,
    time::{Duration, Instant},
};
use windows_sys::Win32::{
    Foundation::{GlobalFree, LocalFree, WAIT_ABANDONED, WAIT_OBJECT_0, WAIT_TIMEOUT},
    Networking::WinInet::*,
    Security::{
        Authorization::ConvertStringSecurityDescriptorToSecurityDescriptorW, SECURITY_ATTRIBUTES,
    },
    System::{
        Pipes::PeekNamedPipe,
        Threading::{
            CreateMutexW, OpenProcess, ReleaseMutex, WaitForSingleObject,
            PROCESS_QUERY_LIMITED_INFORMATION, PROCESS_SYNCHRONIZE,
        },
    },
};

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct ProxySettings {
    pub flags: u32,
    pub server: String,
    pub bypass: String,
    pub pac: String,
}

#[derive(Clone, Serialize, Deserialize)]
struct Snapshot {
    owner_pid: u32,
    #[serde(default)]
    owner_instance: String,
    before: ProxySettings,
    applied: ProxySettings,
}

pub struct SystemProxy {
    directory: PathBuf,
    watchdog: Option<Child>,
    instance: Option<String>,
}

/// Serializes recovery records across GUI restarts and watchdog processes, including
/// two Windows sessions belonging to the same user. An abandoned mutex is recoverable.
struct RecoveryLock(OwnedHandle);

impl RecoveryLock {
    fn acquire() -> Result<Self> {
        let sid = windows::current_sid()?;
        let name = windows::wide(format!("Global\\MihomoOptimized.ProxyRecovery.{sid}"));
        let sddl = windows::wide(format!("D:P(A;;GA;;;SY)(A;;GA;;;{sid})"));
        unsafe {
            let mut descriptor = std::ptr::null_mut();
            if ConvertStringSecurityDescriptorToSecurityDescriptorW(
                sddl.as_ptr(),
                1,
                &mut descriptor,
                std::ptr::null_mut(),
            ) == 0
            {
                return Err(windows::last_error("无法保护代理恢复锁"));
            }
            let attributes = SECURITY_ATTRIBUTES {
                nLength: std::mem::size_of::<SECURITY_ATTRIBUTES>() as u32,
                lpSecurityDescriptor: descriptor,
                bInheritHandle: 0,
            };
            let raw = CreateMutexW(&attributes, 0, name.as_ptr());
            let error = if raw.is_null() {
                Some(windows::last_error("无法创建代理恢复锁"))
            } else {
                None
            };
            LocalFree(descriptor);
            if let Some(error) = error {
                return Err(error);
            }
            let handle = OwnedHandle::from_raw_handle(raw);
            match WaitForSingleObject(handle.as_raw_handle(), 10_000) {
                WAIT_OBJECT_0 | WAIT_ABANDONED => Ok(Self(handle)),
                WAIT_TIMEOUT => Err(AppError::new(
                    "proxy_recovery_busy",
                    "代理恢复正在由另一个进程处理，请稍后重试",
                )),
                _ => Err(windows::last_error("无法取得代理恢复锁")),
            }
        }
    }
}

impl Drop for RecoveryLock {
    fn drop(&mut self) {
        unsafe {
            ReleaseMutex(self.0.as_raw_handle());
        }
    }
}

impl SystemProxy {
    pub fn new(directory: PathBuf) -> Self {
        Self {
            directory,
            watchdog: None,
            instance: None,
        }
    }

    fn snapshot_path(&self) -> PathBuf {
        self.directory.join("system-proxy-recovery.json")
    }

    fn snapshot(&self) -> Result<Option<Snapshot>> {
        let path = self.snapshot_path();
        if !path.exists() {
            return Ok(None);
        }
        let content = fs::read(&path).map_err(|e| AppError::io("无法读取代理恢复记录", e))?;
        if content.len() > 64 * 1024 {
            return Err(AppError::new(
                "proxy_recovery_invalid",
                "代理恢复记录损坏，未修改系统代理",
            ));
        }
        serde_json::from_slice(&content).map(Some).map_err(|_| {
            AppError::new("proxy_recovery_invalid", "代理恢复记录损坏，未修改系统代理")
        })
    }

    pub fn owned(&self) -> Result<bool> {
        let _lock = RecoveryLock::acquire()?;
        Ok(self
            .snapshot()?
            .is_some_and(|snapshot| query().is_ok_and(|current| current == snapshot.applied)))
    }

    pub fn enable(&mut self, port: u16) -> Result<()> {
        // The helper already holds an identity-checked process handle before any OS
        // setting changes. A crash immediately after enable therefore cannot orphan it.
        self.start_watchdog()?;
        let _lock = RecoveryLock::acquire()?;
        let current = query()?;
        let before = match self.snapshot()? {
            Some(snapshot) if snapshot.applied == current => snapshot.before,
            _ => current,
        };
        let requested = ProxySettings {
            flags: PROXY_TYPE_DIRECT | PROXY_TYPE_PROXY,
            server: format!("127.0.0.1:{port}"),
            bypass: "<local>;localhost;127.*;[::1]".into(),
            pac: String::new(),
        };
        // Commit recovery information before changing OS state; the watchdog only restores
        // when the current settings still exactly match our recorded applied settings.
        let snapshot = Snapshot {
            owner_pid: std::process::id(),
            owner_instance: self
                .instance
                .clone()
                .ok_or_else(|| AppError::new("watchdog_unavailable", "代理恢复守护程序未就绪"))?,
            before,
            applied: requested.clone(),
        };
        self.write_snapshot(&snapshot)?;
        if let Err(error) = set(&requested) {
            let _ = self.restore_locked();
            return Err(error);
        }
        Ok(())
    }

    fn write_snapshot(&self, snapshot: &Snapshot) -> Result<()> {
        let bytes = serde_json::to_vec(snapshot)
            .map_err(|_| AppError::new("proxy_recovery_failed", "无法记录代理恢复状态"))?;
        config::atomic_write(&self.snapshot_path(), &bytes)
    }

    pub fn restore(&mut self) -> Result<()> {
        let _lock = RecoveryLock::acquire()?;
        self.restore_locked()
    }

    fn restore_locked(&mut self) -> Result<()> {
        if let Some(snapshot) = self.snapshot()? {
            if query()? == snapshot.applied {
                set(&snapshot.before)?;
            }
            fs::remove_file(self.snapshot_path())
                .map_err(|e| AppError::io("无法清理代理恢复记录", e))?;
        }
        Ok(())
    }

    fn restore_for_instance(&mut self, pid: u32, instance: &str) -> Result<()> {
        let _lock = RecoveryLock::acquire()?;
        if self.snapshot()?.is_some_and(|snapshot| {
            snapshot.owner_pid == pid && snapshot.owner_instance == instance
        }) {
            self.restore_locked()?;
        }
        Ok(())
    }

    fn start_watchdog(&mut self) -> Result<()> {
        if let Some(child) = self.watchdog.as_mut() {
            if child
                .try_wait()
                .map_err(|e| AppError::io("无法检查代理恢复守护程序", e))?
                .is_none()
            {
                return Ok(());
            }
        }
        self.watchdog = None;
        let mut nonce = [0_u8; 16];
        getrandom::getrandom(&mut nonce)
            .map_err(|_| AppError::new("random_failed", "系统随机数不可用"))?;
        let instance = hex::encode(nonce);
        let executable = helper_path()?;
        let mut child = crate::core::quiet_command(&executable)
            .arg("--watch-user")
            .arg(std::process::id().to_string())
            .arg("--instance")
            .arg(&instance)
            .stdin(std::process::Stdio::null())
            .stdout(std::process::Stdio::piped())
            .stderr(std::process::Stdio::null())
            .spawn()
            .map_err(|e| AppError::io("无法启动代理恢复守护程序", e))?;
        let ready = wait_watchdog_ready(&mut child);
        if let Err(error) = ready {
            let _ = child.kill();
            let _ = child.wait();
            return Err(error);
        }
        self.watchdog = Some(child);
        self.instance = Some(instance);
        Ok(())
    }
}

fn wait_watchdog_ready(child: &mut Child) -> Result<()> {
    let mut output = child
        .stdout
        .take()
        .ok_or_else(|| AppError::new("watchdog_unavailable", "无法等待代理恢复守护程序"))?;
    let deadline = Instant::now() + Duration::from_secs(5);
    loop {
        if child
            .try_wait()
            .map_err(|e| AppError::io("无法检查代理恢复守护程序", e))?
            .is_some()
        {
            return Err(AppError::new(
                "watchdog_unavailable",
                "代理恢复守护程序未能验证主程序，系统代理没有更改",
            ));
        }
        let mut available = 0_u32;
        if unsafe {
            PeekNamedPipe(
                output.as_raw_handle(),
                std::ptr::null_mut(),
                0,
                std::ptr::null_mut(),
                &mut available,
                std::ptr::null_mut(),
            )
        } == 0
        {
            return Err(windows::last_error("代理恢复守护程序握手失败"));
        }
        if available >= 6 {
            let mut ready = [0_u8; 6];
            output
                .read_exact(&mut ready)
                .map_err(|e| AppError::io("无法读取代理恢复守护程序状态", e))?;
            if &ready != b"READY\n" {
                return Err(AppError::new(
                    "watchdog_unavailable",
                    "代理恢复守护程序返回无效状态",
                ));
            }
            return Ok(());
        }
        if Instant::now() >= deadline {
            return Err(AppError::new(
                "watchdog_timeout",
                "代理恢复守护程序启动超时，系统代理没有更改",
            ));
        }
        thread::sleep(Duration::from_millis(10));
    }
}

pub fn helper_path() -> Result<PathBuf> {
    let path = std::env::current_exe()
        .map_err(|e| AppError::io("无法定位程序", e))?
        .with_file_name("mihomo-desktop-service.exe");
    if path.is_file() {
        return Ok(path);
    }
    #[cfg(debug_assertions)]
    {
        let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("binaries/mihomo-desktop-service-x86_64-pc-windows-msvc.exe");
        if path.is_file() {
            return Ok(path);
        }
    }
    Err(AppError::new(
        "helper_missing",
        "配套恢复程序缺失，请重新安装",
    ))
}

pub fn watch_user(pid: u32, instance: &str) -> Result<()> {
    if instance.len() != 32 || !instance.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Err(AppError::new(
            "invalid_watchdog_instance",
            "恢复守护程序实例无效",
        ));
    }
    let process = unsafe {
        let process = OpenProcess(
            PROCESS_SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION,
            0,
            pid,
        );
        if process.is_null() {
            return Err(windows::last_error("无法接管主程序生命周期"));
        }
        OwnedHandle::from_raw_handle(process)
    };
    let (path, sid) = windows::process_handle_identity(&process)?;
    let parent = std::env::current_exe()
        .map_err(|e| AppError::io("无法定位程序", e))?
        .with_file_name("mihomo-optimized.exe");
    let same_executable = matches!((path.canonicalize(), parent.canonicalize()), (Ok(actual), Ok(expected)) if actual == expected);
    if sid != windows::current_sid()? || !same_executable {
        return Err(AppError::new(
            "watchdog_identity_failed",
            "恢复守护程序无法验证主程序",
        ));
    }
    std::io::stdout()
        .write_all(b"READY\n")
        .and_then(|_| std::io::stdout().flush())
        .map_err(|e| AppError::io("无法确认代理恢复守护程序状态", e))?;
    loop {
        match unsafe { WaitForSingleObject(process.as_raw_handle(), 10_000) } {
            WAIT_OBJECT_0 => break,
            WAIT_TIMEOUT => {}
            _ => return Err(windows::last_error("无法等待主程序退出")),
        }
    }
    SystemProxy::new(windows::user_data_dir()?).restore_for_instance(pid, instance)
}

pub fn query() -> Result<ProxySettings> {
    unsafe {
        let mut options: [INTERNET_PER_CONN_OPTIONW; 4] = std::mem::zeroed();
        for (option, flag) in options.iter_mut().zip([
            INTERNET_PER_CONN_FLAGS,
            INTERNET_PER_CONN_PROXY_SERVER,
            INTERNET_PER_CONN_PROXY_BYPASS,
            INTERNET_PER_CONN_AUTOCONFIG_URL,
        ]) {
            option.dwOption = flag;
        }
        let mut list: INTERNET_PER_CONN_OPTION_LISTW = std::mem::zeroed();
        list.dwSize = std::mem::size_of_val(&list) as u32;
        list.dwOptionCount = options.len() as u32;
        list.pOptions = options.as_mut_ptr();
        let mut length = list.dwSize;
        if InternetQueryOptionW(
            std::ptr::null(),
            INTERNET_OPTION_PER_CONNECTION_OPTION,
            (&mut list as *mut INTERNET_PER_CONN_OPTION_LISTW).cast(),
            &mut length,
        ) == 0
        {
            return Err(windows::last_error("无法读取系统代理"));
        }
        let result = ProxySettings {
            flags: options[0].Value.dwValue,
            server: windows::read_wide(options[1].Value.pszValue),
            bypass: windows::read_wide(options[2].Value.pszValue),
            pac: windows::read_wide(options[3].Value.pszValue),
        };
        for option in &options[1..] {
            if !option.Value.pszValue.is_null() {
                GlobalFree(option.Value.pszValue.cast());
            }
        }
        Ok(result)
    }
}

fn set(settings: &ProxySettings) -> Result<()> {
    let mut server = windows::wide(&settings.server);
    let mut bypass = windows::wide(&settings.bypass);
    let mut pac = windows::wide(&settings.pac);
    unsafe {
        let mut options: [INTERNET_PER_CONN_OPTIONW; 4] = std::mem::zeroed();
        options[0].dwOption = INTERNET_PER_CONN_FLAGS;
        options[0].Value.dwValue = settings.flags;
        options[1].dwOption = INTERNET_PER_CONN_PROXY_SERVER;
        options[1].Value.pszValue = server.as_mut_ptr();
        options[2].dwOption = INTERNET_PER_CONN_PROXY_BYPASS;
        options[2].Value.pszValue = bypass.as_mut_ptr();
        options[3].dwOption = INTERNET_PER_CONN_AUTOCONFIG_URL;
        options[3].Value.pszValue = pac.as_mut_ptr();
        let mut list: INTERNET_PER_CONN_OPTION_LISTW = std::mem::zeroed();
        list.dwSize = std::mem::size_of_val(&list) as u32;
        list.dwOptionCount = 4;
        list.pOptions = options.as_mut_ptr();
        if InternetSetOptionW(
            std::ptr::null(),
            INTERNET_OPTION_PER_CONNECTION_OPTION,
            (&list as *const INTERNET_PER_CONN_OPTION_LISTW).cast(),
            list.dwSize,
        ) == 0
        {
            return Err(windows::last_error(
                "无法修改系统代理，可能受到系统策略限制",
            ));
        }
        if InternetSetOptionW(
            std::ptr::null(),
            INTERNET_OPTION_SETTINGS_CHANGED,
            std::ptr::null(),
            0,
        ) == 0
        {
            return Err(windows::last_error("无法通知系统代理变更"));
        }
        if InternetSetOptionW(
            std::ptr::null(),
            INTERNET_OPTION_REFRESH,
            std::ptr::null(),
            0,
        ) == 0
        {
            return Err(windows::last_error("无法刷新系统代理"));
        }
    }
    Ok(())
}
