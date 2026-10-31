use crate::{
    config,
    error::{AppError, Result},
    lifecycle::{self, RecoveryWindow, StopReason, HEALTH_INTERVAL},
};
use reqwest::blocking::Client;
use serde::{Deserialize, Serialize};
use std::{
    collections::VecDeque,
    fs,
    io::{Read, Seek, SeekFrom, Write},
    net::TcpListener,
    path::{Path, PathBuf},
    process::{Child, Command, Stdio},
    thread,
    time::{Duration, Instant},
};

const STOP_TIMEOUT: Duration = Duration::from_secs(8);
const RESTART_WINDOW: Duration = Duration::from_secs(60);
const MAX_RESTARTS: usize = 3;
const RECOVERY_RESERVE: Duration = Duration::from_secs(30);
const CLEANUP_RESERVE: Duration = STOP_TIMEOUT;
const CONTROL_TIMEOUT: Duration = Duration::from_secs(3);
const MAX_DIAGNOSTIC_BYTES: usize = 4096;

/// Validation and the main process share one resource deadline. The additional
/// reserve is for confirming readiness or restoring the previous runtime, never
/// another full download allowance.
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

fn duration_argument(duration: Duration) -> String {
    format!("{}.{:09}s", duration.as_secs(), duration.subsec_nanos())
}

fn confirm_process_exit(
    child: &mut Child,
    deadline: Instant,
    kill_error: Option<std::io::Error>,
) -> Result<()> {
    loop {
        match child.try_wait() {
            Ok(Some(_)) => return Ok(()),
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
// Only the bootstrap diagnostic protocol is copied into host errors/logs.
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
                    if let Some(safe) = safe_resource_diagnostic(&String::from_utf8_lossy(&line)) {
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
        if let Some(safe) = safe_resource_diagnostic(&String::from_utf8_lossy(&line)) {
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
    pub controller: Option<Controller>,
    pub version: Option<String>,
    pub error: Option<String>,
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
}

pub struct CoreSupervisor {
    executable: PathBuf,
    home: PathBuf,
    logs: PathBuf,
    child: Option<Child>,
    #[cfg(windows)]
    job: Option<std::os::windows::io::OwnedHandle>,
    controller: Option<Controller>,
    version: Option<String>,
    source: Option<String>,
    tun: bool,
    desired_running: bool,
    error: Option<String>,
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
    stop_reason: Option<StopReason>,
    error_code: Option<String>,
    exit_code: Option<i32>,
    tun_health: Option<String>,
}

pub fn http_client() -> Result<Client> {
    Client::builder()
        .no_proxy()
        .timeout(Duration::from_secs(3))
        .build()
        .map_err(|_| AppError::new("http_init_failed", "无法初始化本地控制连接"))
}

enum ReadinessFailure {
    Temporary(AppError),
    Fatal(AppError),
}

enum HealthCheck {
    Ready,
    NeedsRestart,
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
    let temporary = |reason: String| {
        ReadinessFailure::Temporary(AppError::new(
            "core_readiness_unavailable",
            format!("就绪检查 {endpoint}：{reason}"),
        ))
    };
    let timeout = remaining(deadline)
        .map_err(|_| temporary("已到本次操作截止时间".into()))?
        .min(CONTROL_TIMEOUT);
    let response = client
        .request(method, format!("{}{endpoint}", controller.url))
        .timeout(timeout)
        .bearer_auth(&controller.secret)
        .send()
        .map_err(|error| {
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
    if matches!(endpoint, "/desktop/health" | "/desktop/recover")
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
            #[cfg(windows)]
            job: None,
            controller: None,
            version: None,
            source: None,
            tun: false,
            desired_running: false,
            error: None,
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
            stop_reason: None,
            error_code: None,
            exit_code: None,
            tun_health: None,
        }
    }

    pub fn status(&self) -> CoreStatus {
        CoreStatus {
            running: self.child.is_some()
                && !self.termination_unconfirmed
                && !self.health_blocked
                && !self.health_deferred
                && self.recovery.is_none(),
            controller: self.controller.clone(),
            version: self.version.clone(),
            error: self.error.clone(),
            restart_count: self.restarts.len(),
            lifecycle_api: 1,
            desired_running: self.desired_running,
            recovering: self.recovery.is_some()
                || self.desired_running && self.restart_at.is_some(),
            recovery_generation: self.recovery_generation,
            stop_reason: self.stop_reason,
            error_code: self.error_code.clone(),
            pid: self.child.as_ref().map(Child::id),
            exit_code: self.exit_code,
            tun_health: self.tun_health.clone(),
        }
    }

    pub fn start(&mut self, source: String, tun: bool) -> Result<CoreStatus> {
        let budget = OperationBudget::new(&source)?;
        self.stop()?;
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
            self.error = Some(error.message.clone());
            self.error_code = Some(error.code.clone());
            return Err(error);
        }
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
        let result = self.launch(budget);
        if result.is_err() {
            let cleanup = self.terminate_child_until(budget.cleanup_deadline());
            self.controller = None;
            if let Err(error) = cleanup {
                return Err(error);
            }
        }
        result
    }

    fn launch(&mut self, budget: &OperationBudget) -> Result<()> {
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
        self.validate(&controller, budget)?;
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
        self.termination_unconfirmed = false;
        #[cfg(windows)]
        {
            self.job = Some(crate::windows::create_kill_job(
                self.child.as_ref().expect("child stored above"),
            )?);
        }
        self.controller = Some(controller.clone());
        self.wait_ready(&controller, &source, budget.work_deadline())?;
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
        self.error = None;
        self.error_code = None;
        self.stop_reason = None;
        self.health_blocked = false;
        self.health_deferred = false;
        self.recovery = None;
        self.last_health_check = lifecycle::awake_now()?;
        crate::diagnostics::record(
            &self.logs,
            "core_ready",
            serde_json::json!({"corePid":self.child.as_ref().map(Child::id),"tun":self.tun}),
        );
        Ok(())
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
        let client = http_client()?;
        let expected = config::parse(source)?;
        let mut last_failure = None;
        while remaining(deadline).is_ok() {
            if let Some(exit) = self
                .child
                .as_mut()
                .expect("child stored above")
                .try_wait()
                .map_err(|e| AppError::io("无法读取内核状态", e))?
            {
                let diagnostic = self.recent_diagnostic();
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
                self.verify_listeners(&client, controller, &expected, deadline)
            })();
            match probe {
                Ok(()) => return Ok(()),
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

    fn validate(&self, controller: &Controller, budget: &OperationBudget) -> Result<()> {
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
                let deadline = budget.cleanup_deadline();
                let kill_error = child.kill().err();
                let cleanup = confirm_process_exit(&mut child, deadline, kill_error);
                let _ = finish_diagnostics(stdout, deadline);
                let _ = finish_diagnostics(stderr, deadline);
                cleanup?;
                return Err(error);
            }
        };
        let result = (|| loop {
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
        let deadline = budget.cleanup_deadline();
        let cleanup = confirm_process_exit(&mut child, deadline, kill_error);
        let stdout = finish_diagnostics(stdout, deadline);
        let stderr = finish_diagnostics(stderr, deadline);
        cleanup?;
        let output = [stdout?, stderr?].join("\n");
        let summary = collect_diagnostics(output.as_bytes());
        self.log_diagnostic(&summary);
        result.map_err(|mut error| {
            if let Some(last) = summary.lines().last() {
                error.message.push_str("：");
                error.message.push_str(last);
            }
            error
        })
    }

    fn verify_listeners(
        &self,
        client: &Client,
        controller: &Controller,
        expected: &serde_yaml::Mapping,
        deadline: Instant,
    ) -> std::result::Result<(), ReadinessFailure> {
        let actual = read_readiness_json(client, controller, "/configs", deadline)?;
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
        Ok(())
    }

    fn apply_runtime(
        &mut self,
        controller: &Controller,
        source: &str,
        resource_deadline: Instant,
        completion_deadline: Instant,
    ) -> Result<()> {
        let resource_remaining = remaining(resource_deadline)?;
        let response = http_client()?
            .put(format!("{}/configs?force=true", controller.url))
            .query(&[(
                "resource-bootstrap-timeout",
                duration_argument(resource_remaining),
            )])
            .timeout(remaining(completion_deadline)?)
            .bearer_auth(&controller.secret)
            .json(&serde_json::json!({"path": self.home.join("config.yaml").display().to_string()}))
            .send()
            .map_err(|_| AppError::new("reload_failed", "运行配置重载超时或连接失败"))?;
        if !response.status().is_success() {
            // Error bodies are not trusted as display text: YAML and subscription
            // failures elsewhere in the core may contain secrets or full URLs.
            let mut bytes = Vec::new();
            let _ = response
                .take((MAX_DIAGNOSTIC_BYTES + 1) as u64)
                .read_to_end(&mut bytes);
            let diagnostic = serde_json::from_slice::<serde_json::Value>(&bytes)
                .ok()
                .and_then(|body| {
                    body.get("message")
                        .and_then(|message| message.as_str())
                        .and_then(safe_resource_diagnostic)
                });
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
        self.wait_ready(controller, source, completion_deadline)
    }

    pub fn reload(&mut self, source: String) -> Result<CoreStatus> {
        let budget = OperationBudget::new(&source)?;
        self.log_start = fs::metadata(self.logs.join("core.log")).map_or(0, |file| file.len());
        if self.child.is_none() {
            self.source = Some(source);
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
        let runtime = config::runtime_config(&source, self.tun, port)?;
        config::atomic_write(&self.home.join("config.yaml"), runtime.as_bytes())?;
        if let Err(error) = self.validate(&controller, &budget) {
            let old = config::runtime_config(&previous, self.tun, port)?;
            config::atomic_write(&self.home.join("config.yaml"), old.as_bytes())?;
            self.error = Some(error.message.clone());
            return Err(error);
        }
        if let Err(error) = self.apply_runtime(
            &controller,
            &source,
            budget.resources,
            budget.work_deadline(),
        ) {
            let rollback_deadline = Instant::now()
                .checked_add(RECOVERY_RESERVE - CLEANUP_RESERVE)
                .unwrap_or(budget.work_deadline())
                .min(budget.work_deadline());
            let rollback = (|| {
                let old = config::runtime_config(&previous, self.tun, port)?;
                config::atomic_write(&self.home.join("config.yaml"), old.as_bytes())?;
                self.apply_runtime(&controller, &previous, rollback_deadline, rollback_deadline)
            })();
            if rollback.is_ok() {
                let restored = AppError::new(
                    "reload_failed",
                    format!(
                        "配置已保存，但未能应用：{}。已恢复上次运行配置，请修正后重载",
                        error.message
                    ),
                );
                self.error = Some(restored.message.clone());
                return Err(restored);
            }
            // A timed-out ApplyConfig may still be running. Only confirmed process
            // exit proves that no pending application can keep changing live state.
            self.desired_running = false;
            self.restart_at = None;
            let cleanup = self.terminate_child_until(budget.cleanup_deadline());
            self.controller = None;
            self.version = None;
            self.error = Some(match cleanup {
                Ok(()) => {
                    "配置已保存，重载和运行回滚均失败，内核已停止；请修正配置后重新启动".into()
                }
                Err(cleanup) => format!("配置已保存，重载和运行回滚均失败；{}", cleanup.message),
            });
            return Err(AppError::new(
                "reload_restore_failed",
                self.error.clone().unwrap_or_default(),
            ));
        }
        self.source = Some(source);
        self.error = None;
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
        let controller = self
            .controller
            .clone()
            .ok_or_else(|| fatal("core_controller_missing", "缺少自有内核控制连接"))?;
        let client = http_client().map_err(ReadinessFailure::Fatal)?;
        // A local observation is bounded as a whole. It never opens an Internet
        // connection or borrows another resource/bootstrap operation budget.
        let deadline = Instant::now() + CONTROL_TIMEOUT * 3;
        let info = read_readiness_json(&client, &controller, "/desktop", deadline)?;
        let pid = self.child.as_ref().map(Child::id);
        verify_managed_identity(&info, pid)?;
        let expected = config::parse(self.source.as_deref().unwrap_or_default())
            .map_err(ReadinessFailure::Fatal)?;
        self.verify_listeners(&client, &controller, &expected, deadline)?;
        let capabilities = &info["capabilities"];
        if capabilities["tun_health"].as_bool() != Some(true) {
            self.tun_health = self.tun.then(|| "unsupported".into());
            return Ok(HealthCheck::Ready);
        }
        let health = read_readiness_json(&client, &controller, "/desktop/health", deadline)?;
        verify_managed_identity(&health, pid)?;
        let state = health["tun"]["state"].as_str().unwrap_or("invalid");
        if health["tun"]["enabled"].as_bool() != Some(self.tun) {
            return Err(fatal(
                "tun_configuration_mismatch",
                "TUN 实际启用状态与自有运行模式不一致",
            ));
        }
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
            "disabled" | "healthy" | "degraded" => {}
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
        if !reconcile && state != "degraded" {
            return Ok(HealthCheck::Ready);
        }
        if capabilities["tun_repair"].as_bool() != Some(true) {
            return if state == "degraded" {
                Err(fatal(
                    "tun_repair_unsupported",
                    "配套内核不支持安全修复 TUN",
                ))
            } else {
                Ok(HealthCheck::Ready)
            };
        }
        // The paired core serializes repair and only touches its owned resources.
        // On a healthy resume this also invalidates/requeues background LAN work.
        let recovered = read_managed_json(
            &client,
            &controller,
            "/desktop/recover",
            deadline,
            reqwest::Method::POST,
        )?;
        if recovered["pid"].as_u64() != pid.map(u64::from) {
            return Err(fatal(
                "core_controller_identity",
                "恢复结果的进程与自有内核不一致",
            ));
        }
        match recovered["status"].as_str() {
            Some("healthy" | "repaired") => {
                self.tun_health = recovered["tun"]["state"].as_str().map(str::to_owned);
                if !matches!(self.tun_health.as_deref(), Some("healthy" | "disabled")) {
                    return Err(fatal("tun_recovery_unconfirmed", "TUN 修复结果未确认健康"));
                }
                if recovered["tun"]["enabled"].as_bool() != Some(self.tun)
                    || self.tun && self.tun_health.as_deref() != Some("healthy")
                {
                    return Err(fatal(
                        "tun_recovery_unconfirmed",
                        "TUN 修复后启用状态与运行模式不一致",
                    ));
                }
                self.verify_listeners(&client, &controller, &expected, deadline)?;
                Ok(HealthCheck::Ready)
            }
            Some("needs_restart") => Ok(HealthCheck::NeedsRestart),
            Some("conflict") => Err(fatal(
                "tun_ownership_conflict",
                "TUN 恢复遇到资源所有权冲突，未接管外部资源",
            )),
            Some("unavailable") => {
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

    fn observe_health(&mut self, now: Duration) -> Result<()> {
        self.last_health_check = now;
        let reconciling = self.recovery.is_some();
        match self.check_local_health(reconciling || self.health_deferred) {
            Ok(HealthCheck::Ready) => {
                if reconciling || self.health_deferred {
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
            }
            Ok(HealthCheck::NeedsRestart) => {
                self.schedule_health_restart(AppError::new(
                    "tun_restart_required",
                    "内核确认 TUN 需要重启，正在恢复",
                ))?;
            }
            Err(ReadinessFailure::Temporary(error)) => {
                let observe_only = matches!(
                    error.code.as_str(),
                    "core_executor_busy"
                        | "core_not_ready"
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
        self.stop_for(StopReason::HealthFailure)?;
        self.desired_running = true;
        self.error_code = Some(error.code);
        self.error = Some(error.message);
        self.restart_at = Some(Instant::now() + Duration::from_secs(1));
        Ok(())
    }

    pub fn poll(&mut self) -> Result<CoreStatus> {
        if let Some(child) = self.child.as_mut() {
            if let Some(exit) = child
                .try_wait()
                .map_err(|e| AppError::io("无法读取内核状态", e))?
            {
                self.exit_code = exit.code();
                crate::diagnostics::record(
                    &self.logs,
                    "core_exit",
                    serde_json::json!({"corePid":child.id(),"exitCode":exit.code(),"desiredRunning":self.desired_running}),
                );
                self.child = None;
                self.controller = None;
                self.termination_unconfirmed = false;
                #[cfg(windows)]
                {
                    self.job = None;
                }
                if self.desired_running {
                    self.stop_reason = Some(StopReason::UnexpectedExit);
                    self.error_code = Some("core_unexpected_exit".into());
                    self.error = Some("内核意外退出，正在准备恢复".into());
                    self.restart_at = Some(Instant::now() + Duration::from_secs(1));
                }
                self.recovery = None;
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
                        self.error = Some(error.message);
                        self.error_code = Some(error.code);
                        if !self.termination_unconfirmed {
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
            if self.recovery.is_some()
                || now.saturating_sub(self.last_health_check) >= HEALTH_INTERVAL
            {
                self.observe_health(now)?;
            }
        }
        Ok(self.status())
    }

    pub fn stop(&mut self) -> Result<()> {
        self.stop_for(StopReason::UserStop)
    }

    pub fn stop_for(&mut self, reason: StopReason) -> Result<()> {
        crate::diagnostics::record(
            &self.logs,
            "core_stop",
            serde_json::json!({"reason":reason,"corePid":self.child.as_ref().map(Child::id),"generation":self.recovery_generation}),
        );
        self.stop_reason = Some(reason);
        self.desired_running = false;
        self.restart_at = None;
        self.recovery = None;
        self.health_blocked = false;
        self.health_deferred = false;
        if self.child.is_some() {
            if let Some(controller) = &self.controller {
                if let Ok(client) = http_client() {
                    let _ = client
                        .post(format!("{}/desktop/shutdown", controller.url))
                        .bearer_auth(&controller.secret)
                        .send();
                }
            }
            let stop_started = Instant::now();
            while stop_started.elapsed() < STOP_TIMEOUT {
                let exited = self
                    .child
                    .as_mut()
                    .expect("child exists until cleanup")
                    .try_wait()
                    .map_err(|e| AppError::io("无法等待内核退出", e))?
                    .is_some();
                if exited {
                    self.child = None;
                    break;
                }
                thread::sleep(Duration::from_millis(75));
            }
            self.terminate_child()?;
        }
        self.controller = None;
        self.version = None;
        #[cfg(windows)]
        {
            self.job = None;
        }
        Ok(())
    }

    fn terminate_child(&mut self) -> Result<()> {
        self.terminate_child_until(Instant::now() + CLEANUP_RESERVE)
    }

    fn terminate_child_until(&mut self, deadline: Instant) -> Result<()> {
        let kill_error = self.child.as_mut().and_then(|child| child.kill().err());
        #[cfg(windows)]
        {
            self.job = None;
        }
        if let Some(child) = self.child.as_mut() {
            if let Err(error) = confirm_process_exit(child, deadline, kill_error) {
                // Keep the owned process handle so a later poll/stop can still
                // confirm its exit. In particular, do not report it as stopped.
                self.termination_unconfirmed = true;
                self.desired_running = false;
                self.restart_at = None;
                self.controller = None;
                self.version = None;
                self.error = Some(error.message.clone());
                return Err(error);
            }
        }
        self.child = None;
        self.termination_unconfirmed = false;
        Ok(())
    }
}

impl Drop for CoreSupervisor {
    fn drop(&mut self) {
        if self.termination_unconfirmed {
            // The operation already used its cleanup allowance. Releasing the
            // owner must not silently start another stop/cleanup deadline.
            if let Some(child) = self.child.as_mut() {
                let _ = child.kill();
            }
            #[cfg(windows)]
            {
                self.job = None;
            }
        } else {
            let _ = self.stop();
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
        HealthRecoveryUnavailableOnce,
        HealthBusyOnce,
        HealthBusyUntilReleased,
        HealthNeedsRestart,
        HealthWrongRecoveryPid,
        HealthDisabledAfterRepair,
    }

    struct ReadinessFixture {
        core: CoreSupervisor,
        controller: Controller,
        source: String,
        config_reads: Arc<AtomicUsize>,
        recovery_reads: Arc<AtomicUsize>,
        health_busy: Arc<AtomicBool>,
        health_unavailable: Arc<AtomicBool>,
        finished: Arc<AtomicBool>,
        server: Option<thread::JoinHandle<()>>,
        _occupied_port: Option<TcpListener>,
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
                    | ReadinessScenario::HealthRecoveryUnavailableOnce
                    | ReadinessScenario::HealthBusyOnce
                    | ReadinessScenario::HealthBusyUntilReleased
                    | ReadinessScenario::HealthNeedsRestart
                    | ReadinessScenario::HealthWrongRecoveryPid
                    | ReadinessScenario::HealthDisabledAfterRepair
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
            let health_busy = Arc::new(AtomicBool::new(matches!(
                scenario,
                ReadinessScenario::HealthBusyUntilReleased
            )));
            let health_unavailable = Arc::new(AtomicBool::new(matches!(
                scenario,
                ReadinessScenario::HealthUnavailable
            )));
            let stop = finished.clone();
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
                    stream
                        .set_read_timeout(Some(Duration::from_secs(1)))
                        .unwrap();
                    stream
                        .set_write_timeout(Some(Duration::from_secs(1)))
                        .unwrap();
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
                        (200, serde_json::json!({"managed":true,"api_version":api,"state":"running","pid":reported_pid,"capabilities":{"tun_health":health_enabled,"tun_repair":health_enabled}}).to_string())
                    } else if request.starts_with("get /desktop/health ") {
                        assert!(
                            health_enabled,
                            "legacy core must not receive a health request"
                        );
                        let state = if unavailable.load(Ordering::SeqCst) {
                            "unavailable"
                        } else {
                            match scenario {
                                ReadinessScenario::HealthConflict => "conflict",
                                ReadinessScenario::HealthDegraded
                                | ReadinessScenario::HealthNeedsRestart
                                | ReadinessScenario::HealthDisabledAfterRepair => "degraded",
                                _ => "healthy",
                            }
                        };
                        if busy.load(Ordering::SeqCst) {
                            (503, "{}".into())
                        } else {
                            (200, serde_json::json!({"managed":true,"api_version":1,"state":"running","pid":pid,"tun":{"enabled":true,"state":state,"repairable":state=="degraded"}}).to_string())
                        }
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
                health_busy,
                health_unavailable,
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

    #[test]
    fn readiness_retries_one_listener_disconnect_without_restarting() {
        let mut fixture = ReadinessFixture::new(ReadinessScenario::DisconnectOnce);
        let original_pid = fixture.core.child.as_ref().unwrap().id();
        fixture.wait(Duration::from_secs(2)).unwrap();
        assert_eq!(fixture.config_reads.load(Ordering::SeqCst), 2);
        assert_eq!(fixture.core.child.as_ref().unwrap().id(), original_pid);
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
        assert!(matches!(
            fixture.core.check_local_health(false),
            Ok(HealthCheck::Ready)
        ));
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
