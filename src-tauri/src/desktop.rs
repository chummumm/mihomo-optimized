use crate::{
    config::{self, ConfigDocument, Preferences},
    core::{self, Controller, CoreStatus, CoreSupervisor},
    error::{AppError, Result},
    host::HostContext,
    lifecycle::{self, RecoveryWindow, StopReason},
    proxy::SystemProxy,
    service::{self, ServiceStatusView},
    service_ipc::{Client as ServiceClient, Request},
    windows,
};
use serde::Serialize;
use std::{
    collections::VecDeque,
    path::PathBuf,
    sync::{atomic::Ordering, Arc, Mutex},
    time::Duration,
};

#[derive(Clone)]
pub struct HostState(pub Arc<Mutex<DesktopManager>>);

#[derive(Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Phase {
    Stopped,
    Starting,
    Running,
    Recovering,
    Stopping,
    Error,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Capabilities {
    system_proxy: bool,
    autostart: bool,
    tun: bool,
    config_import: bool,
    service_management: bool,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DesktopStatus {
    pub phase: Phase,
    pub system_proxy: bool,
    pub tun: bool,
    pub autostart: bool,
    pub controller: Option<Controller>,
    pub last_error: Option<String>,
    pub core_version: Option<String>,
    pub config_path: String,
    pub log_path: String,
    pub service: ServiceStatusView,
    pub capabilities: Capabilities,
    pub app_version: String,
    pub desired_running: bool,
    pub stop_reason: Option<StopReason>,
    pub error_code: Option<String>,
    pub recovery_generation: u64,
    pub tun_health: Option<String>,
}

enum RunningCore {
    Direct(CoreSupervisor),
    Service(ServiceClient),
}

pub struct DesktopManager {
    app: HostContext,
    directory: PathBuf,
    config_path: PathBuf,
    logs: PathBuf,
    preferences: Preferences,
    proxy: SystemProxy,
    running: Option<RunningCore>,
    phase: Phase,
    core_status: CoreStatus,
    applied_proxy_port: Option<u16>,
    last_error: Option<String>,
    pub dirty: bool,
    pub exiting: bool,
    updating: bool,
    run_intent: bool,
    resume_pending: Option<u64>,
    communication_recovery: Option<RecoveryWindow>,
    lease_recovery_pending: Option<u64>,
    lease_restarts: VecDeque<Duration>,
    applied_config: Option<String>,
}

impl DesktopManager {
    pub fn new(app: HostContext) -> Result<Self> {
        let directory = windows::user_data_dir()?;
        windows::secure_directory(&directory, Some(&windows::current_sid()?))?;
        let (config_path, logs) = config::data_paths(&directory);
        std::fs::create_dir_all(&logs).map_err(|e| AppError::io("无法创建日志目录", e))?;
        if !config_path.exists() {
            config::atomic_write(&config_path, config::INITIAL_CONFIG.as_bytes())?;
        }
        let (preferences, mut last_error) = match config::load_preferences(&directory) {
            Ok(value) => (value, None),
            Err(error) => (Preferences::default(), Some(error.message)),
        };
        let mut proxy = SystemProxy::new(directory.clone());
        if let Err(error) = proxy.restore() {
            last_error = Some(error.message);
        }
        let run_intent = preferences.desired_running;
        Ok(Self {
            app,
            directory,
            config_path,
            logs,
            preferences,
            proxy,
            running: None,
            phase: Phase::Stopped,
            core_status: CoreStatus::default(),
            applied_proxy_port: None,
            last_error,
            dirty: false,
            exiting: false,
            updating: false,
            run_intent,
            resume_pending: None,
            communication_recovery: None,
            lease_recovery_pending: None,
            lease_restarts: VecDeque::new(),
            applied_config: None,
        })
    }

    /// Publish Starting before the worker can enter resource preparation.
    pub fn prepare_startup(&mut self, allow_start: bool) -> bool {
        let should_start = allow_start && self.preferences.desired_running;
        if should_start {
            self.phase = Phase::Starting;
            self.last_error = None;
        }
        self.publish();
        should_start
    }

    pub fn report_error(&mut self, message: String) {
        self.last_error = Some(message);
        self.publish();
    }

    pub fn status(&self) -> DesktopStatus {
        DesktopStatus {
            phase: self.phase,
            system_proxy: self.proxy.owned().unwrap_or(false),
            tun: self.preferences.tun,
            autostart: windows::autostart_enabled().unwrap_or(false),
            controller: if self.phase == Phase::Running {
                self.core_status.controller.clone()
            } else {
                None
            },
            last_error: self.last_error.clone(),
            core_version: self.core_status.version.clone(),
            config_path: self.config_path.display().to_string(),
            log_path: self.active_log_directory().display().to_string(),
            service: service::status().unwrap_or_default(),
            capabilities: Capabilities {
                system_proxy: true,
                autostart: true,
                tun: true,
                config_import: true,
                service_management: true,
            },
            app_version: env!("CARGO_PKG_VERSION").into(),
            desired_running: self.run_intent,
            stop_reason: self.core_status.stop_reason,
            error_code: self.core_status.error_code.clone(),
            recovery_generation: self
                .core_status
                .recovery_generation
                .max(self.resume_pending.unwrap_or(0)),
            tun_health: self.core_status.tun_health.clone(),
        }
    }

    pub fn publish(&self) {
        let status = self.status();
        self.app.publish_status(status);
    }

    fn active_log_directory(&self) -> PathBuf {
        if self.preferences.tun {
            if let Ok(directory) = windows::service_data_dir() {
                let logs = directory.join("logs");
                if logs.is_dir() {
                    return logs;
                }
            }
        }
        self.logs.clone()
    }

    fn persist(&self) -> Result<()> {
        config::save_preferences(&self.directory, &self.preferences)
    }

    fn require_idle_update(&self) -> Result<()> {
        if self.updating {
            Err(AppError::new(
                "update_in_progress",
                "更新正在安装，请稍后再操作内核",
            ))
        } else {
            Ok(())
        }
    }

    pub fn start(&mut self, persist: bool) -> Result<()> {
        self.require_idle_update()?;
        if self.phase == Phase::Running {
            return Ok(());
        }
        self.phase = Phase::Starting;
        self.run_intent = true;
        self.lease_recovery_pending = None;
        self.communication_recovery = None;
        self.last_error = None;
        self.publish();
        let result = (|| {
            let content = config::read_limited(&self.config_path)?;
            config::parse(&content)?;
            if self.preferences.system_proxy {
                config::mixed_port(&content)?;
            }
            if let Some(running) = self.running.as_mut() {
                stop_running(running)?;
            }
            self.running = None;
            let (running, status) = if self.preferences.tun {
                self.ensure_service()?;
                let client = ServiceClient::new()?;
                let status = client.call(&Request::Start {
                    config: content.clone(),
                })?;
                (RunningCore::Service(client), status)
            } else {
                let mut supervisor = CoreSupervisor::new(
                    core::fixed_core_path()?,
                    self.directory.join("runtime"),
                    self.logs.clone(),
                );
                let status = supervisor.start(content.clone(), false)?;
                (RunningCore::Direct(supervisor), status)
            };
            self.running = Some(running);
            self.core_status = status;
            self.applied_config = Some(content.clone());
            self.applied_proxy_port = config::mixed_port(&content).ok();
            if !self.core_status.running {
                return Err(AppError::new("core_start_failed", "内核尚未就绪"));
            }
            if self.preferences.system_proxy {
                self.proxy.enable(config::mixed_port(&content)?)?;
            }
            self.phase = Phase::Running;
            if persist {
                self.preferences.desired_running = true;
                self.persist()?;
            }
            Ok(())
        })();
        if let Err(error) = &result {
            let _ = self.proxy.restore();
            if let Some(running) = self.running.as_mut() {
                let _ = stop_running(running);
            }
            self.running = None;
            self.core_status = CoreStatus::default();
            self.applied_proxy_port = None;
            self.phase = Phase::Error;
            self.last_error = Some(error.message.clone());
        }
        self.publish();
        result
    }

    pub fn stop(&mut self, persist: bool) -> Result<()> {
        self.resume_pending = None;
        self.lease_recovery_pending = None;
        self.communication_recovery = None;
        if persist {
            // Persist the user's intent even if a later OS cleanup needs retry.
            self.run_intent = false;
            self.preferences.desired_running = false;
            self.persist()?;
        }
        self.proxy.restore()?;
        self.phase = Phase::Stopping;
        self.publish();
        let result = self.running.as_mut().map(stop_running).unwrap_or(Ok(()));
        if let Err(error) = &result {
            self.phase = Phase::Error;
            self.last_error = Some(error.message.clone());
            self.publish();
            return result;
        }
        self.running = None;
        self.core_status = CoreStatus::default();
        self.core_status.stop_reason = Some(StopReason::UserStop);
        self.applied_config = None;
        self.applied_proxy_port = None;
        self.phase = Phase::Stopped;
        self.last_error = None;
        if persist {
            self.preferences.desired_running = false;
            self.persist()?;
        }
        self.publish();
        Ok(())
    }

    pub fn restart(&mut self) -> Result<()> {
        self.require_idle_update()?;
        self.stop(false)?;
        self.start(true)
    }

    pub fn set_system_proxy(&mut self, enabled: bool) -> Result<()> {
        self.require_idle_update()?;
        if enabled {
            if self.phase != Phase::Running {
                self.start(true)?;
            }
            let port = self.applied_proxy_port.ok_or_else(|| {
                AppError::new(
                    "proxy_port_missing",
                    "当前运行配置没有 HTTP 或 mixed 代理端口，请保存并重载配置",
                )
            })?;
            self.proxy.enable(port)?;
        } else {
            self.proxy.restore()?;
        }
        self.preferences.system_proxy = enabled;
        self.persist()?;
        self.publish();
        Ok(())
    }

    pub fn set_tun(&mut self, enabled: bool) -> Result<()> {
        self.require_idle_update()?;
        if self.preferences.tun == enabled {
            return Ok(());
        }
        if enabled {
            config::runtime_config(&config::read_limited(&self.config_path)?, true, 9090)?;
            self.ensure_service()?;
        }
        let was_running = self.phase == Phase::Running;
        let previous = self.preferences.tun;
        if was_running {
            self.stop(false)?;
        }
        self.preferences.tun = enabled;
        if was_running {
            if let Err(error) = self.start(false) {
                self.preferences.tun = previous;
                let recovery = self.start(false);
                self.last_error = Some(if recovery.is_ok() {
                    format!("{}；已恢复先前模式", error.message)
                } else {
                    format!("{}；恢复先前模式失败，请检查配置", error.message)
                });
                self.publish();
                return Err(error);
            }
        }
        self.persist()?;
        self.publish();
        Ok(())
    }

    fn ensure_service(&self) -> Result<()> {
        let state = service::status()?;
        if !state.installed || !state.running {
            windows::run_elevated_service_action("install")?;
        }
        Ok(())
    }

    pub fn install_service(&self) -> Result<()> {
        let state = service::status()?;
        if !state.installed || !state.running {
            windows::run_elevated_service_action("install")?;
        }
        self.publish();
        Ok(())
    }

    pub fn uninstall_service(&mut self) -> Result<()> {
        self.require_idle_update()?;
        if self.preferences.tun {
            self.set_tun(false)?;
        }
        windows::run_elevated_service_action("uninstall")?;
        self.publish();
        Ok(())
    }

    pub fn read_config(&self) -> Result<ConfigDocument> {
        config::read(&self.config_path)
    }

    pub fn save_config(
        &mut self,
        content: &str,
        expected_revision: Option<&str>,
    ) -> Result<ConfigDocument> {
        let saved = config::save(&self.config_path, content, expected_revision)?;
        let _ = self.app.emit(
            "desktop-config-changed",
            serde_json::json!({"revision":saved.revision}),
        );
        Ok(saved)
    }

    pub fn import_config(
        &mut self,
        content: &str,
        expected_revision: Option<&str>,
    ) -> Result<ConfigDocument> {
        self.require_idle_update()?;
        let saved = config::import(&self.config_path, content, expected_revision)?;
        let _ = self.app.emit(
            "desktop-config-changed",
            serde_json::json!({"revision":saved.revision}),
        );
        Ok(saved)
    }

    pub fn reload_config(&mut self, _force: bool) -> Result<()> {
        self.require_idle_update()?;
        let content = config::read_limited(&self.config_path)?;
        config::parse(&content)?;
        if self.phase != Phase::Running {
            return Ok(());
        }
        if self.preferences.system_proxy {
            config::mixed_port(&content)?;
        }
        let result = match self.running.as_mut() {
            Some(RunningCore::Direct(core)) => core.reload(content.clone()),
            Some(RunningCore::Service(client)) => client.call(&Request::Reload {
                config: content.clone(),
            }),
            None => return Ok(()),
        };
        let status = match result {
            Ok(status) => status,
            Err(error) => {
                self.core_status = match self.running.as_mut() {
                    Some(RunningCore::Direct(core)) => core.status(),
                    Some(RunningCore::Service(client)) => {
                        client.call(&Request::Status).unwrap_or_default()
                    }
                    None => CoreStatus::default(),
                };
                self.phase = if self.core_status.running {
                    Phase::Running
                } else {
                    Phase::Error
                };
                self.last_error = Some(error.message.clone());
                if !self.core_status.running {
                    let _ = self.proxy.restore();
                }
                self.publish();
                return Err(error);
            }
        };
        self.core_status = status;
        self.applied_config = Some(content.clone());
        self.last_error = None;
        self.applied_proxy_port = config::mixed_port(&content).ok();
        if self.preferences.system_proxy {
            self.proxy.enable(config::mixed_port(&content)?)?;
        }
        self.publish();
        Ok(())
    }

    pub fn patch_config(
        &mut self,
        patch: serde_json::Map<String, serde_json::Value>,
    ) -> Result<()> {
        self.require_idle_update()?;
        if self.dirty {
            return Err(AppError::new(
                "config_unsaved",
                "编辑器有未保存修改，请先保存或放弃后再修改运行设置",
            ));
        }
        let saved = config::patch(&self.config_path, patch)?;
        let _ = self.app.emit(
            "desktop-config-changed",
            serde_json::json!({"revision":saved.revision}),
        );
        self.reload_config(true)
    }

    pub fn open_directory(&self, kind: &str) -> Result<()> {
        match kind {
            "config" => windows::shell_open(&self.directory.display().to_string()),
            "logs" => windows::shell_open(&self.active_log_directory().display().to_string()),
            _ => Err(AppError::new("invalid_directory", "不支持的目录")),
        }
    }

    pub fn prepare_update(&mut self) -> Result<bool> {
        self.require_idle_update()?;
        if self.dirty {
            return Err(AppError::new(
                "config_unsaved",
                "编辑器有未保存修改，请先保存或放弃后再更新",
            ));
        }
        let was_running = self.phase == Phase::Running;
        self.stop(false)?;
        self.updating = true;
        Ok(was_running)
    }

    pub fn resume_after_update(&mut self, was_running: bool) -> Result<()> {
        self.updating = false;
        if was_running {
            self.start(false)?;
        }
        Ok(())
    }

    pub fn update_proxy_url(&self) -> Option<String> {
        if self.phase == Phase::Running {
            self.applied_proxy_port
                .map(|port| format!("http://127.0.0.1:{port}"))
        } else {
            None
        }
    }

    pub fn poll(&mut self) {
        if self.updating
            || self.exiting
            || self.app.stop_pending.load(Ordering::Acquire) != 0
            || !self.app.owner_alive()
        {
            return;
        }
        let generation = self.app.generation();
        if let Some(pending) = self.lease_recovery_pending.take() {
            if pending == generation && self.run_intent {
                self.recover_lease(generation);
                return;
            }
        }
        let resume = self.resume_pending.take();
        let result = match self.running.as_mut() {
            Some(RunningCore::Direct(core)) => {
                if let Some(resume) = resume {
                    let _ = core.request_reconcile(resume);
                }
                core.poll()
            }
            Some(RunningCore::Service(client)) => {
                if let Some(resume) = resume.filter(|_| self.core_status.lifecycle_api >= 1) {
                    client.call(&Request::Reconcile { generation: resume })
                } else {
                    client.call(&Request::Status)
                }
            }
            None => {
                self.publish();
                return;
            }
        };
        // A queued Stop/Exit supersedes observations started before that action.
        if generation != self.app.generation() || self.app.stop_pending.load(Ordering::Acquire) != 0
        {
            return;
        }
        match result {
            Ok(status) => {
                self.communication_recovery = None;
                // A failed service query preserves the last process status, but
                // withdraws the controller and restores our system proxy. The
                // next confirmed Running observation must also restore the user's
                // preference when that last process status was already running.
                let recovered = self.phase != Phase::Running && status.running;
                self.phase = if status.recovering {
                    Phase::Recovering
                } else if status.running {
                    Phase::Running
                } else if matches!(
                    status.stop_reason,
                    Some(StopReason::UserStop | StopReason::Shutdown)
                ) {
                    Phase::Stopped
                } else if status.stop_reason == Some(StopReason::LeaseExpired) && self.run_intent {
                    self.lease_recovery_pending = Some(generation);
                    Phase::Recovering
                } else {
                    Phase::Error
                };
                self.last_error = status.error.clone();
                self.core_status = status;
                if !self.core_status.running {
                    if let Err(error) = self.proxy.restore() {
                        self.last_error = Some(error.message);
                    }
                } else if recovered && self.preferences.system_proxy {
                    if let Some(port) = self.applied_proxy_port {
                        if let Err(error) = self.proxy.enable(port) {
                            self.last_error = Some(error.message);
                        }
                    }
                }
            }
            Err(error) => {
                let temporary = matches!(
                    error.code.as_str(),
                    "service_unavailable" | "service_timeout" | "io_error"
                );
                let now = lifecycle::awake_now().unwrap_or_default();
                let exhausted = self
                    .communication_recovery
                    .get_or_insert_with(|| RecoveryWindow::new(generation, now))
                    .failed(now);
                self.phase = if temporary && !exhausted {
                    Phase::Recovering
                } else {
                    Phase::Error
                };
                self.core_status.error_code = Some(error.code.clone());
                crate::diagnostics::record(
                    &self.logs,
                    "service_observation_failed",
                    serde_json::json!({"generation":generation,"code":error.code,"exhausted":exhausted}),
                );
                self.last_error = Some(error.message);
                self.core_status.controller = None;
                let _ = self.proxy.restore();
            }
        }
        self.publish();
    }

    pub fn request_reconcile(&mut self, generation: u64) {
        if !self.run_intent
            || self.updating
            || self.exiting
            || self.app.stop_pending.load(Ordering::Acquire) != 0
        {
            return;
        }
        self.resume_pending = Some(self.resume_pending.unwrap_or(0).max(generation));
        if self.running.is_some() {
            self.phase = Phase::Recovering;
        }
        crate::diagnostics::record(
            &self.logs,
            "desktop_resume",
            serde_json::json!({"generation":generation,"ownerPid":self.app.owner.pid,"desiredRunning":self.run_intent,"tun":self.preferences.tun}),
        );
        self.publish();
    }

    fn recover_lease(&mut self, generation: u64) {
        let result = (|| {
            let now = lifecycle::awake_now()?;
            while self
                .lease_restarts
                .front()
                .is_some_and(|last| now.saturating_sub(*last) > Duration::from_secs(60))
            {
                self.lease_restarts.pop_front();
            }
            if self.lease_restarts.len() >= 3 {
                return Err(AppError::new(
                    "lease_recovery_exhausted",
                    "服务租约连续恢复失败，已停止自动恢复",
                ));
            }
            let source = self.applied_config.clone().ok_or_else(|| {
                AppError::new(
                    "recovery_config_missing",
                    "缺少已应用的配置，请手动启动内核",
                )
            })?;
            let Some(RunningCore::Service(client)) = self.running.as_ref() else {
                return Err(AppError::new(
                    "recovery_owner_missing",
                    "服务恢复所属实例已变更",
                ));
            };
            self.lease_restarts.push_back(now);
            let status = client.call(&Request::Start { config: source })?;
            if generation != self.app.generation()
                || self.app.stop_pending.load(Ordering::Acquire) != 0
            {
                return Ok(());
            }
            self.core_status = status;
            if !self.core_status.running {
                return Err(AppError::new(
                    "core_recovery_failed",
                    "服务恢复后内核尚未就绪",
                ));
            }
            self.phase = Phase::Running;
            self.last_error = None;
            if self.preferences.system_proxy {
                if let Some(port) = self.applied_proxy_port {
                    self.proxy.enable(port)?;
                }
            }
            Ok(())
        })();
        if generation != self.app.generation() || self.app.stop_pending.load(Ordering::Acquire) != 0
        {
            return;
        }
        if let Err(error) = result {
            self.phase = Phase::Error;
            self.core_status.error_code = Some(error.code.clone());
            self.last_error = Some(error.message);
            crate::diagnostics::record(
                &self.logs,
                "lease_recovery_failed",
                serde_json::json!({"generation":generation,"code":error.code}),
            );
        }
        self.publish();
    }

    pub fn request_exit(&mut self, force: bool) -> Result<bool> {
        if self.dirty && !force {
            self.app.emit(
                "desktop-exit-requested",
                serde_json::json!({"reason":"unsaved-config","force":false}),
            );
            return Ok(false);
        }
        self.exiting = true;
        self.stop(false)?;
        Ok(true)
    }
}

fn stop_running(running: &mut RunningCore) -> Result<()> {
    match running {
        RunningCore::Direct(core) => core.stop(),
        RunningCore::Service(client) => client.call(&Request::Stop).map(|_| ()),
    }
}
