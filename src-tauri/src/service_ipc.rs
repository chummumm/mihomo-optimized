use crate::{
    config,
    core::CoreStatus,
    error::{AppError, Result},
    windows,
};
use serde::{Deserialize, Serialize};
use std::{
    cell::Cell,
    os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle},
    path::{Path, PathBuf},
    thread,
    time::{Duration, Instant},
};
use windows_sys::Win32::{
    Foundation::{
        GetLastError, LocalFree, ERROR_BROKEN_PIPE, ERROR_NO_DATA, ERROR_PIPE_BUSY,
        ERROR_PIPE_CONNECTED, ERROR_PIPE_LISTENING, GENERIC_READ, GENERIC_WRITE, HANDLE,
        INVALID_HANDLE_VALUE,
    },
    Security::{
        Authorization::ConvertStringSecurityDescriptorToSecurityDescriptorW, SECURITY_ATTRIBUTES,
    },
    Storage::FileSystem::{
        CreateFileW, ReadFile, WriteFile, FILE_FLAG_FIRST_PIPE_INSTANCE, OPEN_EXISTING,
        PIPE_ACCESS_DUPLEX,
    },
    System::Pipes::{
        ConnectNamedPipe, CreateNamedPipeW, DisconnectNamedPipe, GetNamedPipeClientProcessId,
        GetNamedPipeServerProcessId, PeekNamedPipe, SetNamedPipeHandleState, PIPE_NOWAIT,
        PIPE_READMODE_BYTE, PIPE_REJECT_REMOTE_CLIENTS, PIPE_TYPE_BYTE,
    },
};

const MAX_MESSAGE: usize = 8 * 1024 * 1024;
const RESPONSE_RESERVE: Duration = Duration::from_secs(60);

fn operation_timeout(request: &Request, current: Duration) -> Result<Duration> {
    match request {
        Request::Start { config } | Request::Reload { config } => {
            config::resource_bootstrap_timeout(config)?
                .checked_add(RESPONSE_RESERVE)
                .ok_or_else(|| AppError::new("config_invalid", "服务操作时限超出系统支持范围"))
        }
        // Status can observe a bounded automatic core recovery, so it must use
        // the active configuration's budget rather than the old fixed 90s wait.
        Request::Status => Ok(current),
        Request::Stop => Ok(Duration::from_secs(30)),
    }
}

fn deadline_after(timeout: Duration) -> Result<Instant> {
    Instant::now()
        .checked_add(timeout)
        .ok_or_else(|| AppError::new("config_invalid", "服务操作时限超出系统支持范围"))
}

fn time_left(deadline: Instant) -> Result<Duration> {
    deadline
        .checked_duration_since(Instant::now())
        .filter(|remaining| !remaining.is_zero())
        .ok_or_else(|| AppError::new("service_timeout", "等待 TUN 服务超过本次操作总时限"))
}

#[derive(Serialize, Deserialize)]
#[serde(tag = "command", rename_all = "snake_case", deny_unknown_fields)]
pub enum Request {
    Start { config: String },
    Stop,
    Reload { config: String },
    Status,
}

#[derive(Serialize, Deserialize)]
pub struct Reply {
    pub status: Option<CoreStatus>,
    pub error: Option<AppError>,
}

impl Reply {
    pub fn from_result(result: Result<CoreStatus>) -> Self {
        match result {
            Ok(status) => Self {
                status: Some(status),
                error: None,
            },
            Err(error) => Self {
                status: None,
                error: Some(error),
            },
        }
    }
}

fn pipe_name(sid: &str) -> String {
    format!(r"\\.\pipe\MihomoOptimized.{sid}")
}

pub struct Client {
    sid: String,
    expected_service: PathBuf,
    response_timeout: Cell<Duration>,
}

impl Client {
    pub fn new() -> Result<Self> {
        Ok(Self {
            sid: windows::current_sid()?,
            expected_service: windows::protected_install_dir()?.join("mihomo-desktop-service.exe"),
            response_timeout: Cell::new(config::DEFAULT_RESOURCE_TIMEOUT + RESPONSE_RESERVE),
        })
    }

    pub fn call(&self, request: &Request) -> Result<CoreStatus> {
        let timeout = operation_timeout(request, self.response_timeout.get())?;
        let deadline = deadline_after(timeout)?;
        let pipe = windows::wide(pipe_name(&self.sid));
        let start = Instant::now();
        let handle = loop {
            let raw = unsafe {
                CreateFileW(
                    pipe.as_ptr(),
                    GENERIC_READ | GENERIC_WRITE,
                    0,
                    std::ptr::null(),
                    OPEN_EXISTING,
                    0,
                    std::ptr::null_mut(),
                )
            };
            if raw != INVALID_HANDLE_VALUE {
                break unsafe { OwnedHandle::from_raw_handle(raw) };
            }
            let code = unsafe { GetLastError() };
            if start.elapsed() >= Duration::from_secs(4) || ![ERROR_PIPE_BUSY, 2].contains(&code) {
                return Err(AppError::new(
                    "service_unavailable",
                    "无法连接本用户的 TUN 服务，请重新安装或启动服务",
                ));
            }
            thread::sleep(Duration::from_millis(40));
        };
        unsafe {
            let mut pid = 0;
            if GetNamedPipeServerProcessId(handle.as_raw_handle(), &mut pid) == 0 {
                return Err(windows::last_error("无法验证 TUN 服务"));
            }
            let actual = windows::process_path(pid)?
                .canonicalize()
                .map_err(|e| AppError::io("无法验证 TUN 服务路径", e))?;
            let expected = self
                .expected_service
                .canonicalize()
                .map_err(|e| AppError::io("无法验证服务安装目录", e))?;
            if actual != expected {
                return Err(AppError::new(
                    "service_identity_failed",
                    "本地服务身份不匹配，已拒绝发送配置",
                ));
            }
            let mode = PIPE_READMODE_BYTE | PIPE_NOWAIT;
            if SetNamedPipeHandleState(
                handle.as_raw_handle(),
                &mode,
                std::ptr::null(),
                std::ptr::null(),
            ) == 0
            {
                return Err(windows::last_error("无法初始化服务连接"));
            }
        }
        let bytes = serde_json::to_vec(request)
            .map_err(|_| AppError::new("service_protocol", "无法编码服务请求"))?;
        write_message(
            handle.as_raw_handle(),
            &bytes,
            time_left(deadline)?.min(Duration::from_secs(10)),
        )?;
        // Validation, application, rollback and pipe framing share this one
        // outer deadline. A header does not restart the response timer.
        let response = read_message(handle.as_raw_handle(), time_left(deadline)?)?;
        let reply: Reply = serde_json::from_slice(&response)
            .map_err(|_| AppError::new("service_protocol", "服务返回了不兼容的响应"))?;
        if let Ok(remaining) = time_left(deadline) {
            let _ = write_bytes(
                handle.as_raw_handle(),
                &[1],
                remaining.min(Duration::from_secs(1)),
            );
        }
        if let Some(error) = reply.error {
            return Err(error);
        }
        let status = reply
            .status
            .ok_or_else(|| AppError::new("service_protocol", "服务响应缺少状态"))?;
        if matches!(request, Request::Start { .. } | Request::Reload { .. }) {
            self.response_timeout.set(timeout);
        }
        Ok(status)
    }
}

pub struct Server {
    handle: OwnedHandle,
    owner_sid: String,
    expected_gui: PathBuf,
}

impl Server {
    pub fn new(owner_sid: String, expected_gui: PathBuf) -> Result<Self> {
        let sddl = windows::wide(format!("D:P(A;;GA;;;SY)(A;;GRGW;;;{owner_sid})"));
        let name = windows::wide(pipe_name(&owner_sid));
        unsafe {
            let mut descriptor = std::ptr::null_mut();
            if ConvertStringSecurityDescriptorToSecurityDescriptorW(
                sddl.as_ptr(),
                1,
                &mut descriptor,
                std::ptr::null_mut(),
            ) == 0
            {
                return Err(windows::last_error("无法创建服务访问控制"));
            }
            let attributes = SECURITY_ATTRIBUTES {
                nLength: std::mem::size_of::<SECURITY_ATTRIBUTES>() as u32,
                lpSecurityDescriptor: descriptor,
                bInheritHandle: 0,
            };
            let raw = CreateNamedPipeW(
                name.as_ptr(),
                PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_NOWAIT | PIPE_REJECT_REMOTE_CLIENTS,
                1,
                65536,
                65536,
                5000,
                &attributes,
            );
            let error = if raw == INVALID_HANDLE_VALUE {
                Some(windows::last_error("无法创建独占本地服务管道"))
            } else {
                None
            };
            LocalFree(descriptor);
            if let Some(error) = error {
                return Err(error);
            }
            Ok(Self {
                handle: OwnedHandle::from_raw_handle(raw),
                owner_sid,
                expected_gui,
            })
        }
    }

    pub fn receive(&self) -> Result<Option<Request>> {
        let connected =
            unsafe { ConnectNamedPipe(self.handle.as_raw_handle(), std::ptr::null_mut()) };
        if connected == 0 {
            match unsafe { GetLastError() } {
                ERROR_PIPE_LISTENING => return Ok(None),
                ERROR_PIPE_CONNECTED => {}
                ERROR_NO_DATA => {
                    self.disconnect();
                    return Ok(None);
                }
                _ => return Err(windows::last_error("无法接受本地服务请求")),
            }
        }
        let validated = (|| {
            let mut pid = 0;
            if unsafe { GetNamedPipeClientProcessId(self.handle.as_raw_handle(), &mut pid) } == 0 {
                return Err(windows::last_error("无法验证服务调用者"));
            }
            let (path, sid) = windows::process_identity(pid)?;
            if sid != self.owner_sid || !same_file(&path, &self.expected_gui) {
                return Err(AppError::new(
                    "service_access_denied",
                    "仅允许已安装的主程序管理本用户服务",
                ));
            }
            let bytes = read_message(self.handle.as_raw_handle(), Duration::from_secs(6))?;
            serde_json::from_slice::<Request>(&bytes)
                .map_err(|_| AppError::new("service_protocol", "无效的服务操作"))
        })();
        match validated {
            Ok(request) => Ok(Some(request)),
            Err(error) => {
                let _ = self.respond(Reply::from_result(Err(error)));
                Ok(None)
            }
        }
    }

    pub fn respond(&self, reply: Reply) -> Result<()> {
        let result = (|| {
            let bytes = serde_json::to_vec(&reply)
                .map_err(|_| AppError::new("service_protocol", "无法编码服务响应"))?;
            write_message(self.handle.as_raw_handle(), &bytes, Duration::from_secs(3))?;
            let mut acknowledgement = [0_u8; 1];
            read_bytes(
                self.handle.as_raw_handle(),
                &mut acknowledgement,
                Duration::from_secs(2),
            )?;
            Ok(())
        })();
        self.disconnect();
        result
    }

    fn disconnect(&self) {
        unsafe {
            DisconnectNamedPipe(self.handle.as_raw_handle());
        }
    }
}

fn same_file(left: &Path, right: &Path) -> bool {
    match (left.canonicalize(), right.canonicalize()) {
        (Ok(left), Ok(right)) => left == right,
        _ => false,
    }
}

fn write_message(handle: HANDLE, content: &[u8], timeout: Duration) -> Result<()> {
    if content.len() > MAX_MESSAGE {
        return Err(AppError::new(
            "service_request_too_large",
            "服务请求超过大小限制",
        ));
    }
    let deadline = deadline_after(timeout)?;
    write_bytes(
        handle,
        &(content.len() as u32).to_le_bytes(),
        time_left(deadline)?,
    )?;
    write_bytes(handle, content, time_left(deadline)?)
}

fn read_message(handle: HANDLE, timeout: Duration) -> Result<Vec<u8>> {
    let deadline = deadline_after(timeout)?;
    let mut header = [0_u8; 4];
    read_bytes(handle, &mut header, time_left(deadline)?)?;
    let length = u32::from_le_bytes(header) as usize;
    if length > MAX_MESSAGE {
        return Err(AppError::new(
            "service_request_too_large",
            "服务请求超过大小限制",
        ));
    }
    let mut bytes = vec![0; length];
    read_bytes(handle, &mut bytes, time_left(deadline)?)?;
    Ok(bytes)
}

fn read_bytes(handle: HANDLE, buffer: &mut [u8], timeout: Duration) -> Result<()> {
    let start = Instant::now();
    let mut offset = 0;
    while offset < buffer.len() {
        if start.elapsed() >= timeout {
            return Err(AppError::new("service_timeout", "等待 TUN 服务超时"));
        }
        let mut available = 0;
        if unsafe {
            PeekNamedPipe(
                handle,
                std::ptr::null_mut(),
                0,
                std::ptr::null_mut(),
                &mut available,
                std::ptr::null_mut(),
            )
        } == 0
        {
            return Err(windows::last_error("服务连接已中断"));
        }
        if available == 0 {
            thread::sleep(Duration::from_millis(10));
            continue;
        }
        let length = available.min((buffer.len() - offset) as u32);
        let mut read = 0;
        if unsafe {
            ReadFile(
                handle,
                buffer[offset..].as_mut_ptr(),
                length,
                &mut read,
                std::ptr::null_mut(),
            )
        } == 0
        {
            return Err(windows::last_error("读取服务响应失败"));
        }
        if read == 0 {
            return Err(AppError::new("service_disconnected", "服务连接已关闭"));
        }
        offset += read as usize;
    }
    Ok(())
}

fn write_bytes(handle: HANDLE, buffer: &[u8], timeout: Duration) -> Result<()> {
    let start = Instant::now();
    let mut offset = 0;
    while offset < buffer.len() {
        if start.elapsed() >= timeout {
            return Err(AppError::new("service_timeout", "发送 TUN 服务请求超时"));
        }
        let mut written = 0;
        let amount = (buffer.len() - offset).min(32768) as u32;
        let success = unsafe {
            WriteFile(
                handle,
                buffer[offset..].as_ptr(),
                amount,
                &mut written,
                std::ptr::null_mut(),
            )
        };
        if success == 0 && unsafe { GetLastError() } != ERROR_NO_DATA {
            return Err(windows::last_error("发送服务请求失败"));
        }
        if success == 0 && unsafe { GetLastError() } == ERROR_BROKEN_PIPE {
            return Err(AppError::new("service_disconnected", "服务连接已关闭"));
        }
        offset += written as usize;
        if written == 0 {
            thread::sleep(Duration::from_millis(10));
        }
    }
    Ok(())
}

#[cfg(test)]
mod timeout_tests {
    use super::*;

    #[test]
    fn service_wait_tracks_the_configuration_including_automatic_recovery() {
        let current = Duration::from_secs(240);
        assert_eq!(
            operation_timeout(
                &Request::Start {
                    config: "mode: rule".into()
                },
                current
            )
            .unwrap(),
            current
        );
        let enlarged = operation_timeout(
            &Request::Reload {
                config: "resource-bootstrap:\n  timeout: 300\n".into(),
            },
            current,
        )
        .unwrap();
        assert_eq!(enlarged, Duration::from_secs(360));
        assert_eq!(
            operation_timeout(
                &Request::Start {
                    config: "resource-bootstrap:\n  timeout: 1\n".into()
                },
                current
            )
            .unwrap(),
            Duration::from_secs(61)
        );
        assert_eq!(
            operation_timeout(&Request::Status, enlarged).unwrap(),
            enlarged
        );
        assert_eq!(
            operation_timeout(&Request::Stop, enlarged).unwrap(),
            Duration::from_secs(30)
        );
        assert!(operation_timeout(
            &Request::Start {
                config: "resource-bootstrap:\n  timeout: 0\n".into()
            },
            current
        )
        .is_err());
    }

    #[test]
    fn pipe_header_cannot_restart_the_response_deadline() {
        let mut nonce = [0_u8; 12];
        getrandom::getrandom(&mut nonce).unwrap();
        let name = windows::wide(format!(r"\\.\pipe\MihomoBudgetTest.{}", hex::encode(nonce)));
        let raw_server = unsafe {
            CreateNamedPipeW(
                name.as_ptr(),
                PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_NOWAIT | PIPE_REJECT_REMOTE_CLIENTS,
                1,
                4096,
                4096,
                0,
                std::ptr::null(),
            )
        };
        assert_ne!(raw_server, INVALID_HANDLE_VALUE);
        let server = unsafe { OwnedHandle::from_raw_handle(raw_server) };
        let raw_client = unsafe {
            CreateFileW(
                name.as_ptr(),
                GENERIC_READ | GENERIC_WRITE,
                0,
                std::ptr::null(),
                OPEN_EXISTING,
                0,
                std::ptr::null_mut(),
            )
        };
        assert_ne!(raw_client, INVALID_HANDLE_VALUE);
        let client = unsafe { OwnedHandle::from_raw_handle(raw_client) };
        let connected = unsafe { ConnectNamedPipe(server.as_raw_handle(), std::ptr::null_mut()) };
        assert!(connected != 0 || unsafe { GetLastError() } == ERROR_PIPE_CONNECTED);
        let writer = thread::spawn(move || {
            thread::sleep(Duration::from_millis(80));
            write_bytes(
                server.as_raw_handle(),
                &1_u32.to_le_bytes(),
                Duration::from_secs(1),
            )
            .unwrap();
            thread::sleep(Duration::from_millis(100));
            write_bytes(server.as_raw_handle(), &[42], Duration::from_secs(1)).unwrap();
            thread::sleep(Duration::from_millis(50));
        });
        // Neither header nor body individually exceeds 140ms. Their combined
        // duration does, so resetting after the header would incorrectly succeed.
        let error = read_message(client.as_raw_handle(), Duration::from_millis(140)).unwrap_err();
        assert_eq!(error.code, "service_timeout");
        writer.join().unwrap();
    }
}
