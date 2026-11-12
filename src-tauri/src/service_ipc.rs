use crate::{
    config,
    core::CoreStatus,
    core_runtime::RuntimeOperation,
    error::{AppError, Result},
    lifecycle::Cancellation,
    windows,
};
use serde::{Deserialize, Serialize};
use std::{
    os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle},
    path::{Path, PathBuf},
    sync::{
        atomic::{AtomicU64, Ordering},
        Arc,
    },
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
    System::{
        Pipes::{
            ConnectNamedPipe, CreateNamedPipeW, DisconnectNamedPipe, GetNamedPipeClientProcessId,
            GetNamedPipeServerProcessId, PeekNamedPipe, SetNamedPipeHandleState, PIPE_NOWAIT,
            PIPE_READMODE_BYTE, PIPE_REJECT_REMOTE_CLIENTS, PIPE_TYPE_BYTE,
        },
        Threading::{OpenProcess, WaitForSingleObject},
    },
};

const MAX_MESSAGE: usize = 8 * 1024 * 1024;
const RESPONSE_RESERVE: Duration = Duration::from_secs(60);
const CONTROL_TIMEOUT: Duration = Duration::from_secs(3);
pub const PROTOCOL_VERSION: u32 = 4;

fn operation_timeout(request: &Request) -> Result<Duration> {
    match request {
        Request::Start { config, .. } | Request::Reload { config, .. } => {
            config::resource_bootstrap_timeout(config)?
                .checked_add(RESPONSE_RESERVE)
                .ok_or_else(|| AppError::new("config_invalid", "服务操作时限超出系统支持范围"))
        }
        Request::Stop | Request::CompleteCoreUpdate { accept: true } => Ok(Duration::from_secs(18)),
        Request::PrepareCoreUpdate { .. } => Ok(Duration::from_secs(900)),
        Request::CommitCoreUpdate {} | Request::CompleteCoreUpdate { accept: false } => {
            Ok(Duration::from_secs(480))
        }
        _ => Ok(CONTROL_TIMEOUT),
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
    Start { config: String, tun: bool },
    Stop,
    Reload { config: String, tun: bool },
    Status,
    Reconcile { generation: u64 },
    Heartbeat,
    Operation { id: String },
    PrepareCoreUpdate { proxy_port: Option<u16> },
    CommitCoreUpdate {},
    CompleteCoreUpdate { accept: bool },
}

impl Request {
    pub(crate) fn diagnostic_name(&self) -> &'static str {
        match self {
            Self::Start { .. } => "start",
            Self::Stop => "stop",
            Self::Reload { .. } => "reload",
            Self::Status => "status",
            Self::Reconcile { .. } => "reconcile",
            Self::Heartbeat => "heartbeat",
            Self::Operation { .. } => "operation",
            Self::PrepareCoreUpdate { .. } => "prepare_core_update",
            Self::CommitCoreUpdate { .. } => "commit_core_update",
            Self::CompleteCoreUpdate { .. } => "complete_core_update",
        }
    }

    pub(crate) fn lifecycle_request(&self) -> bool {
        matches!(
            self,
            Self::Start { .. }
                | Self::Stop
                | Self::Reload { .. }
                | Self::Reconcile { .. }
                | Self::PrepareCoreUpdate { .. }
                | Self::CommitCoreUpdate { .. }
                | Self::CompleteCoreUpdate { .. }
        )
    }

    fn bulk(&self) -> bool {
        matches!(self, Self::Start { .. } | Self::Reload { .. })
    }
}

#[derive(Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Envelope {
    pub protocol_version: u32,
    pub owner_session: String,
    pub request_id: String,
    pub intent_revision: u64,
    pub request_sequence: u64,
    pub request: Request,
}

impl Envelope {
    fn validate(&self, bulk: bool) -> Result<()> {
        if self.protocol_version != PROTOCOL_VERSION
            || self.request.bulk() != bulk
            || self.owner_session.len() != 32
            || !self.owner_session.bytes().all(|c| c.is_ascii_hexdigit())
            || self.request_id.is_empty()
            || self.request_id.len() > 80
            || !self
                .request_id
                .bytes()
                .all(|c| c.is_ascii_alphanumeric() || matches!(c, b'-' | b'_'))
        {
            return Err(AppError::new(
                "service_protocol",
                "服务请求版本、会话或编号无效",
            ));
        }
        Ok(())
    }

    /// Only protocol identities and a fixed command category cross into logs.
    /// Configurations and authenticated payloads are deliberately not serialized.
    pub(crate) fn diagnostic_fields(&self) -> serde_json::Value {
        let mut fields = serde_json::json!({
            "ownerSession":self.owner_session,"serviceRequestId":self.request_id,
            "requestSequence":self.request_sequence,"intentRevision":self.intent_revision,
            "serviceCommand":self.request.diagnostic_name(),
        });
        if matches!(
            self.request,
            Request::Start { .. }
                | Request::Stop
                | Request::Reload { .. }
                | Request::PrepareCoreUpdate { .. }
                | Request::CommitCoreUpdate {}
                | Request::CompleteCoreUpdate { .. }
        ) {
            fields["operationId"] =
                serde_json::json!(format!("{}:{}", self.owner_session, self.request_id));
        }
        if let Request::Reconcile { generation } = self.request {
            fields["powerGeneration"] = serde_json::json!(generation);
        }
        fields
    }
}

pub struct AuthenticatedOwner {
    pub pid: u32,
    handle: OwnedHandle,
}

impl AuthenticatedOwner {
    #[cfg(test)]
    pub(crate) fn current_for_test() -> Self {
        let pid = std::process::id();
        let raw = unsafe { OpenProcess(windows::PROCESS_IDENTITY_ACCESS, 0, pid) };
        assert!(!raw.is_null());
        Self {
            pid,
            handle: unsafe { OwnedHandle::from_raw_handle(raw) },
        }
    }
    pub fn alive(&self) -> bool {
        unsafe {
            WaitForSingleObject(self.handle.as_raw_handle(), 0)
                == windows_sys::Win32::Foundation::WAIT_TIMEOUT
        }
    }

    pub fn verify(&self, sid: &str, executable: &Path) -> Result<()> {
        if !self.alive() {
            return Err(AppError::new(
                "service_owner_lost",
                "已认证的桌面后台已经退出",
            ));
        }
        let (actual_path, actual_sid) = windows::process_handle_identity(&self.handle)?;
        if actual_sid != sid || !same_file(&actual_path, executable) {
            return Err(AppError::new(
                "service_owner_changed",
                "服务所属桌面后台身份不匹配",
            ));
        }
        Ok(())
    }
}

pub struct AuthenticatedRequest {
    pub envelope: Envelope,
    pub owner: AuthenticatedOwner,
}

#[derive(Serialize, Deserialize)]
pub struct Reply {
    pub protocol_version: u32,
    pub status: Option<CoreStatus>,
    pub operation: Option<RuntimeOperation>,
    pub error: Option<AppError>,
}

impl Reply {
    pub fn from_result(result: Result<CoreStatus>) -> Self {
        match result {
            Ok(status) => Self {
                protocol_version: PROTOCOL_VERSION,
                status: Some(status),
                operation: None,
                error: None,
            },
            Err(error) => Self {
                protocol_version: PROTOCOL_VERSION,
                status: None,
                operation: None,
                error: Some(error),
            },
        }
    }
    pub fn operation(result: Result<RuntimeOperation>) -> Self {
        match result {
            Ok(operation) => Self {
                protocol_version: PROTOCOL_VERSION,
                status: Some(operation.status.clone()),
                operation: Some(operation),
                error: None,
            },
            Err(error) => Self::from_result(Err(error)),
        }
    }
}

fn pipe_name(sid: &str, bulk: bool) -> String {
    format!(
        r"\\.\pipe\MihomoOptimized.{sid}.v2.{}",
        if bulk { "work" } else { "control" }
    )
}

fn request_id() -> Result<String> {
    let mut random = [0u8; 16];
    getrandom::getrandom(&mut random)
        .map_err(|_| AppError::new("random_failed", "无法建立服务请求编号"))?;
    Ok(hex::encode(random))
}

#[derive(Clone)]
pub struct Client {
    sid: String,
    expected_service: PathBuf,
    owner_session: String,
    sequence: Arc<AtomicU64>,
    diagnostic_directory: PathBuf,
}

impl Client {
    pub fn new(owner_session: String, diagnostic_directory: PathBuf) -> Result<Self> {
        Ok(Self {
            sid: windows::current_sid()?,
            expected_service: windows::protected_install_dir()?.join("mihomo-desktop-service.exe"),
            owner_session,
            sequence: Arc::new(AtomicU64::new(0)),
            diagnostic_directory,
        })
    }

    pub fn call(&self, request: Request, cancellation: &Cancellation) -> Result<CoreStatus> {
        let timeout = operation_timeout(&request)?;
        self.call_timeout(request, cancellation, timeout)
    }

    /// The source influences only our wait budget and never crosses the IPC
    /// update boundary. The privileged owner uses its own applied source.
    pub fn core_update_call(
        &self,
        request: Request,
        cancellation: &Cancellation,
        source: Option<&str>,
    ) -> Result<CoreStatus> {
        let timeout = match &request {
            Request::CommitCoreUpdate {} | Request::CompleteCoreUpdate { accept: false } => {
                config::resource_bootstrap_timeout(source.unwrap_or(config::INITIAL_CONFIG))?
                    .checked_mul(2)
                    .and_then(|value| value.checked_add(Duration::from_secs(120)))
                    .ok_or_else(|| {
                        AppError::new("config_invalid", "内核更新总时限超出系统支持范围")
                    })?
            }
            Request::PrepareCoreUpdate { .. } | Request::CompleteCoreUpdate { accept: true } => {
                operation_timeout(&request)?
            }
            _ => return Err(AppError::new("invalid_arguments", "不是内核更新操作")),
        };
        self.call_timeout(request, cancellation, timeout)
    }

    fn call_timeout(
        &self,
        request: Request,
        cancellation: &Cancellation,
        timeout: Duration,
    ) -> Result<CoreStatus> {
        cancellation.check()?;
        let deadline = deadline_after(timeout)?;
        let envelope = self.envelope(request, cancellation.revision())?;
        let _scope = crate::diagnostics::scope(envelope.diagnostic_fields());
        let started = Instant::now();
        crate::diagnostics::record(
            &self.diagnostic_directory,
            "service_call_started",
            envelope.diagnostic_fields(),
        );
        let mut submitted = false;
        let result = self.call_envelope(&envelope, cancellation, deadline, &mut submitted);
        let result = if submitted
            && matches!(
                envelope.request,
                Request::Start { .. } | Request::Reload { .. }
            )
            && result
                .as_ref()
                .err()
                .is_some_and(|error| error.code == "operation_cancelled")
        {
            match self.cancel_submitted_lifecycle(envelope.intent_revision) {
                Ok(()) => result,
                Err(error) if error.code == "operation_cancelled" => result,
                Err(error) => Err(AppError::new(
                    "core_termination_unconfirmed",
                    format!("操作已取消，但服务内核停止尚未确认：{}", error.message),
                )),
            }
        } else {
            result
        };
        crate::diagnostics::record(
            &self.diagnostic_directory,
            "service_call_finished",
            serde_json::json!({
                "code":result.as_ref().err().map(|error| &error.code),
                "corePid":result.as_ref().ok().and_then(|status| status.pid),
                "elapsedMs":started.elapsed().as_millis(),
            }),
        );
        result
    }

    fn call_envelope(
        &self,
        envelope: &Envelope,
        cancellation: &Cancellation,
        deadline: Instant,
        submitted: &mut bool,
    ) -> Result<CoreStatus> {
        // A lost accepted response is retried with the same request ID. The
        // service records it before dispatch, so retries cannot spawn twice.
        let mut attempts = 0;
        let reply = loop {
            cancellation.check()?;
            time_left(deadline)?;
            // Once transmission is attempted, a lost response cannot prove
            // that the service did not accept this lifecycle operation.
            *submitted = true;
            match self.exchange(envelope, deadline) {
                Ok(reply)
                    if reply
                        .error
                        .as_ref()
                        .is_some_and(|error| error.code == "service_owner_cleanup") =>
                {
                    thread::sleep(Duration::from_millis(100));
                    continue;
                }
                Ok(reply) => break reply,
                Err(error)
                    if attempts < 2
                        && matches!(
                            error.code.as_str(),
                            "service_timeout" | "service_unavailable" | "io_error"
                        ) =>
                {
                    attempts += 1;
                    crate::diagnostics::record(
                        &self.diagnostic_directory,
                        "service_call_retry",
                        serde_json::json!({"code":error.code,"attempt":attempts}),
                    );
                    thread::sleep(Duration::from_millis(100));
                }
                Err(error) => return Err(error),
            }
        };
        if let Some(error) = reply.error {
            return Err(error);
        }
        let Some(mut operation) = reply.operation else {
            return reply
                .status
                .ok_or_else(|| AppError::new("service_protocol", "服务响应缺少状态"));
        };
        loop {
            cancellation.check()?;
            if !matches!(operation.phase.as_str(), "queued" | "running") {
                return operation.error.map_or(Ok(operation.status), Err);
            }
            time_left(deadline)?;
            thread::sleep(Duration::from_millis(100));
            cancellation.check()?;
            let probe = self.envelope(
                Request::Operation {
                    id: operation.id.clone(),
                },
                cancellation.revision(),
            )?;
            let reply = match self.exchange(&probe, deadline) {
                Ok(reply) => reply,
                Err(error)
                    if matches!(
                        error.code.as_str(),
                        "service_timeout" | "service_unavailable" | "io_error"
                    ) =>
                {
                    continue
                }
                Err(error) => return Err(error),
            };
            if let Some(error) = reply.error {
                return Err(error);
            }
            operation = reply
                .operation
                .ok_or_else(|| AppError::new("service_protocol", "服务响应缺少操作进度"))?;
        }
    }

    fn cancel_submitted_lifecycle(&self, intent_revision: u64) -> Result<()> {
        let deadline = deadline_after(Duration::from_secs(18))?;
        // Never borrow the newer UI generation. The service rejects this Stop
        // if a newer intent already owns the runtime.
        self.accept_stop(intent_revision)?;
        loop {
            time_left(deadline)?;
            let operation = self.cancelled_stop_operation(intent_revision, deadline)?;
            if cancelled_stop_completed(&operation)? {
                return Ok(());
            }
            thread::sleep(Duration::from_millis(50));
        }
    }

    pub(crate) fn confirmed_cancelled_stop(&self, intent_revision: u64) -> Result<CoreStatus> {
        let operation =
            self.cancelled_stop_operation(intent_revision, deadline_after(CONTROL_TIMEOUT)?)?;
        if cancelled_stop_completed(&operation)? {
            Ok(operation.status)
        } else {
            Err(AppError::new(
                "core_termination_unconfirmed",
                "已取消操作的停止尚未完成",
            ))
        }
    }

    fn cancelled_stop_operation(
        &self,
        intent_revision: u64,
        deadline: Instant,
    ) -> Result<RuntimeOperation> {
        let id = format!("{}:stop-{intent_revision}", self.owner_session);
        let probe = self.envelope(Request::Operation { id }, intent_revision)?;
        let reply = self.exchange(&probe, deadline)?;
        if let Some(error) = reply.error {
            return Err(error);
        }
        reply
            .operation
            .ok_or_else(|| AppError::new("service_protocol", "服务响应缺少停止操作进度"))
    }

    pub fn status(&self) -> Result<CoreStatus> {
        self.observe(Request::Status)
    }
    pub fn heartbeat(&self) -> Result<CoreStatus> {
        self.observe(Request::Heartbeat)
    }
    pub fn reconcile(&self, generation: u64) -> Result<CoreStatus> {
        self.observe(Request::Reconcile { generation })
    }

    fn observe(&self, request: Request) -> Result<CoreStatus> {
        let diagnostic = request.lifecycle_request();
        let envelope = self.envelope(request, 0)?;
        let _scope = crate::diagnostics::scope(envelope.diagnostic_fields());
        if diagnostic {
            crate::diagnostics::record(
                &self.diagnostic_directory,
                "service_observation_requested",
                envelope.diagnostic_fields(),
            );
        }
        let result = self.exchange_status(&envelope);
        if diagnostic {
            crate::diagnostics::record(
                &self.diagnostic_directory,
                "service_observation_finished",
                serde_json::json!({"code":result.as_ref().err().map(|error| &error.code)}),
            );
        }
        result
    }

    /// The reserved host control path only waits for admission, never cleanup.
    /// Its operation ID is deterministic for this intent, including retries.
    pub fn accept_stop(&self, intent_revision: u64) -> Result<CoreStatus> {
        let envelope = Envelope {
            protocol_version: PROTOCOL_VERSION,
            owner_session: self.owner_session.clone(),
            request_id: format!("stop-{intent_revision}"),
            intent_revision,
            request_sequence: self.sequence.fetch_add(1, Ordering::AcqRel) + 1,
            request: Request::Stop,
        };
        let _scope = crate::diagnostics::scope(envelope.diagnostic_fields());
        crate::diagnostics::record(
            &self.diagnostic_directory,
            "service_priority_stop_requested",
            envelope.diagnostic_fields(),
        );
        let result = self.exchange_status(&envelope);
        crate::diagnostics::record(
            &self.diagnostic_directory,
            "service_priority_stop_finished",
            serde_json::json!({"code":result.as_ref().err().map(|error| &error.code)}),
        );
        result
    }

    fn exchange_status(&self, envelope: &Envelope) -> Result<CoreStatus> {
        let reply = self.exchange(envelope, deadline_after(CONTROL_TIMEOUT)?)?;
        if let Some(error) = reply.error {
            return Err(error);
        }
        reply
            .status
            .ok_or_else(|| AppError::new("service_protocol", "服务响应缺少状态"))
    }

    fn envelope(&self, request: Request, intent_revision: u64) -> Result<Envelope> {
        Ok(Envelope {
            protocol_version: PROTOCOL_VERSION,
            owner_session: self.owner_session.clone(),
            request_id: request_id()?,
            intent_revision,
            request_sequence: self.sequence.fetch_add(1, Ordering::AcqRel) + 1,
            request,
        })
    }

    fn exchange(&self, envelope: &Envelope, operation_deadline: Instant) -> Result<Reply> {
        let deadline = deadline_after(CONTROL_TIMEOUT)?.min(operation_deadline);
        let pipe = windows::wide(pipe_name(&self.sid, envelope.request.bulk()));
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
            if time_left(deadline).is_err() || ![ERROR_PIPE_BUSY, 2].contains(&code) {
                return Err(AppError::new(
                    "service_unavailable",
                    "无法连接配套 TUN 服务，请安装完整新版桌面程序或修复服务",
                ));
            }
            thread::sleep(Duration::from_millis(20));
        };
        let _service_process = unsafe {
            let mut pid = 0;
            if GetNamedPipeServerProcessId(handle.as_raw_handle(), &mut pid) == 0 {
                return Err(windows::last_error("无法验证 TUN 服务"));
            }
            let raw = OpenProcess(windows::PROCESS_IDENTITY_ACCESS, 0, pid);
            if raw.is_null() {
                let mut error = windows::last_error("无法保留 TUN 服务身份");
                error.code = "service_identity_failed".into();
                return Err(error);
            }
            let service_process = OwnedHandle::from_raw_handle(raw);
            let (actual, sid) =
                windows::process_handle_identity(&service_process).map_err(|mut error| {
                    error.code = "service_identity_failed".into();
                    error
                })?;
            let actual = actual
                .canonicalize()
                .map_err(|e| AppError::io("无法验证 TUN 服务路径", e))?;
            let expected = self
                .expected_service
                .canonicalize()
                .map_err(|e| AppError::io("无法验证服务安装目录", e))?;
            if actual != expected
                || sid != "S-1-5-18"
                || WaitForSingleObject(service_process.as_raw_handle(), 0)
                    != windows_sys::Win32::Foundation::WAIT_TIMEOUT
            {
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
            service_process
        };
        let bytes = serde_json::to_vec(envelope)
            .map_err(|_| AppError::new("service_protocol", "无法编码服务请求"))?;
        write_message(handle.as_raw_handle(), &bytes, time_left(deadline)?)?;
        let response = read_message(handle.as_raw_handle(), time_left(deadline)?)?;
        let reply: Reply = serde_json::from_slice(&response).map_err(|_| {
            AppError::new(
                "service_version_required",
                "服务协议不兼容，请安装完整新版桌面程序",
            )
        })?;
        if reply.protocol_version != PROTOCOL_VERSION {
            return Err(AppError::new(
                "service_version_required",
                "桌面与 TUN 服务版本不匹配，请修复安装",
            ));
        }
        if let Ok(remaining) = time_left(deadline) {
            let _ = write_bytes(
                handle.as_raw_handle(),
                &[1],
                remaining.min(Duration::from_millis(200)),
            );
        }
        Ok(reply)
    }
}

fn cancelled_stop_completed(operation: &RuntimeOperation) -> Result<bool> {
    match operation.phase.as_str() {
        "queued" | "running" => Ok(false),
        "cancelled" => Err(AppError::new(
            "operation_cancelled",
            "更新的用户指令已接管内核",
        )),
        "succeeded"
            if operation.status.pid.is_none() && operation.status.process_state == "exited" =>
        {
            Ok(true)
        }
        _ => Err(AppError::new(
            "core_termination_unconfirmed",
            "服务尚未确认自有内核已退出",
        )),
    }
}

pub struct Server {
    handle: OwnedHandle,
    owner_sid: String,
    expected_gui: PathBuf,
    bulk: bool,
    diagnostic_directory: PathBuf,
}

impl Server {
    pub fn new(
        owner_sid: String,
        expected_gui: PathBuf,
        bulk: bool,
        first: bool,
        diagnostic_directory: PathBuf,
    ) -> Result<Self> {
        let sddl = windows::wide(format!("D:P(A;;GA;;;SY)(A;;GRGW;;;{owner_sid})"));
        let name = windows::wide(pipe_name(&owner_sid, bulk));
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
                PIPE_ACCESS_DUPLEX
                    | if first {
                        FILE_FLAG_FIRST_PIPE_INSTANCE
                    } else {
                        0
                    },
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_NOWAIT | PIPE_REJECT_REMOTE_CLIENTS,
                if bulk { 1 } else { 3 },
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
                bulk,
                diagnostic_directory,
            })
        }
    }

    pub fn receive(&self) -> Result<Option<AuthenticatedRequest>> {
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
            let raw = unsafe { OpenProcess(windows::PROCESS_IDENTITY_ACCESS, 0, pid) };
            if raw.is_null() {
                return Err(windows::last_error("无法保留服务所有者身份"));
            }
            let owner = AuthenticatedOwner {
                pid,
                handle: unsafe { OwnedHandle::from_raw_handle(raw) },
            };
            owner.verify(&self.owner_sid, &self.expected_gui)?;
            let bytes = read_message(
                self.handle.as_raw_handle(),
                if self.bulk {
                    Duration::from_secs(3)
                } else {
                    Duration::from_secs(1)
                },
            )?;
            let envelope = serde_json::from_slice::<Envelope>(&bytes).map_err(|_| {
                AppError::new("service_version_required", "服务要求配套新版桌面控制协议")
            })?;
            envelope.validate(self.bulk)?;
            Ok(AuthenticatedRequest { envelope, owner })
        })();
        match validated {
            Ok(request) => Ok(Some(request)),
            Err(error) => {
                crate::diagnostics::record(
                    &self.diagnostic_directory,
                    "service_frame_rejected",
                    serde_json::json!({"code":error.code}),
                );
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
                Duration::from_millis(200),
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
    fn cancelled_stop_needs_its_completed_operation_not_an_empty_cached_pid() {
        let mut operation = RuntimeOperation {
            id: "owner:stop-7".into(),
            phase: "queued".into(),
            intent_revision: 7,
            status: CoreStatus {
                process_state: "exited".into(),
                ..CoreStatus::default()
            },
            error: None,
        };
        for phase in ["queued", "running"] {
            operation.phase = phase.into();
            assert!(!cancelled_stop_completed(&operation).unwrap());
        }
        operation.phase = "succeeded".into();
        assert!(cancelled_stop_completed(&operation).unwrap());
        operation.status.pid = Some(111);
        assert_eq!(
            cancelled_stop_completed(&operation).unwrap_err().code,
            "core_termination_unconfirmed"
        );
        operation.phase = "cancelled".into();
        assert_eq!(
            cancelled_stop_completed(&operation).unwrap_err().code,
            "operation_cancelled"
        );
        operation.phase = "failed".into();
        assert_eq!(
            cancelled_stop_completed(&operation).unwrap_err().code,
            "core_termination_unconfirmed"
        );
    }

    #[test]
    fn lifecycle_protocol_requires_an_explicit_tun_mode_and_version_four() {
        for command in ["start", "reload"] {
            let missing = serde_json::json!({"command":command, "config":"mixed-port: 7890"});
            assert!(serde_json::from_value::<Request>(missing).is_err());
            for tun in [false, true] {
                let request: Request = serde_json::from_value(serde_json::json!({
                    "command":command, "config":"mixed-port: 7890", "tun":tun,
                }))
                .unwrap();
                assert_eq!(serde_json::to_value(&request).unwrap()["tun"], tun);
                let mut envelope = Envelope {
                    protocol_version: PROTOCOL_VERSION,
                    owner_session: "00000000000000000000000000000001".into(),
                    request_id: "mode-switch".into(),
                    intent_revision: 1,
                    request_sequence: 1,
                    request,
                };
                assert!(envelope.validate(true).is_ok());
                assert!(envelope.validate(false).is_err());
                envelope.protocol_version = 3;
                assert_eq!(
                    envelope.validate(true).unwrap_err().code,
                    "service_protocol"
                );
            }
        }
        assert_eq!(PROTOCOL_VERSION, 4);
    }

    #[test]
    fn core_update_ipc_does_not_accept_paths_urls_or_digests() {
        for payload in [
            r#"{"command":"prepare_core_update","proxy_port":7890,"url":"https://attacker.invalid/core.zip"}"#,
            r#"{"command":"commit_core_update","path":"C:\\user\\evil.exe"}"#,
            r#"{"command":"complete_core_update","accept":true,"sha256":"anything"}"#,
        ] {
            assert!(
                serde_json::from_str::<Request>(payload).is_err(),
                "{payload}"
            );
        }
        let request: Request =
            serde_json::from_str(r#"{"command":"prepare_core_update","proxy_port":7890}"#).unwrap();
        assert!(request.lifecycle_request());
        assert!(!request.bulk());
        assert_eq!(
            operation_timeout(&request).unwrap(),
            Duration::from_secs(900)
        );
    }

    #[test]
    fn diagnostics_map_lifecycle_ids_without_serializing_request_payloads() {
        let mut envelope = Envelope {
            protocol_version: PROTOCOL_VERSION,
            owner_session: "00000000000000000000000000000001".into(),
            request_id: "request-17".into(),
            intent_revision: 4,
            request_sequence: 17,
            request: Request::Start {
                config: "secret: must-not-be-logged".into(),
                tun: true,
            },
        };
        let fields = envelope.diagnostic_fields();
        assert_eq!(fields["serviceCommand"], "start");
        assert_eq!(fields["serviceRequestId"], "request-17");
        assert_eq!(
            fields["operationId"],
            "00000000000000000000000000000001:request-17"
        );
        assert_eq!(fields["requestSequence"], 17);
        assert_eq!(fields["intentRevision"], 4);
        assert!(!fields.to_string().contains("must-not-be-logged"));
        envelope.request = Request::Operation {
            id: "unvalidated-target-must-not-be-logged".into(),
        };
        let fields = envelope.diagnostic_fields();
        assert!(fields.get("operationId").is_none());
        assert!(!fields
            .to_string()
            .contains("unvalidated-target-must-not-be-logged"));
        envelope.request = Request::Reconcile { generation: 7 };
        let fields = envelope.diagnostic_fields();
        assert_eq!(fields["powerGeneration"], 7);
        assert!(fields.get("operationId").is_none());
    }

    #[test]
    fn status_and_heartbeat_never_borrow_a_resource_budget() {
        assert_eq!(
            operation_timeout(&Request::Status).unwrap(),
            CONTROL_TIMEOUT
        );
        assert_eq!(
            operation_timeout(&Request::Heartbeat).unwrap(),
            CONTROL_TIMEOUT
        );
        assert_eq!(
            operation_timeout(&Request::Reconcile { generation: 99 }).unwrap(),
            CONTROL_TIMEOUT
        );
        assert_eq!(
            operation_timeout(&Request::Stop).unwrap(),
            Duration::from_secs(18)
        );
        assert_eq!(
            operation_timeout(&Request::Start {
                config: "resource-bootstrap:\n  timeout: 300\n".into(),
                tun: true,
            })
            .unwrap(),
            Duration::from_secs(360)
        );
        assert_ne!(
            pipe_name("S-1-5-21-test", true),
            pipe_name("S-1-5-21-test", false)
        );
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
