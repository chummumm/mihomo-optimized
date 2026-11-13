use super::Controller;
use crate::error::{AppError, Result};
use serde::Deserialize;
use std::{
    fs,
    io::Read,
    net::{Ipv4Addr, SocketAddrV4},
    path::{Path, PathBuf},
};

pub(super) const NONCE_ENV: &str = "MIHOMO_DESKTOP_LAUNCH_NONCE";
const RECORD_NAME: &str = "desktop-controller.json";
const MAX_RECORD_BYTES: u64 = 4096;

/// The child binds port zero once and keeps that listener. The record only
/// discovers its address; bearer authentication and /desktop still prove
/// readiness before the supervisor exposes the controller.
pub(super) struct LaunchDiscovery {
    path: PathBuf,
    nonce: String,
}

pub(super) struct DiscoveredController {
    pub controller: Controller,
    pub core_instance: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ControllerRecord {
    protocol_version: u32,
    pid: u32,
    controller: String,
    core_instance: String,
    launch_nonce: String,
}

fn incompatible(message: &str) -> AppError {
    AppError::new("core_controller_incompatible", message)
}

fn ordinary_file(metadata: &fs::Metadata) -> bool {
    if !metadata.file_type().is_file() {
        return false;
    }
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        // Reject every reparse point, including non-symlink reparse types.
        metadata.file_attributes()
            & windows_sys::Win32::Storage::FileSystem::FILE_ATTRIBUTE_REPARSE_POINT
            == 0
    }
    #[cfg(not(windows))]
    {
        true
    }
}

impl LaunchDiscovery {
    /// Called only after the previous owned process has confirmed exit. A new
    /// nonce prevents a stale record (including a reused PID) from authorizing
    /// any connection or leaking the controller secret to an unrelated port.
    pub fn prepare(home: &Path) -> Result<Self> {
        let path = home.join(RECORD_NAME);
        match fs::symlink_metadata(&path) {
            Ok(metadata) => {
                if !ordinary_file(&metadata) {
                    return Err(incompatible("本地控制就绪记录必须是普通文件"));
                }
                fs::remove_file(&path)
                    .map_err(|error| AppError::io("无法清除上次本地控制就绪记录", error))?;
            }
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
            Err(error) => return Err(AppError::io("无法检查本地控制就绪记录", error)),
        }
        let mut random = [0u8; 32];
        getrandom::getrandom(&mut random)
            .map_err(|_| AppError::new("random_failed", "系统随机数不可用"))?;
        Ok(Self {
            path,
            nonce: hex::encode(random),
        })
    }

    pub fn nonce(&self) -> &str {
        &self.nonce
    }

    pub fn read(&self, expected_pid: u32, secret: &str) -> Result<Option<DiscoveredController>> {
        let mut options = fs::OpenOptions::new();
        options.read(true);
        #[cfg(windows)]
        {
            use std::os::windows::fs::OpenOptionsExt;
            options.custom_flags(
                windows_sys::Win32::Storage::FileSystem::FILE_FLAG_OPEN_REPARSE_POINT,
            );
        }
        #[cfg(unix)]
        {
            use std::os::unix::fs::OpenOptionsExt;
            // O_NONBLOCK also makes a substituted FIFO fail the regular-file
            // check without blocking lifecycle cancellation.
            options.custom_flags(libc::O_NOFOLLOW | libc::O_NONBLOCK);
        }
        let file = match options.open(&self.path) {
            Ok(file) => file,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(None),
            Err(error) => return Err(AppError::io("无法打开本地控制就绪记录", error)),
        };
        let metadata = file
            .metadata()
            .map_err(|error| AppError::io("无法检查本地控制就绪记录", error))?;
        if !ordinary_file(&metadata) || metadata.len() > MAX_RECORD_BYTES {
            return Err(incompatible("本地控制就绪记录类型或长度无效"));
        }
        let mut bytes = Vec::new();
        file.take(MAX_RECORD_BYTES + 1)
            .read_to_end(&mut bytes)
            .map_err(|error| AppError::io("无法读取本地控制就绪记录", error))?;
        if bytes.len() as u64 > MAX_RECORD_BYTES {
            return Err(incompatible("本地控制就绪记录超过长度限制"));
        }
        let record: ControllerRecord = serde_json::from_slice(&bytes)
            .map_err(|_| incompatible("本地控制就绪记录格式不兼容"))?;
        if record.protocol_version != 1 {
            return Err(incompatible("配套内核不支持当前控制端口发现协议"));
        }
        if expected_pid == 0 || record.pid != expected_pid || record.launch_nonce != self.nonce {
            return Err(AppError::new(
                "core_controller_identity",
                "本地控制就绪记录不属于本次自有内核启动",
            ));
        }
        let address: SocketAddrV4 = record
            .controller
            .parse()
            .map_err(|_| incompatible("本地控制就绪记录地址无效"))?;
        if *address.ip() != Ipv4Addr::LOCALHOST
            || address.port() == 0
            || address.to_string() != record.controller
        {
            return Err(incompatible("本地控制就绪记录必须使用 IPv4 回环实际端口"));
        }
        if record.core_instance.is_empty()
            || record.core_instance.len() > 128
            || !record
                .core_instance
                .bytes()
                .all(|byte| byte.is_ascii_alphanumeric() || b"-_:.{}".contains(&byte))
        {
            return Err(incompatible("本地控制就绪记录缺少有效内核实例"));
        }
        Ok(Some(DiscoveredController {
            controller: Controller {
                url: format!("http://{address}"),
                secret: secret.to_owned(),
            },
            core_instance: record.core_instance,
        }))
    }
}

/// Only this exact legacy policy error means :0 discovery is unsupported.
/// Other startup failures keep their resource, native TUN or process cause.
pub(super) fn legacy_incompatible_diagnostic(line: &str) -> Option<&'static str> {
    const LEGACY: &str = "desktop-managed controller port must be 1..65535";
    let decoded;
    let message = if line.starts_with("time=") || line.starts_with("level=") {
        if !line.contains("level=fatal msg=") && !line.contains("level=error msg=") {
            return None;
        }
        decoded = serde_json::Deserializer::from_str(line.split_once("msg=")?.1)
            .into_iter::<String>()
            .next()?
            .ok()?;
        decoded.as_str()
    } else {
        line
    };
    let message = message
        .strip_prefix("parse config error: ")
        .unwrap_or(message);
    (message == LEGACY).then_some(LEGACY)
}

#[cfg(test)]
mod tests {
    use super::*;

    struct Fixture {
        home: PathBuf,
        discovery: LaunchDiscovery,
    }

    impl Fixture {
        fn new() -> Self {
            let mut random = [0u8; 12];
            getrandom::getrandom(&mut random).unwrap();
            let home =
                std::env::temp_dir().join(format!("mihomo-controller-{}", hex::encode(random)));
            fs::create_dir(&home).unwrap();
            let discovery = LaunchDiscovery::prepare(&home).unwrap();
            Self { home, discovery }
        }

        fn record(&self) -> serde_json::Value {
            serde_json::json!({
                "protocolVersion": 1,
                "pid": 1234,
                "controller": "127.0.0.1:23456",
                "coreInstance": "record-instance",
                "launchNonce": self.discovery.nonce(),
            })
        }

        fn write(&self, record: &serde_json::Value) {
            fs::write(&self.discovery.path, serde_json::to_vec(record).unwrap()).unwrap();
        }
    }

    impl Drop for Fixture {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.home);
        }
    }

    #[test]
    fn ready_record_requires_exact_launch_process_and_loopback_address() {
        let fixture = Fixture::new();
        assert!(fixture
            .discovery
            .read(1234, "test-secret")
            .unwrap()
            .is_none());
        let valid = fixture.record();
        fixture.write(&valid);
        let discovered = fixture
            .discovery
            .read(1234, "test-secret")
            .unwrap()
            .unwrap();
        assert_eq!(discovered.controller.url, "http://127.0.0.1:23456");
        assert_eq!(discovered.controller.secret, "test-secret");
        assert_eq!(discovered.core_instance, "record-instance");
        for (field, value, code) in [
            ("pid", serde_json::json!(1235), "core_controller_identity"),
            (
                "launchNonce",
                serde_json::json!("old-launch-nonce"),
                "core_controller_identity",
            ),
            (
                "protocolVersion",
                serde_json::json!(2),
                "core_controller_incompatible",
            ),
            (
                "coreInstance",
                serde_json::json!(""),
                "core_controller_incompatible",
            ),
            (
                "coreInstance",
                serde_json::json!("secret\nnot-an-identity"),
                "core_controller_incompatible",
            ),
        ] {
            let mut record = valid.clone();
            record[field] = value;
            fixture.write(&record);
            let error = fixture.discovery.read(1234, "test-secret").err().unwrap();
            assert_eq!(error.code, code);
            assert!(!error.message.contains("test-secret"));
            assert!(!error.message.contains("old-launch-nonce"));
        }
        for address in [
            "127.0.0.1:0",
            "localhost:23456",
            "127.0.0.2:23456",
            "0.0.0.0:23456",
            "[::1]:23456",
            "https://127.0.0.1:23456",
            "127.0.0.1:23456/path",
        ] {
            let mut record = valid.clone();
            record["controller"] = serde_json::json!(address);
            fixture.write(&record);
            assert_eq!(
                fixture
                    .discovery
                    .read(1234, "test-secret")
                    .err()
                    .unwrap()
                    .code,
                "core_controller_incompatible"
            );
        }
    }

    #[test]
    fn new_launch_removes_stale_record_and_rotates_nonce() {
        let fixture = Fixture::new();
        fixture.write(&fixture.record());
        let next = LaunchDiscovery::prepare(&fixture.home).unwrap();
        assert_eq!(next.nonce().len(), 64);
        assert!(next.nonce().bytes().all(|byte| byte.is_ascii_hexdigit()));
        assert_ne!(next.nonce(), fixture.discovery.nonce());
        assert!(next.read(1234, "test-secret").unwrap().is_none());
        fixture.write(&fixture.record());
        assert_eq!(
            next.read(1234, "test-secret").err().unwrap().code,
            "core_controller_identity"
        );
    }

    #[test]
    fn invalid_or_oversized_records_never_disclose_their_contents() {
        let fixture = Fixture::new();
        for bytes in [
            b"not-json-private-value".to_vec(),
            vec![b'x'; MAX_RECORD_BYTES as usize + 1],
        ] {
            fs::write(&fixture.discovery.path, bytes).unwrap();
            let error = fixture.discovery.read(1234, "test-secret").err().unwrap();
            assert_eq!(error.code, "core_controller_incompatible");
            assert!(!error.message.contains("private-value"));
        }
        fs::remove_file(&fixture.discovery.path).unwrap();
        fs::create_dir(&fixture.discovery.path).unwrap();
        assert!(fixture.discovery.read(1234, "test-secret").is_err());
        assert!(LaunchDiscovery::prepare(&fixture.home).is_err());
    }

    #[cfg(unix)]
    #[test]
    fn reader_and_new_launch_never_follow_record_symlinks() {
        let fixture = Fixture::new();
        let target = fixture.home.join("different-file.json");
        fs::write(&target, serde_json::to_vec(&fixture.record()).unwrap()).unwrap();
        std::os::unix::fs::symlink(&target, &fixture.discovery.path).unwrap();
        assert!(fixture.discovery.read(1234, "test-secret").is_err());
        assert!(LaunchDiscovery::prepare(&fixture.home).is_err());
        assert!(target.is_file());
    }

    #[test]
    fn legacy_discovery_rejection_is_exact_not_a_general_startup_category() {
        const LEGACY: &str = "desktop-managed controller port must be 1..65535";
        for line in [
            LEGACY.to_owned(),
            format!("parse config error: {LEGACY}"),
            format!("time=now level=fatal msg={}", serde_json::json!(LEGACY)),
            format!(
                "level=error msg={}",
                serde_json::json!(format!("parse config error: {LEGACY}"))
            ),
        ] {
            assert_eq!(legacy_incompatible_diagnostic(&line), Some(LEGACY));
        }
        for line in [
            "desktop-managed controller port must be 1..65535 private-token",
            "resource unavailable",
            "configure tun interface: access denied",
            "level=info msg=\"desktop-managed controller port must be 1..65535\"",
        ] {
            assert!(legacy_incompatible_diagnostic(line).is_none());
        }
    }
}
