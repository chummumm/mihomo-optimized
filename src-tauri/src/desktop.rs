use crate::{
    config::{self, ConfigDocument, Preferences},
    core::{self, Controller, CoreStatus, CoreSupervisor},
    error::{AppError, Result},
    host::HostContext,
    lifecycle::{self, Cancellation, RecoveryWindow, StopReason},
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

#[derive(Clone, Copy, Default, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Phase {
    #[default]
    Stopped,
    Starting,
    Running,
    Recovering,
    Stopping,
    Error,
}

#[derive(Clone, Default, Serialize)]
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
pub struct TunDeviceStatus {
    pub configured_name: Option<String>,
    pub effective_name: Option<String>,
    pub managed_name: String,
    pub managed: bool,
    pub can_use_managed: bool,
    pub config_revision: String,
}

impl Default for TunDeviceStatus {
    fn default() -> Self {
        Self {
            configured_name: None,
            effective_name: None,
            managed_name: config::MANAGED_TUN_DEVICE.into(),
            managed: false,
            can_use_managed: false,
            config_revision: String::new(),
        }
    }
}

#[derive(Clone, Default, Serialize)]
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
    pub process_state: String,
    pub control_state: String,
    pub listeners_ready: Option<bool>,
    pub pid: Option<u32>,
    pub operation: Option<String>,
    pub status_instance: String,
    pub status_sequence: u64,
    pub intent_revision: u64,
    pub observed_at: u64,
    pub status_stale: bool,
    pub service_instance: Option<String>,
    pub service_sequence: u64,
    pub tun_device: TunDeviceStatus,
}

enum RunningCore {
    Direct(Box<CoreSupervisor>),
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
    applied_config_revision: Option<String>,
    cancellation: Cancellation,
    tun_device: TunDeviceStatus,
    source_tun_valid: bool,
    attempted_config_revision: Option<String>,
    conflict_config_revision: Option<String>,
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
        let cancellation = app.cancellation();
        let mut manager = Self {
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
            applied_config_revision: None,
            cancellation,
            tun_device: TunDeviceStatus::default(),
            source_tun_valid: false,
            attempted_config_revision: None,
            conflict_config_revision: None,
        };
        match config::read(&manager.config_path)
            .and_then(|document| manager.remember_config(&document))
        {
            Ok(()) => (),
            Err(error) => manager.last_error = Some(error.message),
        }
        Ok(manager)
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

    pub fn operation_cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }

    pub fn begin_operation(&mut self, generation: u64) {
        if let Ok(status) = self.app.status() {
            if status.intent_revision == generation {
                self.run_intent = status.desired_running;
            }
        }
        self.cancellation = Cancellation::new(self.app.runtime_generation.clone(), generation);
        if let Some(RunningCore::Direct(core)) = &mut self.running {
            core.set_cancellation(self.cancellation.clone());
        }
    }

    pub fn status(&self) -> DesktopStatus {
        self.app.status().unwrap_or_else(|_| self.build_status())
    }

    fn build_status(&self) -> DesktopStatus {
        let mut tun_device = self.tun_device.clone();
        tun_device.can_use_managed = self.can_use_managed_tun_device();
        DesktopStatus {
            phase: self.phase,
            system_proxy: self.proxy.owned().unwrap_or(false),
            tun: self.preferences.tun,
            autostart: windows::autostart_enabled().unwrap_or(false),
            controller: if self.core_status.process_state == "alive"
                && matches!(self.core_status.control_state.as_str(), "ready" | "busy")
            {
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
            process_state: if self.core_status.process_state.is_empty() {
                "exited".into()
            } else {
                self.core_status.process_state.clone()
            },
            control_state: if self.core_status.control_state.is_empty() {
                "unknown".into()
            } else {
                self.core_status.control_state.clone()
            },
            listeners_ready: self.core_status.listeners_ready,
            pid: self.core_status.pid,
            operation: match self.phase {
                Phase::Starting => Some("starting".into()),
                Phase::Stopping => Some("stopping".into()),
                _ => self.core_status.operation.clone(),
            },
            status_instance: self.app.instance.clone(),
            status_sequence: 0,
            intent_revision: self.cancellation.revision(),
            observed_at: self.core_status.observed_at,
            status_stale: false,
            service_instance: self.core_status.service_instance.clone(),
            service_sequence: self.core_status.status_sequence,
            tun_device,
        }
    }

    pub fn publish(&self) {
        self.app.publish_status(self.build_status());
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

    fn remember_config(&mut self, document: &ConfigDocument) -> Result<()> {
        self.tun_device.config_revision = document.revision.clone();
        let name = match config::configured_tun_device(&document.content) {
            Ok(name) => name,
            Err(_) => {
                // The editor must still be able to read and repair invalid YAML.
                self.source_tun_valid = false;
                self.tun_device.configured_name = None;
                self.tun_device.effective_name = None;
                self.tun_device.managed = false;
                return Ok(());
            }
        };
        self.source_tun_valid = true;
        let retired =
            config::reconcile_tun_device_override(&mut self.preferences, &document.content)?;
        self.tun_device.configured_name = name.clone();
        self.tun_device.managed = self
            .preferences
            .tun_device_override
            .as_ref()
            .is_some_and(|choice| choice.applies_to(name.as_deref()));
        self.tun_device.effective_name = if self.tun_device.managed {
            Some(config::MANAGED_TUN_DEVICE.into())
        } else {
            name
        };
        if retired {
            self.cancellation.check()?;
            self.persist()?;
        }
        Ok(())
    }

    fn read_runtime_source(&mut self, expected_revision: Option<&str>) -> Result<(String, String)> {
        let document = config::read(&self.config_path)?;
        self.remember_config(&document)?;
        if let Some(expected) = expected_revision {
            config::require_revision(&document.content, expected)?;
        }
        let source = config::source_with_tun_device(
            &document.content,
            self.preferences.tun_device_override.as_ref(),
        )?;
        Ok((source, document.revision))
    }

    fn observe_tun_device_conflict(&mut self) {
        if self.core_status.error_code.as_deref() == Some("tun_device_conflict") {
            if self.conflict_config_revision.is_none() {
                self.conflict_config_revision = self.attempted_config_revision.clone();
            }
        } else {
            self.conflict_config_revision = None;
            self.attempted_config_revision = self.applied_config_revision.clone();
        }
    }

    fn can_use_managed_tun_device(&self) -> bool {
        self.preferences.tun
            && self.source_tun_valid
            && !self.tun_device.managed
            && !self
                .tun_device
                .configured_name
                .as_deref()
                .is_some_and(|name| name.eq_ignore_ascii_case(config::MANAGED_TUN_DEVICE))
            && self.core_status.error_code.as_deref() == Some("tun_device_conflict")
            && self.conflict_config_revision.as_deref()
                == Some(self.tun_device.config_revision.as_str())
            && matches!(self.phase, Phase::Error | Phase::Running)
            && matches!(self.core_status.process_state.as_str(), "exited" | "alive")
            && self.core_status.operation.is_none()
            && !self.dirty
            && !self.updating
            && !self.exiting
            && self.app.stop_pending.load(Ordering::Acquire) == 0
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
        self.start_with_revision(persist, None)
    }

    fn start_with_revision(
        &mut self,
        persist: bool,
        expected_revision: Option<&str>,
    ) -> Result<()> {
        self.require_idle_update()?;
        self.cancellation.check()?;
        if self.phase == Phase::Running {
            return Ok(());
        }
        self.phase = Phase::Starting;
        self.run_intent = true;
        self.lease_recovery_pending = None;
        self.communication_recovery = None;
        self.last_error = None;
        self.publish();
        let mut result = (|| {
            let (content, revision) = self.read_runtime_source(expected_revision)?;
            if self.preferences.system_proxy {
                config::mixed_port(&content)?;
            }
            if let Some(running) = self.running.as_mut() {
                stop_running(running, &self.cancellation)?;
            }
            self.running = None;
            self.app.pause_service_observation();
            self.cancellation.check()?;
            if self.preferences.tun {
                self.ensure_service()?;
                self.cancellation.check()?;
                let client = match self.app.service_client() {
                    Some(client) => client,
                    None => ServiceClient::new(self.app.instance.clone())?,
                };
                self.app.set_service_client(Some(client.clone()));
                self.running = Some(RunningCore::Service(client));
            } else {
                let mut supervisor = CoreSupervisor::new(
                    core::fixed_core_path()?,
                    self.directory.join("runtime"),
                    self.logs.clone(),
                );
                supervisor.set_cancellation(self.cancellation.clone());
                self.running = Some(RunningCore::Direct(Box::new(supervisor)));
            }
            self.attempted_config_revision = Some(revision.clone());
            self.conflict_config_revision = None;
            self.core_status = match self.running.as_mut().expect("runtime set above") {
                RunningCore::Service(client) => client.call(
                    Request::Start {
                        config: content.clone(),
                    },
                    &self.cancellation,
                )?,
                RunningCore::Direct(supervisor) => supervisor.start(content.clone(), false)?,
            };
            self.cancellation.check()?;
            self.applied_config = Some(content.clone());
            self.applied_config_revision = Some(revision);
            self.applied_proxy_port = config::mixed_port(&content).ok();
            if !self.core_status.running {
                return Err(AppError::new("core_start_failed", "内核尚未就绪"));
            }
            if self.preferences.system_proxy {
                self.proxy.enable(config::mixed_port(&content)?)?;
            }
            self.cancellation.check()?;
            self.phase = Phase::Running;
            if persist {
                self.preferences.desired_running = true;
                self.persist()?;
            }
            Ok(())
        })();
        if let Some(mut error) = result.as_ref().err().cloned() {
            if error.code != "operation_cancelled" && self.cancellation.check().is_ok() {
                let _ = self.proxy.restore();
                let mut cleanup_unconfirmed = false;
                if let Some(running) = self.running.as_mut() {
                    if let Err(cleanup) = stop_running(running, &self.cancellation) {
                        cleanup_unconfirmed = true;
                        error = core::startup_cleanup_error(&error, &cleanup);
                    }
                    self.core_status =
                        running_status(running).unwrap_or_else(|_| self.core_status.clone());
                }
                if cleanup_unconfirmed {
                    self.core_status.process_state = "unknown".into();
                    self.core_status.control_state = "unknown".into();
                    self.core_status.controller = None;
                    self.core_status.operation = Some("stopping".into());
                } else if self.core_status.pid.is_none() && self.core_status.operation.is_none() {
                    self.running = None;
                    self.app.pause_service_observation();
                }
                self.applied_proxy_port = None;
                self.phase = if cleanup_unconfirmed {
                    Phase::Stopping
                } else {
                    Phase::Error
                };
                self.last_error = Some(error.message.clone());
                self.core_status.error_code = Some(error.code.clone());
                self.observe_tun_device_conflict();
                result = Err(error);
            }
        }
        self.publish();
        result
    }

    pub fn stop(&mut self, persist: bool) -> Result<()> {
        self.resume_pending = None;
        self.lease_recovery_pending = None;
        self.communication_recovery = None;
        if persist {
            self.run_intent = false;
            self.preferences.desired_running = false;
        }
        self.phase = Phase::Stopping;
        self.publish();
        // Preferences/proxy restoration are independent cleanup steps. Neither
        // failure is allowed to prevent the owned process from receiving Stop.
        let persist_error = if persist { self.persist().err() } else { None };
        let proxy_error = self.proxy.restore().err();
        let result = self
            .running
            .as_mut()
            .map(|running| stop_running(running, &self.cancellation))
            .unwrap_or(Ok(()));
        if let Some(running) = self.running.as_mut() {
            self.core_status = running_status(running).unwrap_or_else(|_| self.core_status.clone());
        }
        if let Err(error) = result {
            self.phase = Phase::Stopping;
            self.last_error = Some(error.message.clone());
            self.core_status.error_code = Some(error.code.clone());
            self.core_status.operation = Some("stopping".into());
            self.core_status.process_state = "unknown".into();
            self.publish();
            return Err(error);
        }
        self.running = None;
        self.app.pause_service_observation();
        self.core_status = CoreStatus {
            process_state: "exited".into(),
            control_state: "unknown".into(),
            listeners_ready: Some(false),
            stop_reason: Some(StopReason::UserStop),
            observed_at: core::observed_now(),
            ..CoreStatus::default()
        };
        self.applied_config = None;
        self.applied_config_revision = None;
        self.applied_proxy_port = None;
        self.phase = Phase::Stopped;
        let error = persist_error.or(proxy_error);
        self.last_error = error.as_ref().map(|error| error.message.clone());
        self.publish();
        error.map_or(Ok(()), Err)
    }

    pub fn restart(&mut self) -> Result<()> {
        self.require_idle_update()?;
        self.stop(false)?;
        self.cancellation.check()?;
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
            config::runtime_config(&self.read_runtime_source(None)?.0, true, 9090)?;
            self.ensure_service()?;
        }
        let was_running = self.run_intent;
        let previous = self.preferences.tun;
        if was_running {
            self.stop(false)?;
        }
        self.preferences.tun = enabled;
        if was_running {
            if let Err(error) = self.start(false) {
                if error.code == "operation_cancelled" {
                    return Err(error);
                }
                if error.code == "tun_device_conflict" {
                    // Keep the explicit TUN intent so the conflict action can
                    // repair its name; falling back to direct mode would hide it.
                    self.persist()?;
                    self.publish();
                    return Err(error);
                }
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

    pub fn use_managed_tun_device(&mut self, expected_revision: &str) -> Result<()> {
        self.select_tun_device(true, expected_revision)
    }

    pub fn use_configured_tun_device(&mut self, expected_revision: &str) -> Result<()> {
        self.select_tun_device(false, expected_revision)
    }

    fn select_tun_device(&mut self, managed: bool, expected_revision: &str) -> Result<()> {
        let result = (|| {
            self.require_idle_update()?;
            self.cancellation.check()?;
            if self.dirty {
                return Err(AppError::new(
                    "config_unsaved",
                    "编辑器有未保存修改，请先保存或放弃后再选择 TUN 接口",
                ));
            }
            let document = config::read(&self.config_path)?;
            self.remember_config(&document)?;
            config::require_revision(&document.content, expected_revision)?;
            if (managed && !self.can_use_managed_tun_device())
                || (!managed && !self.tun_device.managed)
            {
                return Err(AppError::new(
                    "tun_device_choice_unavailable",
                    "TUN 接口状态已变化，请刷新状态后再选择接口",
                ));
            }
            let was_running = self.run_intent;
            let core_version = self.core_status.version.clone();
            // A retained process/validator must finish cleanup before the
            // selected interface can change. Stop may cancel this whole action.
            self.stop(false)?;
            self.cancellation.check()?;
            let current = config::read(&self.config_path)?;
            self.remember_config(&current)?;
            config::require_revision(&current.content, expected_revision)?;
            let previous = self.preferences.tun_device_override.clone();
            let selected = managed.then(|| {
                config::TunDeviceOverride::managed(self.tun_device.configured_name.clone())
            });
            self.cancellation.check()?;
            self.preferences.tun_device_override = selected;
            if let Err(error) = self.persist() {
                self.preferences.tun_device_override = previous;
                return Err(error);
            }
            // The preferences commit cannot resurrect an operation cancelled
            // during disk I/O. Roll back this choice while the manager is still
            // exclusively owned, before any queued Stop writes its own intent.
            let confirmed: Result<ConfigDocument> = (|| {
                self.cancellation.check()?;
                let latest = config::read(&self.config_path)?;
                config::require_revision(&latest.content, expected_revision)?;
                Ok(latest)
            })();
            let latest = match confirmed {
                Ok(latest) => latest,
                Err(error) => {
                    self.preferences.tun_device_override = previous;
                    if let Err(rollback) = self.persist() {
                        return Err(AppError::new(
                            &error.code,
                            format!("{}；恢复接口选择失败：{}", error.message, rollback.message),
                        ));
                    }
                    if error.code == "config_conflict" {
                        let latest = config::read(&self.config_path)?;
                        self.remember_config(&latest)?;
                    } else {
                        self.remember_config(&current)?;
                    }
                    return Err(error);
                }
            };
            self.remember_config(&latest)?;
            self.conflict_config_revision = None;
            crate::diagnostics::record(
                &self.logs,
                "tun_device_choice",
                serde_json::json!({
                    "selection":if managed { "managed" } else { "configured" },
                    "configuredName":config::diagnostic_tun_device(self.tun_device.configured_name.as_deref()),
                    "effectiveName":config::diagnostic_tun_device(self.tun_device.effective_name.as_deref()),
                    "configRevision":expected_revision,"generation":self.cancellation.revision(),
                    "desiredRunning":was_running,"desktopVersion":env!("CARGO_PKG_VERSION"),
                    "coreVersion":core_version,
                }),
            );
            self.publish();
            self.cancellation.check()?;
            if was_running {
                self.start_with_revision(true, Some(expected_revision))?;
            }
            Ok(())
        })();
        self.publish();
        result
    }

    fn ensure_service(&self) -> Result<()> {
        windows::wait_for_service_maintenance("install", Some(&self.cancellation))?;
        let state = service::status()?;
        if !state.installed || !state.running {
            windows::run_elevated_service_action_cancellable("install", Some(&self.cancellation))?;
        }
        Ok(())
    }

    pub fn install_service(&self) -> Result<()> {
        windows::wait_for_service_maintenance("install", Some(&self.cancellation))?;
        let state = service::status()?;
        if !state.installed || !state.running {
            windows::run_elevated_service_action_cancellable("install", Some(&self.cancellation))?;
        }
        self.publish();
        Ok(())
    }

    pub fn uninstall_service(&mut self) -> Result<()> {
        self.require_idle_update()?;
        if self.preferences.tun {
            self.set_tun(false)?;
        }
        windows::run_elevated_service_action_cancellable("uninstall", Some(&self.cancellation))?;
        self.publish();
        Ok(())
    }

    pub fn read_config(&mut self) -> Result<ConfigDocument> {
        let document = config::read(&self.config_path)?;
        self.remember_config(&document)?;
        self.publish();
        Ok(document)
    }

    pub fn save_config(
        &mut self,
        content: &str,
        expected_revision: Option<&str>,
    ) -> Result<ConfigDocument> {
        let saved = config::save(&self.config_path, content, expected_revision)?;
        self.remember_config(&saved)?;
        self.publish();
        self.app.emit(
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
        self.remember_config(&saved)?;
        self.publish();
        self.app.emit(
            "desktop-config-changed",
            serde_json::json!({"revision":saved.revision}),
        );
        Ok(saved)
    }

    pub fn reload_config(&mut self, _force: bool) -> Result<()> {
        self.require_idle_update()?;
        let (content, revision) = self.read_runtime_source(None)?;
        if self.phase != Phase::Running {
            return Ok(());
        }
        if self.preferences.system_proxy {
            config::mixed_port(&content)?;
        }
        self.attempted_config_revision = Some(revision.clone());
        self.conflict_config_revision = None;
        let result = match self.running.as_mut() {
            Some(RunningCore::Direct(core)) => core.reload(content.clone()),
            Some(RunningCore::Service(client)) => client.call(
                Request::Reload {
                    config: content.clone(),
                },
                &self.cancellation,
            ),
            None => return Ok(()),
        };
        let status = match result {
            Ok(status) => status,
            Err(error) => {
                self.core_status = match self.running.as_mut() {
                    Some(RunningCore::Direct(core)) => core.status(),
                    Some(RunningCore::Service(client)) => {
                        client.status().unwrap_or_else(|_| self.core_status.clone())
                    }
                    None => CoreStatus::default(),
                };
                self.phase = if self.core_status.running {
                    Phase::Running
                } else {
                    Phase::Error
                };
                self.last_error = Some(error.message.clone());
                self.core_status.error_code = Some(error.code.clone());
                self.observe_tun_device_conflict();
                if self.core_status.process_state == "exited"
                    || self.core_status.listeners_ready == Some(false)
                {
                    let _ = self.proxy.restore();
                }
                self.publish();
                return Err(error);
            }
        };
        self.core_status = status;
        self.applied_config = Some(content.clone());
        self.applied_config_revision = Some(revision);
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
        self.app.emit(
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

    pub fn abort_update(&mut self) {
        self.updating = false;
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
        self.begin_operation(generation);
        if self.core_status.error_code.as_deref() == Some("core_termination_unconfirmed") {
            // An outstanding cleanup is an ownership barrier, not a health
            // recovery request. Keep observing until exit is confirmed.
            self.lease_recovery_pending = None;
            self.resume_pending = None;
        }
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
                    client.reconcile(resume)
                } else {
                    client.status()
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
        if let Some(status) =
            unconfirmed_cleanup_observation(&self.core_status, self.last_error.as_deref(), &result)
        {
            self.communication_recovery = None;
            self.phase = Phase::Stopping;
            self.last_error = status.error.clone();
            self.core_status = status;
            self.publish();
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
                self.phase =
                    if status.stop_reason == Some(StopReason::LeaseExpired) && self.run_intent {
                        self.lease_recovery_pending = Some(generation);
                        Phase::Recovering
                    } else {
                        phase_for_core(&status)
                    };
                self.last_error = status.error.clone();
                self.core_status = status;
                self.observe_tun_device_conflict();
                if self.core_status.process_state == "exited"
                    || self.core_status.listeners_ready == Some(false)
                {
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
                    "service_unavailable"
                        | "service_timeout"
                        | "service_owner_cleanup"
                        | "io_error"
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
                self.core_status.control_state = "unreachable".into();
                // Transport uncertainty does not prove proxy listeners stopped.
                // A disproved service identity/protocol is a separate hard fault.
                if !temporary {
                    self.core_status.controller = None;
                    let _ = self.proxy.restore();
                }
            }
        }
        self.publish();
    }

    pub fn request_reconcile(&mut self, generation: u64) {
        if !self.run_intent
            || self.updating
            || self.exiting
            || self.core_status.error_code.as_deref() == Some("core_termination_unconfirmed")
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
            self.attempted_config_revision = self.applied_config_revision.clone();
            self.conflict_config_revision = None;
            let status = client.call(Request::Start { config: source }, &self.cancellation)?;
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
            self.observe_tun_device_conflict();
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

fn stop_running(running: &mut RunningCore, cancellation: &Cancellation) -> Result<()> {
    match running {
        RunningCore::Direct(core) => core.stop(),
        RunningCore::Service(client) => client.call(Request::Stop, cancellation).map(|_| ()),
    }
}

fn running_status(running: &mut RunningCore) -> Result<CoreStatus> {
    match running {
        RunningCore::Direct(core) => Ok(core.status()),
        RunningCore::Service(client) => client.status(),
    }
}

fn unconfirmed_cleanup_observation(
    previous: &CoreStatus,
    failure: Option<&str>,
    observation: &Result<CoreStatus>,
) -> Option<CoreStatus> {
    if previous.error_code.as_deref() != Some("core_termination_unconfirmed")
        || observation
            .as_ref()
            .is_ok_and(|status| status.process_state == "exited")
    {
        return None;
    }
    let mut status = observation.as_ref().unwrap_or(previous).clone();
    status.running = false;
    status.recovering = false;
    status.control_state = "unknown".into();
    status.listeners_ready = None;
    status.controller = None;
    status.operation = Some("stopping".into());
    status.error_code = previous.error_code.clone();
    status.error = failure
        .map(str::to_owned)
        .or_else(|| previous.error.clone());
    Some(status)
}

fn phase_for_core(status: &CoreStatus) -> Phase {
    match status.operation.as_deref() {
        Some("stopping") => Phase::Stopping,
        Some("starting" | "restarting") => Phase::Starting,
        Some(_) => Phase::Recovering,
        None if status.recovering => Phase::Recovering,
        None if status.running => Phase::Running,
        None if status.process_state == "alive" || status.process_state == "unknown" => {
            Phase::Recovering
        }
        None if matches!(
            status.stop_reason,
            Some(StopReason::UserStop | StopReason::Shutdown)
        ) =>
        {
            Phase::Stopped
        }
        None => Phase::Error,
    }
}

impl DesktopStatus {
    pub fn preserve_core_observation(&mut self, current: &Self) {
        // A service snapshot orders process facts, not a later host-side trust
        // failure. Keep the newest PID/observation while preserving a locally
        // disproved controller or failed authenticated observation.
        let local_failure = self.control_state == "unreachable"
            && self
                .error_code
                .as_deref()
                .is_some_and(|code| code.starts_with("service_") || code == "io_error");
        // Cleanup's newer "stopped" service observation must not erase the
        // precise native failure that made the host stop the failed attempt.
        let tun_conflict = self.error_code.as_deref() == Some("tun_device_conflict");
        let cleanup_unconfirmed =
            self.error_code.as_deref() == Some("core_termination_unconfirmed");
        let failure = (local_failure || tun_conflict || cleanup_unconfirmed)
            .then(|| (self.phase, self.error_code.clone(), self.last_error.clone()));
        self.phase = current.phase;
        self.process_state = current.process_state.clone();
        self.control_state = current.control_state.clone();
        self.listeners_ready = current.listeners_ready;
        self.pid = current.pid;
        self.operation = current.operation.clone();
        self.observed_at = current.observed_at;
        self.stop_reason = current.stop_reason;
        self.error_code = current.error_code.clone();
        self.last_error = current.last_error.clone();
        self.tun_health = current.tun_health.clone();
        self.core_version = current.core_version.clone();
        self.recovery_generation = current.recovery_generation;
        self.service_sequence = current.service_sequence;
        self.controller = current.controller.clone();
        if let Some((phase, code, message)) = failure {
            self.phase = phase;
            self.error_code = code;
            self.last_error = message;
            if local_failure {
                self.control_state = "unreachable".into();
                self.controller = None;
            }
            if cleanup_unconfirmed {
                self.retain_unconfirmed_cleanup();
            }
        }
    }
    pub fn apply_core_observation(&mut self, core: &CoreStatus) {
        let cleanup_failure = (self.error_code.as_deref() == Some("core_termination_unconfirmed"))
            .then(|| self.last_error.clone());
        self.phase = phase_for_core(core);
        self.process_state = core.process_state.clone();
        self.control_state = core.control_state.clone();
        self.listeners_ready = core.listeners_ready;
        self.pid = core.pid;
        self.operation = core.operation.clone();
        self.observed_at = core.observed_at;
        self.stop_reason = core.stop_reason;
        self.error_code = core.error_code.clone();
        self.last_error = core.error.clone();
        self.tun_health = core.tun_health.clone();
        self.core_version = core.version.clone();
        self.recovery_generation = core.recovery_generation;
        self.service_instance = core.service_instance.clone();
        self.service_sequence = core.status_sequence;
        self.controller = if core.process_state == "alive"
            && matches!(core.control_state.as_str(), "ready" | "busy")
        {
            core.controller.clone()
        } else {
            None
        };
        if let Some(message) = cleanup_failure {
            self.error_code = Some("core_termination_unconfirmed".into());
            self.last_error = message;
            self.retain_unconfirmed_cleanup();
        }
    }

    fn retain_unconfirmed_cleanup(&mut self) {
        // A heartbeat can refresh the exact process facts, but only the
        // lifecycle owner can confirm its outstanding cleanup and release it.
        self.phase = Phase::Stopping;
        self.operation = Some("stopping".into());
        self.control_state = "unknown".into();
        self.listeners_ready = None;
        self.controller = None;
        self.tun_device.can_use_managed = false;
    }
}

#[cfg(test)]
mod cleanup_observation_tests {
    use super::*;

    #[test]
    fn cleanup_poll_requires_confirmed_exit_and_retains_the_original_failure() {
        let previous = CoreStatus {
            process_state: "unknown".into(),
            error_code: Some("core_termination_unconfirmed".into()),
            error: Some("original native conflict; cleanup exit unconfirmed".into()),
            service_instance: Some("service".into()),
            status_sequence: 1,
            ..CoreStatus::default()
        };
        let running = CoreStatus {
            running: true,
            process_state: "alive".into(),
            control_state: "ready".into(),
            pid: Some(111),
            listeners_ready: Some(true),
            controller: Some(Controller {
                url: "http://127.0.0.1:9090".into(),
                secret: "fixture".into(),
            }),
            service_instance: Some("service".into()),
            status_sequence: 2,
            observed_at: 2,
            ..CoreStatus::default()
        };
        let observed =
            unconfirmed_cleanup_observation(&previous, None, &Ok(running.clone())).unwrap();
        assert_eq!(observed.pid, Some(111));
        assert_eq!(observed.status_sequence, 2);
        assert_eq!(observed.observed_at, 2);
        assert_eq!(observed.process_state, "alive");
        assert!(!observed.running);
        assert!(phase_for_core(&observed) == Phase::Stopping);
        assert_eq!(observed.control_state, "unknown");
        assert!(observed.controller.is_none());
        assert_eq!(observed.error, previous.error);
        assert_eq!(observed.error_code, previous.error_code);

        let failed_query = unconfirmed_cleanup_observation(
            &observed,
            previous.error.as_deref(),
            &Err(AppError::new("service_timeout", "temporary status timeout")),
        )
        .unwrap();
        assert_eq!(failed_query.pid, observed.pid);
        assert_eq!(failed_query.observed_at, observed.observed_at);
        assert_eq!(failed_query.error, previous.error);
        assert_eq!(failed_query.error_code, previous.error_code);
        assert!(phase_for_core(&failed_query) == Phase::Stopping);

        let exited = CoreStatus {
            process_state: "exited".into(),
            ..CoreStatus::default()
        };
        assert!(unconfirmed_cleanup_observation(&observed, None, &Ok(exited)).is_none());
        assert!(unconfirmed_cleanup_observation(&running, None, &Ok(running.clone())).is_none());
    }
}
