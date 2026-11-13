use crate::{
    config,
    core_update_release::{self, PreparedCoreUpdate},
    error::{AppError, Result},
    lifecycle::{self, Cancellation, RecoveryWindow, StopReason, HEALTH_INTERVAL},
};
use reqwest::blocking::Client;
use serde::{Deserialize, Serialize};
use std::{
    collections::VecDeque,
    fs,
    io::{Read, Seek, SeekFrom, Write},
    net::TcpListener,
    path::{Path, PathBuf},
    process::{Child, Command, ExitStatus, Stdio},
    thread,
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

#[path = "core_update_transaction.rs"]
mod replacement;
use replacement::CoreReplacement;

const STOP_TIMEOUT: Duration = Duration::from_secs(8);
const TOTAL_STOP_TIMEOUT: Duration = Duration::from_secs(12);
const RESTART_WINDOW: Duration = Duration::from_secs(60);
const MAX_RESTARTS: usize = 3;
const RECOVERY_RESERVE: Duration = Duration::from_secs(30);
const CLEANUP_RESERVE: Duration = STOP_TIMEOUT;
const CONTROL_TIMEOUT: Duration = Duration::from_secs(3);
const TUN_PATCH_TIMEOUT: Duration = Duration::from_secs(15);
const MAX_DIAGNOSTIC_BYTES: usize = 4096;

/// Reload validation and application share one resource deadline. A fresh
/// process validates during its own startup. The reserve confirms readiness or
/// restores the previous runtime, never grants another full download allowance.
struct OperationBudget {
    resources: Instant,
    total: Instant,
}

impl OperationBudget {
    fn new(source: &str) -> Result<Self> {
        Self::from_timeout(config::resource_bootstrap_timeout(source)?, Instant::now())
    }

    fn from_timeout(timeout: Duration, started: Instant) -> Result<Self> {
        let resources = started.checked_add(timeout).ok_or_else(budget_overflow)?;
        let total = resources
            .checked_add(RECOVERY_RESERVE)
            .ok_or_else(budget_overflow)?;
        Ok(Self { resources, total })
    }

    fn resource_remaining(&self) -> Result<Duration> {
        remaining(self.resources).map_err(|_| {
            AppError::new(
                "resource_bootstrap_timeout",
                "资源准备已超过 resource-bootstrap.timeout，请检查规则、代理提供者或 GeoData 来源后重试",
            )
        })
    }

    fn work_deadline(&self) -> Instant {
        self.total - CLEANUP_RESERVE
    }

    fn cleanup_deadline(&self) -> Instant {
        Instant::now()
            .checked_add(CLEANUP_RESERVE)
            .unwrap_or(self.total)
            .min(self.total)
    }
}

fn budget_overflow() -> AppError {
    AppError::new(
        "config_invalid",
        "resource-bootstrap.timeout 超出系统支持的计时范围",
    )
}

fn remaining(deadline: Instant) -> Result<Duration> {
    deadline
        .checked_duration_since(Instant::now())
        .filter(|value| !value.is_zero())
        .ok_or_else(|| AppError::new("core_operation_timeout", "本次内核操作已超过总时限"))
}

/// The future is polled by its owning lifecycle thread. Cancellation drops it
/// (and its sockets) without leaving a background worker behind.
pub fn wait_cancellable<T>(
    cancellation: Option<&Cancellation>,
    deadline: Instant,
    operation: impl std::future::Future<Output = Result<T>>,
) -> Result<T> {
    // Reuse one bounded resolver pool. Dropping a per-request runtime can wait
    // for an OS DNS lookup after its HTTP future was already cancelled.
    static IO_RUNTIME: std::sync::OnceLock<std::io::Result<tokio::runtime::Runtime>> =
        std::sync::OnceLock::new();
    let runtime = IO_RUNTIME
        .get_or_init(|| {
            tokio::runtime::Builder::new_current_thread()
                .enable_all()
                .max_blocking_threads(4)
                .build()
        })
        .as_ref()
        .map_err(|_| AppError::new("http_init_failed", "无法初始化可取消的操作"))?;
    let mut operation = Box::pin(operation);
    loop {
        if let Some(cancellation) = cancellation {
            cancellation.check()?;
        }
        let slice = remaining(deadline)?.min(Duration::from_millis(50));
        match runtime.block_on(async { tokio::time::timeout(slice, &mut operation).await }) {
            Ok(result) => return result,
            Err(_) => continue,
        }
    }
}

fn duration_argument(duration: Duration) -> String {
    format!("{}.{:09}s", duration.as_secs(), duration.subsec_nanos())
}

fn confirm_process_exit(
    child: &mut Child,
    deadline: Instant,
    kill_error: Option<std::io::Error>,
) -> Result<ExitStatus> {
    loop {
        match child.try_wait() {
            Ok(Some(status)) => return Ok(status),
            Ok(None) => {}
            Err(error) => return Err(AppError::io("无法确认自有内核进程退出", error)),
        }
        let Ok(timeout) = remaining(deadline) else {
            let detail = kill_error
                .as_ref()
                .and_then(std::io::Error::raw_os_error)
                .map(|code| format!("，系统错误：{code}"))
                .unwrap_or_default();
            return Err(AppError::new("core_termination_unconfirmed", format!("未能在清理时限内确认自有内核进程退出（PID：{}{detail}），请完全退出应用后检查进程", child.id())));
        };
        thread::sleep(timeout.min(Duration::from_millis(20)));
    }
}

fn finish_diagnostics(reader: thread::JoinHandle<String>, deadline: Instant) -> Result<String> {
    while !reader.is_finished() {
        let timeout = remaining(deadline).map_err(|_| {
            AppError::new(
                "core_diagnostic_timeout",
                "配置校验进程输出未在清理时限内关闭",
            )
        })?;
        thread::sleep(timeout.min(Duration::from_millis(10)));
    }
    Ok(reader.join().unwrap_or_default())
}

// Core failures can include complete subscription URLs or YAML source lines.
// Only recognized bootstrap fields and the exact native TUN ownership guard
// are copied into host errors/logs.
fn safe_resource_diagnostic(line: &str) -> Option<String> {
    let (_, record) = line.split_once("[resource-bootstrap]")?;
    let field = |name: &str| -> Option<&str> {
        record
            .split_whitespace()
            .find_map(|value| value.strip_prefix(name))
    };
    let code = field("code=")?;
    let kind = field("kind=")?;
    if code.is_empty()
        || code.len() > 64
        || !code
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || byte == b'_')
        || ![
            "provider",
            "geo",
            "proxy",
            "config",
            "configuration",
            "bootstrap",
        ]
        .contains(&kind)
    {
        return None;
    }
    let mut safe = format!("[resource-bootstrap] code={code} kind={kind}");
    if let Some((_, name)) = record.split_once("resource=") {
        let mut values = serde_json::Deserializer::from_str(name).into_iter::<String>();
        if let Some(Ok(name)) = values.next() {
            // Names are useful to locate the failed provider, but a URL or an
            // opaque credential accidentally used as a name must not be echoed.
            let allowed = !name.is_empty()
                && name.chars().count() <= 80
                && name
                    .chars()
                    .all(|c| c.is_alphanumeric() || " ._-".contains(c))
                && !(name.len() >= 32
                    && name
                        .bytes()
                        .all(|c| c.is_ascii_alphanumeric() || c == b'_' || c == b'-'));
            if allowed {
                safe.push_str(" resource=");
                safe.push_str(&serde_json::to_string(&name).ok()?);
            }
        }
    }
    Some(safe)
}

const TUN_DEVICE_CONFLICT_SUFFIX: &str = " already exists with active, foreign or unverified ownership; stop its owner or choose an unused tun.device: file already exists";

fn safe_tun_device_conflict(line: &str) -> Option<String> {
    let line = line.trim();
    let decoded;
    let message = if (line.starts_with("time=") || line.starts_with("level="))
        && (line.contains("level=fatal msg=") || line.contains("level=error msg="))
    {
        let (_, value) = line.split_once("msg=")?;
        decoded = serde_json::Deserializer::from_str(value)
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
    let value = message.strip_prefix("configure tun interface: adapter ")?;
    let mut quoted = serde_json::Deserializer::from_str(value).into_iter::<String>();
    let name = quoted.next()?.ok()?;
    if value.get(quoted.byte_offset()..)? != TUN_DEVICE_CONFLICT_SUFFIX {
        return None;
    }
    // Preserve the native cause, without echoing an accidental URL, credential,
    // control character or unbounded source string used as an interface name.
    let name = config::diagnostic_tun_device(Some(&name))?;
    Some(format!(
        "configure tun interface: adapter {}{TUN_DEVICE_CONFLICT_SUFFIX}",
        serde_json::to_string(&name).ok()?
    ))
}

fn structured_tun_diagnostic(line: &str) -> Option<(&'static str, String)> {
    let line = line.trim();
    let decoded;
    let message = if (line.starts_with("time=") || line.starts_with("level="))
        && (line.contains("level=fatal msg=") || line.contains("level=error msg="))
    {
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
        .or_else(|| message.strip_prefix("Start TUN listening error: "))
        .unwrap_or(message);
    let value = message.strip_prefix("configure tun interface: adapter ")?;
    let mut quoted = serde_json::Deserializer::from_str(value).into_iter::<String>();
    let name = quoted.next()?.ok()?;
    let details = value
        .get(quoted.byte_offset()..)?
        .strip_prefix(": [wintun ")?;
    let header = details.split_once(']')?.0;
    if header.len() > 512 {
        return None;
    }
    let field = |prefix: &str| {
        header
            .split_whitespace()
            .find_map(|part| part.strip_prefix(prefix))
    };
    let stage = field("stage=")?;
    let reason = field("reason=")?;
    let code = match (stage, reason) {
        ("inspect", "foreign_alias") => "tun_device_conflict",
        ("inspect", "ambiguous_alias" | "native_identity_missing" | "identity_mismatch") => {
            "tun_device_identity_unverified"
        }
        ("inspect", "native_query_failed" | "interface_query_failed") => {
            "tun_device_inspection_failed"
        }
        ("create", "native_busy") => "tun_device_busy",
        ("create", "native_create_failed") => "tun_device_create_failed",
        ("initialize", "native_initialization_failed") => "tun_device_initialization_failed",
        _ => return None,
    };
    let name = config::diagnostic_tun_device(Some(&name))?;
    let mut safe = format!(
        "configure tun interface: adapter {}: [wintun stage={stage} reason={reason}",
        serde_json::to_string(&name).ok()?
    );
    for key in ["expected_guid", "actual_guid"] {
        if let Some(guid) = field(&format!("{key}=")).filter(|guid| {
            let value = guid
                .strip_prefix('{')
                .and_then(|guid| guid.strip_suffix('}'))
                .unwrap_or(guid);
            value.len() == 36
                && value.bytes().enumerate().all(|(index, byte)| {
                    if [8, 13, 18, 23].contains(&index) {
                        byte == b'-'
                    } else {
                        byte.is_ascii_hexdigit()
                    }
                })
        }) {
            safe.push_str(&format!(" {key}={guid}"));
        }
    }
    for key in ["native_code", "win32_code", "luid", "index", "cfgmgr_code"] {
        if let Some(value) = field(&format!("{key}=")).filter(|value| {
            let parsed = if let Some(hex) = value.strip_prefix("0x") {
                !hex.is_empty()
                    && hex.len() <= 16
                    && hex.bytes().all(|byte| byte.is_ascii_hexdigit())
                    && u64::from_str_radix(hex, 16).is_ok()
            } else {
                !value.is_empty()
                    && value.len() <= 20
                    && value.bytes().all(|byte| byte.is_ascii_digit())
                    && value.parse::<u64>().is_ok()
            };
            parsed
        }) {
            safe.push_str(&format!(" {key}={value}"));
        }
    }
    safe.push(']');
    Some((code, safe))
}

fn safe_core_diagnostic(line: &str) -> Option<String> {
    structured_tun_diagnostic(line)
        .map(|(_, diagnostic)| diagnostic)
        .or_else(|| safe_tun_device_conflict(line))
        .or_else(|| safe_resource_diagnostic(line))
}

fn tun_device_error(diagnostic: &str) -> Option<AppError> {
    if let Some((code, cause)) = diagnostic.lines().find_map(structured_tun_diagnostic) {
        return Some(AppError::new(code, format!("TUN 接口操作失败：{cause}")));
    }
    diagnostic
        .lines()
        .find_map(safe_tun_device_conflict)
        .map(|cause| AppError::new("tun_device_conflict", format!("TUN 网卡名称冲突：{cause}")))
}

pub(crate) fn startup_cleanup_error(cause: &AppError, cleanup: &AppError) -> AppError {
    if cleanup.code == "operation_cancelled" {
        return cleanup.clone();
    }
    AppError::new(
        "core_termination_unconfirmed",
        format!(
            "{}；无法确认启动清理完成：{}",
            cause.message, cleanup.message
        ),
    )
}

fn collect_diagnostics(mut reader: impl Read) -> String {
    let mut output = VecDeque::new();
    let mut size = 0;
    let mut line = Vec::new();
    let mut overflow = false;
    let mut chunk = [0_u8; 2048];
    loop {
        let count = match reader.read(&mut chunk) {
            Ok(0) | Err(_) => break,
            Ok(count) => count,
        };
        for byte in &chunk[..count] {
            if *byte == b'\n' {
                if !overflow {
                    if let Some(safe) = safe_core_diagnostic(&String::from_utf8_lossy(&line)) {
                        size += safe.len() + 1;
                        output.push_back(safe);
                        while size > MAX_DIAGNOSTIC_BYTES {
                            if let Some(old) = output.pop_front() {
                                size -= old.len() + 1;
                            }
                        }
                    }
                }
                line.clear();
                overflow = false;
            } else if line.len() < MAX_DIAGNOSTIC_BYTES {
                line.push(*byte);
            } else {
                overflow = true;
            }
        }
    }
    if !overflow {
        if let Some(safe) = safe_core_diagnostic(&String::from_utf8_lossy(&line)) {
            size += safe.len() + 1;
            output.push_back(safe);
            while size > MAX_DIAGNOSTIC_BYTES {
                if let Some(old) = output.pop_front() {
                    size -= old.len() + 1;
                }
            }
        }
    }
    output.into_iter().collect::<Vec<_>>().join("\n")
}

#[derive(Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct Controller {
    pub url: String,
    pub secret: String,
}

#[derive(Clone, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct CoreStatus {
    pub running: bool,
    /// Last confirmed runtime mode, retained while the process is stopped.
    pub tun: bool,
    pub controller: Option<Controller>,
    pub version: Option<String>,
    pub error: Option<String>,
    /// A rejected request is distinct from the health of the restored runtime.
    pub last_operation_failure: Option<OperationFailure>,
    pub restart_count: usize,
    pub lifecycle_api: u32,
    pub desired_running: bool,
    pub recovering: bool,
    pub recovery_generation: u64,
    pub stop_reason: Option<StopReason>,
    pub error_code: Option<String>,
    pub pid: Option<u32>,
    pub exit_code: Option<i32>,
    pub tun_health: Option<String>,
    pub process_state: String,
    pub control_state: String,
    pub listeners_ready: Option<bool>,
    pub operation: Option<String>,
    pub observed_at: u64,
    pub service_instance: Option<String>,
    pub status_sequence: u64,
    pub intent_revision: u64,
    pub core_update: Option<CoreUpdateStatus>,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct OperationFailure {
    pub operation: String,
    pub code: String,
    pub message: String,
    pub occurred_at: u64,
    pub requested_tun: bool,
    pub applied_tun: bool,
    pub intent_revision: u64,
}

impl OperationFailure {
    pub fn new(
        operation: &str,
        error: &AppError,
        requested_tun: bool,
        applied_tun: bool,
        intent_revision: u64,
    ) -> Self {
        Self {
            operation: operation.into(),
            code: error.code.clone(),
            message: error.message.clone(),
            occurred_at: observed_now(),
            requested_tun,
            applied_tun,
            intent_revision,
        }
    }
}

#[derive(Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CoreUpdateStatus {
    pub phase: String,
    pub version: Option<String>,
}

struct PendingCoreUpdate {
    intent_revision: u64,
    version: String,
    prepared: Option<PreparedCoreUpdate>,
    replacement: Option<CoreReplacement>,
    previous_source: Option<String>,
    previous_tun: bool,
    was_running: bool,
    stopped_for_update: bool,
    installation_started: bool,
}

pub fn observed_now() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis() as u64
}

struct TunRecovery {
    request_id: String,
    operation_id: Option<String>,
    core_instance: String,
    config_generation: u64,
    tun_generation: u64,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct ProcessObservation {
    state: &'static str,
    exit_code: Option<i32>,
    exit_success: Option<bool>,
    os_error: Option<i32>,
}

fn observe_process(child: Option<&mut Child>) -> ProcessObservation {
    let mut observation = ProcessObservation {
        state: "not_running",
        exit_code: None,
        exit_success: None,
        os_error: None,
    };
    if let Some(child) = child {
        match child.try_wait() {
            Ok(Some(exit)) => {
                observation.state = "exited";
                observation.exit_code = exit.code();
                observation.exit_success = Some(exit.success());
            }
            Ok(None) => observation.state = "alive",
            Err(error) => {
                observation.state = "unknown";
                observation.os_error = error.raw_os_error();
            }
        }
    }
    observation
}

#[derive(Default)]
struct CleanupEvidence {
    main_exit: Option<ExitStatus>,
    force_requested: bool,
}

fn diagnostic_identity(value: &serde_json::Value) -> Option<&str> {
    value.as_str().filter(|value| {
        !value.is_empty()
            && value.len() <= 128
            && value
                .bytes()
                .all(|byte| byte.is_ascii_alphanumeric() || b"-_:.{}".contains(&byte))
    })
}

fn tun_reason_code(reason: Option<&str>) -> &'static str {
    match reason.unwrap_or_default() {
        "" => "none",
        "owned TUN device is missing" => "device_missing",
        "owned TUN device is down" => "device_down",
        "owned TUN address is missing or not ready" => "address_not_ready",
        "owned TUN route is missing" => "route_missing",
        "TUN device identity changed" => "identity_changed",
        "original TUN identity is unavailable" => "identity_unavailable",
        "owned TUN listener is unavailable" => "listener_unavailable",
        "TUN route inspection is unavailable" => "route_inspection_unavailable",
        "TUN health observation is pending" => "observation_pending",
        "TUN health observation is stale" => "observation_stale",
        value if value.starts_with("inspect TUN device:") => "device_inspection_failed",
        value if value.starts_with("inspect TUN identity:") => "identity_inspection_failed",
        value if value.starts_with("inspect TUN addresses:") => "address_inspection_failed",
        value if value.starts_with("inspect TUN routes:") => "route_inspection_failed",
        value if value.starts_with("owned TUN replacement failed:") => "replacement_failed",
        value if value.starts_with("owned TUN recovery failed:") => "recovery_failed",
        value if value.starts_with("TUN operation is in progress:") => "operation_pending",
        _ => "unclassified",
    }
}

struct ScalarConfigPatch {
    before: serde_json::Map<String, serde_json::Value>,
    after: serde_json::Map<String, serde_json::Value>,
}

fn scalar_config_change(previous: &str, next: &str) -> Result<Option<ScalarConfigPatch>> {
    let mut previous = config::parse(previous)?;
    let mut next = config::parse(next)?;
    // These defaults match the core's RawConfig defaults. Values outside the
    // desktop's scalar settings stay on the fully validated reload path.
    let scalar = |document: &mut serde_yaml::Mapping, field: &str| {
        let value = match document.remove(field) {
            None => match field {
                "mode" => serde_json::json!("rule"),
                "allow-lan" => serde_json::json!(false),
                "log-level" => serde_json::json!("info"),
                _ => serde_json::json!(0),
            },
            Some(value) => serde_json::to_value(value).ok()?,
        };
        let valid = match field {
            "mode" => value
                .as_str()
                .is_some_and(|value| matches!(value, "rule" | "global" | "direct")),
            "allow-lan" => value.is_boolean(),
            "log-level" => value.as_str().is_some_and(|value| {
                matches!(value, "silent" | "error" | "warning" | "info" | "debug")
            }),
            _ => value.as_u64().is_some_and(|port| port <= u16::MAX as u64),
        };
        valid.then_some(value)
    };
    let mut patch = ScalarConfigPatch {
        before: serde_json::Map::new(),
        after: serde_json::Map::new(),
    };
    for field in [
        "mode",
        "allow-lan",
        "log-level",
        "port",
        "socks-port",
        "mixed-port",
    ] {
        let (Some(before), Some(after)) = (scalar(&mut previous, field), scalar(&mut next, field))
        else {
            return Ok(None);
        };
        if before != after {
            patch.before.insert(field.into(), before);
            patch.after.insert(field.into(), after);
        }
    }
    Ok((previous == next && !patch.after.is_empty()).then_some(patch))
}

pub struct CoreSupervisor {
    executable: PathBuf,
    home: PathBuf,
    logs: PathBuf,
    child: Option<Child>,
    validation_child: Option<Child>,
    #[cfg(windows)]
    job: Option<std::os::windows::io::OwnedHandle>,
    controller: Option<Controller>,
    version: Option<String>,
    source: Option<String>,
    tun: bool,
    desired_running: bool,
    error: Option<String>,
    last_operation_failure: Option<OperationFailure>,
    log_start: u64,
    restarts: VecDeque<Instant>,
    failed_recoveries: usize,
    termination_unconfirmed: bool,
    restart_at: Option<Instant>,
    recovery: Option<RecoveryWindow>,
    recovery_generation: u64,
    last_health_check: Duration,
    health_blocked: bool,
    health_deferred: bool,
    initial_health_check: Option<RecoveryWindow>,
    stop_reason: Option<StopReason>,
    error_code: Option<String>,
    exit_code: Option<i32>,
    tun_health: Option<String>,
    control_state: String,
    listeners_ready: Option<bool>,
    observed_at: u64,
    cancellation: Option<Cancellation>,
    operation_intent_revision: u64,
    tun_recovery: Option<TunRecovery>,
    diagnostic_health: Option<serde_json::Value>,
    diagnostic_health_at: Option<u64>,
    pending_core_update: Option<PendingCoreUpdate>,
    core_update_status: Option<CoreUpdateStatus>,
}

pub fn http_client() -> Result<Client> {
    Client::builder()
        .no_proxy()
        .timeout(Duration::from_secs(3))
        .build()
        .map_err(|_| AppError::new("http_init_failed", "无法初始化本地控制连接"))
}

#[cfg_attr(test, derive(Debug))]
enum ReadinessFailure {
    Temporary(AppError),
    Fatal(AppError),
}

#[cfg_attr(test, derive(Debug))]
enum HealthCheck {
    Ready,
    NeedsRestart,
}

fn observation_failure(failure: ReadinessFailure) -> ReadinessFailure {
    match failure {
        ReadinessFailure::Temporary(mut error) if error.code == "core_readiness_unavailable" => {
            error.code = "core_observation_busy".into();
            ReadinessFailure::Temporary(error)
        }
        failure => failure,
    }
}

fn verify_managed_identity(
    info: &serde_json::Value,
    expected_pid: Option<u32>,
) -> std::result::Result<(), ReadinessFailure> {
    let fatal = |code, message| ReadinessFailure::Fatal(AppError::new(code, message));
    if info.get("managed").and_then(serde_json::Value::as_bool) != Some(true)
        || info.get("api_version").and_then(serde_json::Value::as_u64) != Some(1)
    {
        return Err(fatal("core_controller_incompatible", "本地控制协议不兼容"));
    }
    if info.get("pid").and_then(serde_json::Value::as_u64) != expected_pid.map(u64::from) {
        return Err(fatal(
            "core_controller_identity",
            "本地控制接口进程与自有内核不一致",
        ));
    }
    match info.get("state").and_then(serde_json::Value::as_str) {
        Some("running") => Ok(()),
        Some("loading" | "stopping") => Err(ReadinessFailure::Temporary(AppError::new(
            "core_not_ready",
            "本地内核正在应用配置或停止",
        ))),
        _ => Err(fatal(
            "core_controller_incompatible",
            "本地内核返回未知运行状态",
        )),
    }
}

/// Only transport failures and temporary HTTP failures can be retried. Responses
/// that disprove the controller's identity/protocol must never become readiness.
fn read_readiness_json(
    client: &Client,
    controller: &Controller,
    endpoint: &str,
    deadline: Instant,
) -> std::result::Result<serde_json::Value, ReadinessFailure> {
    read_managed_json(client, controller, endpoint, deadline, reqwest::Method::GET)
}

fn read_managed_json(
    client: &Client,
    controller: &Controller,
    endpoint: &str,
    deadline: Instant,
    method: reqwest::Method,
) -> std::result::Result<serde_json::Value, ReadinessFailure> {
    read_managed_json_body(client, controller, endpoint, deadline, method, None)
}

fn read_managed_json_body(
    client: &Client,
    controller: &Controller,
    endpoint: &str,
    deadline: Instant,
    method: reqwest::Method,
    body: Option<&serde_json::Value>,
) -> std::result::Result<serde_json::Value, ReadinessFailure> {
    let temporary = |reason: String| {
        ReadinessFailure::Temporary(AppError::new(
            "core_readiness_unavailable",
            format!("就绪检查 {endpoint}：{reason}"),
        ))
    };
    let timeout = remaining(deadline)
        .map_err(|_| temporary("已到本次操作截止时间".into()))?
        .min(CONTROL_TIMEOUT);
    let request = client
        .request(method, format!("{}{endpoint}", controller.url))
        .timeout(timeout)
        .bearer_auth(&controller.secret);
    let request = if let Some(body) = body {
        request.json(body)
    } else {
        request
    };
    let response = request.send().map_err(|error| {
        // reqwest's formatted errors may include URLs. Keep only a fixed
        // category; never copy response bodies or controller secrets.
        temporary(
            if error.is_timeout() {
                "本地请求超时"
            } else if error.is_connect() {
                "本地连接尚未建立或已被拒绝"
            } else {
                "本地连接中断"
            }
            .into(),
        )
    })?;
    let status = response.status();
    if matches!(status.as_u16(), 401 | 403) {
        return Err(ReadinessFailure::Fatal(AppError::new(
            "core_controller_unauthorized",
            format!("就绪检查 {endpoint}：本地控制接口鉴权失败（HTTP {status}）"),
        )));
    }
    if (matches!(endpoint, "/desktop/health" | "/desktop/recover")
        || endpoint.starts_with("/desktop/recovery"))
        && matches!(status.as_u16(), 409 | 503)
    {
        return Err(ReadinessFailure::Temporary(AppError::new(
            "core_executor_busy",
            "内核配置执行器暂忙，正在等待本地状态稳定",
        )));
    }
    if matches!(status.as_u16(), 408 | 429 | 500 | 502 | 503 | 504)
        || endpoint == "/desktop/recover" && status.as_u16() == 409
    {
        return Err(temporary(format!("本地控制接口暂不可用（HTTP {status}）")));
    }
    if !status.is_success() {
        return Err(ReadinessFailure::Fatal(AppError::new(
            "core_controller_incompatible",
            format!("就绪检查 {endpoint}：配套内核控制协议不兼容（HTTP {status}）"),
        )));
    }
    // Read failures (including a timeout halfway through a body) are temporary;
    // a complete response containing invalid JSON is a protocol failure.
    let body = response.bytes().map_err(|error| {
        temporary(
            if error.is_timeout() {
                "本地响应读取超时"
            } else {
                "本地响应读取中断"
            }
            .into(),
        )
    })?;
    serde_json::from_slice(&body).map_err(|_| {
        ReadinessFailure::Fatal(AppError::new(
            "core_controller_incompatible",
            format!("就绪检查 {endpoint}：内核返回无效 JSON"),
        ))
    })
}

pub fn fixed_core_path() -> Result<PathBuf> {
    let current = std::env::current_exe().map_err(|e| AppError::io("无法定位程序", e))?;
    let parent = current
        .parent()
        .ok_or_else(|| AppError::new("core_missing", "找不到程序目录"))?;
    let installed = parent.join(if cfg!(windows) {
        "mihomo-core.exe"
    } else {
        "mihomo-core"
    });
    if installed.is_file() {
        return Ok(installed);
    }
    #[cfg(debug_assertions)]
    {
        let dev = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("binaries")
            .join(if cfg!(windows) {
                "mihomo-core-x86_64-pc-windows-msvc.exe"
            } else {
                "mihomo-core-x86_64-unknown-linux-gnu"
            });
        if dev.is_file() {
            return Ok(dev);
        }
    }
    Err(AppError::new(
        "core_missing",
        "未找到配套内核，请重新安装完整程序",
    ))
}

/// Restore only after the caller establishes that an unconfirmed new child is
/// no longer running. Service startup passes false to repair only a rename gap.
pub fn recover_core_update(executable: &Path, restore_unconfirmed: bool) -> Result<()> {
    CoreReplacement::recover(executable, restore_unconfirmed)
}

pub fn core_update_recovery_paths(executable: &Path) -> Result<(PathBuf, PathBuf)> {
    replacement::journal_paths(executable)
}

/// A desktop host can detect a privileged recovery obligation without reading
/// the service-owned journal. Only the fixed sibling marker is inspected.
pub fn core_update_needs_recovery(executable: &Path) -> Result<bool> {
    CoreReplacement::needs_recovery(executable)
}

pub fn quiet_command(executable: &Path) -> Command {
    #[allow(unused_mut)]
    let mut command = Command::new(executable);
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        command.creation_flags(windows_sys::Win32::System::Threading::CREATE_NO_WINDOW);
    }
    command
}

impl CoreSupervisor {
    pub fn new(executable: PathBuf, home: PathBuf, logs: PathBuf) -> Self {
        Self {
            executable,
            home,
            logs,
            child: None,
            validation_child: None,
            #[cfg(windows)]
            job: None,
            controller: None,
            version: None,
            source: None,
            tun: false,
            desired_running: false,
            error: None,
            last_operation_failure: None,
            log_start: 0,
            restarts: VecDeque::new(),
            failed_recoveries: 0,
            termination_unconfirmed: false,
            restart_at: None,
            recovery: None,
            recovery_generation: 0,
            last_health_check: Duration::ZERO,
            health_blocked: false,
            health_deferred: false,
            initial_health_check: None,
            stop_reason: None,
            error_code: None,
            exit_code: None,
            tun_health: None,
            control_state: "unknown".into(),
            listeners_ready: None,
            observed_at: observed_now(),
            cancellation: None,
            operation_intent_revision: 0,
            tun_recovery: None,
            diagnostic_health: None,
            diagnostic_health_at: None,
            pending_core_update: None,
            core_update_status: None,
        }
    }

    pub fn set_cancellation(&mut self, cancellation: Cancellation) {
        self.operation_intent_revision = cancellation.revision();
        self.cancellation = Some(cancellation);
    }

    pub fn set_cancellation_for_intent(
        &mut self,
        cancellation: Cancellation,
        intent_revision: u64,
    ) {
        self.set_cancellation(cancellation);
        // The service worker's cancellation revision is local to that worker;
        // UI-facing operation outcomes belong to the authenticated host intent.
        self.operation_intent_revision = intent_revision;
    }

    pub(crate) fn record_lifecycle(&self, event: &str, fields: serde_json::Value) {
        crate::diagnostics::record(&self.logs, event, fields);
    }

    fn check_cancelled(&self) -> Result<()> {
        self.cancellation
            .as_ref()
            .map_or(Ok(()), Cancellation::check)
    }

    pub fn status(&self) -> CoreStatus {
        CoreStatus {
            running: self.child.is_some()
                && !self.termination_unconfirmed
                && !self.health_blocked
                && !self.health_deferred
                && self.recovery.is_none(),
            tun: self.tun,
            controller: self.controller.clone(),
            version: self.version.clone(),
            error: self.error.clone(),
            last_operation_failure: self.last_operation_failure.as_ref().map(|failure| {
                let mut failure = failure.clone();
                failure.applied_tun = self.tun;
                failure
            }),
            restart_count: self.restarts.len(),
            lifecycle_api: 2,
            desired_running: self.desired_running,
            recovering: self.recovery.is_some()
                || self.desired_running && self.restart_at.is_some(),
            recovery_generation: self.recovery_generation,
            stop_reason: self.stop_reason,
            error_code: self.error_code.clone(),
            pid: self
                .child
                .as_ref()
                .or(self.validation_child.as_ref())
                .map(Child::id),
            exit_code: self.exit_code,
            tun_health: self.tun_health.clone(),
            process_state: if self.termination_unconfirmed {
                "unknown"
            } else if self.child.is_some() {
                "alive"
            } else {
                "exited"
            }
            .into(),
            control_state: self.control_state.clone(),
            listeners_ready: self.listeners_ready,
            operation: if self.termination_unconfirmed {
                Some("stopping".into())
            } else if self.tun_recovery.is_some()
                || self.error_code.as_deref() == Some("tun_recovery_pending")
            {
                Some("recovering_tun".into())
            } else {
                None
            },
            observed_at: self.observed_at,
            intent_revision: self.cancellation.as_ref().map_or(0, Cancellation::revision),
            core_update: self.core_update_status.clone(),
            ..CoreStatus::default()
        }
    }

    /// Download and verify a candidate while the current process keeps serving.
    /// The stage belongs to one accepted host intent, not a caller-chosen path.
    pub fn prepare_core_update(
        &mut self,
        proxy_port: Option<u16>,
        intent_revision: u64,
    ) -> Result<CoreStatus> {
        self.check_cancelled()?;
        if proxy_port == Some(0) {
            return Err(AppError::new("core_update_proxy", "内核更新代理端口无效"));
        }
        self.abort_core_update(true)?;
        let cancellation = self.update_cancellation()?;
        let proxy = proxy_port.map(|port| format!("http://127.0.0.1:{port}"));
        let prepared =
            core_update_release::prepare(&self.executable, proxy.as_deref(), &cancellation)?;
        self.check_cancelled()?;
        let Some(prepared) = prepared else {
            self.core_update_status = Some(CoreUpdateStatus {
                phase: "up_to_date".into(),
                version: Some(core_update_release::executable_version(
                    &self.executable,
                    &cancellation,
                )?),
            });
            return Ok(self.status());
        };
        let version = prepared.version().to_owned();
        self.pending_core_update = Some(PendingCoreUpdate {
            intent_revision,
            version: version.clone(),
            prepared: Some(prepared),
            replacement: None,
            previous_source: None,
            previous_tun: false,
            was_running: false,
            stopped_for_update: false,
            installation_started: false,
        });
        self.core_update_status = Some(CoreUpdateStatus {
            phase: "prepared".into(),
            version: Some(version),
        });
        Ok(self.status())
    }

    /// Commit only after this owner can stop every child it launched. A host
    /// owning a direct child stops it before asking the privileged file owner.
    pub fn commit_core_update(&mut self, intent_revision: u64) -> Result<CoreStatus> {
        self.check_cancelled()?;
        let mut pending = self.take_core_update(intent_revision, true)?;
        if pending.replacement.is_some() {
            self.pending_core_update = Some(pending);
            return Ok(self.status());
        }
        let result =
            (|| {
                let cancellation = self.update_cancellation()?;
                let prepared = pending.prepared.as_ref().ok_or_else(|| {
                    AppError::new("core_update_not_prepared", "尚未准备可安装的内核")
                })?;
                prepared.verify(&cancellation)?;
                pending.was_running = self.child.is_some() && self.desired_running;
                pending.previous_source = self.source.clone();
                pending.previous_tun = self.tun;
                // This bypasses the public Stop's abort path: pending is exclusively
                // owned by this transaction until readiness or rollback completes.
                self.stop_for_cause(StopReason::CoreUpdate, "core_update_replace")?;
                pending.stopped_for_update = true;
                self.check_cancelled()?;
                pending.installation_started = true;
                let replacement = CoreReplacement::install(
                    &self.executable,
                    prepared.executable(),
                    prepared.expected_sha256(),
                    &cancellation,
                )?;
                pending.replacement = Some(replacement);
                self.check_cancelled()?;
                if pending.was_running {
                    let source = pending.previous_source.clone().ok_or_else(|| {
                        AppError::new("core_update_source", "无法恢复内核运行配置")
                    })?;
                    self.start_after_core_update(source, pending.previous_tun)?;
                    self.check_cancelled()?;
                    if self.version.as_deref() != Some(pending.version.as_str()) {
                        return Err(AppError::new(
                            "core_update_version",
                            "新内核返回的运行版本不匹配",
                        ));
                    }
                }
                Ok(())
            })();
        if let Err(error) = result {
            let rollback = self.restore_core_update(&mut pending, true);
            if let Err(rollback) = rollback {
                self.pending_core_update = Some(pending);
                return Err(AppError::new(
                    "core_update_restore_failed",
                    format!("{}；恢复旧内核失败：{}", error.message, rollback.message),
                ));
            }
            return Err(error);
        }
        self.core_update_status = Some(CoreUpdateStatus {
            phase: "committed".into(),
            version: Some(pending.version.clone()),
        });
        self.pending_core_update = Some(pending);
        Ok(self.status())
    }

    /// A direct host acknowledges its own new child's readiness here. The TUN
    /// service already checked readiness during commit, but uses the same ack.
    pub fn complete_core_update(
        &mut self,
        accept: bool,
        intent_revision: u64,
    ) -> Result<CoreStatus> {
        if !accept && self.pending_core_update.is_none() {
            // Recovery after a service restart. The authenticated host must have
            // stopped its direct child before requesting an unconfirmed rollback.
            if CoreReplacement::has_unconfirmed(&self.executable)? {
                self.stop_for_cause(StopReason::CoreUpdate, "core_update_recover")?;
                CoreReplacement::recover(&self.executable, true)?;
                self.core_update_status = Some(CoreUpdateStatus {
                    phase: "rolled_back".into(),
                    version: None,
                });
            } else {
                // A commit failure may already have restored and restarted the
                // old core. Repeating rollback must not stop that good process.
                CoreReplacement::recover(&self.executable, false)?;
            }
            return Ok(self.status());
        }
        if accept {
            self.check_cancelled()?;
        }
        let mut pending = self.take_core_update(intent_revision, accept)?;
        let result = if accept {
            match pending.replacement.as_mut() {
                Some(replacement) => replacement.accept(),
                None => Err(AppError::new("core_update_not_committed", "新内核尚未提交")),
            }
        } else {
            let resume = pending.intent_revision == intent_revision;
            self.restore_core_update(&mut pending, resume)
        };
        if let Err(error) = result {
            self.pending_core_update = Some(pending);
            return Err(error);
        }
        if accept {
            self.core_update_status = Some(CoreUpdateStatus {
                phase: "confirmed".into(),
                version: Some(pending.version),
            });
        }
        Ok(self.status())
    }

    fn update_cancellation(&self) -> Result<Cancellation> {
        self.cancellation
            .clone()
            .ok_or_else(|| AppError::new("core_update_owner", "内核更新缺少有效的生命周期所有者"))
    }

    fn take_core_update(
        &mut self,
        intent_revision: u64,
        require_exact: bool,
    ) -> Result<PendingCoreUpdate> {
        if require_exact
            && self
                .pending_core_update
                .as_ref()
                .map(|pending| pending.intent_revision)
                != Some(intent_revision)
        {
            return Err(AppError::new(
                "core_update_not_prepared",
                "内核更新已取消或不属于当前操作",
            ));
        }
        self.pending_core_update
            .take()
            .ok_or_else(|| AppError::new("core_update_not_prepared", "尚未准备可安装的内核"))
    }

    fn abort_core_update(&mut self, resume_previous: bool) -> Result<()> {
        let Some(mut pending) = self.pending_core_update.take() else {
            return Ok(());
        };
        if pending.replacement.is_some() && !pending.was_running {
            self.pending_core_update = Some(pending);
            return Err(AppError::new(
                "core_update_pending",
                "请先确认桌面内核已退出并完成内核更新回滚",
            ));
        }
        if let Err(error) = self.restore_core_update(&mut pending, resume_previous) {
            self.pending_core_update = Some(pending);
            return Err(error);
        }
        Ok(())
    }

    fn abort_core_update_for_stop(&mut self) -> Result<()> {
        // A privileged file owner may not own the direct host's new process.
        // Stop cancels work but leaves this backup until that host explicitly
        // confirms process exit through Complete(false).
        if self
            .pending_core_update
            .as_ref()
            .is_some_and(|pending| pending.replacement.is_some() && !pending.was_running)
        {
            return Ok(());
        }
        self.abort_core_update(false)
    }

    fn restore_core_update(
        &mut self,
        pending: &mut PendingCoreUpdate,
        resume_previous: bool,
    ) -> Result<()> {
        if let Some(replacement) = &pending.replacement {
            // Cleanup is deliberately not cancellable. Stop must confirm exit
            // before touching a running executable or launching the old image.
            self.stop_for_cause(StopReason::CoreUpdate, "core_update_rollback")?;
            replacement.rollback()?;
            pending.replacement = None;
        } else if pending.installation_started {
            // An interrupted install may have left a durable rename-gap record.
            CoreReplacement::recover(&self.executable, true)?;
        }
        self.core_update_status = Some(CoreUpdateStatus {
            phase: "rolled_back".into(),
            version: None,
        });
        if resume_previous
            && pending.stopped_for_update
            && pending.was_running
            && self.check_cancelled().is_ok()
        {
            if let Some(source) = pending.previous_source.clone() {
                self.start_after_core_update(source, pending.previous_tun)?;
            }
        }
        Ok(())
    }

    pub fn start(&mut self, source: String, tun: bool) -> Result<CoreStatus> {
        self.start_for(source, tun, StopReason::UserStop)
    }

    pub(crate) fn start_after_core_update(
        &mut self,
        source: String,
        tun: bool,
    ) -> Result<CoreStatus> {
        self.start_for(source, tun, StopReason::CoreUpdate)
    }

    fn start_for(&mut self, source: String, tun: bool, reason: StopReason) -> Result<CoreStatus> {
        self.check_cancelled()?;
        self.abort_core_update(false)?;
        let budget = OperationBudget::new(&source)?;
        self.stop_for_cause(
            reason,
            if reason == StopReason::CoreUpdate {
                "core_update_start_replace"
            } else {
                "start_replace"
            },
        )?;
        self.check_cancelled()?;
        self.source = Some(source);
        self.tun = tun;
        self.restarts.clear();
        self.failed_recoveries = 0;
        self.restart_at = None;
        self.desired_running = true;
        self.error = None;
        self.error_code = None;
        self.stop_reason = None;
        self.health_blocked = false;
        self.health_deferred = false;
        self.recovery = None;
        if let Err(error) = self.spawn_with_budget(&budget) {
            self.controller = None;
            #[cfg(windows)]
            {
                self.job = None;
            }
            self.desired_running = false;
            if self.child.is_none() && !self.termination_unconfirmed {
                self.tun = false;
            }
            self.error = Some(error.message.clone());
            self.error_code = Some(error.code.clone());
            if self.check_cancelled().is_ok() && error.code != "operation_cancelled" {
                self.last_operation_failure = Some(OperationFailure::new(
                    "start",
                    &error,
                    tun,
                    self.tun,
                    self.operation_intent_revision,
                ));
            }
            return Err(error);
        }
        self.last_operation_failure = None;
        Ok(self.status())
    }

    fn spawn(&mut self) -> Result<()> {
        let source = self
            .source
            .as_deref()
            .ok_or_else(|| AppError::new("config_missing", "没有可运行的配置"))?;
        let budget = OperationBudget::new(source)?;
        self.spawn_with_budget(&budget)
    }

    fn spawn_with_budget(&mut self, budget: &OperationBudget) -> Result<()> {
        let started = Instant::now();
        self.record_lifecycle("core_start", serde_json::json!({"tun":self.tun,"generation":self.recovery_generation,"cancellationRevision":self.cancellation.as_ref().map(Cancellation::revision)}));
        let result = self.launch(budget);
        self.record_lifecycle("core_start_result", serde_json::json!({"tun":self.tun,"corePid":self.child.as_ref().map(Child::id),"durationMs":started.elapsed().as_millis(),"success":result.is_ok(),"errorCode":result.as_ref().err().map(|error|&error.code)}));
        if let Err(cause) = &result {
            let _scope = crate::diagnostics::scope(
                serde_json::json!({"cleanupCause":"startup_failure","startupErrorCode":cause.code}),
            );
            if self.validation_child.is_some() && self.termination_unconfirmed {
                // Validation already consumed its cleanup allowance and retained
                // the unresolved handle. Do not silently grant another budget.
                return result;
            }
            let cleanup = self.terminate_child_until(self.operation_cleanup_deadline(budget));
            self.controller = None;
            if let Err(cleanup) = cleanup {
                return Err(startup_cleanup_error(cause, &cleanup));
            }
        }
        result
    }

    fn operation_cleanup_deadline(&self, budget: &OperationBudget) -> Instant {
        let deadline = budget.cleanup_deadline();
        if self.check_cancelled().is_err() {
            deadline.min(Instant::now() + Duration::from_secs(2))
        } else {
            deadline
        }
    }

    fn launch(&mut self, budget: &OperationBudget) -> Result<()> {
        let preparation_started = Instant::now();
        self.check_cancelled()?;
        fs::create_dir_all(&self.home).map_err(|e| AppError::io("无法创建运行目录", e))?;
        fs::create_dir_all(&self.logs).map_err(|e| AppError::io("无法创建日志目录", e))?;
        let listener = TcpListener::bind(("127.0.0.1", 0))
            .map_err(|e| AppError::io("无法分配本地控制端口", e))?;
        let port = listener
            .local_addr()
            .map_err(|e| AppError::io("无法读取控制端口", e))?
            .port();
        let mut random = [0_u8; 32];
        getrandom::getrandom(&mut random)
            .map_err(|_| AppError::new("random_failed", "系统随机数不可用"))?;
        let controller = Controller {
            url: format!("http://127.0.0.1:{port}"),
            secret: hex::encode(random),
        };
        let source = self
            .source
            .clone()
            .ok_or_else(|| AppError::new("config_missing", "没有可运行的配置"))?;
        let runtime = config::runtime_config(&source, self.tun, port)?;
        config::atomic_write(&self.home.join("config.yaml"), runtime.as_bytes())?;
        self.rotate_log()?;
        self.log_start = fs::metadata(self.logs.join("core.log")).map_or(0, |file| file.len());
        // There is no old process to protect here. The main process performs
        // the same parse/resource validation as -t before managed readiness;
        // its complete stdout/stderr remains in core.log on startup failure.
        self.record_lifecycle("core_start_stage", serde_json::json!({"stage":"prepare","durationMs":preparation_started.elapsed().as_millis()}));
        self.check_cancelled()?;
        let spawn_started = Instant::now();
        let stdout = self.open_log()?;
        let stderr = stdout
            .try_clone()
            .map_err(|e| AppError::io("无法打开内核日志", e))?;
        let mut command = self.command(&controller, budget.resource_remaining()?);
        command
            .stdin(Stdio::null())
            .stdout(Stdio::from(stdout))
            .stderr(Stdio::from(stderr));
        drop(listener);
        let child = command
            .spawn()
            .map_err(|e| AppError::io("无法启动配套内核", e))?;
        self.child = Some(child);
        self.record_lifecycle(
            "core_start_stage",
            serde_json::json!({"stage":"spawn","durationMs":spawn_started.elapsed().as_millis()}),
        );
        self.diagnostic_health = None;
        self.diagnostic_health_at = None;
        self.record_lifecycle("core_spawned", serde_json::json!({"corePid":self.child.as_ref().map(Child::id),"tun":self.tun,"generation":self.recovery_generation}));
        self.termination_unconfirmed = false;
        #[cfg(windows)]
        {
            self.job = Some(crate::windows::create_kill_job(
                self.child.as_ref().expect("child stored above"),
            )?);
        }
        self.controller = Some(controller.clone());
        self.check_cancelled()?;
        let readiness_started = Instant::now();
        let ready = self.wait_ready(&controller, &source, budget.work_deadline());
        self.record_lifecycle("core_start_stage", serde_json::json!({"stage":"readiness","durationMs":readiness_started.elapsed().as_millis(),"success":ready.is_ok(),"errorCode":ready.as_ref().err().map(|error| &error.code)}));
        ready?;
        self.check_cancelled()?;
        let version_started = Instant::now();
        if let Ok(timeout) = remaining(budget.work_deadline()) {
            self.version = http_client()?
                .get(format!("{}/version", controller.url))
                .timeout(timeout.min(CONTROL_TIMEOUT))
                .bearer_auth(&controller.secret)
                .send()
                .ok()
                .and_then(|r| r.json::<serde_json::Value>().ok())
                .and_then(|v| {
                    v.get("version")
                        .and_then(|v| v.as_str())
                        .map(str::to_string)
                });
        }
        self.record_lifecycle("core_start_stage", serde_json::json!({"stage":"version","durationMs":version_started.elapsed().as_millis(),"success":self.version.is_some()}));
        self.check_cancelled()?;
        self.error = None;
        self.error_code = None;
        self.stop_reason = None;
        self.health_blocked = false;
        self.health_deferred = false;
        self.recovery = None;
        self.last_health_check = lifecycle::awake_now()?;
        self.begin_health_observation();
        self.control_state = "ready".into();
        self.listeners_ready = Some(true);
        self.observed_at = observed_now();
        crate::diagnostics::record(
            &self.logs,
            "core_ready",
            serde_json::json!({"corePid":self.child.as_ref().map(Child::id),"tun":self.tun,
                "tunDevice":config::diagnostic_tun_device(config::configured_tun_device(&source).ok().flatten().as_deref()),
                "desktopVersion":env!("CARGO_PKG_VERSION"),"coreVersion":self.version}),
        );
        Ok(())
    }

    fn begin_health_observation(&mut self) {
        self.initial_health_check = self.tun.then(|| {
            // A failed clock read must not extend the initial grace period.
            RecoveryWindow::new(
                self.recovery_generation,
                lifecycle::awake_now().unwrap_or(self.last_health_check),
            )
        });
        self.tun_health = Some(if self.tun { "checking" } else { "disabled" }.into());
    }

    fn rotate_log(&self) -> Result<()> {
        let log_path = self.logs.join("core.log");
        if fs::metadata(&log_path).is_ok_and(|m| m.len() > 10 * 1024 * 1024) {
            let old = self.logs.join("core.previous.log");
            if old.exists() {
                fs::remove_file(&old).map_err(|e| AppError::io("无法轮换日志", e))?;
            }
            fs::rename(&log_path, old).map_err(|e| AppError::io("无法轮换日志", e))?;
        }
        Ok(())
    }

    fn open_log(&self) -> Result<fs::File> {
        fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(self.logs.join("core.log"))
            .map_err(|e| AppError::io("无法打开内核日志", e))
    }

    fn log_diagnostic(&self, summary: &str) {
        if !summary.is_empty() {
            if let Ok(mut log) = self.open_log() {
                let _ = writeln!(log, "{summary}");
            }
        }
    }

    fn recent_diagnostic(&self) -> String {
        let Ok(mut file) = fs::File::open(self.logs.join("core.log")) else {
            return String::new();
        };
        if let Ok(length) = file.metadata().map(|metadata| metadata.len()) {
            let _ = file.seek(SeekFrom::Start(
                length.saturating_sub(64 * 1024).max(self.log_start),
            ));
        }
        collect_diagnostics(file.take(64 * 1024))
            .lines()
            .last()
            .unwrap_or_default()
            .to_string()
    }

    fn wait_ready(
        &mut self,
        controller: &Controller,
        source: &str,
        deadline: Instant,
    ) -> Result<()> {
        self.wait_ready_for(controller, source, self.tun, deadline)
    }

    fn wait_ready_for(
        &mut self,
        controller: &Controller,
        source: &str,
        tun: bool,
        deadline: Instant,
    ) -> Result<()> {
        let client = http_client()?;
        let expected = config::parse(source)?;
        let mut last_failure = None;
        while remaining(deadline).is_ok() {
            self.check_cancelled()?;
            if let Some(exit) = self
                .child
                .as_mut()
                .expect("child stored above")
                .try_wait()
                .map_err(|e| AppError::io("无法读取内核状态", e))?
            {
                let diagnostic = self.recent_diagnostic();
                if let Some(error) = tun_device_error(&diagnostic) {
                    return Err(error);
                }
                return Err(AppError::new(
                    "core_start_failed",
                    format!(
                        "内核在就绪前退出（{}），请检查资源来源、配置或日志{}",
                        exit.code()
                            .map_or_else(|| "已终止".into(), |n| n.to_string()),
                        if diagnostic.is_empty() {
                            String::new()
                        } else {
                            format!("：{diagnostic}")
                        },
                    ),
                ));
            }
            let probe = (|| {
                let info = read_readiness_json(&client, controller, "/desktop", deadline)?;
                if info.get("managed").and_then(serde_json::Value::as_bool) != Some(true)
                    || info.get("api_version").and_then(serde_json::Value::as_u64) != Some(1)
                {
                    return Err(ReadinessFailure::Fatal(AppError::new(
                        "core_controller_incompatible",
                        "就绪检查 /desktop：配套内核的桌面控制协议不兼容",
                    )));
                }
                let expected_pid = self.child.as_ref().map(|child| child.id() as u64);
                if info.get("pid").and_then(serde_json::Value::as_u64) != expected_pid {
                    return Err(ReadinessFailure::Fatal(AppError::new(
                        "core_controller_identity",
                        "就绪检查 /desktop：控制接口进程与自有内核不一致",
                    )));
                }
                match info.get("state").and_then(serde_json::Value::as_str) {
                    Some("running") => {}
                    Some("loading" | "stopping") => {
                        return Err(ReadinessFailure::Temporary(AppError::new(
                            "core_not_ready",
                            "就绪检查 /desktop：内核仍在准备资源、应用配置或停止中",
                        )));
                    }
                    _ => {
                        return Err(ReadinessFailure::Fatal(AppError::new(
                            "core_controller_incompatible",
                            "就绪检查 /desktop：内核返回未知运行状态",
                        )));
                    }
                }
                self.verify_listeners_for(&client, controller, &expected, tun, deadline)
            })();
            match probe {
                Ok(()) => {
                    self.check_cancelled()?;
                    return Ok(());
                }
                Err(ReadinessFailure::Fatal(error)) => return Err(error),
                Err(ReadinessFailure::Temporary(error)) => last_failure = Some(error),
            }
            if let Ok(timeout) = remaining(deadline) {
                thread::sleep(timeout.min(Duration::from_millis(150)));
            }
        }
        Err(AppError::new(
            "core_start_timeout",
            format!(
                "资源准备与内核就绪已超过本次操作总时限，请检查资源来源、端口或配置日志{}",
                last_failure.map_or_else(String::new, |error| format!("；{}", error.message)),
            ),
        ))
    }

    fn command(&self, controller: &Controller, resource_remaining: Duration) -> Command {
        let mut command = quiet_command(&self.executable);
        command
            .arg("-desktop-managed")
            .arg(format!(
                "-resource-bootstrap-timeout={}",
                duration_argument(resource_remaining)
            ))
            .arg("-d")
            .arg(&self.home)
            .arg("-f")
            .arg(self.home.join("config.yaml"))
            .arg("-ext-ctl")
            .arg(controller.url.trim_start_matches("http://"))
            .env("CLASH_OVERRIDE_SECRET", &controller.secret)
            .current_dir(&self.home);
        command
    }

    fn validate(&mut self, controller: &Controller, budget: &OperationBudget) -> Result<()> {
        self.check_cancelled()?;
        if self.validation_child.is_some() {
            return Err(AppError::new(
                "core_termination_unconfirmed",
                "上次配置校验进程退出尚未确认，不能启动新的校验进程",
            ));
        }
        let mut child = self
            .command(controller, budget.resource_remaining()?)
            .arg("-t")
            .stdin(Stdio::null())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .map_err(|e| AppError::io("无法校验配置", e))?;
        let stdout = child.stdout.take().expect("piped stdout configured above");
        let stderr = child.stderr.take().expect("piped stderr configured above");
        let stdout = thread::spawn(move || collect_diagnostics(stdout));
        let stderr = thread::spawn(move || collect_diagnostics(stderr));
        #[cfg(windows)]
        let job = match crate::windows::create_kill_job(&child) {
            Ok(job) => job,
            Err(error) => {
                let deadline = self.operation_cleanup_deadline(budget);
                let kill_error = child.kill().err();
                let cleanup = confirm_process_exit(&mut child, deadline, kill_error);
                let _ = finish_diagnostics(stdout, deadline);
                let _ = finish_diagnostics(stderr, deadline);
                if let Err(cleanup) = cleanup {
                    self.retain_validation_child(child, &cleanup);
                    return Err(cleanup);
                }
                return Err(error);
            }
        };
        let result = (|| loop {
            self.check_cancelled()?;
            if let Some(status) = child
                .try_wait()
                .map_err(|e| AppError::io("无法读取配置校验结果", e))?
            {
                return if status.success() {
                    Ok(())
                } else {
                    Err(AppError::new(
                        "reload_invalid",
                        "配置或必需资源准备失败，当前运行配置未替换",
                    ))
                };
            }
            let timeout = budget.resource_remaining()?;
            thread::sleep(timeout.min(Duration::from_millis(50)));
        })();
        let kill_error = result.as_ref().err().and_then(|_| child.kill().err());
        // Closing the job terminates any owned descendants before we wait for
        // process/pipe closure. Neither a failed kill nor an inherited pipe can
        // extend the shared operation deadline indefinitely.
        #[cfg(windows)]
        drop(job);
        let deadline = self.operation_cleanup_deadline(budget);
        let cleanup = confirm_process_exit(&mut child, deadline, kill_error);
        let stdout = finish_diagnostics(stdout, deadline);
        let stderr = finish_diagnostics(stderr, deadline);
        if let Err(error) = cleanup {
            self.retain_validation_child(child, &error);
            return Err(error);
        }
        let output = [stdout?, stderr?].join("\n");
        let summary = collect_diagnostics(output.as_bytes());
        self.log_diagnostic(&summary);
        result.map_err(|mut error| {
            if error.code == "reload_invalid" {
                if let Some(conflict) = tun_device_error(&summary) {
                    return conflict;
                }
            }
            if let Some(last) = summary.lines().last() {
                error.message.push('：');
                error.message.push_str(last);
            }
            error
        })
    }

    fn retain_validation_child(&mut self, child: Child, error: &AppError) {
        self.validation_child = Some(child);
        self.termination_unconfirmed = true;
        self.desired_running = false;
        self.restart_at = None;
        self.error = Some(error.message.clone());
        self.error_code = Some("core_termination_unconfirmed".into());
        self.observed_at = observed_now();
    }

    fn verify_listeners(
        &self,
        client: &Client,
        controller: &Controller,
        expected: &serde_yaml::Mapping,
        deadline: Instant,
    ) -> std::result::Result<(), ReadinessFailure> {
        self.verify_listeners_for(client, controller, expected, self.tun, deadline)
    }

    fn verify_listeners_for(
        &self,
        client: &Client,
        controller: &Controller,
        expected: &serde_yaml::Mapping,
        tun: bool,
        deadline: Instant,
    ) -> std::result::Result<(), ReadinessFailure> {
        let actual = read_readiness_json(client, controller, "/configs", deadline)?;
        Self::verify_listener_snapshot(&actual, expected, tun)
    }

    fn verify_listener_snapshot(
        actual: &serde_json::Value,
        expected: &serde_yaml::Mapping,
        tun: bool,
    ) -> std::result::Result<(), ReadinessFailure> {
        // Mihomo reports these values from its actual TCP listeners, so a port bind
        // failure cannot be mistaken for a successful config application.
        for field in ["port", "socks-port", "mixed-port"] {
            let requested = expected
                .get(field)
                .and_then(serde_yaml::Value::as_u64)
                .unwrap_or(0);
            let actual_port = actual
                .get(field)
                .and_then(serde_json::Value::as_u64)
                .filter(|port| *port <= u16::MAX as u64)
                .ok_or_else(|| {
                    ReadinessFailure::Fatal(AppError::new(
                        "core_controller_incompatible",
                        format!("就绪检查 /configs：内核返回无效监听字段 {field}"),
                    ))
                })?;
            if actual_port != requested {
                return Err(ReadinessFailure::Fatal(AppError::new(
                    "core_listener_failed",
                    format!("就绪检查 /configs：内核未能监听配置的 {field}，请检查端口是否被占用"),
                )));
            }
        }
        if let Some(enabled) = actual["tun"]["enable"].as_bool() {
            if enabled != tun {
                return Err(ReadinessFailure::Temporary(AppError::new(
                    "tun_not_ready",
                    "TUN 实际设备尚未就绪",
                )));
            }
        }
        Ok(())
    }

    fn apply_runtime(
        &mut self,
        controller: &Controller,
        source: &str,
        resource_deadline: Instant,
        completion_deadline: Instant,
    ) -> Result<()> {
        self.apply_runtime_for(
            controller,
            source,
            self.tun,
            resource_deadline,
            completion_deadline,
        )
    }

    fn apply_runtime_for(
        &mut self,
        controller: &Controller,
        source: &str,
        tun: bool,
        resource_deadline: Instant,
        completion_deadline: Instant,
    ) -> Result<()> {
        self.check_cancelled()?;
        let resource_remaining = remaining(resource_deadline)?;
        // One local request future remains owned by this operation. Polling it
        // in short slices lets Stop drop the transport immediately, instead of
        // waiting for the resource budget or leaving a detached HTTP worker.
        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .map_err(|_| AppError::new("http_init_failed", "无法初始化可取消的本地控制连接"))?;
        let _entered = runtime.enter();
        let client = reqwest::Client::builder()
            .no_proxy()
            .build()
            .map_err(|_| AppError::new("http_init_failed", "无法初始化本地控制连接"))?;
        let request = client
            .put(format!("{}/configs?force=true", controller.url))
            .query(&[(
                "resource-bootstrap-timeout",
                duration_argument(resource_remaining),
            )])
            .timeout(remaining(completion_deadline)?)
            .bearer_auth(&controller.secret)
            .json(&serde_json::json!({"path": self.home.join("config.yaml").display().to_string()}))
            .send();
        let mut request = Box::pin(async {
            let mut response = request.await?;
            let status = response.status();
            let mut bytes = Vec::new();
            if !status.is_success() {
                while let Some(chunk) = response.chunk().await? {
                    let remaining = (MAX_DIAGNOSTIC_BYTES + 1).saturating_sub(bytes.len());
                    bytes.extend_from_slice(&chunk[..chunk.len().min(remaining)]);
                    if bytes.len() > MAX_DIAGNOSTIC_BYTES {
                        break;
                    }
                }
            }
            Ok::<_, reqwest::Error>((status, bytes))
        });
        let (status, bytes) = loop {
            self.check_cancelled()?;
            remaining(completion_deadline)?;
            match runtime.block_on(async {
                tokio::time::timeout(Duration::from_millis(50), &mut request).await
            }) {
                Ok(result) => {
                    break result.map_err(|_| {
                        AppError::new("reload_failed", "运行配置重载超时或连接失败")
                    })?
                }
                Err(_) => continue,
            }
        };
        self.check_cancelled()?;
        if !status.is_success() {
            // Error bodies are not trusted as display text: YAML and subscription
            // failures elsewhere in the core may contain secrets or full URLs.
            let diagnostic = serde_json::from_slice::<serde_json::Value>(&bytes)
                .ok()
                .and_then(|body| {
                    body.get("message")
                        .and_then(|message| message.as_str())
                        .and_then(safe_core_diagnostic)
                });
            if let Some(conflict) = diagnostic.as_deref().and_then(tun_device_error) {
                self.log_diagnostic(diagnostic.as_deref().unwrap_or_default());
                return Err(conflict);
            }
            let message = match diagnostic {
                Some(diagnostic) => {
                    self.log_diagnostic(&diagnostic);
                    format!("内核拒绝应用运行配置：{diagnostic}")
                }
                None => "内核拒绝应用运行配置，请检查资源来源、配置或日志".into(),
            };
            return Err(AppError::new("reload_failed", message));
        }
        // HTTP success alone cannot prove the actual listener/TUN application
        // succeeded. Only this owned PID's managed readiness can confirm it.
        self.wait_ready_for(controller, source, tun, completion_deadline)
    }

    fn runtime_json(
        &self,
        controller: &Controller,
        method: reqwest::Method,
        endpoint: &str,
        body: Option<serde_json::Value>,
        deadline: Instant,
    ) -> Result<serde_json::Value> {
        let mutation = method == reqwest::Method::PATCH;
        let tun_mutation = mutation && body.as_ref().is_some_and(|body| body.get("tun").is_some());
        let client = reqwest::Client::builder()
            .no_proxy()
            .build()
            .map_err(|_| AppError::new("http_init_failed", "无法初始化本地控制连接"))?;
        let request = client
            .request(method, format!("{}{endpoint}", controller.url))
            .bearer_auth(&controller.secret)
            .timeout(remaining(deadline)?.min(if tun_mutation {
                TUN_PATCH_TIMEOUT
            } else {
                CONTROL_TIMEOUT
            }));
        let request = if let Some(body) = body {
            request.json(&body)
        } else {
            request
        };
        let result = wait_cancellable(self.cancellation.as_ref(), deadline, async move {
            let transport_error = || {
                AppError::new(
                    if mutation {
                        "config_patch_unconfirmed"
                    } else {
                        "core_readiness_unavailable"
                    },
                    "本地运行配置请求未能确认完成",
                )
            };
            let mut response = request.send().await.map_err(|_| transport_error())?;
            if !response.status().is_success() {
                if tun_mutation {
                    let mut bytes = Vec::new();
                    while let Some(chunk) = response.chunk().await.map_err(|_| transport_error())? {
                        if bytes.len().saturating_add(chunk.len()) > 16 * 1024 {
                            break;
                        }
                        bytes.extend_from_slice(&chunk);
                    }
                    if let Ok(body) = serde_json::from_slice::<serde_json::Value>(&bytes) {
                        if let Some(error) = body
                            .get("message")
                            .or_else(|| body.get("error"))
                            .and_then(serde_json::Value::as_str)
                            .and_then(tun_device_error)
                        {
                            return Err(error);
                        }
                    }
                }
                return Err(AppError::new(
                    if mutation {
                        "config_patch_failed"
                    } else {
                        "core_readiness_unavailable"
                    },
                    "内核拒绝本地运行配置请求",
                ));
            }
            if mutation {
                return Ok(serde_json::Value::Null);
            }
            let mut bytes = Vec::new();
            while let Some(chunk) = response.chunk().await.map_err(|_| transport_error())? {
                if bytes.len().saturating_add(chunk.len()) > 1024 * 1024 {
                    return Err(AppError::new(
                        "core_controller_incompatible",
                        "内核运行状态响应过大",
                    ));
                }
                bytes.extend_from_slice(&chunk);
            }
            serde_json::from_slice(&bytes)
                .map_err(|_| AppError::new("core_controller_incompatible", "内核运行状态响应无效"))
        });
        result.map_err(|error| {
            if mutation && error.code == "core_operation_timeout" {
                AppError::new("config_patch_unconfirmed", "本地设置更新结果尚未确认")
            } else {
                error
            }
        })
    }

    fn verify_config_patch(
        &self,
        controller: &Controller,
        patch: &serde_json::Map<String, serde_json::Value>,
        source: &str,
        tun: bool,
        deadline: Instant,
    ) -> Result<()> {
        let identity =
            self.runtime_json(controller, reqwest::Method::GET, "/desktop", None, deadline)?;
        verify_managed_identity(&identity, self.child.as_ref().map(Child::id)).map_err(
            |error| match error {
                ReadinessFailure::Temporary(error) | ReadinessFailure::Fatal(error) => error,
            },
        )?;
        let actual =
            self.runtime_json(controller, reqwest::Method::GET, "/configs", None, deadline)?;
        self.check_cancelled()?;
        if patch
            .iter()
            .any(|(key, value)| actual.get(key) != Some(value))
            || actual["tun"]["enable"].as_bool() != Some(tun)
        {
            return Err(AppError::new(
                "config_patch_readback_mismatch",
                "内核设置回读与请求不一致",
            ));
        }
        // PATCH can return 204 even when a native listener bind failed. Verify
        // all actual listener ports, including those a LAN change recreated.
        Self::verify_listener_snapshot(&actual, &config::parse(source)?, tun).map_err(|error| {
            match error {
                ReadinessFailure::Temporary(error) | ReadinessFailure::Fatal(error) => error,
            }
        })
    }

    fn apply_config_patch(
        &self,
        controller: &Controller,
        patch: &serde_json::Map<String, serde_json::Value>,
        source: &str,
        deadline: Instant,
    ) -> Result<()> {
        let patched = self.runtime_json(
            controller,
            reqwest::Method::PATCH,
            "/configs",
            Some(serde_json::Value::Object(patch.clone())),
            deadline,
        );
        match patched {
            Ok(_) => self.verify_config_patch(controller, patch, source, self.tun, deadline),
            Err(error) if error.code == "config_patch_unconfirmed" => {
                // The reply may have been lost after application. A readback of
                // the desired scalar is safe: any late identical PATCH agrees.
                self.verify_config_patch(controller, patch, source, self.tun, deadline)
                    .map_err(|readback| {
                        if readback.code == "operation_cancelled" {
                            readback
                        } else {
                            error
                        }
                    })
            }
            Err(error) => Err(error),
        }
    }

    fn reconfigure_scalars(
        &mut self,
        controller: &Controller,
        source: String,
        previous: &str,
        port: u16,
        patch: ScalarConfigPatch,
    ) -> Result<CoreStatus> {
        let started = Instant::now();
        let deadline = started + CONTROL_TIMEOUT * 4;
        let identity =
            self.runtime_json(controller, reqwest::Method::GET, "/desktop", None, deadline)?;
        verify_managed_identity(&identity, self.child.as_ref().map(Child::id)).map_err(
            |error| match error {
                ReadinessFailure::Temporary(error) | ReadinessFailure::Fatal(error) => error,
            },
        )?;
        let path = self.home.join("config.yaml");
        let old_runtime = config::runtime_config(previous, self.tun, port)?;
        let runtime = config::runtime_config(&source, self.tun, port)?;
        self.check_cancelled()?;
        config::atomic_write(&path, runtime.as_bytes())?;
        let result = self.apply_config_patch(controller, &patch.after, &source, deadline);
        self.record_lifecycle("core_config_patch_result", serde_json::json!({"corePid":self.child.as_ref().map(Child::id),"keys":patch.after.keys().collect::<Vec<_>>(),"durationMs":started.elapsed().as_millis(),"success":result.is_ok(),"errorCode":result.as_ref().err().map(|error| &error.code)}));
        if let Err(error) = result {
            let restored_file = config::atomic_write(&path, old_runtime.as_bytes());
            let uncertain = matches!(
                error.code.as_str(),
                "operation_cancelled" | "config_patch_unconfirmed"
            );
            let restored = !uncertain
                && restored_file.is_ok()
                && self
                    .apply_config_patch(
                        controller,
                        &patch.before,
                        previous,
                        Instant::now() + CONTROL_TIMEOUT * 3,
                    )
                    .is_ok();
            let cancellation = self.check_cancelled().err();
            if !restored || cancellation.is_some() {
                // An unanswered PATCH could still be queued in the core. An old
                // value readback cannot prove it will stay old; process exit can.
                self.stop_for(StopReason::UserStop)?;
            }
            if let Some(cancellation) = cancellation {
                restored_file?;
                return Err(cancellation);
            }
            let failure = AppError::new(
                if restored {
                    "config_patch_failed"
                } else {
                    "config_patch_unconfirmed"
                },
                if restored {
                    "配置已保存，但设置未能应用；已恢复先前运行配置"
                } else {
                    "配置已保存，设置更新或回滚未能确认；自有内核已停止"
                },
            );
            if !restored {
                self.error_code = Some(failure.code.clone());
                self.error = Some(failure.message.clone());
            }
            return Err(failure);
        }
        self.source = Some(source);
        if !self.health_blocked && !self.health_deferred && self.recovery.is_none() {
            self.error = None;
            self.error_code = None;
        }
        self.observed_at = observed_now();
        Ok(self.status())
    }

    fn apply_tun_patch(
        &self,
        controller: &Controller,
        parameters: &serde_json::Value,
        source: &str,
        tun: bool,
        deadline: Instant,
    ) -> Result<()> {
        let patch = serde_json::Map::from_iter([("tun".into(), parameters.clone())]);
        self.runtime_json(
            controller,
            reqwest::Method::PATCH,
            "/configs",
            Some(serde_json::Value::Object(patch.clone())),
            deadline,
        )?;
        // Unlike scalar changes, an early disabled readback can be published
        // while native Close is still running. A lost PATCH acknowledgement is
        // therefore never recovered by reading that value; confirmed exit is
        // the only safe barrier before a successor operation.
        self.verify_config_patch(controller, &patch, source, tun, deadline)
    }

    fn reconfigure_tun(
        &mut self,
        controller: &Controller,
        source: String,
        port: u16,
        tun: bool,
    ) -> Result<CoreStatus> {
        let started = Instant::now();
        let deadline = started + TUN_PATCH_TIMEOUT + CONTROL_TIMEOUT * 3;
        let identity =
            self.runtime_json(controller, reqwest::Method::GET, "/desktop", None, deadline)?;
        verify_managed_identity(&identity, self.child.as_ref().map(Child::id)).map_err(
            |failure| match failure {
                ReadinessFailure::Temporary(error) | ReadinessFailure::Fatal(error) => error,
            },
        )?;
        let actual =
            self.runtime_json(controller, reqwest::Method::GET, "/configs", None, deadline)?;
        Self::verify_listener_snapshot(&actual, &config::parse(&source)?, self.tun).map_err(
            |failure| match failure {
                ReadinessFailure::Temporary(error) | ReadinessFailure::Fatal(error) => error,
            },
        )?;
        let before = actual
            .get("tun")
            .filter(|value| {
                value.is_object()
                    && value["enable"].as_bool() == Some(self.tun)
                    && value["device"].is_string()
                    && value["stack"].is_string()
                    && value["auto-route"].is_boolean()
                    && value["auto-detect-interface"].is_boolean()
            })
            .cloned()
            .ok_or_else(|| {
                AppError::new(
                    "core_controller_incompatible",
                    "内核缺少完整的 TUN 运行参数",
                )
            })?;
        // The source is semantically unchanged, so the core's parsed block also
        // retains derived addresses, sorted lists and defaults that YAML omits.
        let mut after = before.clone();
        after["enable"] = serde_json::json!(tun);
        let path = self.home.join("config.yaml");
        let old_runtime = config::runtime_config(&source, self.tun, port)?;
        let runtime = config::runtime_config(&source, tun, port)?;
        self.check_cancelled()?;
        config::atomic_write(&path, runtime.as_bytes())?;
        let result = self.apply_tun_patch(controller, &after, &source, tun, deadline);
        self.record_lifecycle("core_tun_patch_result", serde_json::json!({"corePid":self.child.as_ref().map(Child::id),"requestedTun":tun,"durationMs":started.elapsed().as_millis(),"success":result.is_ok(),"errorCode":result.as_ref().err().map(|error| &error.code)}));
        if let Err(error) = result {
            let restored_file = config::atomic_write(&path, old_runtime.as_bytes());
            let uncertain = matches!(
                error.code.as_str(),
                "operation_cancelled" | "config_patch_unconfirmed"
            );
            let restored = !uncertain
                && restored_file.is_ok()
                && self
                    .apply_tun_patch(
                        controller,
                        &before,
                        &source,
                        self.tun,
                        Instant::now() + TUN_PATCH_TIMEOUT + CONTROL_TIMEOUT * 3,
                    )
                    .is_ok();
            let cancelled = self.check_cancelled().err();
            if !restored || cancelled.is_some() {
                self.stop_for(StopReason::UserStop)?;
            }
            restored_file?;
            if let Some(cancelled) = cancelled {
                return Err(cancelled);
            }
            let failure = AppError::new(
                if uncertain {
                    "tun_patch_unconfirmed"
                } else {
                    error.code.as_str()
                },
                if restored {
                    format!("TUN 切换未能应用：{}。已恢复先前运行模式", error.message)
                } else {
                    format!("TUN 切换或回滚未能确认；自有内核已停止。{}", error.message)
                },
            );
            if restored {
                self.begin_health_observation();
            } else {
                self.error_code = Some(failure.code.clone());
                self.error = Some(failure.message.clone());
            }
            return Err(failure);
        }
        self.source = Some(source);
        self.tun = tun;
        self.recovery = None;
        self.health_blocked = false;
        self.health_deferred = false;
        self.tun_recovery = None;
        self.begin_health_observation();
        self.error = None;
        self.error_code = None;
        self.observed_at = observed_now();
        Ok(self.status())
    }

    pub fn reload(&mut self, source: String) -> Result<CoreStatus> {
        self.reconfigure(source, self.tun)
    }

    /// Apply a configuration and runtime mode as one owned operation. The
    /// previous source/mode remains authoritative until readback succeeds.
    pub fn reconfigure(&mut self, source: String, tun: bool) -> Result<CoreStatus> {
        self.reconfigure_request(source, tun, false)
    }

    /// `tun_change` records an explicit mode request, including acknowledgement
    /// of an already disabled TUN after a failed enable. An ordinary reload of
    /// the same source must never acknowledge an unmet user request.
    pub fn reconfigure_request(
        &mut self,
        source: String,
        tun: bool,
        tun_change: bool,
    ) -> Result<CoreStatus> {
        let started = Instant::now();
        let tun_operation = tun_change || tun != self.tun;
        self.record_lifecycle(
            "core_reload",
            serde_json::json!({"corePid":self.child.as_ref().map(Child::id),"tun":self.tun,"requestedTun":tun}),
        );
        let result = self.reconfigure_inner(source, tun, tun_change);
        if self.check_cancelled().is_ok() {
            match &result {
                Err(error) if error.code != "operation_cancelled" => {
                    self.last_operation_failure = Some(OperationFailure::new(
                        if tun_operation {
                            "tun_change"
                        } else {
                            "config_reload"
                        },
                        error,
                        tun,
                        self.tun,
                        self.operation_intent_revision,
                    ));
                }
                Ok(_)
                    if tun_operation
                        || self
                            .last_operation_failure
                            .as_ref()
                            .is_some_and(|failure| failure.operation != "tun_change") =>
                {
                    self.last_operation_failure = None
                }
                _ => (),
            }
        }
        self.record_lifecycle("core_reload_result", serde_json::json!({"corePid":self.child.as_ref().map(Child::id),"durationMs":started.elapsed().as_millis(),"success":result.is_ok(),"errorCode":result.as_ref().err().map(|error|&error.code)}));
        result.map(|_| self.status())
    }

    fn reconfigure_inner(
        &mut self,
        source: String,
        tun: bool,
        tun_change: bool,
    ) -> Result<CoreStatus> {
        self.check_cancelled()?;
        self.abort_core_update(true)?;
        let budget = OperationBudget::new(&source)?;
        self.log_start = fs::metadata(self.logs.join("core.log")).map_or(0, |file| file.len());
        if self.child.is_none() {
            self.source = Some(source);
            self.tun = tun;
            return Ok(self.status());
        }
        let controller = self
            .controller
            .clone()
            .ok_or_else(|| AppError::new("core_stopped", "内核未运行"))?;
        let port = controller
            .url
            .rsplit(':')
            .next()
            .and_then(|v| v.parse::<u16>().ok())
            .ok_or_else(|| AppError::new("controller_invalid", "本地控制接口无效"))?;
        let previous = self.source.clone().unwrap_or_default();
        if config::parse(&previous)? == config::parse(&source)? {
            if tun != self.tun {
                return self.reconfigure_tun(&controller, source, port, tun);
            }
            if tun_change {
                // Explicitly confirming the already applied mode acknowledges
                // the previous failed switch without reloading resources.
                self.verify_config_patch(
                    &controller,
                    &serde_json::Map::new(),
                    &source,
                    tun,
                    Instant::now() + CONTROL_TIMEOUT * 3,
                )?;
                return Ok(self.status());
            }
        }
        if tun == self.tun {
            if let Some(patch) = scalar_config_change(&previous, &source)? {
                return self.reconfigure_scalars(&controller, source, &previous, port, patch);
            }
        }
        let previous_tun = self.tun;
        let runtime = config::runtime_config(&source, tun, port)?;
        config::atomic_write(&self.home.join("config.yaml"), runtime.as_bytes())?;
        let validation_started = Instant::now();
        let validation = self.validate(&controller, &budget);
        self.record_lifecycle("core_reload_stage", serde_json::json!({"stage":"validation","durationMs":validation_started.elapsed().as_millis(),"success":validation.is_ok()}));
        if let Err(error) = validation {
            if error.code == "operation_cancelled" {
                self.stop_for(StopReason::UserStop)?;
            }
            let old = config::runtime_config(&previous, self.tun, port)?;
            config::atomic_write(&self.home.join("config.yaml"), old.as_bytes())?;
            return Err(error);
        }
        if let Err(error) = self.apply_runtime_for(
            &controller,
            &source,
            tun,
            budget.resources,
            budget.work_deadline(),
        ) {
            if error.code == "operation_cancelled" {
                // A cancelled PUT may already be executing in the core. Do not
                // race it with rollback; confirmed process exit is the barrier.
                self.stop_for(StopReason::UserStop)?;
                let old = config::runtime_config(&previous, previous_tun, port)?;
                config::atomic_write(&self.home.join("config.yaml"), old.as_bytes())?;
                return Err(error);
            }
            let rollback_deadline = Instant::now()
                .checked_add(RECOVERY_RESERVE - CLEANUP_RESERVE)
                .unwrap_or(budget.work_deadline())
                .min(budget.work_deadline());
            let rollback = (|| {
                let old = config::runtime_config(&previous, self.tun, port)?;
                config::atomic_write(&self.home.join("config.yaml"), old.as_bytes())?;
                self.apply_runtime_for(
                    &controller,
                    &previous,
                    previous_tun,
                    rollback_deadline,
                    rollback_deadline,
                )
            })();
            if let Err(cancelled) = self.check_cancelled() {
                self.stop_for(StopReason::UserStop)?;
                return Err(cancelled);
            }
            if rollback.is_ok() {
                let restored = AppError::new(
                    if error.code.starts_with("tun_device_") {
                        error.code.as_str()
                    } else {
                        "reload_failed"
                    },
                    format!(
                        "配置已保存，但未能应用：{}。已恢复上次运行配置，请修正后重载",
                        error.message
                    ),
                );
                self.begin_health_observation();
                return Err(restored);
            }
            // A timed-out ApplyConfig may still be running. Only confirmed process
            // exit proves that no pending application can keep changing live state.
            self.desired_running = false;
            self.restart_at = None;
            let cleanup = self.terminate_child_until(budget.cleanup_deadline());
            self.controller = None;
            self.version = None;
            let code = if self.termination_unconfirmed {
                "core_termination_unconfirmed"
            } else if error.code.starts_with("tun_device_") {
                error.code.as_str()
            } else {
                "reload_restore_failed"
            };
            self.error = Some(match cleanup {
                Ok(()) => {
                    format!(
                        "配置已保存，重载和运行回滚均失败，内核已停止；{}",
                        error.message
                    )
                }
                Err(cleanup) => format!(
                    "配置已保存，重载和运行回滚均失败；{}；{}",
                    error.message, cleanup.message
                ),
            });
            self.error_code = Some(code.into());
            return Err(AppError::new(code, self.error.clone().unwrap_or_default()));
        }
        self.source = Some(source);
        self.tun = tun;
        self.recovery = None;
        self.health_blocked = false;
        self.health_deferred = false;
        self.tun_recovery = None;
        self.begin_health_observation();
        self.error = None;
        self.error_code = None;
        self.observed_at = observed_now();
        Ok(self.status())
    }

    pub fn request_reconcile(&mut self, generation: u64) -> Result<()> {
        if !self.desired_running || self.child.is_none() || self.health_blocked {
            return Ok(());
        }
        self.recovery_generation = self.recovery_generation.max(generation);
        if let Some(recovery) = &mut self.recovery {
            recovery.coalesce(self.recovery_generation);
        } else {
            self.recovery = Some(RecoveryWindow::new(
                self.recovery_generation,
                lifecycle::awake_now()?,
            ));
        }
        crate::diagnostics::record(
            &self.logs,
            "core_reconcile",
            serde_json::json!({"generation":self.recovery_generation,"corePid":self.child.as_ref().map(Child::id),"tun":self.tun}),
        );
        Ok(())
    }

    fn check_local_health(
        &mut self,
        reconcile: bool,
    ) -> std::result::Result<HealthCheck, ReadinessFailure> {
        let fatal = |code, message| ReadinessFailure::Fatal(AppError::new(code, message));
        self.check_cancelled().map_err(ReadinessFailure::Fatal)?;
        let controller = self
            .controller
            .clone()
            .ok_or_else(|| fatal("core_controller_missing", "缺少自有内核控制连接"))?;
        let client = http_client().map_err(ReadinessFailure::Fatal)?;
        let deadline = Instant::now() + CONTROL_TIMEOUT * 3;
        let info = read_readiness_json(&client, &controller, "/desktop", deadline)?;
        let pid = self.child.as_ref().map(Child::id);
        verify_managed_identity(&info, pid)?;
        self.control_state = "ready".into();
        self.check_cancelled().map_err(ReadinessFailure::Fatal)?;
        let expected = config::parse(self.source.as_deref().unwrap_or_default())
            .map_err(ReadinessFailure::Fatal)?;
        let capabilities = &info["capabilities"];
        if capabilities["tun_health"].as_bool() != Some(true) {
            self.tun_health = self.tun.then(|| "unsupported".into());
            self.verify_health_listeners(&client, &controller, &expected, deadline)?;
            return Ok(HealthCheck::Ready);
        }
        // Observe the owned operation before listener configuration. A repair
        // can legitimately close the device while /desktop remains responsive.
        let health = read_readiness_json(&client, &controller, "/desktop/health", deadline)
            .map_err(observation_failure)?;
        verify_managed_identity(&health, pid)?;
        self.record_health_observation(&health);
        self.check_cancelled().map_err(ReadinessFailure::Fatal)?;
        let recovery = &health["recovery"];
        let recovery_current = recovery.is_object()
            && recovery["core_instance"].as_str().is_some()
            && recovery["core_instance"] == health["core_instance"]
            && health["core_instance"] == info["core_instance"]
            && recovery["config_generation"].as_u64().is_some()
            && recovery["config_generation"] == health["config_generation"];
        if recovery.is_object() && !recovery_current {
            return Err(fatal(
                "core_controller_incompatible",
                "TUN 恢复快照所属实例或配置不匹配",
            ));
        }
        if recovery_current
            && recovery["phase"].as_str() == Some("running")
            && recovery["status"].as_str() == Some("running")
        {
            self.control_state = "busy".into();
            self.tun_health = Some("unavailable".into());
            return Err(ReadinessFailure::Temporary(AppError::new(
                "tun_recovery_pending",
                "正在恢复自有 TUN，内核进程继续运行",
            )));
        }
        if recovery_current
            && recovery["phase"].as_str() == Some("failed")
            && recovery["status"].as_str() == Some("needs_restart")
        {
            self.tun_health = Some("degraded".into());
            return Ok(HealthCheck::NeedsRestart);
        }
        if health["tun"]["enabled"].as_bool() != Some(self.tun) {
            return Err(fatal(
                "tun_configuration_mismatch",
                "TUN 运行意图与自有运行模式不一致",
            ));
        }
        if health["stale"].as_bool() == Some(true) {
            if self.tun
                && self.initial_health_check.is_some()
                && health["observed_at"].as_str() == Some("")
                && tun_reason_code(health["tun"]["reason"].as_str()) == "observation_pending"
            {
                return Err(ReadinessFailure::Temporary(AppError::new(
                    "tun_health_pending",
                    "正在确认 TUN 初始状态",
                )));
            }
            self.tun_health = Some("unavailable".into());
            return Err(ReadinessFailure::Temporary(AppError::new(
                "tun_health_unavailable",
                "TUN 状态快照待刷新，保留内核并继续检查",
            )));
        }
        let state = health["tun"]["state"].as_str().unwrap_or("invalid");
        self.tun_health = Some(state.into());
        match state {
            "conflict" => {
                return Err(fatal(
                    "tun_ownership_conflict",
                    "TUN 设备或路由所有权冲突，未接管外部资源",
                ))
            }
            "unavailable" => {
                return Err(ReadinessFailure::Temporary(AppError::new(
                    "tun_health_unavailable",
                    "暂时无法核验 TUN 实际状态，保留内核并继续检查",
                )))
            }
            "disabled" if self.tun => {
                return Err(fatal("tun_not_enabled", "托管 TUN 未启用，请检查运行配置"))
            }
            "disabled" | "healthy"
                if !reconcile
                    || capabilities["tun_recovery_operations"].as_bool() == Some(true) =>
            {
                self.tun_recovery = None;
                self.verify_health_listeners(&client, &controller, &expected, deadline)?;
                return Ok(HealthCheck::Ready);
            }
            "disabled" | "healthy" | "degraded" => (),
            _ => {
                return Err(fatal(
                    "core_controller_incompatible",
                    "本地内核返回无效 TUN 健康状态",
                ))
            }
        }
        if state == "degraded" && health["tun"]["repairable"].as_bool() != Some(true) {
            return Err(fatal(
                "tun_not_repairable",
                "TUN 状态异常且无法安全自动修复",
            ));
        }
        if capabilities["tun_repair"].as_bool() != Some(true) {
            return Err(fatal(
                "tun_repair_unsupported",
                "配套内核不支持安全修复 TUN",
            ));
        }
        self.check_cancelled().map_err(ReadinessFailure::Fatal)?;
        let recovered = if capabilities["tun_recovery_operations"].as_bool() == Some(true) {
            self.read_tun_recovery(&client, &controller, &health, deadline)
                .map_err(observation_failure)?
        } else {
            let recovered = read_managed_json(
                &client,
                &controller,
                "/desktop/recover",
                deadline,
                reqwest::Method::POST,
            )
            .map_err(observation_failure)?;
            if recovered["pid"].as_u64() != pid.map(u64::from) {
                return Err(fatal(
                    "core_controller_identity",
                    "恢复结果的进程与自有内核不一致",
                ));
            }
            recovered
        };
        match recovered["status"].as_str() {
            Some("running") => {
                self.control_state = "busy".into();
                Err(ReadinessFailure::Temporary(AppError::new(
                    "tun_recovery_pending",
                    "正在恢复自有 TUN，内核进程继续运行",
                )))
            }
            Some("healthy" | "repaired") => {
                self.tun_health = recovered["tun"]["state"].as_str().map(str::to_owned);
                if !matches!(self.tun_health.as_deref(), Some("healthy" | "disabled"))
                    || recovered["tun"]["enabled"].as_bool() != Some(self.tun)
                    || self.tun && self.tun_health.as_deref() != Some("healthy")
                {
                    return Err(fatal("tun_recovery_unconfirmed", "TUN 修复结果未确认健康"));
                }
                self.tun_recovery = None;
                self.verify_health_listeners(&client, &controller, &expected, deadline)?;
                Ok(HealthCheck::Ready)
            }
            Some("needs_restart") => Ok(HealthCheck::NeedsRestart),
            Some("conflict") => Err(fatal(
                "tun_ownership_conflict",
                "TUN 恢复遇到资源所有权冲突，未接管外部资源",
            )),
            Some("unavailable") => {
                self.tun_recovery = None;
                self.tun_health = Some("unavailable".into());
                Err(ReadinessFailure::Temporary(AppError::new(
                    "tun_recovery_unavailable",
                    "暂时无法安全恢复 TUN，保留内核并继续检查",
                )))
            }
            _ => Err(fatal(
                "core_controller_incompatible",
                "本地内核返回无效恢复结果",
            )),
        }
    }

    fn record_health_observation(&mut self, health: &serde_json::Value) {
        // Take only typed lifecycle fields from the existing read-only API.
        // In particular, raw reason/error strings may contain configuration.
        let observation = serde_json::json!({
            "corePid": self.child.as_ref().map(Child::id),
            "coreInstance": diagnostic_identity(&health["core_instance"]),
            "configGeneration": health["config_generation"].as_u64(),
            "tunGeneration": health["tun_generation"].as_u64(),
            "creationCount": health["creation_count"].as_u64(),
            "stale": health["stale"].as_bool(),
            "tun": {
                "enabled": health["tun"]["enabled"].as_bool(),
                "state": health["tun"]["state"].as_str()
                    .filter(|state| matches!(*state, "healthy" | "disabled" | "unavailable" | "degraded" | "conflict"))
                    .unwrap_or("invalid"),
                "device": config::diagnostic_tun_device(health["tun"]["device"].as_str()),
                "index": health["tun"]["index"].as_u64(),
                "identity": diagnostic_identity(&health["tun"]["identity"]),
                "reasonCode": tun_reason_code(health["tun"]["reason"].as_str()),
                "repairable": health["tun"]["repairable"].as_bool(),
            },
            "recoveryId": diagnostic_identity(&health["recovery"]["id"]),
            "recoveryPhase": diagnostic_identity(&health["recovery"]["phase"]),
            "recoveryStatus": diagnostic_identity(&health["recovery"]["status"]),
        });
        self.diagnostic_health_at = Some(observed_now());
        if self.diagnostic_health.as_ref() != Some(&observation) {
            self.record_lifecycle("core_health_observation", observation.clone());
            self.diagnostic_health = Some(observation);
        }
    }

    fn verify_health_listeners(
        &mut self,
        client: &Client,
        controller: &Controller,
        expected: &serde_yaml::Mapping,
        deadline: Instant,
    ) -> std::result::Result<(), ReadinessFailure> {
        self.check_cancelled().map_err(ReadinessFailure::Fatal)?;
        match self.verify_listeners(client, controller, expected, deadline) {
            Ok(()) => {
                self.listeners_ready = Some(true);
                Ok(())
            }
            // A responsive owned /desktop proves the process has not died. A
            // temporarily busy listener snapshot must not spend restart budget.
            Err(ReadinessFailure::Temporary(mut error)) => {
                error.code = "core_observation_busy".into();
                self.control_state = "busy".into();
                Err(ReadinessFailure::Temporary(error))
            }
            Err(error) => {
                self.listeners_ready = Some(false);
                Err(error)
            }
        }
    }

    fn read_tun_recovery(
        &mut self,
        client: &Client,
        controller: &Controller,
        health: &serde_json::Value,
        deadline: Instant,
    ) -> std::result::Result<serde_json::Value, ReadinessFailure> {
        let invalid = || {
            ReadinessFailure::Fatal(AppError::new(
                "core_controller_incompatible",
                "TUN 恢复操作协议不兼容",
            ))
        };
        let instance = health["core_instance"].as_str().ok_or_else(invalid)?;
        let config_generation = health["config_generation"].as_u64().ok_or_else(invalid)?;
        let tun_generation = health["tun_generation"].as_u64().ok_or_else(invalid)?;
        let same = self.tun_recovery.as_ref().is_some_and(|operation| {
            operation.core_instance == instance
                && operation.config_generation == config_generation
                && operation.tun_generation == tun_generation
        });
        if !same {
            let mut random = [0u8; 16];
            getrandom::getrandom(&mut random).map_err(|_| invalid())?;
            self.tun_recovery = Some(TunRecovery {
                request_id: hex::encode(random),
                operation_id: None,
                core_instance: instance.into(),
                config_generation,
                tun_generation,
            });
        }
        let operation = self.tun_recovery.as_ref().expect("operation set above");
        let response = if let Some(id) = &operation.operation_id {
            if !id
                .bytes()
                .all(|c| c.is_ascii_alphanumeric() || matches!(c, b'-' | b'_'))
            {
                return Err(invalid());
            }
            read_readiness_json(
                client,
                controller,
                &format!("/desktop/recovery/{id}"),
                deadline,
            )?
        } else {
            read_managed_json_body(
                client,
                controller,
                "/desktop/recovery",
                deadline,
                reqwest::Method::POST,
                Some(
                    &serde_json::json!({"request_id":operation.request_id,"config_generation":config_generation,"tun_generation":tun_generation}),
                ),
            )?
        };
        let result = response.get("operation").ok_or_else(invalid)?;
        if result["core_instance"].as_str() != Some(instance)
            || result["request_id"].as_str() != Some(operation.request_id.as_str())
            || result["config_generation"].as_u64() != Some(config_generation)
            || result["tun_generation"].as_u64() != Some(tun_generation)
            || !matches!(
                result["phase"].as_str(),
                Some("running" | "succeeded" | "failed" | "cancelled")
            )
        {
            return Err(invalid());
        }
        let id = result["id"].as_str().ok_or_else(invalid)?;
        if operation
            .operation_id
            .as_deref()
            .is_some_and(|previous| previous != id)
        {
            return Err(invalid());
        }
        self.tun_recovery
            .as_mut()
            .expect("operation set above")
            .operation_id = Some(id.into());
        if result["phase"].as_str() == Some("cancelled") {
            self.tun_recovery = None;
            return Err(ReadinessFailure::Temporary(AppError::new(
                "core_executor_busy",
                "TUN 恢复已被配置更新取消，等待最新状态",
            )));
        }
        if result["phase"].as_str() == Some("running")
            && result["status"].as_str() != Some("running")
            || result["phase"].as_str() == Some("succeeded")
                && !matches!(result["status"].as_str(), Some("healthy" | "repaired"))
            || result["phase"].as_str() == Some("failed")
                && !matches!(
                    result["status"].as_str(),
                    Some("needs_restart" | "conflict" | "unavailable")
                )
        {
            return Err(invalid());
        }
        Ok(result.clone())
    }

    fn observe_health(&mut self, now: Duration) -> Result<()> {
        self.last_health_check = now;
        let reconciling = self.recovery.is_some();
        match self.check_local_health(reconciling || self.health_deferred) {
            Ok(HealthCheck::Ready) => {
                if reconciling || self.health_deferred || self.initial_health_check.is_some() {
                    crate::diagnostics::record(
                        &self.logs,
                        "core_recovered",
                        serde_json::json!({"generation":self.recovery_generation,"corePid":self.child.as_ref().map(Child::id),"tunHealth":self.tun_health}),
                    );
                    self.error = None;
                    self.error_code = None;
                }
                self.recovery = None;
                self.health_deferred = false;
                self.initial_health_check = None;
            }
            Ok(HealthCheck::NeedsRestart) => {
                self.schedule_health_restart(AppError::new(
                    "tun_recovery_needs_restart",
                    "内核确认 TUN 需要重启，正在恢复",
                ))?;
            }
            Err(ReadinessFailure::Temporary(error)) => {
                if error.code == "tun_health_pending" {
                    let exhausted = self
                        .initial_health_check
                        .as_mut()
                        .expect("initial observation requires an active grace period")
                        .failed(now);
                    self.record_lifecycle("core_health_retry", serde_json::json!({"generation":self.recovery_generation,"code":error.code,"exhausted":exhausted}));
                    if exhausted {
                        self.initial_health_check = None;
                        self.tun_health = Some("unavailable".into());
                        self.health_deferred = true;
                        self.recovery = None;
                        self.error_code = Some("tun_health_unavailable".into());
                        self.error = Some("TUN 初始状态仍无法确认，保留内核并继续检查".into());
                    } else {
                        // Initial read-only sampling is not a recovery task and
                        // must not hide the ready controller or block actions.
                        self.tun_health = Some("checking".into());
                    }
                    return Ok(());
                }
                self.initial_health_check = None;
                if error.code == "core_readiness_unavailable" {
                    self.control_state = "unreachable".into();
                } else if matches!(
                    error.code.as_str(),
                    "core_executor_busy"
                        | "core_observation_busy"
                        | "core_not_ready"
                        | "tun_recovery_pending"
                ) {
                    self.control_state = "busy".into();
                }
                let observe_only = matches!(
                    error.code.as_str(),
                    "core_executor_busy"
                        | "core_observation_busy"
                        | "core_not_ready"
                        | "tun_recovery_pending"
                        | "tun_health_unavailable"
                        | "tun_recovery_unavailable"
                );
                if self.health_deferred && observe_only {
                    // This window has ended. Keep only the ordinary low-rate
                    // monitor, without a new window, restart or resource work.
                    self.recovery = None;
                    self.error_code = Some(error.code);
                    self.error = Some(error.message);
                    return Ok(());
                }
                self.health_deferred = false;
                let exhausted = self
                    .recovery
                    .get_or_insert_with(|| RecoveryWindow::new(self.recovery_generation, now))
                    .failed(lifecycle::awake_now()?);
                crate::diagnostics::record(
                    &self.logs,
                    "core_health_retry",
                    serde_json::json!({"generation":self.recovery_generation,"code":error.code,"exhausted":exhausted}),
                );
                if exhausted {
                    if observe_only {
                        // A responding executor may still own a legitimate
                        // operation, or Windows may temporarily fail to enumerate
                        // its devices/routes. Neither proves this process is broken.
                        self.recovery = None;
                        self.health_deferred = true;
                        self.error_code = Some(error.code);
                        self.error = Some(error.message);
                    } else {
                        self.schedule_health_restart(error)?;
                    }
                } else {
                    self.error_code = Some(error.code);
                    self.error = Some(error.message);
                }
            }
            Err(ReadinessFailure::Fatal(error)) => {
                self.initial_health_check = None;
                if error.code == "operation_cancelled" {
                    return Err(error);
                }
                if error.code.starts_with("core_controller_") {
                    self.control_state = "unreachable".into();
                    self.controller = None;
                }
                crate::diagnostics::record(
                    &self.logs,
                    "core_health_blocked",
                    serde_json::json!({"generation":self.recovery_generation,"code":error.code,"corePid":self.child.as_ref().map(Child::id)}),
                );
                self.recovery = None;
                self.health_blocked = true;
                self.health_deferred = false;
                self.error_code = Some(error.code);
                self.error = Some(error.message);
            }
        }
        Ok(())
    }

    fn schedule_health_restart(&mut self, error: AppError) -> Result<()> {
        // stop_for confirms this owned process has exited before a later poll
        // may spawn. The intervening user Stop cancels the pending recovery.
        let _scope = crate::diagnostics::scope(
            serde_json::json!({"trigger":"health_restart","triggerCode":error.code}),
        );
        self.stop_for(StopReason::HealthFailure)?;
        self.check_cancelled()?;
        self.desired_running = true;
        self.error_code = Some(error.code);
        self.error = Some(error.message);
        self.restart_at = Some(Instant::now() + Duration::from_secs(1));
        self.record_lifecycle("core_restart_scheduled", serde_json::json!({"cause":"health_failure","errorCode":self.error_code,"delayMs":1000,"generation":self.recovery_generation}));
        Ok(())
    }

    pub fn poll(&mut self) -> Result<CoreStatus> {
        self.check_cancelled()?;
        if let Some(validation) = self.validation_child.as_mut() {
            if validation
                .try_wait()
                .map_err(|error| AppError::io("无法确认配置校验进程退出", error))?
                .is_some()
            {
                self.validation_child = None;
                if self.child.is_none() {
                    self.termination_unconfirmed = false;
                }
            }
        }
        if let Some(child) = self.child.as_mut() {
            if let Some(exit) = child
                .try_wait()
                .map_err(|e| AppError::io("无法读取内核状态", e))?
            {
                self.exit_code = exit.code();
                crate::diagnostics::record(
                    &self.logs,
                    "core_exit",
                    serde_json::json!({"corePid":child.id(),"exitCode":exit.code(),"exitSuccess":exit.success(),"desiredRunning":self.desired_running,
                        "cancellationRevision":self.cancellation.as_ref().map(Cancellation::revision),"lastHealth":self.diagnostic_health,"lastHealthObservedAtMs":self.diagnostic_health_at}),
                );
                self.child = None;
                self.controller = None;
                self.termination_unconfirmed = self.validation_child.is_some();
                #[cfg(windows)]
                {
                    self.job = None;
                }
                if self.desired_running {
                    self.stop_reason = Some(StopReason::UnexpectedExit);
                    self.error_code = Some("core_unexpected_exit".into());
                    self.error = Some("内核意外退出，正在准备恢复".into());
                    self.restart_at = Some(Instant::now() + Duration::from_secs(1));
                    self.record_lifecycle("core_restart_scheduled", serde_json::json!({"cause":"unexpected_exit","exitCode":exit.code(),"delayMs":1000,"generation":self.recovery_generation}));
                }
                self.recovery = None;
                self.initial_health_check = None;
            }
        }
        while self
            .restarts
            .front()
            .is_some_and(|time| time.elapsed() > RESTART_WINDOW)
        {
            self.restarts.pop_front();
        }
        if self.desired_running
            && self.child.is_none()
            && self.restart_at.is_some_and(|time| Instant::now() >= time)
        {
            if self.failed_recoveries >= MAX_RESTARTS || self.restarts.len() >= MAX_RESTARTS {
                self.desired_running = false;
                self.restart_at = None;
                self.stop_reason = Some(StopReason::RecoveryExhausted);
                self.error_code = Some("core_recovery_exhausted".into());
                self.error = Some(
                    "内核连续恢复失败或在 60 秒内重启 3 次，已停止自动重启，请检查配置与资源来源"
                        .into(),
                );
            } else {
                self.restarts.push_back(Instant::now());
                match self.spawn() {
                    Ok(()) => {
                        self.restart_at = None;
                        self.failed_recoveries = 0;
                    }
                    Err(error) => {
                        self.failed_recoveries += 1;
                        self.controller = None;
                        let device_conflict = error.code == "tun_device_conflict";
                        self.error = Some(error.message);
                        self.error_code = Some(error.code);
                        if device_conflict {
                            // Repeating the same rejected alias cannot repair
                            // native ownership. Preserve the exact failure for
                            // the desktop's bounded source-bound recovery.
                            self.desired_running = false;
                            self.restart_at = None;
                        } else if !self.termination_unconfirmed {
                            self.restart_at = Some(
                                Instant::now() + Duration::from_secs(1 << self.restarts.len()),
                            );
                        }
                    }
                }
            }
        }
        if self.child.is_some()
            && self.desired_running
            && !self.health_blocked
            && !self.termination_unconfirmed
        {
            let now = lifecycle::awake_now()?;
            if self.initial_health_check.is_some()
                || self.recovery.is_some()
                || now.saturating_sub(self.last_health_check) >= HEALTH_INTERVAL
            {
                self.observe_health(now)?;
            }
        }
        self.observed_at = observed_now();
        Ok(self.status())
    }

    pub fn stop(&mut self) -> Result<()> {
        self.stop_for(StopReason::UserStop)
    }

    pub fn stop_for(&mut self, reason: StopReason) -> Result<()> {
        self.abort_core_update_for_stop()?;
        let cause = match reason {
            StopReason::UserStop => "requested_stop",
            StopReason::CoreUpdate => "core_update",
            StopReason::OwnerLost => "owner_lost",
            StopReason::LeaseExpired => "lease_expired",
            StopReason::UnexpectedExit => "unexpected_exit",
            StopReason::RecoveryExhausted => "recovery_exhausted",
            StopReason::HealthFailure => "health_failure",
            StopReason::Shutdown => "shutdown",
        };
        self.stop_for_cause(reason, cause)?;
        if reason == StopReason::UserStop {
            self.last_operation_failure = None;
        }
        Ok(())
    }

    fn stop_for_cause(&mut self, reason: StopReason, cause: &'static str) -> Result<()> {
        let started = Instant::now();
        let deadline = started + TOTAL_STOP_TIMEOUT;
        let pid = self.child.as_ref().map(Child::id);
        let validation_pid = self.validation_child.as_ref().map(Child::id);
        let before = observe_process(self.child.as_mut());
        let mut exit_code = before.exit_code;
        let mut exit_success = before.exit_success;
        let mut shutdown_outcome = "not_attempted";
        let mut shutdown_status = None;
        let mut force_requested = false;
        let mut outcome = if pid.is_none() {
            "no_process"
        } else if before.state == "exited" {
            "already_exited"
        } else {
            "exit_unconfirmed"
        };
        // A destructor with no process performs no lifecycle action worth a
        // second event. This also avoids recreating removed fixture directories.
        let record = cause != "drop_cleanup" || pid.is_some() || self.validation_child.is_some();
        if record {
            self.record_lifecycle("core_stop", serde_json::json!({
                "reason":reason,"cause":cause,"corePid":pid,"generation":self.recovery_generation,
                "cancellationRevision":self.cancellation.as_ref().map(Cancellation::revision),
                "processBefore":before.state,"processObservation":before,
                "validationPid":self.validation_child.as_ref().map(Child::id),
                "lastHealth":self.diagnostic_health,"lastHealthObservedAtMs":self.diagnostic_health_at,
            }));
        }
        let result: Result<()> = (|| {
            self.stop_reason = Some(reason);
            self.desired_running = false;
            self.restart_at = None;
            self.recovery = None;
            self.initial_health_check = None;
            self.health_blocked = false;
            self.health_deferred = false;
            self.tun_recovery = None;
            if self.child.is_some() {
                if let Some(controller) = &self.controller {
                    if let Ok(client) = http_client() {
                        let sent = client
                            .post(format!("{}/desktop/shutdown", controller.url))
                            .bearer_auth(&controller.secret)
                            .timeout(remaining(deadline)?.min(CONTROL_TIMEOUT))
                            .send();
                        match sent {
                            Ok(response) => {
                                shutdown_status = Some(response.status().as_u16());
                                shutdown_outcome = if response.status().is_success() {
                                    "accepted"
                                } else {
                                    "rejected"
                                };
                            }
                            Err(_) => shutdown_outcome = "transport_error",
                        }
                    } else {
                        shutdown_outcome = "client_unavailable";
                    }
                } else {
                    shutdown_outcome = "controller_unavailable";
                }
                self.record_lifecycle("core_shutdown_request", serde_json::json!({"corePid":pid,"cause":cause,"outcome":shutdown_outcome,"httpStatus":shutdown_status,"durationMs":started.elapsed().as_millis()}));
                let graceful_deadline = (Instant::now() + Duration::from_secs(4)).min(deadline);
                while Instant::now() < graceful_deadline {
                    let exit = self
                        .child
                        .as_mut()
                        .expect("child exists until cleanup")
                        .try_wait()
                        .map_err(|e| AppError::io("无法等待内核退出", e))?;
                    if let Some(exit) = exit {
                        exit_code = exit.code();
                        exit_success = Some(exit.success());
                        if before.state != "exited" {
                            outcome = "exited_during_grace";
                        }
                        self.child = None;
                        break;
                    }
                    thread::sleep(Duration::from_millis(75));
                }
                force_requested = self.child.is_some() || self.validation_child.is_some();
                let cleanup = self.terminate_child_with_evidence(deadline)?;
                force_requested |= cleanup.force_requested;
                if let Some(exit) = cleanup.main_exit {
                    exit_code = exit.code();
                    exit_success = Some(exit.success());
                    outcome = "exit_confirmed_after_force";
                }
            }
            if self.validation_child.is_some() {
                force_requested = true;
                self.terminate_child_with_evidence(deadline)?;
                if pid.is_none() {
                    outcome = "validation_exit_confirmed_after_force";
                }
            }
            self.controller = None;
            self.version = None;
            self.control_state = "unknown".into();
            self.listeners_ready = Some(false);
            self.error = None;
            self.error_code = None;
            self.tun_health = Some("disabled".into());
            self.observed_at = observed_now();
            #[cfg(windows)]
            {
                self.job = None;
            }
            Ok(())
        })();
        if let Err(error) = &result {
            outcome = if error.code == "core_termination_unconfirmed" {
                "exit_unconfirmed"
            } else {
                "error"
            };
        }
        if record {
            self.record_lifecycle("core_stop_result", serde_json::json!({
                "reason":reason,"cause":cause,"corePid":pid,"generation":self.recovery_generation,
                "validationPid":validation_pid,
                "processBefore":before.state,"outcome":outcome,
                "exitConfirmed":self.child.is_none() && self.validation_child.is_none(),
                "forceRequested":force_requested,"exitCode":exit_code,"exitSuccess":exit_success,
                "shutdownOutcome":shutdown_outcome,"shutdownHttpStatus":shutdown_status,
                "durationMs":started.elapsed().as_millis(),"errorCode":result.as_ref().err().map(|error| &error.code),
            }));
        }
        result
    }

    #[cfg(test)]
    fn terminate_child(&mut self) -> Result<()> {
        self.terminate_child_until(Instant::now() + CLEANUP_RESERVE)
    }

    fn terminate_child_until(&mut self, deadline: Instant) -> Result<()> {
        self.terminate_child_with_evidence(deadline).map(|_| ())
    }

    fn terminate_child_with_evidence(&mut self, deadline: Instant) -> Result<CleanupEvidence> {
        let started = Instant::now();
        let main_pid = self.child.as_ref().map(Child::id);
        let validation_pid = self.validation_child.as_ref().map(Child::id);
        let main_before = observe_process(self.child.as_mut());
        let validation_before = observe_process(self.validation_child.as_mut());
        let kill_error = self.child.as_mut().and_then(|child| child.kill().err());
        let validation_kill_error = self
            .validation_child
            .as_mut()
            .and_then(|child| child.kill().err());
        for (role, pid, before, error) in [
            ("main", main_pid, &main_before, &kill_error),
            (
                "validation",
                validation_pid,
                &validation_before,
                &validation_kill_error,
            ),
        ] {
            if let Some(pid) = pid {
                self.record_lifecycle("core_force_stop",serde_json::json!({"corePid":pid,"role":role,"processBefore":before.state,"killRequested":true,"killAccepted":error.is_none(),"osError":error.as_ref().and_then(std::io::Error::raw_os_error)}));
            }
        }
        #[cfg(windows)]
        {
            self.job = None;
        }
        let mut evidence = CleanupEvidence {
            main_exit: None,
            force_requested: main_pid.is_some() || validation_pid.is_some(),
        };
        let result: Result<()> = (|| {
            if let Some(validation) = self.validation_child.as_mut() {
                let exit = confirm_process_exit(validation, deadline, validation_kill_error)?;
                self.record_lifecycle("core_force_stop_result", serde_json::json!({"corePid":validation_pid,"role":"validation","exitConfirmed":true,"exitCode":exit.code(),"exitSuccess":exit.success(),"durationMs":started.elapsed().as_millis()}));
                self.validation_child = None;
            }
            if let Some(child) = self.child.as_mut() {
                let exit = confirm_process_exit(child, deadline, kill_error)?;
                self.record_lifecycle("core_force_stop_result", serde_json::json!({"corePid":main_pid,"role":"main","exitConfirmed":true,"exitCode":exit.code(),"exitSuccess":exit.success(),"durationMs":started.elapsed().as_millis()}));
                evidence.main_exit = Some(exit);
            }
            self.child = None;
            Ok(())
        })();
        if let Err(error) = result {
            // Retain unresolved handles and the original cleanup deadline.
            for (role, pid) in [
                ("main", self.child.as_ref().map(Child::id)),
                ("validation", self.validation_child.as_ref().map(Child::id)),
            ] {
                if let Some(pid) = pid {
                    self.record_lifecycle("core_force_stop_result", serde_json::json!({"corePid":pid,"role":role,"exitConfirmed":false,"durationMs":started.elapsed().as_millis(),"errorCode":error.code}));
                }
            }
            self.termination_unconfirmed = true;
            self.desired_running = false;
            self.restart_at = None;
            self.controller = None;
            self.version = None;
            self.error = Some(error.message.clone());
            self.error_code = Some("core_termination_unconfirmed".into());
            self.observed_at = observed_now();
            return Err(error);
        }
        self.termination_unconfirmed = false;
        Ok(evidence)
    }
}

impl Drop for CoreSupervisor {
    fn drop(&mut self) {
        if self.termination_unconfirmed {
            self.record_lifecycle("core_drop_unconfirmed", serde_json::json!({"corePid":self.child.as_ref().map(Child::id),"validationPid":self.validation_child.as_ref().map(Child::id),"cause":"drop_cleanup","exitConfirmed":false}));
            // The operation already used its cleanup allowance. Releasing the
            // owner must not silently start another stop/cleanup deadline.
            if let Some(child) = self.child.as_mut() {
                let _ = child.kill();
            }
            if let Some(child) = self.validation_child.as_mut() {
                let _ = child.kill();
            }
            #[cfg(windows)]
            {
                self.job = None;
            }
        } else {
            if self
                .stop_for_cause(StopReason::UserStop, "drop_cleanup")
                .is_ok()
            {
                let _ = self.abort_core_update_for_stop();
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::{
        atomic::{AtomicBool, AtomicUsize, Ordering},
        Arc,
    };

    struct UpdateFixture {
        core: CoreSupervisor,
        directory: PathBuf,
    }

    impl UpdateFixture {
        fn committed(owned: bool) -> Self {
            use sha2::{Digest, Sha256};
            let mut random = [0u8; 12];
            getrandom::getrandom(&mut random).unwrap();
            let directory =
                std::env::temp_dir().join(format!("core-update-owner-{}", hex::encode(random)));
            fs::create_dir(&directory).unwrap();
            let executable = directory.join("mihomo-core.exe");
            let candidate = directory.join("candidate.exe");
            fs::write(&executable, b"old core bytes").unwrap();
            fs::write(&candidate, b"new core bytes").unwrap();
            let cancellation = Cancellation::new(Arc::new(std::sync::atomic::AtomicU64::new(7)), 7);
            let replacement = CoreReplacement::install(
                &executable,
                &candidate,
                &hex::encode(Sha256::digest(b"new core bytes")),
                &cancellation,
            )
            .unwrap();
            let mut core =
                CoreSupervisor::new(executable, directory.join("home"), directory.join("logs"));
            core.set_cancellation(cancellation);
            core.pending_core_update = Some(PendingCoreUpdate {
                intent_revision: 7,
                version: "mihomo-optimized-v1.0.30".into(),
                prepared: None,
                replacement: Some(replacement),
                previous_source: Some(config::INITIAL_CONFIG.into()),
                previous_tun: owned,
                was_running: owned,
                stopped_for_update: true,
                installation_started: true,
            });
            core.core_update_status = Some(CoreUpdateStatus {
                phase: "committed".into(),
                version: Some("mihomo-optimized-v1.0.30".into()),
            });
            Self { core, directory }
        }
        fn bytes(&self) -> Vec<u8> {
            fs::read(&self.core.executable).unwrap()
        }
    }

    impl Drop for UpdateFixture {
        fn drop(&mut self) {
            let _ = self
                .core
                .stop_for_cause(StopReason::Shutdown, "test_cleanup");
            if let Some(mut pending) = self.core.pending_core_update.take() {
                let _ = self.core.restore_core_update(&mut pending, false);
            }
            let _ = fs::remove_dir_all(&self.directory);
        }
    }

    #[test]
    fn external_update_stop_retains_backup_until_host_confirms_exit() {
        let mut fixture = UpdateFixture::committed(false);
        fixture.core.stop_for(StopReason::UserStop).unwrap();
        assert_eq!(fixture.bytes(), b"new core bytes");
        assert!(fixture.core.pending_core_update.is_some());
        assert!(!fixture.core.status().desired_running);
        let status = fixture.core.complete_core_update(false, 8).unwrap();
        assert_eq!(fixture.bytes(), b"old core bytes");
        assert_eq!(status.core_update.unwrap().phase, "rolled_back");
        assert!(!status.running && status.pid.is_none());
    }

    #[test]
    fn external_update_cannot_be_accepted_by_a_new_intent() {
        let mut fixture = UpdateFixture::committed(false);
        let error = fixture.core.complete_core_update(true, 8).err().unwrap();
        assert_eq!(error.code, "core_update_not_prepared");
        assert_eq!(fixture.bytes(), b"new core bytes");
        fixture.core.complete_core_update(true, 7).unwrap();
        assert!(fixture.core.pending_core_update.is_none());
        assert_eq!(fixture.bytes(), b"new core bytes");
        assert_eq!(
            fixture.core.status().core_update.unwrap().phase,
            "confirmed"
        );
    }

    #[test]
    fn canceled_update_cannot_restart_or_acknowledge_candidate() {
        let mut fixture = UpdateFixture::committed(false);
        let revision = Arc::new(std::sync::atomic::AtomicU64::new(8));
        fixture
            .core
            .set_cancellation(Cancellation::new(revision.clone(), 7));
        assert_eq!(
            fixture
                .core
                .complete_core_update(true, 7)
                .err()
                .unwrap()
                .code,
            "operation_cancelled"
        );
        fixture.core.stop_for(StopReason::UserStop).unwrap();
        assert_eq!(fixture.bytes(), b"new core bytes");
        fixture
            .core
            .set_cancellation(Cancellation::new(revision, 8));
        fixture.core.complete_core_update(false, 8).unwrap();
        assert_eq!(fixture.bytes(), b"old core bytes");
        assert!(fixture.core.child.is_none() && !fixture.core.desired_running);
    }

    #[test]
    fn external_pending_update_rejects_start_until_explicit_rollback() {
        let mut fixture = UpdateFixture::committed(false);
        assert_eq!(
            fixture
                .core
                .start(config::INITIAL_CONFIG.into(), false)
                .err()
                .unwrap()
                .code,
            "core_update_pending"
        );
        assert_eq!(fixture.bytes(), b"new core bytes");
        assert!(fixture.core.child.is_none());
        fixture.core.complete_core_update(false, 8).unwrap();
        assert_eq!(fixture.bytes(), b"old core bytes");
    }

    #[test]
    fn owned_update_stop_restores_old_file_without_restarting() {
        let mut fixture = UpdateFixture::committed(true);
        let child = quiet_command(&std::env::current_exe().unwrap())
            .args([
                "--ignored",
                "--exact",
                "core::tests::readiness_owned_process",
            ])
            .env("MIHOMO_READINESS_TEST_CHILD", "1")
            .stdin(Stdio::piped())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .unwrap();
        fixture.core.child = Some(child);
        fixture.core.desired_running = true;
        fixture.core.stop_for(StopReason::UserStop).unwrap();
        assert_eq!(fixture.bytes(), b"old core bytes");
        assert!(fixture.core.child.is_none());
        assert!(!fixture.core.desired_running);
        assert!(fixture.core.pending_core_update.is_none());
    }

    #[test]
    fn repeating_completed_rollback_preserves_the_restored_running_core() {
        let mut fixture = UpdateFixture::committed(true);
        let pending = fixture.core.pending_core_update.take().unwrap();
        pending.replacement.as_ref().unwrap().rollback().unwrap();
        fixture.core.core_update_status = Some(CoreUpdateStatus {
            phase: "rolled_back".into(),
            version: None,
        });
        let child = quiet_command(&std::env::current_exe().unwrap())
            .args([
                "--ignored",
                "--exact",
                "core::tests::readiness_owned_process",
            ])
            .env("MIHOMO_READINESS_TEST_CHILD", "1")
            .stdin(Stdio::piped())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .unwrap();
        let pid = child.id();
        fixture.core.child = Some(child);
        fixture.core.desired_running = true;
        for _ in 0..2 {
            let status = fixture.core.complete_core_update(false, 7).unwrap();
            assert_eq!(status.pid, Some(pid));
            assert!(status.running && status.desired_running);
            assert_eq!(fixture.bytes(), b"old core bytes");
        }
    }

    #[derive(Clone, Copy)]
    enum ReadinessScenario {
        DisconnectOnce,
        TimeoutOnce,
        DisconnectAlways,
        PortConflict,
        Unauthorized,
        InvalidJson,
        WrongPid,
        IncompatibleApi,
        HealthHealthy,
        HealthDegraded,
        HealthConflict,
        HealthUnavailable,
        HealthInitialPending,
        HealthStale,
        HealthRecoveryUnavailableOnce,
        HealthBusyOnce,
        HealthBusyUntilReleased,
        HealthNeedsRestart,
        HealthWrongRecoveryPid,
        HealthDisabledAfterRepair,
        AsyncHealthy,
        AsyncPending,
        AsyncLostReply,
        AsyncFailed,
        AsyncWrongTarget,
        AsyncWrongInstance,
        LongApply,
        HealthDisconnect,
        AsyncLostAlways,
    }

    struct ReadinessFixture {
        core: CoreSupervisor,
        controller: Controller,
        source: String,
        config_reads: Arc<AtomicUsize>,
        recovery_reads: Arc<AtomicUsize>,
        recovery_requests: Arc<std::sync::Mutex<Vec<String>>>,
        health_busy: Arc<AtomicBool>,
        health_unavailable: Arc<AtomicBool>,
        health_pending: Arc<AtomicBool>,
        finished: Arc<AtomicBool>,
        server: Option<thread::JoinHandle<()>>,
        _occupied_port: Option<TcpListener>,
    }

    fn configure_readiness_fixture_stream(stream: &std::net::TcpStream) {
        // Windows accept inherits the nonblocking listener mode. HTTP headers
        // can arrive after accept or in fragments, so the fixture must wait for
        // them within its existing timeout instead of discarding WouldBlock.
        stream.set_nonblocking(false).unwrap();
        stream
            .set_read_timeout(Some(Duration::from_secs(1)))
            .unwrap();
        stream
            .set_write_timeout(Some(Duration::from_secs(1)))
            .unwrap();
    }

    impl ReadinessFixture {
        fn new(scenario: ReadinessScenario) -> Self {
            let listener = TcpListener::bind(("127.0.0.1", 0)).unwrap();
            listener.set_nonblocking(true).unwrap();
            let controller = Controller {
                url: format!("http://{}", listener.local_addr().unwrap()),
                secret: "readiness-fixture-secret".into(),
            };
            // A real owned child keeps PID/liveness checks active. Its only input
            // is a pipe owned by this fixture; it cannot launch another process.
            let child = quiet_command(&std::env::current_exe().unwrap())
                .args([
                    "--ignored",
                    "--exact",
                    "core::tests::readiness_owned_process",
                ])
                .env("MIHOMO_READINESS_TEST_CHILD", "1")
                .stdin(Stdio::piped())
                .stdout(Stdio::null())
                .stderr(Stdio::null())
                .spawn()
                .unwrap();
            let pid = child.id();
            let mut core = CoreSupervisor::new(PathBuf::new(), PathBuf::new(), PathBuf::new());
            core.child = Some(child);
            let health_enabled = matches!(
                scenario,
                ReadinessScenario::HealthHealthy
                    | ReadinessScenario::HealthDegraded
                    | ReadinessScenario::HealthConflict
                    | ReadinessScenario::HealthUnavailable
                    | ReadinessScenario::HealthInitialPending
                    | ReadinessScenario::HealthStale
                    | ReadinessScenario::HealthRecoveryUnavailableOnce
                    | ReadinessScenario::HealthBusyOnce
                    | ReadinessScenario::HealthBusyUntilReleased
                    | ReadinessScenario::HealthNeedsRestart
                    | ReadinessScenario::HealthWrongRecoveryPid
                    | ReadinessScenario::HealthDisabledAfterRepair
                    | ReadinessScenario::AsyncHealthy
                    | ReadinessScenario::AsyncPending
                    | ReadinessScenario::AsyncLostReply
                    | ReadinessScenario::AsyncFailed
                    | ReadinessScenario::AsyncWrongTarget
                    | ReadinessScenario::AsyncWrongInstance
                    | ReadinessScenario::HealthDisconnect
                    | ReadinessScenario::AsyncLostAlways
            );
            core.tun = health_enabled;
            core.desired_running = true;
            core.controller = Some(controller.clone());
            let occupied_port = matches!(scenario, ReadinessScenario::PortConflict)
                .then(|| TcpListener::bind(("127.0.0.1", 0)).unwrap());
            let requested_port = occupied_port
                .as_ref()
                .map_or(7890, |socket| socket.local_addr().unwrap().port());
            let actual_port = if let Some(socket) = &occupied_port {
                // Match the core's actual-listener response after a real bind
                // failure: the requested port is nonzero but the listener is 0.
                assert!(TcpListener::bind(socket.local_addr().unwrap()).is_err());
                0
            } else {
                requested_port
            };
            let finished = Arc::new(AtomicBool::new(false));
            let config_reads = Arc::new(AtomicUsize::new(0));
            let recovery_reads = Arc::new(AtomicUsize::new(0));
            let recovery_requests = Arc::new(std::sync::Mutex::new(Vec::<String>::new()));
            let request_ids = recovery_requests.clone();
            let async_recovery = matches!(
                scenario,
                ReadinessScenario::AsyncHealthy
                    | ReadinessScenario::AsyncPending
                    | ReadinessScenario::AsyncLostReply
                    | ReadinessScenario::AsyncFailed
                    | ReadinessScenario::AsyncWrongTarget
                    | ReadinessScenario::AsyncWrongInstance
                    | ReadinessScenario::AsyncLostAlways
            );
            let health_busy = Arc::new(AtomicBool::new(matches!(
                scenario,
                ReadinessScenario::HealthBusyUntilReleased
            )));
            let health_unavailable = Arc::new(AtomicBool::new(matches!(
                scenario,
                ReadinessScenario::HealthUnavailable
            )));
            let stop = finished.clone();
            let health_pending = Arc::new(AtomicBool::new(matches!(
                scenario,
                ReadinessScenario::HealthInitialPending
            )));
            let pending = health_pending.clone();
            let reads = config_reads.clone();
            let recoveries = recovery_reads.clone();
            let busy = health_busy.clone();
            let unavailable = health_unavailable.clone();
            let server = thread::spawn(move || {
                while !stop.load(Ordering::SeqCst) {
                    let (mut stream, _) = match listener.accept() {
                        Ok(connection) => connection,
                        Err(error) if error.kind() == std::io::ErrorKind::WouldBlock => {
                            thread::sleep(Duration::from_millis(5));
                            continue;
                        }
                        Err(error) => panic!("fixture accept failed: {error}"),
                    };
                    configure_readiness_fixture_stream(&stream);
                    let mut request = Vec::new();
                    let mut byte = [0_u8];
                    while request.len() < 4096 && !request.ends_with(b"\r\n\r\n") {
                        if stream.read(&mut byte).ok() != Some(1) {
                            break;
                        }
                        request.push(byte[0]);
                    }
                    // The shared deadline may cancel a connection before its
                    // headers arrive. Only complete HTTP requests authenticate;
                    // an EOF/timeout on a partial request is simply discarded.
                    if !request.ends_with(b"\r\n\r\n") {
                        continue;
                    }
                    let request = String::from_utf8_lossy(&request).to_ascii_lowercase();
                    assert!(request.contains("authorization: bearer readiness-fixture-secret\r\n"));
                    let (status, body) = if request.starts_with("get /desktop ") {
                        let reported_pid = if matches!(scenario, ReadinessScenario::WrongPid) {
                            pid as u64 + 1
                        } else {
                            pid as u64
                        };
                        let api = if matches!(scenario, ReadinessScenario::IncompatibleApi) {
                            2
                        } else {
                            1
                        };
                        (200, serde_json::json!({"managed":true,"api_version":api,"state":"running","pid":reported_pid,"core_instance":"fixture-instance","capabilities":{"tun_health":health_enabled,"tun_repair":health_enabled,"tun_recovery_operations":async_recovery}}).to_string())
                    } else if request.starts_with("get /desktop/health ") {
                        if matches!(scenario, ReadinessScenario::HealthDisconnect) {
                            continue;
                        }
                        assert!(
                            health_enabled,
                            "legacy core must not receive a health request"
                        );
                        let initial_pending = pending.load(Ordering::SeqCst);
                        let stale =
                            initial_pending || matches!(scenario, ReadinessScenario::HealthStale);
                        let state = if unavailable.load(Ordering::SeqCst) || stale {
                            "unavailable"
                        } else {
                            match scenario {
                                ReadinessScenario::HealthConflict => "conflict",
                                ReadinessScenario::HealthDegraded
                                | ReadinessScenario::HealthNeedsRestart
                                | ReadinessScenario::HealthDisabledAfterRepair
                                | ReadinessScenario::AsyncLostReply
                                | ReadinessScenario::AsyncFailed
                                | ReadinessScenario::AsyncWrongTarget
                                | ReadinessScenario::AsyncLostAlways => "degraded",
                                ReadinessScenario::AsyncPending => "unavailable",
                                _ => "healthy",
                            }
                        };
                        if busy.load(Ordering::SeqCst) {
                            (503, "{}".into())
                        } else {
                            let recovery = if matches!(
                                scenario,
                                ReadinessScenario::AsyncPending
                                    | ReadinessScenario::AsyncFailed
                                    | ReadinessScenario::AsyncWrongInstance
                            ) {
                                let pending = matches!(scenario, ReadinessScenario::AsyncPending);
                                serde_json::json!({"id":"op1","request_id":"request1","core_instance":if matches!(scenario, ReadinessScenario::AsyncWrongInstance) { "foreign-instance" } else { "fixture-instance" },"config_generation":1,"tun_generation":1,"phase":if pending {"running"} else {"failed"},"status":if pending {"running"} else {"needs_restart"}})
                            } else {
                                serde_json::Value::Null
                            };
                            (200, serde_json::json!({"managed":true,"api_version":1,"state":"running","pid":pid,"core_instance":"fixture-instance","config_generation":1,"tun_generation":1,"recovery":recovery,"stale":stale,"observed_at":if initial_pending {""} else {"2026-01-01T00:00:00Z"},"tun":{"enabled":true,"state":state,"repairable":state=="degraded","reason":if initial_pending {"TUN health observation is pending"} else if stale {"TUN health observation is stale"} else {""}}}).to_string())
                        }
                    } else if request.starts_with("post /desktop/recovery ") {
                        let length = request
                            .lines()
                            .find_map(|line| {
                                line.strip_prefix("content-length:")
                                    .and_then(|length| length.trim().parse::<usize>().ok())
                            })
                            .unwrap();
                        let mut bytes = vec![0; length];
                        stream.read_exact(&mut bytes).unwrap();
                        let body: serde_json::Value = serde_json::from_slice(&bytes).unwrap();
                        let request_id = body["request_id"].as_str().unwrap().to_string();
                        request_ids.lock().unwrap().push(request_id.clone());
                        let attempt = recoveries.fetch_add(1, Ordering::SeqCst);
                        if matches!(scenario, ReadinessScenario::AsyncLostAlways)
                            || matches!(scenario, ReadinessScenario::AsyncLostReply) && attempt == 0
                        {
                            continue;
                        }
                        (202, serde_json::json!({"operation":{"id":"op1","request_id":request_id,"core_instance":"fixture-instance","config_generation":if matches!(scenario, ReadinessScenario::AsyncWrongTarget) { 99 } else {1},"tun_generation":1,"phase":"running","status":"running"}}).to_string())
                    } else if request.starts_with("get /desktop/recovery/op1 ") {
                        let request_id = request_ids.lock().unwrap().last().unwrap().clone();
                        (200, serde_json::json!({"operation":{"id":"op1","request_id":request_id,"core_instance":"fixture-instance","config_generation":1,"tun_generation":1,"phase":"succeeded","status":"repaired","tun":{"enabled":true,"state":"healthy","repairable":false}}}).to_string())
                    } else if request.starts_with("put /configs?")
                        && matches!(scenario, ReadinessScenario::LongApply)
                    {
                        let until = Instant::now() + Duration::from_secs(5);
                        while !stop.load(Ordering::Acquire) && Instant::now() < until {
                            thread::sleep(Duration::from_millis(10));
                        }
                        (200, "{}".into())
                    } else if request.starts_with("post /desktop/recover ") {
                        assert!(
                            health_enabled,
                            "legacy core must not receive a recovery request"
                        );
                        let attempt = recoveries.fetch_add(1, Ordering::SeqCst);
                        if matches!(scenario, ReadinessScenario::HealthBusyOnce) && attempt == 0 {
                            (503, "{}".into())
                        } else if matches!(
                            scenario,
                            ReadinessScenario::HealthRecoveryUnavailableOnce
                        ) && attempt == 0
                        {
                            (200, serde_json::json!({"pid":pid,"status":"unavailable","tun":{"enabled":true,"state":"unavailable","repairable":false}}).to_string())
                        } else {
                            let status =
                                if matches!(scenario, ReadinessScenario::HealthNeedsRestart) {
                                    "needs_restart"
                                } else {
                                    "repaired"
                                };
                            let reported_pid =
                                if matches!(scenario, ReadinessScenario::HealthWrongRecoveryPid) {
                                    pid + 1
                                } else {
                                    pid
                                };
                            let enabled =
                                !matches!(scenario, ReadinessScenario::HealthDisabledAfterRepair);
                            (200, serde_json::json!({"pid":reported_pid,"status":status,"tun":{"enabled":enabled,"state":if enabled { "healthy" } else { "disabled" },"repairable":false}}).to_string())
                        }
                    } else {
                        assert!(request.starts_with("get /configs "));
                        let attempt = reads.fetch_add(1, Ordering::SeqCst);
                        // After two failed reads, hold /configs across the
                        // parent's remaining deadline. This tests both retries
                        // and timeout clipping without racing the next /desktop.
                        let hold_response = matches!(scenario, ReadinessScenario::TimeoutOnce)
                            && attempt == 0
                            || matches!(scenario, ReadinessScenario::DisconnectAlways)
                                && attempt >= 2;
                        if hold_response {
                            let delayed_until =
                                Instant::now() + CONTROL_TIMEOUT + Duration::from_millis(200);
                            while !stop.load(Ordering::SeqCst) && Instant::now() < delayed_until {
                                thread::sleep(Duration::from_millis(10));
                            }
                            continue;
                        }
                        match scenario {
                            ReadinessScenario::DisconnectAlways => continue,
                            ReadinessScenario::DisconnectOnce if attempt == 0 => continue,
                            ReadinessScenario::Unauthorized => (401, "fixture-secret-must-not-be-displayed".into()),
                            ReadinessScenario::InvalidJson => (200, "fixture-secret-must-not-be-displayed".into()),
                            _ => (200, serde_json::json!({"port":0,"socks-port":0,"mixed-port":actual_port}).to_string()),
                        }
                    };
                    let response = format!("HTTP/1.1 {status} Fixture\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}", body.len());
                    let _ = stream.write_all(response.as_bytes());
                }
            });
            core.source = Some(format!("mixed-port: {requested_port}\n"));
            Self {
                core,
                controller,
                source: format!("mixed-port: {requested_port}\n"),
                config_reads,
                recovery_reads,
                recovery_requests,
                health_busy,
                health_unavailable,
                health_pending,
                finished,
                server: Some(server),
                _occupied_port: occupied_port,
            }
        }

        fn wait(&mut self, timeout: Duration) -> Result<()> {
            self.core
                .wait_ready(&self.controller, &self.source, Instant::now() + timeout)
        }
    }

    impl Drop for ReadinessFixture {
        fn drop(&mut self) {
            self.finished.store(true, Ordering::SeqCst);
            let _ = self.core.terminate_child();
            if let Some(server) = self.server.take() {
                let _ = server.join();
            }
        }
    }

    #[test]
    #[ignore = "owned child fixture, invoked only by the readiness tests"]
    fn readiness_owned_process() {
        if std::env::var_os("MIHOMO_READINESS_TEST_CHILD").is_some() {
            let _ = std::io::stdin().read_to_end(&mut Vec::new());
        }
    }

    fn diagnostic_test_directory(label: &str) -> PathBuf {
        let mut random = [0u8; 8];
        getrandom::getrandom(&mut random).unwrap();
        std::env::temp_dir().join(format!(
            "mihomo-core-diagnostics-{label}-{}",
            hex::encode(random)
        ))
    }

    fn diagnostic_test_child() -> Child {
        quiet_command(&std::env::current_exe().unwrap())
            .args([
                "--ignored",
                "--exact",
                "core::tests::readiness_owned_process",
            ])
            .env("MIHOMO_READINESS_TEST_CHILD", "1")
            .stdin(Stdio::piped())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .unwrap()
    }

    #[derive(Clone, Copy)]
    enum ConfigurationScenario {
        Healthy,
        LostPatchReply,
        IgnoreFirstPatch,
        UnconfirmedPatch,
        HoldPatch,
        HoldRollback,
        RejectFirstPut,
        BindFailure,
        TunNativeBusy,
        LateTunPatch,
    }

    struct ConfigurationFixture {
        core: CoreSupervisor,
        directory: PathBuf,
        requests: Arc<std::sync::Mutex<Vec<String>>>,
        patches: Arc<std::sync::Mutex<Vec<serde_json::Value>>>,
        patch_seen: Arc<AtomicBool>,
        release_patch: Arc<AtomicBool>,
        finished: Arc<AtomicBool>,
        server: Option<thread::JoinHandle<()>>,
    }

    impl ConfigurationFixture {
        fn new(scenario: ConfigurationScenario) -> Self {
            let directory = diagnostic_test_directory("configuration");
            let home = directory.join("runtime");
            fs::create_dir_all(&home).unwrap();
            let listener = TcpListener::bind(("127.0.0.1", 0)).unwrap();
            let port = listener.local_addr().unwrap().port();
            listener.set_nonblocking(true).unwrap();
            let mut child = diagnostic_test_child();
            let pid = child.id();
            let mut child_input = child.stdin.take();
            let source = "mixed-port: 7890\nmode: rule\n".to_string();
            let mut core = CoreSupervisor::new(
                directory.join("no-validator"),
                home.clone(),
                directory.join("logs"),
            );
            core.child = Some(child);
            core.source = Some(source.clone());
            core.desired_running = true;
            core.controller = Some(Controller {
                url: format!("http://127.0.0.1:{port}"),
                secret: "configuration-fixture-secret".into(),
            });
            core.control_state = "ready".into();
            core.listeners_ready = Some(true);
            config::atomic_write(
                &home.join("config.yaml"),
                config::runtime_config(&source, false, port)
                    .unwrap()
                    .as_bytes(),
            )
            .unwrap();
            let requests = Arc::new(std::sync::Mutex::new(Vec::new()));
            let mutations = requests.clone();
            let patches = Arc::new(std::sync::Mutex::new(Vec::new()));
            let patch_bodies = patches.clone();
            let patch_seen = Arc::new(AtomicBool::new(false));
            let seen = patch_seen.clone();
            let release_patch = Arc::new(AtomicBool::new(false));
            let release = release_patch.clone();
            let finished = Arc::new(AtomicBool::new(false));
            let stop = finished.clone();
            let server = thread::spawn(move || {
                let mut actual = serde_json::json!({"port":0,"socks-port":0,"mixed-port":7890,"mode":"rule","allow-lan":false,"log-level":"info","tun":{"enable":false,"device":"","stack":"mixed","dns-mode":"","dns-hijack":["any:53"],"auto-route":true,"auto-detect-interface":true,"inet4-address":["198.18.0.1/30"],"inet6-address":["fdfe:dcba:9876::1/126"],"file-descriptor":0,"include-interface":["Ethernet","Wi-Fi"]}});
                while !stop.load(Ordering::Acquire) {
                    let (mut stream, _) = match listener.accept() {
                        Ok(connection) => connection,
                        Err(error) if error.kind() == std::io::ErrorKind::WouldBlock => {
                            thread::sleep(Duration::from_millis(2));
                            continue;
                        }
                        Err(error) => panic!("configuration fixture accept failed: {error}"),
                    };
                    configure_readiness_fixture_stream(&stream);
                    let mut request = Vec::new();
                    let mut byte = [0u8];
                    while request.len() < 4096 && !request.ends_with(b"\r\n\r\n") {
                        if stream.read(&mut byte).ok() != Some(1) {
                            break;
                        }
                        request.push(byte[0]);
                    }
                    if !request.ends_with(b"\r\n\r\n") {
                        continue;
                    }
                    let request = String::from_utf8(request).unwrap().to_ascii_lowercase();
                    assert!(
                        request.contains("authorization: bearer configuration-fixture-secret\r\n")
                    );
                    let length = request
                        .lines()
                        .find_map(|line| {
                            line.strip_prefix("content-length:")
                                .and_then(|value| value.trim().parse().ok())
                        })
                        .unwrap_or(0);
                    let mut bytes = vec![0u8; length];
                    stream.read_exact(&mut bytes).unwrap();
                    let (status, body) = if request.starts_with("get /desktop ") {
                        (200, serde_json::json!({"managed":true,"api_version":1,"state":"running","pid":pid}).to_string())
                    } else if request.starts_with("get /configs ") {
                        if matches!(
                            scenario,
                            ConfigurationScenario::UnconfirmedPatch
                                | ConfigurationScenario::LateTunPatch
                        ) && !mutations.lock().unwrap().is_empty()
                        {
                            (503, "{}".into())
                        } else {
                            (200, actual.to_string())
                        }
                    } else if request.starts_with("patch /configs ") {
                        let body: serde_json::Value = serde_json::from_slice(&bytes).unwrap();
                        assert!(
                            body.as_object().unwrap().keys().all(|key| [
                                "mode",
                                "allow-lan",
                                "log-level",
                                "port",
                                "socks-port",
                                "mixed-port",
                                "tun"
                            ]
                            .contains(&key.as_str())),
                            "PATCH must not alter unrelated runtime fields"
                        );
                        patch_bodies.lock().unwrap().push(body.clone());
                        let attempt = {
                            let mut requests = mutations.lock().unwrap();
                            requests.push("PATCH".into());
                            requests.len()
                        };
                        if matches!(scenario, ConfigurationScenario::LateTunPatch) && attempt == 1 {
                            seen.store(true, Ordering::Release);
                            let deadline = Instant::now() + Duration::from_secs(5);
                            while !release.load(Ordering::Acquire)
                                && !stop.load(Ordering::Acquire)
                                && Instant::now() < deadline
                            {
                                thread::sleep(Duration::from_millis(2));
                            }
                        }
                        if !matches!(
                            scenario,
                            ConfigurationScenario::IgnoreFirstPatch
                                | ConfigurationScenario::HoldRollback
                                | ConfigurationScenario::TunNativeBusy
                        ) || attempt != 1
                        {
                            for (key, value) in body.as_object().unwrap() {
                                actual[key] = value.clone();
                            }
                        }
                        if matches!(scenario, ConfigurationScenario::BindFailure) {
                            if attempt == 1 {
                                if let Some(port) = body["mixed-port"].as_u64() {
                                    assert!(
                                        TcpListener::bind(("127.0.0.1", port as u16)).is_err(),
                                        "fixture must hold a real conflicting port"
                                    );
                                }
                                actual["mixed-port"] = serde_json::json!(0);
                            } else {
                                actual["mixed-port"] = serde_json::json!(7890);
                            }
                        }
                        if !matches!(scenario, ConfigurationScenario::HoldRollback) || attempt == 2
                        {
                            seen.store(true, Ordering::Release);
                        }
                        if matches!(scenario, ConfigurationScenario::HoldPatch)
                            || matches!(scenario, ConfigurationScenario::HoldRollback)
                                && attempt == 2
                        {
                            let deadline = Instant::now() + Duration::from_secs(5);
                            while !release.load(Ordering::Acquire)
                                && !stop.load(Ordering::Acquire)
                                && Instant::now() < deadline
                            {
                                thread::sleep(Duration::from_millis(2));
                            }
                            continue;
                        }
                        if matches!(scenario, ConfigurationScenario::UnconfirmedPatch)
                            || matches!(scenario, ConfigurationScenario::LostPatchReply)
                                && attempt == 1
                        {
                            continue;
                        }
                        if matches!(scenario, ConfigurationScenario::TunNativeBusy) && attempt == 1
                        {
                            (500, serde_json::json!({"message":"configure tun interface: adapter \"utun\": [wintun stage=create reason=native_busy expected_guid={3c3ae50c-e232-5514-9ea5-79082e75332a}] The object already exists"}).to_string())
                        } else {
                            (204, String::new())
                        }
                    } else if request.starts_with("put /configs?") {
                        let attempt = {
                            let mut requests = mutations.lock().unwrap();
                            requests.push("PUT".into());
                            requests.len()
                        };
                        if matches!(scenario, ConfigurationScenario::RejectFirstPut) && attempt == 1
                        {
                            (400, "{}".into())
                        } else {
                            let body: serde_json::Value = serde_json::from_slice(&bytes).unwrap();
                            let runtime =
                                fs::read_to_string(body["path"].as_str().unwrap()).unwrap();
                            let runtime = config::parse(&runtime).unwrap();
                            actual["mode"] = serde_json::json!(runtime
                                .get("mode")
                                .and_then(serde_yaml::Value::as_str)
                                .unwrap_or("rule"));
                            actual["tun"]["enable"] =
                                serde_json::json!(runtime.get("tun").unwrap()["enable"]
                                    .as_bool()
                                    .unwrap());
                            (204, String::new())
                        }
                    } else if request.starts_with("post /desktop/shutdown ") {
                        child_input.take();
                        (204, String::new())
                    } else {
                        panic!(
                            "unexpected configuration request: {}",
                            request.lines().next().unwrap_or_default()
                        );
                    };
                    let response = format!("HTTP/1.1 {status} Fixture\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}", body.len());
                    let _ = stream.write_all(response.as_bytes());
                }
            });
            Self {
                core,
                directory,
                requests,
                patches,
                patch_seen,
                release_patch,
                finished,
                server: Some(server),
            }
        }

        fn runtime(&self) -> serde_yaml::Mapping {
            config::parse(&fs::read_to_string(self.core.home.join("config.yaml")).unwrap()).unwrap()
        }

        fn tun_source(&mut self) -> String {
            let source = format!(
                "{}dns:\n  enable: true\n",
                self.core.source.as_deref().unwrap()
            );
            let port = self
                .core
                .controller
                .as_ref()
                .unwrap()
                .url
                .rsplit(':')
                .next()
                .unwrap()
                .parse()
                .unwrap();
            config::atomic_write(
                &self.core.home.join("config.yaml"),
                config::runtime_config(&source, self.core.tun, port)
                    .unwrap()
                    .as_bytes(),
            )
            .unwrap();
            self.core.source = Some(source.clone());
            source
        }

        #[cfg(unix)]
        fn allow_validation(&mut self) {
            use std::os::unix::fs::PermissionsExt;
            let executable = self.directory.join("validator");
            fs::write(
                &executable,
                "#!/bin/sh\nprintf 'validated\\n' >> validation-runs\nexit 0\n",
            )
            .unwrap();
            fs::set_permissions(&executable, fs::Permissions::from_mode(0o700)).unwrap();
            self.core.executable = executable;
        }
    }

    impl Drop for ConfigurationFixture {
        fn drop(&mut self) {
            self.release_patch.store(true, Ordering::Release);
            let _ = self.core.stop();
            self.finished.store(true, Ordering::Release);
            if let Some(server) = self.server.take() {
                server.join().unwrap();
            }
            crate::diagnostics::flush(Duration::from_secs(2));
            let _ = fs::remove_dir_all(&self.directory);
        }
    }

    #[test]
    fn scalar_delta_requires_only_valid_whitelisted_semantic_changes() {
        let changed =
            scalar_config_change("mixed-port: 7890\n", "mode: global\nmixed-port: 7890\n")
                .unwrap()
                .unwrap();
        assert_eq!(
            serde_json::Value::Object(changed.before),
            serde_json::json!({"mode":"rule"})
        );
        assert_eq!(
            serde_json::Value::Object(changed.after),
            serde_json::json!({"mode":"global"})
        );
        let changed = scalar_config_change("mixed-port: 7890\n", "# comment\nmixed-port: 7900\nmode: direct\nallow-lan: true\nlog-level: debug\nport: 7888\nsocks-port: 7889\n").unwrap().unwrap();
        assert_eq!(
            serde_json::Value::Object(changed.before),
            serde_json::json!({"mode":"rule","allow-lan":false,"log-level":"info","port":0,"socks-port":0,"mixed-port":7890})
        );
        assert_eq!(
            serde_json::Value::Object(changed.after),
            serde_json::json!({"mode":"direct","allow-lan":true,"log-level":"debug","port":7888,"socks-port":7889,"mixed-port":7900})
        );
        for next in [
            "mode: rule\n",
            "mode: invalid\n",
            "mode: false\n",
            "mode: global\ndns:\n  enable: true\n",
            "mode: global\ntun:\n  enable: true\n",
            "allow-lan: unknown\n",
            "port: 65536\n",
            "log-level: private\n",
            "log-level: null\n",
        ] {
            assert!(scalar_config_change("mode: rule\n", next)
                .unwrap()
                .is_none());
        }
    }

    #[test]
    fn mode_patch_preserves_owned_pid_and_persists_confirmed_runtime_without_validation() {
        for scenario in [
            ConfigurationScenario::Healthy,
            ConfigurationScenario::LostPatchReply,
        ] {
            let mut fixture = ConfigurationFixture::new(scenario);
            let pid = fixture.core.status().pid;
            let source = "mode: global\nmixed-port: 7890\n".to_string();
            let status = fixture.core.reload(source.clone()).unwrap();
            assert!(status.running && status.desired_running && !status.tun);
            assert_eq!(status.pid, pid);
            assert!(status.error_code.is_none());
            assert_eq!(fixture.core.source.as_ref(), Some(&source));
            assert_eq!(
                fixture
                    .runtime()
                    .get("mode")
                    .and_then(serde_yaml::Value::as_str),
                Some("global")
            );
            assert_eq!(*fixture.requests.lock().unwrap(), ["PATCH"]);
        }
    }

    #[test]
    fn all_scalar_settings_can_toggle_repeatedly_without_reload_or_pid_change() {
        let mut fixture = ConfigurationFixture::new(ConfigurationScenario::Healthy);
        let pid = fixture.core.status().pid;
        let original = fixture.core.source.clone().unwrap();
        let changed = "mixed-port: 7900\nmode: global\nallow-lan: true\nlog-level: debug\nport: 7888\nsocks-port: 7889\n";
        for source in [changed, &original, changed, &original] {
            let status = fixture.core.reload(source.into()).unwrap();
            assert_eq!(status.pid, pid);
            assert!(status.running && status.desired_running && status.error_code.is_none());
            assert_eq!(fixture.core.source.as_deref(), Some(source));
        }
        assert_eq!(
            *fixture.requests.lock().unwrap(),
            ["PATCH", "PATCH", "PATCH", "PATCH"]
        );
        assert_eq!(
            fixture
                .runtime()
                .get("mixed-port")
                .and_then(serde_yaml::Value::as_u64),
            Some(7890)
        );
    }

    #[test]
    fn scalar_port_and_lan_changes_require_actual_listeners_even_after_http_success() {
        let occupied = TcpListener::bind(("127.0.0.1", 0)).unwrap();
        let requested = occupied.local_addr().unwrap().port();
        for source in [
            format!("mixed-port: {requested}\nmode: rule\n"),
            "mixed-port: 7890\nmode: rule\nallow-lan: true\n".into(),
        ] {
            let mut fixture = ConfigurationFixture::new(ConfigurationScenario::BindFailure);
            let pid = fixture.core.status().pid;
            let previous = fixture.core.source.clone();
            let error = fixture.core.reload(source).err().unwrap();
            assert_eq!(error.code, "config_patch_failed");
            let status = fixture.core.status();
            assert_eq!(status.pid, pid);
            assert!(status.running && status.desired_running);
            assert_eq!(fixture.core.source, previous);
            assert_eq!(
                fixture
                    .runtime()
                    .get("mixed-port")
                    .and_then(serde_yaml::Value::as_u64),
                Some(7890)
            );
            assert_eq!(*fixture.requests.lock().unwrap(), ["PATCH", "PATCH"]);
        }
    }

    #[test]
    fn mismatched_mode_readback_restores_previous_runtime_and_source() {
        let mut fixture = ConfigurationFixture::new(ConfigurationScenario::IgnoreFirstPatch);
        let pid = fixture.core.status().pid;
        let previous = fixture.core.source.clone();
        let error = fixture
            .core
            .reload("mixed-port: 7890\nmode: global\n".into())
            .err()
            .unwrap();
        assert_eq!(error.code, "config_patch_failed");
        assert_eq!(fixture.core.source, previous);
        assert_eq!(fixture.core.status().pid, pid);
        assert!(fixture.core.status().running && fixture.core.desired_running);
        assert_eq!(
            fixture
                .runtime()
                .get("mode")
                .and_then(serde_yaml::Value::as_str),
            Some("rule")
        );
        assert_eq!(*fixture.requests.lock().unwrap(), ["PATCH", "PATCH"]);
    }

    #[test]
    fn unconfirmed_mode_mutation_stops_owned_process_before_returning_old_source() {
        let mut fixture = ConfigurationFixture::new(ConfigurationScenario::UnconfirmedPatch);
        let previous = fixture.core.source.clone();
        let error = fixture
            .core
            .reload("mixed-port: 7890\nmode: global\n".into())
            .err()
            .unwrap();
        assert_eq!(error.code, "config_patch_unconfirmed");
        assert!(fixture.core.child.is_none());
        assert!(!fixture.core.desired_running && !fixture.core.termination_unconfirmed);
        assert_eq!(fixture.core.source, previous);
        assert_eq!(
            fixture
                .runtime()
                .get("mode")
                .and_then(serde_yaml::Value::as_str),
            Some("rule")
        );
        assert_eq!(
            *fixture.requests.lock().unwrap(),
            ["PATCH"],
            "unknown mutation must not race a rollback PATCH"
        );
    }

    #[test]
    fn stop_cancels_mode_transport_and_confirms_exit_without_committing_target() {
        for scenario in [
            ConfigurationScenario::HoldPatch,
            ConfigurationScenario::HoldRollback,
        ] {
            let mut fixture = ConfigurationFixture::new(scenario);
            let revision = Arc::new(std::sync::atomic::AtomicU64::new(1));
            fixture.core.cancellation = Some(Cancellation::new(revision.clone(), 1));
            let seen = fixture.patch_seen.clone();
            let release = fixture.release_patch.clone();
            let canceller = thread::spawn(move || {
                let deadline = Instant::now() + Duration::from_secs(5);
                while !seen.load(Ordering::Acquire) && Instant::now() < deadline {
                    thread::sleep(Duration::from_millis(2));
                }
                let accepted = seen.load(Ordering::Acquire);
                revision.store(2, Ordering::Release);
                release.store(true, Ordering::Release);
                assert!(
                    accepted,
                    "cancellation is synchronized after the core accepts PATCH"
                );
            });
            let previous = fixture.core.source.clone();
            let started = Instant::now();
            let error = fixture
                .core
                .reload("mixed-port: 7890\nmode: global\n".into())
                .err()
                .unwrap();
            canceller.join().unwrap();
            assert_eq!(error.code, "operation_cancelled");
            assert!(started.elapsed() < Duration::from_secs(2));
            assert!(fixture.core.child.is_none() && !fixture.core.desired_running);
            assert_eq!(fixture.core.source, previous);
            let expected = if matches!(scenario, ConfigurationScenario::HoldRollback) {
                vec!["PATCH", "PATCH"]
            } else {
                vec!["PATCH"]
            };
            assert_eq!(*fixture.requests.lock().unwrap(), expected);
            assert_eq!(
                fixture
                    .runtime()
                    .get("mode")
                    .and_then(serde_yaml::Value::as_str),
                Some("rule")
            );
        }
    }

    #[test]
    fn tun_only_patch_uses_complete_parsed_parameters_and_preserves_pid() {
        let mut fixture = ConfigurationFixture::new(ConfigurationScenario::Healthy);
        let source = fixture.tun_source();
        let pid = fixture.core.status().pid;
        for enabled in [true, false, true] {
            let status = fixture.core.reconfigure(source.clone(), enabled).unwrap();
            assert_eq!(status.pid, pid);
            assert_eq!(status.tun, enabled);
            assert!(
                status.running && status.error.is_none() && status.last_operation_failure.is_none()
            );
            assert_eq!(fixture.runtime()["tun"]["enable"].as_bool(), Some(enabled));
        }
        assert_eq!(
            *fixture.requests.lock().unwrap(),
            ["PATCH", "PATCH", "PATCH"]
        );
        let patches = fixture.patches.lock().unwrap();
        for patch in patches.iter() {
            assert_eq!(patch.as_object().unwrap().len(), 1);
            assert_eq!(patch["tun"]["device"], "");
            assert_eq!(patch["tun"]["stack"], "mixed");
            assert_eq!(patch["tun"]["auto-route"], true);
            assert_eq!(patch["tun"]["auto-detect-interface"], true);
            assert_eq!(patch["tun"]["dns-hijack"], serde_json::json!(["any:53"]));
            assert_eq!(
                patch["tun"]["inet4-address"],
                serde_json::json!(["198.18.0.1/30"])
            );
            assert_eq!(
                patch["tun"]["inet6-address"],
                serde_json::json!(["fdfe:dcba:9876::1/126"])
            );
            assert_eq!(
                patch["tun"]["include-interface"],
                serde_json::json!(["Ethernet", "Wi-Fi"])
            );
        }
    }

    #[test]
    fn failed_tun_patch_keeps_healthy_runtime_and_retains_the_unmet_request() {
        let mut fixture = ConfigurationFixture::new(ConfigurationScenario::TunNativeBusy);
        let source = fixture.tun_source();
        let pid = fixture.core.status().pid;
        fixture.core.set_cancellation_for_intent(
            Cancellation::new(Arc::new(std::sync::atomic::AtomicU64::new(29)), 29),
            4,
        );
        let error = fixture
            .core
            .reconfigure(source.clone(), true)
            .err()
            .unwrap();
        assert_eq!(error.code, "tun_device_busy");
        let status = fixture.core.status();
        assert_eq!(status.pid, pid);
        assert!(!status.tun && status.running);
        assert!(status.error.is_none() && status.error_code.is_none());
        let failure = status.last_operation_failure.unwrap();
        assert_eq!(
            failure.intent_revision, 4,
            "worker cancellation revisions must not leak into host outcomes"
        );
        assert_eq!(failure.operation, "tun_change");
        assert!(failure.requested_tun && !failure.applied_tun && failure.occurred_at > 0);
        assert_eq!(fixture.runtime()["tun"]["enable"].as_bool(), Some(false));
        assert_eq!(*fixture.requests.lock().unwrap(), ["PATCH", "PATCH"]);
        fixture.core.poll().unwrap();
        assert_eq!(
            fixture.core.status().last_operation_failure,
            Some(failure.clone())
        );
        let changed = source.replace("mode: rule", "mode: global");
        let updated = fixture.core.reconfigure(changed.clone(), false).unwrap();
        assert_eq!(updated.last_operation_failure, Some(failure));
        let confirmed = fixture
            .core
            .reconfigure_request(changed.clone(), false, true)
            .unwrap();
        assert!(
            confirmed.last_operation_failure.is_none(),
            "explicitly confirming disabled acknowledges the failed switch"
        );
        assert_eq!(
            *fixture.requests.lock().unwrap(),
            ["PATCH", "PATCH", "PATCH"]
        );
        let enabled = fixture.core.reconfigure(changed, true).unwrap();
        assert!(enabled.tun && enabled.last_operation_failure.is_none());
        assert_eq!(enabled.pid, pid);
    }

    #[test]
    #[cfg(unix)]
    fn unchanged_config_reload_does_not_acknowledge_a_failed_tun_request() {
        let mut fixture = ConfigurationFixture::new(ConfigurationScenario::TunNativeBusy);
        fixture.allow_validation();
        let source = fixture.tun_source();
        assert_eq!(
            fixture
                .core
                .reconfigure_request(source.clone(), true, true)
                .err()
                .unwrap()
                .code,
            "tun_device_busy"
        );
        let failure = fixture.core.status().last_operation_failure;
        let unchanged = fixture.core.reload(source).unwrap();
        assert_eq!(unchanged.last_operation_failure, failure);
        assert!(unchanged.running && !unchanged.tun && unchanged.error_code.is_none());
        assert_eq!(*fixture.requests.lock().unwrap(), ["PATCH", "PATCH", "PUT"]);
    }

    #[test]
    fn mismatched_tun_readback_rolls_back_the_complete_old_block() {
        let mut fixture = ConfigurationFixture::new(ConfigurationScenario::IgnoreFirstPatch);
        let source = fixture.tun_source();
        let pid = fixture.core.status().pid;
        let error = fixture
            .core
            .reconfigure(source.clone(), true)
            .err()
            .unwrap();
        assert_eq!(error.code, "config_patch_readback_mismatch");
        assert_eq!(fixture.core.status().pid, pid);
        assert!(fixture.core.status().running && !fixture.core.tun);
        let patches = fixture.patches.lock().unwrap();
        assert_eq!(patches.len(), 2);
        let mut requested = patches[0].clone();
        requested["tun"]["enable"] = serde_json::json!(false);
        assert_eq!(patches[1], requested);
        assert_eq!(fixture.runtime()["tun"]["enable"].as_bool(), Some(false));
    }

    #[test]
    fn lost_tun_patch_reply_requires_exit_even_when_target_readback_would_match() {
        let mut fixture = ConfigurationFixture::new(ConfigurationScenario::LostPatchReply);
        let source = fixture.tun_source();
        let error = fixture.core.reconfigure(source, true).err().unwrap();
        assert_eq!(error.code, "tun_patch_unconfirmed");
        assert!(fixture.core.child.is_none() && !fixture.core.desired_running);
        assert_eq!(
            *fixture.requests.lock().unwrap(),
            ["PATCH"],
            "an unacknowledged native operation must not race rollback"
        );
        assert_eq!(fixture.runtime()["tun"]["enable"].as_bool(), Some(false));
    }

    #[test]
    fn cancelling_tun_patch_or_rollback_confirms_exit_before_a_successor() {
        for scenario in [
            ConfigurationScenario::LateTunPatch,
            ConfigurationScenario::HoldRollback,
        ] {
            let mut fixture = ConfigurationFixture::new(scenario);
            let source = fixture.tun_source();
            let revision = Arc::new(std::sync::atomic::AtomicU64::new(1));
            fixture
                .core
                .set_cancellation(Cancellation::new(revision.clone(), 1));
            let seen = fixture.patch_seen.clone();
            let release = fixture.release_patch.clone();
            let canceller = thread::spawn(move || {
                let deadline = Instant::now() + Duration::from_secs(5);
                while !seen.load(Ordering::Acquire) && Instant::now() < deadline {
                    thread::sleep(Duration::from_millis(2));
                }
                assert!(seen.load(Ordering::Acquire));
                revision.store(2, Ordering::Release);
                release.store(true, Ordering::Release);
            });
            let error = fixture
                .core
                .reconfigure(source.clone(), true)
                .err()
                .unwrap();
            canceller.join().unwrap();
            assert_eq!(error.code, "operation_cancelled");
            assert!(fixture.core.child.is_none() && !fixture.core.desired_running);
            assert!(!fixture.core.tun && fixture.core.status().last_operation_failure.is_none());
            assert_eq!(fixture.core.source.as_deref(), Some(source.as_str()));
            assert_eq!(fixture.runtime()["tun"]["enable"].as_bool(), Some(false));
            assert_eq!(
                fixture.requests.lock().unwrap().len(),
                if matches!(scenario, ConfigurationScenario::HoldRollback) {
                    2
                } else {
                    1
                }
            );
        }
    }

    #[test]
    fn unrelated_source_or_tun_changes_keep_the_full_validation_path() {
        for (source, tun) in [
            (
                "mixed-port: 7890\nmode: global\ndns:\n  enable: true\n",
                false,
            ),
            ("mixed-port: 7890\nmode: global\n", true),
        ] {
            let mut fixture = ConfigurationFixture::new(ConfigurationScenario::Healthy);
            let previous = fixture.core.source.clone();
            let pid = fixture.core.status().pid;
            assert!(
                fixture.core.reconfigure(source.into(), tun).is_err(),
                "missing validator must fail before a full reload"
            );
            assert!(fixture.requests.lock().unwrap().is_empty());
            assert_eq!(fixture.core.source, previous);
            assert_eq!(fixture.core.status().pid, pid);
            assert!(!fixture.core.tun);
            assert_eq!(
                fixture
                    .runtime()
                    .get("mode")
                    .and_then(serde_yaml::Value::as_str),
                Some("rule")
            );
        }
    }

    #[test]
    #[cfg(unix)]
    fn source_changes_use_full_apply_then_tun_only_switch_uses_patch() {
        let mut fixture = ConfigurationFixture::new(ConfigurationScenario::Healthy);
        fixture.allow_validation();
        let pid = fixture.core.status().pid;
        let source = format!(
            "{}dns:\n  enable: true\n",
            fixture.core.source.clone().unwrap()
        );
        let enabled = fixture.core.reconfigure(source.clone(), true).unwrap();
        assert_eq!(enabled.pid, pid);
        assert!(enabled.tun && enabled.running && enabled.desired_running);
        assert_eq!(enabled.tun_health.as_deref(), Some("checking"));
        let disabled = fixture.core.reconfigure(source, false).unwrap();
        assert_eq!(disabled.pid, pid);
        assert!(!disabled.tun && disabled.running && disabled.desired_running);
        assert_eq!(disabled.tun_health.as_deref(), Some("disabled"));
        assert_eq!(
            fixture.runtime().get("tun").unwrap()["enable"].as_bool(),
            Some(false)
        );
        assert_eq!(*fixture.requests.lock().unwrap(), ["PUT", "PATCH"]);
        assert_eq!(
            fs::read_to_string(fixture.core.home.join("validation-runs"))
                .unwrap()
                .lines()
                .count(),
            1
        );
    }

    #[test]
    #[cfg(unix)]
    fn failed_tun_reconfiguration_rolls_back_mode_file_and_runtime_intent() {
        let mut fixture = ConfigurationFixture::new(ConfigurationScenario::RejectFirstPut);
        fixture.allow_validation();
        let pid = fixture.core.status().pid;
        let previous = fixture.core.source.clone();
        let error = fixture
            .core
            .reconfigure(
                "mixed-port: 7890\nmode: global\ndns:\n  enable: true\n".into(),
                true,
            )
            .err()
            .unwrap();
        assert_eq!(error.code, "reload_failed");
        assert_eq!(fixture.core.source, previous);
        let status = fixture.core.status();
        assert_eq!(status.pid, pid);
        assert!(!status.tun && status.running && status.desired_running);
        let runtime = fixture.runtime();
        assert_eq!(
            runtime.get("mode").and_then(serde_yaml::Value::as_str),
            Some("rule")
        );
        assert_eq!(runtime.get("tun").unwrap()["enable"].as_bool(), Some(false));
        assert_eq!(*fixture.requests.lock().unwrap(), ["PUT", "PUT"]);
    }

    fn diagnostic_events(directory: &Path) -> Vec<serde_json::Value> {
        assert!(crate::diagnostics::flush(Duration::from_secs(2)));
        fs::read_to_string(directory.join("recovery.log"))
            .unwrap()
            .lines()
            .map(|line| serde_json::from_str(line).unwrap())
            .collect()
    }

    #[test]
    fn stop_diagnostics_distinguish_observed_exit_grace_and_confirmed_force() {
        for mode in [
            "already_exited",
            "exited_during_grace",
            "exit_confirmed_after_force",
        ] {
            let directory = diagnostic_test_directory(mode);
            let mut core = CoreSupervisor::new(PathBuf::new(), PathBuf::new(), directory.clone());
            let mut child = diagnostic_test_child();
            let pid = child.id();
            let closing = match mode {
                "already_exited" => {
                    drop(child.stdin.take());
                    child.wait().unwrap();
                    None
                }
                "exited_during_grace" => {
                    let input = child.stdin.take();
                    Some(thread::spawn(move || {
                        thread::sleep(Duration::from_millis(150));
                        drop(input);
                    }))
                }
                _ => None,
            };
            core.child = Some(child);
            let _scope = crate::diagnostics::scope(
                serde_json::json!({"operationId":"fixture-stop","cause":"automatic_host_recovery"}),
            );
            core.stop_for_cause(StopReason::UserStop, "start_replace")
                .unwrap();
            if let Some(closing) = closing {
                closing.join().unwrap();
            }
            assert!(core.child.is_none());
            drop(core);
            let events = diagnostic_events(&directory);
            let began = events
                .iter()
                .find(|event| event["event"] == "core_stop")
                .unwrap();
            let ended = events
                .iter()
                .find(|event| event["event"] == "core_stop_result")
                .unwrap();
            assert_eq!(began["data"]["corePid"], pid);
            assert_eq!(
                began["data"]["processBefore"],
                if mode == "already_exited" {
                    "exited"
                } else {
                    "alive"
                }
            );
            assert_eq!(ended["data"]["cause"], "start_replace");
            assert_eq!(ended["context"]["cause"], "automatic_host_recovery");
            assert_eq!(ended["context"]["operationId"], "fixture-stop");
            assert_eq!(ended["data"]["outcome"], mode);
            assert_eq!(ended["data"]["exitConfirmed"], true);
            assert_eq!(
                ended["data"]["forceRequested"],
                mode == "exit_confirmed_after_force"
            );
            assert!(ended["data"]["errorCode"].is_null());
            if mode != "exit_confirmed_after_force" {
                assert_eq!(ended["data"]["exitCode"], 0);
            }
            fs::remove_dir_all(directory).unwrap();
        }
    }

    #[test]
    fn unavailable_diagnostic_destination_cannot_fail_an_explicit_stop() {
        let directory = diagnostic_test_directory("unwritable");
        fs::write(&directory, b"not a directory").unwrap();
        let mut core = CoreSupervisor::new(PathBuf::new(), PathBuf::new(), directory.clone());
        core.desired_running = true;
        core.restart_at = Some(Instant::now());
        core.stop().unwrap();
        assert!(!core.desired_running && core.restart_at.is_none());
        drop(core);
        assert!(crate::diagnostics::flush(Duration::from_secs(2)));
        fs::remove_file(directory).unwrap();
    }

    #[test]
    fn health_diagnostics_keep_identity_and_fixed_reason_without_raw_errors_or_duplicate_polls() {
        let directory = diagnostic_test_directory("health");
        let mut core = CoreSupervisor::new(PathBuf::new(), PathBuf::new(), directory.clone());
        let mut health = serde_json::json!({
            "core_instance":"instance-1","config_generation":2,"tun_generation":3,"creation_count":1,
            "stale":false,"tun":{"enabled":true,"state":"unavailable","device":"MihomoDesktop","index":42,
                "identity":"windows-luid:1234","reason":"inspect TUN routes: https://private.example/token-secret","repairable":false},
            "secret":"must-never-appear","config":"subscription-secret",
        });
        core.record_health_observation(&health);
        core.record_health_observation(&health);
        health["tun"]["state"] = "healthy".into();
        health["tun"]["reason"] = "".into();
        core.record_health_observation(&health);
        drop(core);
        let events = diagnostic_events(&directory);
        assert_eq!(events.len(), 2);
        assert_eq!(
            events[0]["data"]["tun"]["reasonCode"],
            "route_inspection_failed"
        );
        assert_eq!(events[0]["data"]["tun"]["identity"], "windows-luid:1234");
        assert_eq!(events[0]["data"]["creationCount"], 1);
        let text = serde_json::to_string(&events).unwrap();
        for secret in [
            "private.example",
            "token-secret",
            "must-never-appear",
            "subscription-secret",
        ] {
            assert!(!text.contains(secret));
        }
        fs::remove_dir_all(directory).unwrap();
    }

    #[test]
    fn readiness_fixture_waits_for_delayed_header_fragments() {
        use std::sync::mpsc;

        let listener = TcpListener::bind(("127.0.0.1", 0)).unwrap();
        let mut client = std::net::TcpStream::connect(listener.local_addr().unwrap()).unwrap();
        let (mut stream, _) = listener.accept().unwrap();
        // Model Windows' accepted mode on every platform, then use the exact
        // configuration applied by ReadinessFixture to its real HTTP sockets.
        stream.set_nonblocking(true).unwrap();
        let mut empty = [0_u8];
        assert_eq!(
            stream.read(&mut empty).unwrap_err().kind(),
            std::io::ErrorKind::WouldBlock
        );
        configure_readiness_fixture_stream(&stream);
        let prefix = b"GET /desktop/health HTTP/1.1\r\nAuthorization: Bearer ";
        let suffix = b"readiness-fixture-secret\r\n\r\n";
        client.write_all(prefix).unwrap();
        let (prefix_sent, prefix_read) = mpsc::channel();
        let (finished, received) = mpsc::channel();
        let server = thread::spawn(move || {
            let mut headers = vec![0; prefix.len()];
            stream.read_exact(&mut headers).unwrap();
            prefix_sent.send(()).unwrap();
            let mut tail = vec![0; suffix.len()];
            let result = stream.read_exact(&mut tail).map(|()| {
                headers.extend_from_slice(&tail);
                headers
            });
            let _ = finished.send(result);
        });
        prefix_read.recv_timeout(Duration::from_secs(1)).unwrap();
        // Only after the server has consumed the first fragment do we delay
        // the rest. A nonblocking read would already have returned WouldBlock.
        let pending = received.recv_timeout(Duration::from_millis(50));
        assert!(
            matches!(&pending, Err(mpsc::RecvTimeoutError::Timeout)),
            "fixture discarded a delayed header fragment: {pending:?}"
        );
        client.write_all(suffix).unwrap();
        let headers = received
            .recv_timeout(Duration::from_secs(1))
            .unwrap()
            .unwrap();
        assert_eq!(headers, [prefix.as_slice(), suffix.as_slice()].concat());
        server.join().unwrap();
    }

    #[test]
    fn readiness_retries_one_listener_disconnect_without_restarting() {
        let mut fixture = ReadinessFixture::new(ReadinessScenario::DisconnectOnce);
        let original_pid = fixture.core.child.as_ref().unwrap().id();
        fixture.wait(Duration::from_secs(2)).unwrap();
        assert_eq!(fixture.config_reads.load(Ordering::SeqCst), 2);
        assert_eq!(fixture.core.child.as_ref().unwrap().id(), original_pid);
    }

    #[test]
    fn initial_tun_observation_runs_on_next_poll_without_blocking_ready_state() {
        let mut fixture = ReadinessFixture::new(ReadinessScenario::HealthInitialPending);
        let pid = fixture.core.status().pid;
        fixture.core.last_health_check = lifecycle::awake_now().unwrap();
        fixture.core.begin_health_observation();
        let status = fixture.core.poll().unwrap();
        assert_eq!(status.tun_health.as_deref(), Some("checking"));
        assert!(status.running && !status.recovering && status.error_code.is_none());
        assert!(
            fixture.core.diagnostic_health.is_some(),
            "first poll must sample without waiting ten seconds"
        );
        assert!(fixture.core.recovery.is_none());
        fixture.health_pending.store(false, Ordering::SeqCst);
        let ready = fixture.core.poll().unwrap();
        assert_eq!(ready.tun_health.as_deref(), Some("healthy"));
        assert_eq!(ready.pid, pid);
        assert!(ready.running && ready.error.is_none());
        assert!(fixture.core.initial_health_check.is_none());
        assert_eq!(fixture.recovery_reads.load(Ordering::SeqCst), 0);
    }

    #[test]
    fn initial_tun_observation_exhausts_without_restarting_and_can_recover() {
        let mut fixture = ReadinessFixture::new(ReadinessScenario::HealthInitialPending);
        let pid = fixture.core.status().pid;
        fixture.core.begin_health_observation();
        for _ in 0..3 {
            fixture.core.poll().unwrap();
        }
        let status = fixture.core.status();
        assert_eq!(status.pid, pid);
        assert_eq!(status.tun_health.as_deref(), Some("unavailable"));
        assert_eq!(status.error_code.as_deref(), Some("tun_health_unavailable"));
        assert!(fixture.core.health_deferred && fixture.core.recovery.is_none());
        assert!(fixture.core.initial_health_check.is_none() && fixture.core.restart_at.is_none());
        fixture.health_pending.store(false, Ordering::SeqCst);
        fixture
            .core
            .observe_health(lifecycle::awake_now().unwrap())
            .unwrap();
        let ready = fixture.core.status();
        assert!(ready.running && ready.error.is_none() && ready.error_code.is_none());
        assert_eq!(ready.pid, pid);
    }

    #[test]
    fn initial_tun_grace_never_hides_a_previously_observed_stale_snapshot() {
        let mut fixture = ReadinessFixture::new(ReadinessScenario::HealthStale);
        let pid = fixture.core.status().pid;
        fixture.core.begin_health_observation();
        let status = fixture.core.poll().unwrap();
        assert_eq!(status.pid, pid);
        assert_eq!(status.tun_health.as_deref(), Some("unavailable"));
        assert_eq!(status.error_code.as_deref(), Some("tun_health_unavailable"));
        assert!(fixture.core.initial_health_check.is_none());
        assert!(fixture.core.restart_at.is_none());
    }

    #[test]
    fn unconfirmed_validation_ownership_blocks_replacement_until_stop_reaps_it() {
        let mut fixture = ReadinessFixture::new(ReadinessScenario::HealthHealthy);
        let child = fixture.core.child.take().unwrap();
        let pid = child.id();
        fixture.core.retain_validation_child(
            child,
            &AppError::new(
                "core_termination_unconfirmed",
                "fixture confirmation failure",
            ),
        );
        let budget = OperationBudget::new(&fixture.source).unwrap();
        let error = fixture
            .core
            .validate(&fixture.controller, &budget)
            .unwrap_err();
        assert_eq!(error.code, "core_termination_unconfirmed");
        assert_eq!(fixture.core.status().pid, Some(pid));
        assert_eq!(fixture.core.status().process_state, "unknown");
        fixture.core.stop().unwrap();
        assert!(fixture.core.validation_child.is_none());
        assert!(!fixture.core.termination_unconfirmed);
        assert!(fixture.core.status().pid.is_none());
    }

    #[test]
    fn health_and_recovery_transport_uncertainty_after_identity_stays_observation_only() {
        for scenario in [
            ReadinessScenario::HealthDisconnect,
            ReadinessScenario::AsyncLostAlways,
        ] {
            let mut fixture = ReadinessFixture::new(scenario);
            let pid = fixture.core.child.as_ref().unwrap().id();
            for now in [1, 2, 3] {
                fixture
                    .core
                    .observe_health(Duration::from_secs(now))
                    .unwrap();
            }
            assert_eq!(fixture.core.status().pid, Some(pid));
            assert_eq!(
                fixture.core.error_code.as_deref(),
                Some("core_observation_busy")
            );
            assert!(fixture.core.restart_at.is_none());
            assert!(fixture.core.restarts.is_empty());
            let requests = fixture.recovery_requests.lock().unwrap();
            assert!(requests.windows(2).all(|pair| pair[0] == pair[1]));
        }
    }

    #[test]
    fn responsive_identity_and_slow_configs_never_spend_restart_budget() {
        let mut fixture = ReadinessFixture::new(ReadinessScenario::DisconnectAlways);
        let pid = fixture.core.child.as_ref().unwrap().id();
        for now in [1, 2, 3] {
            fixture
                .core
                .observe_health(Duration::from_secs(now))
                .unwrap();
        }
        let status = fixture.core.status();
        assert_eq!(status.pid, Some(pid));
        assert_eq!(status.process_state, "alive");
        assert_eq!(status.error_code.as_deref(), Some("core_observation_busy"));
        assert!(fixture.core.restart_at.is_none());
        assert!(fixture.core.restarts.is_empty());
    }

    #[test]
    fn persistent_local_identity_transport_failure_schedules_one_bounded_restart() {
        let mut fixture = ReadinessFixture::new(ReadinessScenario::HealthHealthy);
        let unused = TcpListener::bind(("127.0.0.1", 0)).unwrap();
        fixture.core.controller = Some(Controller {
            url: format!("http://{}", unused.local_addr().unwrap()),
            secret: "unused".into(),
        });
        drop(unused);
        for now in [1, 2, 3] {
            fixture
                .core
                .observe_health(Duration::from_secs(now))
                .unwrap();
        }
        assert!(fixture.core.child.is_none());
        assert_eq!(fixture.core.stop_reason, Some(StopReason::HealthFailure));
        assert!(fixture.core.restart_at.is_some());
        fixture.core.stop().unwrap();
        fixture.core.request_reconcile(99).unwrap();
        assert!(!fixture.core.desired_running);
        assert!(fixture.core.restart_at.is_none());
    }

    #[test]
    fn disproved_controller_identity_withdraws_the_control_capability() {
        let mut fixture = ReadinessFixture::new(ReadinessScenario::WrongPid);
        fixture.core.control_state = "ready".into();
        fixture.core.observe_health(Duration::ZERO).unwrap();
        assert_eq!(fixture.core.status().control_state, "unreachable");
        assert!(fixture.core.status().controller.is_none());
        assert!(fixture.core.child.is_some());
    }

    #[test]
    fn operation_capable_healthy_resume_does_not_mutate_tun() {
        let mut fixture = ReadinessFixture::new(ReadinessScenario::AsyncHealthy);
        let pid = fixture.core.child.as_ref().unwrap().id();
        fixture.core.request_reconcile(7).unwrap();
        let status = fixture.core.poll().unwrap();
        assert!(status.running);
        assert_eq!(status.pid, Some(pid));
        assert_eq!(fixture.recovery_reads.load(Ordering::Acquire), 0);
    }

    #[test]
    fn pending_tun_operation_bypasses_configs_without_spending_restart_budget() {
        let mut fixture = ReadinessFixture::new(ReadinessScenario::AsyncPending);
        let pid = fixture.core.child.as_ref().unwrap().id();
        for now in [1, 2, 21, 31] {
            fixture
                .core
                .observe_health(Duration::from_secs(now))
                .unwrap();
        }
        let status = fixture.core.status();
        assert_eq!(status.pid, Some(pid));
        assert_eq!(status.process_state, "alive");
        assert_eq!(status.operation.as_deref(), Some("recovering_tun"));
        assert_eq!(fixture.config_reads.load(Ordering::Acquire), 0);
        assert!(fixture.core.restarts.is_empty());
        assert!(fixture.core.restart_at.is_none());
    }

    #[test]
    fn lost_recovery_acceptance_reuses_request_and_queries_owned_operation() {
        let mut fixture = ReadinessFixture::new(ReadinessScenario::AsyncLostReply);
        let pid = fixture.core.child.as_ref().unwrap().id();
        assert!(matches!(
            fixture.core.check_local_health(false),
            Err(ReadinessFailure::Temporary(_))
        ));
        assert!(matches!(
            fixture.core.check_local_health(false),
            Err(ReadinessFailure::Temporary(_))
        ));
        assert!(matches!(
            fixture.core.check_local_health(false),
            Ok(HealthCheck::Ready)
        ));
        let requests = fixture.recovery_requests.lock().unwrap();
        assert_eq!(requests.len(), 2);
        assert_eq!(
            requests[0], requests[1],
            "an uncertain acceptance must retain its idempotency key"
        );
        assert_eq!(fixture.core.child.as_ref().unwrap().id(), pid);
        assert_eq!(fixture.config_reads.load(Ordering::Acquire), 1);
    }

    #[test]
    fn durable_failed_repair_is_observed_without_reposting_or_masking_failure() {
        let mut fixture = ReadinessFixture::new(ReadinessScenario::AsyncFailed);
        for _ in 0..2 {
            assert!(matches!(
                fixture.core.check_local_health(false),
                Ok(HealthCheck::NeedsRestart)
            ));
        }
        assert_eq!(fixture.recovery_reads.load(Ordering::Acquire), 0);
        assert_eq!(fixture.config_reads.load(Ordering::Acquire), 0);
    }

    #[test]
    fn recovery_results_must_belong_to_the_owned_instance_and_target() {
        for scenario in [
            ReadinessScenario::AsyncWrongTarget,
            ReadinessScenario::AsyncWrongInstance,
        ] {
            let mut fixture = ReadinessFixture::new(scenario);
            assert!(matches!(
                fixture.core.check_local_health(false),
                Err(ReadinessFailure::Fatal(_))
            ));
            assert!(fixture.core.restarts.is_empty());
        }
    }

    #[test]
    fn cancelled_reload_drops_its_transport_without_waiting_for_resource_timeout() {
        use std::sync::atomic::AtomicU64;
        let mut fixture = ReadinessFixture::new(ReadinessScenario::LongApply);
        let revision = Arc::new(AtomicU64::new(1));
        fixture
            .core
            .set_cancellation(Cancellation::new(revision.clone(), 1));
        let canceller = thread::spawn(move || {
            thread::sleep(Duration::from_millis(150));
            revision.store(2, Ordering::Release);
        });
        let started = Instant::now();
        let error = fixture
            .core
            .apply_runtime(
                &fixture.controller,
                &fixture.source,
                started + Duration::from_secs(60),
                started + Duration::from_secs(60),
            )
            .unwrap_err();
        assert_eq!(error.code, "operation_cancelled");
        assert!(started.elapsed() < Duration::from_secs(2));
        canceller.join().unwrap();
    }

    #[test]
    fn healthy_resume_keeps_pid_and_calls_recover_only_when_requested() {
        let mut fixture = ReadinessFixture::new(ReadinessScenario::HealthHealthy);
        let pid = fixture.core.child.as_ref().unwrap().id();
        assert!(matches!(
            fixture.core.check_local_health(false),
            Ok(HealthCheck::Ready)
        ));
        assert_eq!(fixture.recovery_reads.load(Ordering::SeqCst), 0);
        fixture.core.request_reconcile(7).unwrap();
        let status = fixture.core.poll().unwrap();
        assert!(status.running && !status.recovering);
        assert_eq!(status.pid, Some(pid));
        assert_eq!(status.recovery_generation, 7);
        assert_eq!(fixture.recovery_reads.load(Ordering::SeqCst), 1);
        assert!(fixture.core.restarts.is_empty());
    }

    #[test]
    fn degraded_owned_tun_uses_repair_without_restarting_process() {
        let mut fixture = ReadinessFixture::new(ReadinessScenario::HealthDegraded);
        let pid = fixture.core.child.as_ref().unwrap().id();
        let observed = fixture.core.check_local_health(false);
        assert!(
            matches!(&observed, Ok(HealthCheck::Ready)),
            "health fixture returned {observed:?}; config reads={}, recovery reads={}",
            fixture.config_reads.load(Ordering::SeqCst),
            fixture.recovery_reads.load(Ordering::SeqCst)
        );
        assert_eq!(fixture.recovery_reads.load(Ordering::SeqCst), 1);
        assert_eq!(fixture.core.child.as_ref().unwrap().id(), pid);
    }

    #[test]
    fn foreign_tun_stops_automatic_repair_without_killing_core() {
        let mut fixture = ReadinessFixture::new(ReadinessScenario::HealthConflict);
        let pid = fixture.core.child.as_ref().unwrap().id();
        fixture.core.request_reconcile(1).unwrap();
        let status = fixture.core.poll().unwrap();
        assert!(!status.running && !status.recovering);
        assert!(fixture.core.health_blocked);
        assert_eq!(status.pid, Some(pid));
        assert_eq!(fixture.recovery_reads.load(Ordering::SeqCst), 0);
        assert!(fixture.core.restarts.is_empty());
        assert!(fixture.core.restart_at.is_none());
    }

    #[test]
    fn unavailable_tun_is_only_observed_until_healthy_without_restarting() {
        let mut fixture = ReadinessFixture::new(ReadinessScenario::HealthUnavailable);
        let pid = fixture.core.child.as_ref().unwrap().id();
        fixture.core.request_reconcile(1).unwrap();
        for attempt in 0..3 {
            let status = fixture.core.poll().unwrap();
            assert!(!status.running);
            assert_eq!(status.recovering, attempt < 2);
            assert_eq!(status.pid, Some(pid));
            assert_eq!(status.tun_health.as_deref(), Some("unavailable"));
            assert_eq!(fixture.recovery_reads.load(Ordering::SeqCst), 0);
        }
        assert!(fixture.core.health_deferred && !fixture.core.health_blocked);
        let reads = fixture.config_reads.load(Ordering::SeqCst);
        for _ in 0..5 {
            fixture.core.poll().unwrap();
        }
        assert_eq!(fixture.config_reads.load(Ordering::SeqCst), reads);
        // Another resume must not turn a finished window into unlimited fast
        // polling. It still cannot repair while native ownership is unknown.
        fixture.core.request_reconcile(2).unwrap();
        fixture.core.poll().unwrap();
        assert!(fixture.core.health_deferred && fixture.core.recovery.is_none());
        assert_eq!(fixture.recovery_reads.load(Ordering::SeqCst), 0);
        fixture
            .core
            .observe_health(lifecycle::awake_now().unwrap())
            .unwrap();
        assert_eq!(fixture.recovery_reads.load(Ordering::SeqCst), 0);
        fixture.health_unavailable.store(false, Ordering::SeqCst);
        fixture
            .core
            .observe_health(lifecycle::awake_now().unwrap())
            .unwrap();
        let status = fixture.core.status();
        assert!(status.running && !status.recovering);
        assert_eq!(status.pid, Some(pid));
        assert_eq!(status.tun_health.as_deref(), Some("healthy"));
        assert!(status.error.is_none() && status.error_code.is_none());
        assert!(fixture.core.restarts.is_empty() && fixture.core.restart_at.is_none());
        // Only after GET confirms health may resume notify the paired core to
        // requeue LAN work; a healthy TUN is kept by that endpoint's contract.
        assert_eq!(fixture.recovery_reads.load(Ordering::SeqCst), 1);
    }

    #[test]
    fn unavailable_during_recovery_is_observed_again_without_restarting() {
        let mut fixture = ReadinessFixture::new(ReadinessScenario::HealthRecoveryUnavailableOnce);
        let pid = fixture.core.child.as_ref().unwrap().id();
        fixture.core.request_reconcile(1).unwrap();
        let status = fixture.core.poll().unwrap();
        assert!(status.recovering && !status.running);
        assert!(!fixture.core.health_blocked);
        assert_eq!(status.tun_health.as_deref(), Some("unavailable"));
        assert_eq!(
            status.error_code.as_deref(),
            Some("tun_recovery_unavailable")
        );
        let status = fixture.core.poll().unwrap();
        assert!(status.running && !status.recovering);
        assert_eq!(status.pid, Some(pid));
        assert!(fixture.core.restarts.is_empty() && fixture.core.restart_at.is_none());
    }

    #[test]
    fn temporary_busy_recovers_in_the_same_generation_and_process() {
        let mut fixture = ReadinessFixture::new(ReadinessScenario::HealthBusyOnce);
        let pid = fixture.core.child.as_ref().unwrap().id();
        fixture.core.request_reconcile(1).unwrap();
        assert!(fixture.core.poll().unwrap().recovering);
        fixture.core.request_reconcile(2).unwrap();
        let recovered = fixture.core.poll().unwrap();
        assert!(recovered.running && !recovered.recovering);
        assert_eq!(recovered.pid, Some(pid));
        assert_eq!(recovered.recovery_generation, 2);
        assert_eq!(fixture.recovery_reads.load(Ordering::SeqCst), 2);
    }

    #[test]
    fn exhausted_busy_window_keeps_low_rate_observation_and_eventually_same_pid() {
        let mut fixture = ReadinessFixture::new(ReadinessScenario::HealthBusyUntilReleased);
        let pid = fixture.core.child.as_ref().unwrap().id();
        fixture.core.request_reconcile(1).unwrap();
        // Exhaust the bounded observation window while the executor genuinely
        // answers Busy. It must become a visible error, not a permanent latch
        // and not an excuse to terminate a legitimate configuration operation.
        for _ in 0..3 {
            fixture.core.poll().unwrap();
        }
        let deferred = fixture.core.status();
        assert!(!deferred.running && !deferred.recovering);
        assert!(fixture.core.health_deferred && !fixture.core.health_blocked);
        assert_eq!(deferred.pid, Some(pid));
        let reads = fixture.config_reads.load(Ordering::SeqCst);
        for _ in 0..5 {
            fixture.core.poll().unwrap();
        }
        assert_eq!(
            fixture.config_reads.load(Ordering::SeqCst),
            reads,
            "ordinary polls must honor the normal health interval"
        );
        fixture
            .core
            .observe_health(lifecycle::awake_now().unwrap())
            .unwrap();
        assert!(fixture.core.health_deferred && fixture.core.recovery.is_none());
        fixture.health_busy.store(false, Ordering::SeqCst);
        // Run the next scheduled observation without making the test sleep.
        fixture
            .core
            .observe_health(lifecycle::awake_now().unwrap())
            .unwrap();
        let ready = fixture.core.status();
        assert!(ready.running && !ready.recovering);
        assert_eq!(ready.pid, Some(pid));
        assert!(ready.error.is_none() && ready.error_code.is_none());
        assert!(fixture.core.restarts.is_empty() && fixture.core.restart_at.is_none());
        assert_eq!(fixture.recovery_reads.load(Ordering::SeqCst), 1);
    }

    #[test]
    fn recovery_rejects_foreign_pid_or_disabled_tun_and_honors_explicit_restart_result() {
        for scenario in [
            ReadinessScenario::HealthWrongRecoveryPid,
            ReadinessScenario::HealthDisabledAfterRepair,
        ] {
            let mut fixture = ReadinessFixture::new(scenario);
            assert!(matches!(
                fixture.core.check_local_health(true),
                Err(ReadinessFailure::Fatal(_))
            ));
            assert!(fixture.core.restart_at.is_none());
        }
        let mut fixture = ReadinessFixture::new(ReadinessScenario::HealthNeedsRestart);
        assert!(matches!(
            fixture.core.check_local_health(true),
            Ok(HealthCheck::NeedsRestart)
        ));
    }

    #[test]
    fn legacy_core_does_not_receive_new_health_or_recovery_endpoints() {
        let mut fixture = ReadinessFixture::new(ReadinessScenario::DisconnectOnce);
        assert!(matches!(
            fixture.core.check_local_health(true),
            Err(ReadinessFailure::Temporary(_))
        ));
        assert!(matches!(
            fixture.core.check_local_health(true),
            Ok(HealthCheck::Ready)
        ));
        assert_eq!(fixture.recovery_reads.load(Ordering::SeqCst), 0);
    }

    #[test]
    fn explicit_stop_cancels_a_pending_recovery_and_late_resume() {
        let mut core = CoreSupervisor::new(PathBuf::new(), PathBuf::new(), PathBuf::new());
        core.desired_running = true;
        core.restart_at = Some(Instant::now());
        core.recovery = Some(RecoveryWindow::new(1, Duration::ZERO));
        core.stop().unwrap();
        core.request_reconcile(2).unwrap();
        let status = core.poll().unwrap();
        assert!(!status.running && !status.recovering && !status.desired_running);
        assert_eq!(status.stop_reason, Some(StopReason::UserStop));
        assert!(core.restart_at.is_none() && core.recovery.is_none());
    }

    #[test]
    fn readiness_fixture_discards_cancelled_incomplete_requests() {
        let mut fixture = ReadinessFixture::new(ReadinessScenario::DisconnectOnce);
        let address = fixture.controller.url.trim_start_matches("http://");
        drop(std::net::TcpStream::connect(address).unwrap());
        {
            let mut cancelled = std::net::TcpStream::connect(address).unwrap();
            cancelled
                .write_all(b"GET /desktop HTTP/1.1\r\nAuthorization: Bearer ")
                .unwrap();
        }
        // Neither cancellation is an authenticated request. The next complete
        // requests must still be served and must still pass the Bearer check.
        fixture.wait(Duration::from_secs(2)).unwrap();
        assert_eq!(fixture.config_reads.load(Ordering::SeqCst), 2);
    }

    #[test]
    fn readiness_retries_one_listener_timeout_within_the_original_deadline() {
        let mut fixture = ReadinessFixture::new(ReadinessScenario::TimeoutOnce);
        let started = Instant::now();
        fixture.wait(Duration::from_secs(6)).unwrap();
        assert!(started.elapsed() >= CONTROL_TIMEOUT);
        assert!(started.elapsed() < Duration::from_secs(6));
        assert_eq!(fixture.config_reads.load(Ordering::SeqCst), 2);
    }

    #[test]
    fn readiness_persistent_unreadable_listener_uses_one_deadline() {
        let mut fixture = ReadinessFixture::new(ReadinessScenario::DisconnectAlways);
        let budget = Duration::from_secs(2);
        let started = Instant::now();
        let error = fixture.wait(budget).unwrap_err();
        let elapsed = started.elapsed();
        assert_eq!(error.code, "core_start_timeout");
        assert!(error.message.contains("/configs"));
        assert!(fixture.config_reads.load(Ordering::SeqCst) >= 3);
        assert!(elapsed >= budget);
        // A fresh CONTROL_TIMEOUT for the held third request would exceed this
        // bound; it must use only the original operation's remaining time.
        assert!(elapsed < Duration::from_secs(3));
    }

    #[test]
    fn readiness_real_port_conflict_is_not_retried_as_transport_failure() {
        let mut fixture = ReadinessFixture::new(ReadinessScenario::PortConflict);
        let started = Instant::now();
        let error = fixture.wait(Duration::from_secs(5)).unwrap_err();
        assert_eq!(error.code, "core_listener_failed");
        assert_eq!(fixture.config_reads.load(Ordering::SeqCst), 1);
        assert!(started.elapsed() < Duration::from_secs(2));
    }

    #[test]
    fn readiness_rejects_authentication_protocol_and_owned_pid_mismatches() {
        for scenario in [
            ReadinessScenario::Unauthorized,
            ReadinessScenario::InvalidJson,
            ReadinessScenario::WrongPid,
            ReadinessScenario::IncompatibleApi,
        ] {
            let mut fixture = ReadinessFixture::new(scenario);
            let started = Instant::now();
            let error = fixture.wait(Duration::from_secs(5)).unwrap_err();
            assert_ne!(error.code, "core_start_timeout");
            assert!(!error.message.contains("fixture-secret"));
            assert!(started.elapsed() < Duration::from_secs(2));
            assert!(fixture.config_reads.load(Ordering::SeqCst) <= 1);
        }
    }

    #[test]
    fn validation_main_and_recovery_use_one_operation_budget() {
        let started = Instant::now();
        let budget = OperationBudget::from_timeout(Duration::from_secs(180), started).unwrap();
        let after_validation = started + Duration::from_secs(37);
        let after_main = after_validation + Duration::from_secs(140);
        assert_eq!(
            budget.resources.duration_since(after_validation),
            Duration::from_secs(143)
        );
        assert_eq!(
            budget.resources.duration_since(after_main),
            Duration::from_secs(3)
        );
        assert_eq!(
            budget.total.duration_since(after_main),
            Duration::from_secs(33)
        );
        assert_eq!(
            budget.total.duration_since(budget.resources),
            RECOVERY_RESERVE
        );
        assert_eq!(
            budget.work_deadline().duration_since(budget.resources),
            Duration::from_secs(22)
        );
        assert_eq!(
            budget.total.duration_since(budget.work_deadline()),
            CLEANUP_RESERVE
        );
        assert!(budget.cleanup_deadline() <= budget.total);
        assert_eq!(
            duration_argument(Duration::from_micros(137_250_001)),
            "137.250001000s"
        );
        assert!(OperationBudget::from_timeout(Duration::MAX, started).is_err());
    }

    #[test]
    fn unconfirmed_start_cleanup_takes_priority_and_keeps_the_original_cause() {
        let original = AppError::new(
            "tun_device_conflict",
            "native ownership guard for the configured device",
        );
        let cleanup = AppError::new("service_timeout", "Stop completion is not confirmed");
        let error = startup_cleanup_error(&original, &cleanup);
        assert_eq!(error.code, "core_termination_unconfirmed");
        assert!(error.message.contains(&original.message));
        assert!(error.message.contains(&cleanup.message));
        assert_eq!(
            startup_cleanup_error(
                &original,
                &AppError::new("operation_cancelled", "newer Stop")
            )
            .code,
            "operation_cancelled"
        );
    }

    #[test]
    fn wintun_diagnostics_keep_safe_native_identity_and_distinguish_failure_stages() {
        for (stage, reason, expected) in [
            ("inspect", "foreign_alias", "tun_device_conflict"),
            (
                "inspect",
                "native_identity_missing",
                "tun_device_identity_unverified",
            ),
            (
                "inspect",
                "native_query_failed",
                "tun_device_inspection_failed",
            ),
            ("create", "native_busy", "tun_device_busy"),
            ("create", "native_create_failed", "tun_device_create_failed"),
            (
                "initialize",
                "native_initialization_failed",
                "tun_device_initialization_failed",
            ),
        ] {
            let header = format!("[wintun stage={stage} reason={reason} expected_guid={{3c3ae50c-e232-5514-9ea5-79082e75332a}} native_code=0x800700b7 win32_code=183 actual_guid={{11111111-2222-3333-4444-555555555555}} luid=123456 index=18 cfgmgr_code=0x00000000]");
            let message = format!("configure tun interface: adapter \"utun\": {header} raw private output; snapshot={{token:never-echo,reason:foreign_alias}}");
            let captured = safe_core_diagnostic(&message).unwrap();
            let error = tun_device_error(&captured).unwrap();
            assert_eq!(error.code, expected);
            for field in [
                "expected_guid={3c3ae50c-e232-5514-9ea5-79082e75332a}",
                "native_code=0x800700b7",
                "win32_code=183",
                "actual_guid={11111111-2222-3333-4444-555555555555}",
                "luid=123456",
                "index=18",
                "cfgmgr_code=0x00000000",
            ] {
                assert!(captured.contains(field), "missing {field} in {captured}");
            }
            assert!(!error.message.contains("never-echo"));
            assert!(!error.message.contains("snapshot"));
        }
        let invalid_fields = "configure tun interface: adapter \"utun\": [wintun stage=inspect reason=native_query_failed expected_guid=bad-guid actual_guid={password} native_code=https://private win32_code=-1 luid=18446744073709551616 index=not-a-number cfgmgr_code=0xZZZZ] snapshot={reason:foreign_alias}";
        let error = tun_device_error(invalid_fields).unwrap();
        assert_eq!(error.code, "tun_device_inspection_failed");
        for field in [
            "expected_guid=",
            "actual_guid=",
            "native_code=",
            "win32_code=",
            "luid=",
            "index=",
            "cfgmgr_code=",
        ] {
            assert!(!error.message.contains(field));
        }
        assert!(tun_device_error("configure tun interface: adapter \"utun\": [wintun stage=inspect reason=unrecognized] snapshot={reason:foreign_alias}").is_none());
    }

    #[test]
    fn exact_native_tun_guard_becomes_typed_without_exposing_other_core_output() {
        let cause =
            format!("configure tun interface: adapter \"utun\"{TUN_DEVICE_CONFLICT_SUFFIX}");
        let line = format!(
            "time=\"2000-01-01T00:00:00Z\" level=fatal msg={}",
            serde_json::to_string(&format!("parse config error: {cause}")).unwrap()
        );
        let captured = collect_diagnostics(format!("secret: never-echo\n{line}\n").as_bytes());
        assert_eq!(captured, cause);
        let error = tun_device_error(&captured).unwrap();
        assert_eq!(error.code, "tun_device_conflict");
        assert!(error.message.contains(&cause));
        assert!(!error.message.contains("never-echo"));
        for unrelated in [
            "configure tun interface: adapter \"utun\" already exists",
            "parse config error: access is denied",
            "adapter utun: file already exists",
            "[resource-bootstrap] code=timeout kind=provider",
        ] {
            assert!(tun_device_error(unrelated).is_none(), "{unrelated}");
        }
        let sensitive = format!("configure tun interface: adapter \"https://user:password@example.invalid/secret\"{TUN_DEVICE_CONFLICT_SUFFIX}");
        let error = tun_device_error(&sensitive).unwrap();
        assert_eq!(error.code, "tun_device_conflict");
        assert!(!error.message.contains("password"));
        assert!(error
            .message
            .contains("active, foreign or unverified ownership"));
    }

    #[test]
    fn diagnostic_capture_is_bounded_and_does_not_echo_raw_credentials() {
        let diagnostic = "[resource-bootstrap] code=timeout kind=provider resource=\"rules-demo\"";
        let output = format!("password: never-echo-this\nfailed GET https://user:password@example.invalid/?token=private\n{diagnostic}\n");
        assert_eq!(collect_diagnostics(output.as_bytes()), diagnostic);
        let unsafe_name = "[resource-bootstrap] code=timeout kind=provider resource=\"https://example.invalid/?token=private\"";
        assert_eq!(
            collect_diagnostics(unsafe_name.as_bytes()),
            "[resource-bootstrap] code=timeout kind=provider"
        );
        assert!(
            safe_resource_diagnostic("[resource-bootstrap] code=https://private kind=geo")
                .is_none()
        );
        let repeated = format!(
            "{}\n{}\n{diagnostic}",
            "x".repeat(64 * 1024),
            (diagnostic.to_string() + "\n").repeat(2000)
        );
        let safe = collect_diagnostics(repeated.as_bytes());
        assert!(safe.len() <= MAX_DIAGNOSTIC_BYTES);
        assert!(safe.ends_with(diagnostic));
        assert!(!safe.contains("xxxx"));
        assert_eq!(
            safe_resource_diagnostic(
                "[resource-bootstrap] code=deadline kind=config resource=\"configuration\""
            ),
            Some(
                "[resource-bootstrap] code=deadline kind=config resource=\"configuration\"".into()
            )
        );
    }

    #[test]
    #[cfg(unix)]
    fn native_name_conflict_after_restart_preserves_cause_without_retrying_original_name() {
        use std::os::unix::fs::PermissionsExt;
        let mut random = [0u8; 12];
        getrandom::getrandom(&mut random).unwrap();
        let directory = std::env::temp_dir().join(format!("tun-conflict-{}", hex::encode(random)));
        fs::create_dir_all(&directory).unwrap();
        let executable = directory.join("fixture-core");
        let cause =
            format!("configure tun interface: adapter \"utun\"{TUN_DEVICE_CONFLICT_SUFFIX}");
        fs::write(&executable, format!(
            "#!/bin/sh\nprintf '%s\\n' \"$*\" >> launches\nfor argument in \"$@\"; do\n  if [ \"$argument\" = '-t' ]; then exit 0; fi\ndone\nprintf '%s\\n' '{cause}'\nexit 1\n"
        )).unwrap();
        fs::set_permissions(&executable, fs::Permissions::from_mode(0o700)).unwrap();
        let source = format!("{}tun:\n  device: utun\n", config::INITIAL_CONFIG);
        let mut supervisor = CoreSupervisor::new(
            executable,
            directory.join("runtime"),
            directory.join("logs"),
        );
        supervisor.source = Some(source.clone());
        supervisor.tun = true;
        supervisor.desired_running = true;
        supervisor.restart_at = Some(Instant::now());
        let failed = supervisor.poll().unwrap();
        let launches = fs::read_to_string(directory.join("runtime").join("launches")).unwrap();
        assert_eq!(
            launches.lines().count(),
            1,
            "fresh startup must use the main process's own validation only"
        );
        assert!(!launches.split_whitespace().any(|argument| argument == "-t"));
        assert_eq!(failed.error_code.as_deref(), Some("tun_device_conflict"));
        assert!(failed.error.as_deref().unwrap().contains(&cause));
        assert_eq!(failed.process_state, "exited");
        assert!(!failed.recovering && !failed.desired_running);
        assert_eq!(failed.restart_count, 1);
        for _ in 0..4 {
            let observed = supervisor.poll().unwrap();
            assert_eq!(observed.error_code, failed.error_code);
            assert_eq!(observed.restart_count, 1);
            assert!(supervisor.restart_at.is_none());
            assert!(supervisor.child.is_none() && supervisor.validation_child.is_none());
        }
        assert_eq!(supervisor.source.as_deref(), Some(source.as_str()));
        drop(supervisor);
        assert!(crate::diagnostics::flush(Duration::from_secs(2)));
        fs::remove_dir_all(directory).unwrap();
    }

    #[test]
    fn long_failed_recoveries_do_not_evade_the_restart_limit() {
        let mut core = CoreSupervisor::new(PathBuf::from("unused"), PathBuf::new(), PathBuf::new());
        core.desired_running = true;
        core.failed_recoveries = MAX_RESTARTS;
        core.restart_at = Some(Instant::now());
        // Each long failed preparation has aged out of the rapid-crash window.
        core.restarts
            .push_back(Instant::now() - RESTART_WINDOW - Duration::from_secs(1));
        let status = core.poll().unwrap();
        assert!(!status.running);
        assert!(!core.desired_running);
        assert!(core.restart_at.is_none());
        assert!(status.error.unwrap().contains("已停止自动重启"));
    }
}
