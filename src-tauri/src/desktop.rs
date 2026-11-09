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
    collections::{HashSet, VecDeque},
    path::PathBuf,
    sync::{atomic::Ordering, Arc, Mutex},
    time::{Duration, Instant},
};

#[derive(Clone)]
pub struct HostState(pub Arc<Mutex<DesktopManager>>);

const TUN_DEVICE_RECOVERY_OPERATION: &str = "recovering_tun_device";

#[derive(Clone, Default)]
pub(crate) enum TunDeviceRecoveryStatus {
    #[default]
    Idle,
    Pending,
    Succeeded,
    Failed(AppError),
}

#[derive(Default)]
struct TunDeviceRecovery {
    active: bool,
    generation: u64,
    attempted_revisions: HashSet<String>,
    outcome: Option<Result<()>>,
}

impl TunDeviceRecovery {
    fn enter_generation(&mut self, generation: u64) {
        if self.generation != generation {
            self.generation = generation;
            self.attempted_revisions.clear();
            self.outcome = None;
        }
    }

    fn claim(&mut self, generation: u64, revision: &str) -> bool {
        self.enter_generation(generation);
        let claimed = self.attempted_revisions.insert(revision.into());
        if claimed {
            self.outcome = None;
        }
        claimed
    }
}

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
    pub config_revision: String,
}

impl Default for TunDeviceStatus {
    fn default() -> Self {
        Self {
            configured_name: None,
            effective_name: None,
            managed_name: config::MANAGED_TUN_DEVICE.into(),
            managed: false,
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
    // Raw service heartbeats share the host's admission policy, but this is
    // internal sequencing state rather than a public UI capability.
    #[serde(skip)]
    pub(crate) auto_tun_recovery_eligible: bool,
    #[serde(skip)]
    pub(crate) tun_device_recovery: TunDeviceRecoveryStatus,
    #[serde(skip)]
    pub(crate) core_observation: Option<CoreStatus>,
}

enum RunningCore {
    Direct(Box<CoreSupervisor>),
    Service(ServiceClient),
    #[cfg(test)]
    Fixture(Arc<Mutex<TestRuntime>>),
}

#[cfg(test)]
type RecoveryCheckpoint = Box<dyn FnMut(&str) + Send>;

/// The desktop tests script owned lifecycle results; no test starts a service,
/// changes a real adapter, or replaces the source revision/cancellation checks.
#[cfg(test)]
#[derive(Default)]
struct TestRuntime {
    status: CoreStatus,
    starts: VecDeque<Result<CoreStatus>>,
    reloads: VecDeque<Result<CoreStatus>>,
    observations: VecDeque<Result<CoreStatus>>,
    stops: VecDeque<Result<()>>,
    actions: Vec<String>,
    checkpoint: Option<RecoveryCheckpoint>,
}

#[cfg(test)]
impl TestRuntime {
    fn checkpoint(&mut self, stage: &str) {
        if let Some(checkpoint) = &mut self.checkpoint {
            checkpoint(stage);
        }
    }

    fn start(&mut self, source: &str) -> Result<CoreStatus> {
        self.actions.push(format!(
            "start:{}",
            config::configured_tun_device(source)?.unwrap_or_default()
        ));
        self.checkpoint("start");
        let result = self.starts.pop_front().expect("unexpected extra start");
        self.status = match &result {
            Ok(status) => status.clone(),
            Err(error) => CoreStatus {
                process_state: "exited".into(),
                error_code: Some(error.code.clone()),
                error: Some(error.message.clone()),
                observed_at: core::observed_now(),
                ..CoreStatus::default()
            },
        };
        result
    }

    fn reload(&mut self, source: &str) -> Result<CoreStatus> {
        self.actions.push(format!(
            "reload:{}",
            config::configured_tun_device(source)?.unwrap_or_default()
        ));
        let result = self.reloads.pop_front().expect("unexpected reload");
        match &result {
            Ok(status) => self.status = status.clone(),
            Err(error) => {
                self.status.error_code = Some(error.code.clone());
                self.status.error = Some(error.message.clone());
            }
        }
        result
    }

    fn stop(&mut self) -> Result<()> {
        self.actions.push("stop".into());
        self.checkpoint("stop");
        let result = self.stops.pop_front().unwrap_or(Ok(()));
        if result.is_ok() {
            self.status = CoreStatus {
                process_state: "exited".into(),
                observed_at: core::observed_now(),
                ..CoreStatus::default()
            };
        } else {
            self.status.process_state = "unknown".into();
        }
        result
    }

    fn poll(&mut self) -> Result<CoreStatus> {
        self.actions.push("poll".into());
        let result = self
            .observations
            .pop_front()
            .unwrap_or_else(|| Ok(self.status.clone()));
        if let Ok(status) = &result {
            self.status = status.clone();
        }
        result
    }
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
    tun_device_recovery: TunDeviceRecovery,
    #[cfg(test)]
    test_runtime: Option<Arc<Mutex<TestRuntime>>>,
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
            tun_device_recovery: TunDeviceRecovery::default(),
            #[cfg(test)]
            test_runtime: None,
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
        self.tun_device_recovery.enter_generation(generation);
        if let Some(RunningCore::Direct(core)) = &mut self.running {
            core.set_cancellation(self.cancellation.clone());
        }
    }

    pub fn status(&self) -> DesktopStatus {
        self.app.status().unwrap_or_else(|_| self.build_status())
    }

    fn build_status(&self) -> DesktopStatus {
        let recovering_device = (self.tun_device_recovery.active || self.can_recover_tun_device())
            && self.core_status.error_code.as_deref() != Some("core_termination_unconfirmed");
        let mut status = DesktopStatus {
            phase: if recovering_device {
                Phase::Recovering
            } else {
                self.phase
            },
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
            operation: if recovering_device {
                Some(TUN_DEVICE_RECOVERY_OPERATION.into())
            } else {
                match self.phase {
                    Phase::Starting => Some("starting".into()),
                    Phase::Stopping => Some("stopping".into()),
                    _ => self.core_status.operation.clone(),
                }
            },
            status_instance: self.app.instance.clone(),
            status_sequence: 0,
            intent_revision: self.cancellation.revision(),
            observed_at: self.core_status.observed_at,
            status_stale: false,
            service_instance: self.core_status.service_instance.clone(),
            service_sequence: self.core_status.status_sequence,
            tun_device: self.tun_device.clone(),
            auto_tun_recovery_eligible: self.auto_tun_recovery_eligible(),
            tun_device_recovery: if recovering_device {
                TunDeviceRecoveryStatus::Pending
            } else if self.tun_device_recovery.generation == self.cancellation.revision() {
                match &self.tun_device_recovery.outcome {
                    Some(Ok(())) => TunDeviceRecoveryStatus::Succeeded,
                    Some(Err(error)) => TunDeviceRecoveryStatus::Failed(error.clone()),
                    None => TunDeviceRecoveryStatus::Idle,
                }
            } else {
                TunDeviceRecoveryStatus::Idle
            },
            core_observation: Some(self.core_status.clone()),
        };
        status.project_tun_device_recovery();
        status
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

    fn auto_tun_recovery_eligible(&self) -> bool {
        self.run_intent
            && self.preferences.tun
            && self.source_tun_valid
            && !self.tun_device.managed
            && !self
                .tun_device
                .configured_name
                .as_deref()
                .is_some_and(|name| name.eq_ignore_ascii_case(config::MANAGED_TUN_DEVICE))
            && self
                .attempted_config_revision
                .as_ref()
                .or(self.applied_config_revision.as_ref())
                == Some(&self.tun_device.config_revision)
            && !self.dirty
            && !self.updating
            && !self.exiting
            && !self.tun_device_recovery.active
            && (self.tun_device_recovery.generation != self.cancellation.revision()
                || !self
                    .tun_device_recovery
                    .attempted_revisions
                    .contains(&self.tun_device.config_revision))
            && self.cancellation.check().is_ok()
            && self.app.stop_pending.load(Ordering::Acquire) == 0
    }

    fn can_recover_tun_device(&self) -> bool {
        self.auto_tun_recovery_eligible()
            && self.core_status.error_code.as_deref() == Some("tun_device_conflict")
            && self.conflict_config_revision.as_deref()
                == Some(self.tun_device.config_revision.as_str())
            && matches!(self.core_status.process_state.as_str(), "exited" | "alive")
            && self.core_status.operation.is_none()
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
        self.record_lifecycle(
            "desktop_start_requested",
            serde_json::json!({"persistIntent":persist}),
        );
        let mut result = self.start_once(persist, None);
        if result.is_err() {
            if let Some(recovery) = self.recover_tun_device(persist) {
                result = recovery;
            }
        }
        self.record_lifecycle(
            "desktop_start_finished",
            serde_json::json!({"code":result.as_ref().err().map(|error| &error.code)}),
        );
        result
    }

    fn record_lifecycle(&self, event: &str, mut data: serde_json::Value) {
        if let Some(fields) = data.as_object_mut() {
            fields.insert("corePid".into(), serde_json::json!(self.core_status.pid));
            fields.insert("phase".into(), serde_json::json!(self.phase));
            fields.insert("desiredRunning".into(), serde_json::json!(self.run_intent));
            fields.insert("tun".into(), serde_json::json!(self.preferences.tun));
            fields.insert(
                "intentRevision".into(),
                serde_json::json!(self.cancellation.revision()),
            );
        }
        crate::diagnostics::record(&self.logs, event, data);
    }

    fn start_once(&mut self, persist: bool, expected_revision: Option<&str>) -> Result<()> {
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
        if self.core_status.error_code.as_deref() == Some("tun_device_conflict") {
            self.core_status.error_code = None;
            self.conflict_config_revision = None;
        }
        self.publish();
        let mut result = (|| {
            let (content, revision) = self.read_runtime_source(expected_revision)?;
            if self.preferences.system_proxy {
                config::mixed_port(&content)?;
            }
            if let Some(running) = self.running.as_mut() {
                let _scope = crate::diagnostics::scope(
                    serde_json::json!({"step":"replace_previous_runtime"}),
                );
                stop_running(running, &self.cancellation)?;
            }
            self.running = None;
            self.app.pause_service_observation();
            self.cancellation.check()?;
            self.running = Some(self.new_running_core()?);
            self.attempted_config_revision = Some(revision.clone());
            self.conflict_config_revision = None;
            self.publish();
            self.core_status = match self.running.as_mut().expect("runtime set above") {
                RunningCore::Service(client) => client.call(
                    Request::Start {
                        config: content.clone(),
                    },
                    &self.cancellation,
                )?,
                RunningCore::Direct(supervisor) => supervisor.start(content.clone(), false)?,
                #[cfg(test)]
                RunningCore::Fixture(runtime) => runtime.lock().unwrap().start(&content)?,
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
                let _scope = crate::diagnostics::scope(
                    serde_json::json!({"step":"startup_failure_cleanup","triggerCode":error.code}),
                );
                self.record_lifecycle("desktop_start_cleanup_requested", serde_json::json!({}));
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
        let started = Instant::now();
        self.record_lifecycle(
            "desktop_stop_requested",
            serde_json::json!({"persistIntent":persist}),
        );
        if !self.tun_device_recovery.active {
            self.tun_device_recovery.outcome = None;
        }
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
            self.record_lifecycle("desktop_stop_finished", serde_json::json!({"code":error.code,"elapsedMs":started.elapsed().as_millis(),"exitConfirmed":false}));
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
        self.record_lifecycle("desktop_stop_finished", serde_json::json!({"code":error.as_ref().map(|error| &error.code),"elapsedMs":started.elapsed().as_millis(),"exitConfirmed":true}));
        error.map_or(Ok(()), Err)
    }

    pub fn restart(&mut self) -> Result<()> {
        let _scope = crate::diagnostics::scope(serde_json::json!({"cause":"restart"}));
        self.require_idle_update()?;
        self.stop(false)?;
        self.cancellation.check()?;
        self.start(true)
    }

    pub fn set_system_proxy(&mut self, enabled: bool) -> Result<()> {
        let _scope = crate::diagnostics::scope(serde_json::json!({"cause":"system_proxy_change"}));
        self.require_idle_update()?;
        if enabled {
            if self.phase != Phase::Running {
                self.record_lifecycle(
                    "desktop_implicit_start",
                    serde_json::json!({"trigger":"enable_system_proxy"}),
                );
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
        let _scope = crate::diagnostics::scope(
            serde_json::json!({"cause":"tun_mode_change","requestedTun":enabled}),
        );
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
                if error.code == "tun_device_conflict"
                    || self.tun_device_recovery.generation == self.cancellation.revision()
                        && self
                            .tun_device_recovery
                            .attempted_revisions
                            .contains(&self.tun_device.config_revision)
                {
                    // An exhausted automatic TUN recovery remains a TUN
                    // failure, not a silent fallback to the previous mode.
                    self.persist()?;
                    self.publish();
                    return Err(error);
                }
                self.preferences.tun = previous;
                let _rollback_scope = crate::diagnostics::scope(
                    serde_json::json!({"cause":"tun_mode_rollback","triggerCode":error.code}),
                );
                self.record_lifecycle("desktop_mode_rollback_requested", serde_json::json!({}));
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

    /// Recover only an exact native ownership conflict. A selected fixed name
    /// is retried once; normal starts and observations never rename interfaces.
    fn recover_tun_device(&mut self, persist: bool) -> Option<Result<()>> {
        if !self.can_recover_tun_device() {
            return None;
        }
        let revision = self.tun_device.config_revision.clone();
        let generation = self.cancellation.revision();
        if !self.tun_device_recovery.claim(generation, &revision) {
            return None;
        }
        let _scope = crate::diagnostics::scope(
            serde_json::json!({"cause":"automatic_tun_device_recovery","triggerCode":"tun_device_conflict"}),
        );
        self.record_lifecycle("desktop_tun_recovery_started", serde_json::json!({}));
        let original = AppError::new(
            "tun_device_conflict",
            self.last_error
                .as_deref()
                .or(self.core_status.error.as_deref())
                .unwrap_or("TUN 网卡名称冲突"),
        );
        let previous = self.preferences.tun_device_override.clone();
        let previous_core_version = self.core_status.version.clone();
        let mut selected = false;
        let mut source_check_failed = false;
        self.tun_device_recovery.active = true;
        self.phase = Phase::Recovering;
        self.publish();
        let mut result = (|| {
            self.confirm_tun_recovery_revision(&revision)?;
            // Reload may have rolled back to a live old process. Only this
            // exact owner's successful Stop permits changing the runtime name.
            if let Err(cleanup) = self.stop(false) {
                return Err(if self.core_status.process_state == "unknown" {
                    core::startup_cleanup_error(&original, &cleanup)
                } else {
                    cleanup
                });
            }
            self.confirm_tun_recovery_revision(&revision)?;
            self.preferences.tun_device_override = Some(config::TunDeviceOverride::managed(
                self.tun_device.configured_name.clone(),
            ));
            if let Err(error) = self.persist() {
                self.preferences.tun_device_override = previous.clone();
                return Err(error);
            }
            selected = true;
            #[cfg(test)]
            self.tun_recovery_checkpoint("selected");
            if let Err(error) = self.confirm_tun_recovery_revision(&revision) {
                source_check_failed = true;
                return Err(error);
            }
            self.record_tun_device_recovery(
                "tun_device_auto_recovery",
                &revision,
                previous_core_version.as_deref(),
            );
            self.publish();
            // This is the primitive start, deliberately not the automatic
            // wrapper. A collision of MihomoDesktop is a terminal real error.
            self.start_once(persist, Some(&revision))?;
            #[cfg(test)]
            self.tun_recovery_checkpoint("ready");
            if let Err(error) = self.confirm_tun_recovery_revision(&revision) {
                source_check_failed = true;
                return Err(error);
            }
            Ok(())
        })();
        if let Err(mut error) = result {
            if selected
                && (source_check_failed
                    || matches!(
                        error.code.as_str(),
                        "operation_cancelled" | "config_conflict"
                    ))
            {
                // A file edit during startup must not leave the now-stale
                // runtime alive. A newer Stop already owns cancellation cleanup.
                if self.cancellation.check().is_ok() && self.running.is_some() {
                    if let Err(cleanup) = self.stop(false) {
                        error = core::startup_cleanup_error(&error, &cleanup);
                    }
                }
                self.preferences.tun_device_override = previous;
                if let Err(rollback) = self.persist() {
                    error.message =
                        format!("{}；恢复接口映射失败：{}", error.message, rollback.message);
                }
                if let Ok(document) = config::read(&self.config_path) {
                    let _ = self.remember_config(&document);
                }
            }
            if error.code != "operation_cancelled" && error.code != "core_termination_unconfirmed" {
                error.message = format!(
                    "{}；自动恢复 TUN 接口失败：{}",
                    original.message, error.message
                );
            }
            self.phase = if error.code == "core_termination_unconfirmed" {
                Phase::Stopping
            } else {
                Phase::Error
            };
            self.last_error = Some(error.message.clone());
            self.core_status.error = Some(error.message.clone());
            self.core_status.error_code = Some(error.code.clone());
            self.core_status.operation =
                (error.code == "core_termination_unconfirmed").then(|| "stopping".into());
            self.observe_tun_device_conflict();
            result = Err(error);
        } else {
            self.record_tun_device_recovery(
                "tun_device_auto_recovery_succeeded",
                &revision,
                self.core_status.version.as_deref(),
            );
        }
        self.tun_device_recovery.active = false;
        self.record_lifecycle(
            "desktop_tun_recovery_finished",
            serde_json::json!({"code":result.as_ref().err().map(|error| &error.code)}),
        );
        self.tun_device_recovery.outcome = Some(result.clone());
        self.publish();
        Some(result)
    }

    fn confirm_tun_recovery_revision(&mut self, expected: &str) -> Result<()> {
        self.cancellation.check()?;
        let document = config::read(&self.config_path)?;
        self.remember_config(&document)?;
        config::require_revision(&document.content, expected)?;
        self.cancellation.check()
    }

    fn record_tun_device_recovery(&self, event: &str, revision: &str, core_version: Option<&str>) {
        crate::diagnostics::record(
            &self.logs,
            event,
            serde_json::json!({
                "causeCode":"tun_device_conflict",
                "configuredName":config::diagnostic_tun_device(self.tun_device.configured_name.as_deref()),
                "effectiveName":config::diagnostic_tun_device(self.tun_device.effective_name.as_deref()),
                "configRevision":revision,"generation":self.cancellation.revision(),
                "attempt":1,"previousExitConfirmed":true,
                "desktopVersion":env!("CARGO_PKG_VERSION"),"coreVersion":core_version,
                "corePid":self.core_status.pid,
            }),
        );
    }

    fn ensure_service(&self) -> Result<()> {
        #[cfg(test)]
        if self.test_runtime.is_some() {
            return Ok(());
        }
        windows::wait_for_service_maintenance("install", Some(&self.cancellation))?;
        let state = service::status()?;
        if !state.installed || !state.running {
            windows::run_elevated_service_action_cancellable("install", Some(&self.cancellation))?;
        }
        Ok(())
    }

    fn new_running_core(&mut self) -> Result<RunningCore> {
        #[cfg(test)]
        if let Some(runtime) = &self.test_runtime {
            return Ok(RunningCore::Fixture(runtime.clone()));
        }
        if self.preferences.tun {
            self.ensure_service()?;
            self.cancellation.check()?;
            let client = match self.app.service_client() {
                Some(client) => client,
                None => ServiceClient::new(self.app.instance.clone(), self.logs.clone())?,
            };
            self.app.set_service_client(Some(client.clone()));
            Ok(RunningCore::Service(client))
        } else {
            let mut supervisor = CoreSupervisor::new(
                core::fixed_core_path()?,
                self.directory.join("runtime"),
                self.logs.clone(),
            );
            supervisor.set_cancellation(self.cancellation.clone());
            Ok(RunningCore::Direct(Box::new(supervisor)))
        }
    }

    #[cfg(test)]
    fn tun_recovery_checkpoint(&self, stage: &str) {
        if let Some(runtime) = &self.test_runtime {
            runtime.lock().unwrap().checkpoint(stage);
        }
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
        let _scope = crate::diagnostics::scope(serde_json::json!({"cause":"service_uninstall"}));
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
        let _scope = crate::diagnostics::scope(serde_json::json!({"cause":"config_reload"}));
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
        self.publish();
        let result = match self.running.as_mut() {
            Some(RunningCore::Direct(core)) => core.reload(content.clone()),
            Some(RunningCore::Service(client)) => client.call(
                Request::Reload {
                    config: content.clone(),
                },
                &self.cancellation,
            ),
            #[cfg(test)]
            Some(RunningCore::Fixture(runtime)) => runtime.lock().unwrap().reload(&content),
            None => return Ok(()),
        };
        let status = match result {
            Ok(status) => status,
            Err(error) => {
                self.core_status = self
                    .running
                    .as_mut()
                    .and_then(|running| running_status(running).ok())
                    .unwrap_or_else(|| self.core_status.clone());
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
                return self.recover_tun_device(false).unwrap_or(Err(error));
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
        let _scope = crate::diagnostics::scope(serde_json::json!({"cause":"update_prepare"}));
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
        let _scope = crate::diagnostics::scope(serde_json::json!({"cause":"update_rollback"}));
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
        let automatic_failure = match &self.tun_device_recovery.outcome {
            Some(Err(error)) => Some(error.clone()),
            _ => None,
        };
        if self.core_status.error_code.as_deref() == Some("core_termination_unconfirmed")
            || automatic_failure.is_some()
        {
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
            #[cfg(test)]
            Some(RunningCore::Fixture(runtime)) => runtime.lock().unwrap().poll(),
            None => {
                if self.recover_tun_device(false).is_none() {
                    self.publish();
                }
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
                let recovered =
                    automatic_failure.is_none() && self.phase != Phase::Running && status.running;
                self.phase = if automatic_failure.is_none()
                    && status.stop_reason == Some(StopReason::LeaseExpired)
                    && self.run_intent
                {
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
        if self.core_status.error_code.as_deref() == Some("core_termination_unconfirmed") {
            // A later owned cleanup failure outranks an earlier recovery
            // outcome; ordinary polling cannot erase its ownership barrier.
            self.phase = Phase::Stopping;
            self.core_status.operation = Some("stopping".into());
            self.core_status.control_state = "unknown".into();
            self.core_status.controller = None;
            self.publish();
        } else if let Some(error) = automatic_failure {
            if error.code == "core_termination_unconfirmed"
                && self.core_status.process_state == "exited"
            {
                self.tun_device_recovery.outcome = None;
            } else {
                self.phase = Phase::Error;
                self.last_error = Some(error.message.clone());
                self.core_status.error = Some(error.message);
                self.core_status.error_code = Some(error.code);
                self.core_status.operation = None;
            }
            self.publish();
        } else if self.recover_tun_device(false).is_none() {
            self.publish();
        }
    }

    pub fn request_reconcile(&mut self, generation: u64) {
        if !self.run_intent
            || self.updating
            || self.exiting
            || self.core_status.error_code.as_deref() == Some("core_termination_unconfirmed")
            || matches!(self.tun_device_recovery.outcome, Some(Err(_)))
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
        let _scope = crate::diagnostics::scope(
            serde_json::json!({"cause":"automatic_lease_recovery","intentRevision":generation}),
        );
        self.record_lifecycle("lease_recovery_started", serde_json::json!({}));
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
            self.lease_restarts.push_back(now);
            self.attempted_config_revision = self.applied_config_revision.clone();
            self.conflict_config_revision = None;
            self.publish();
            let status = match self.running.as_ref() {
                Some(RunningCore::Service(client)) => {
                    client.call(Request::Start { config: source }, &self.cancellation)?
                }
                #[cfg(test)]
                Some(RunningCore::Fixture(runtime)) => runtime.lock().unwrap().start(&source)?,
                _ => {
                    return Err(AppError::new(
                        "recovery_owner_missing",
                        "服务恢复所属实例已变更",
                    ))
                }
            };
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
        self.record_lifecycle("lease_recovery_finished", serde_json::json!({
            "code":result.as_ref().err().map(|error| &error.code),
            "superseded":generation != self.app.generation() || self.app.stop_pending.load(Ordering::Acquire) != 0,
        }));
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
        if self.recover_tun_device(false).is_none() {
            self.publish();
        }
    }

    pub fn request_exit(&mut self, force: bool) -> Result<bool> {
        let _scope = crate::diagnostics::scope(serde_json::json!({"step":"application_exit"}));
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
        #[cfg(test)]
        RunningCore::Fixture(runtime) => runtime.lock().unwrap().stop(),
    }
}

fn running_status(running: &mut RunningCore) -> Result<CoreStatus> {
    match running {
        RunningCore::Direct(core) => Ok(core.status()),
        RunningCore::Service(client) => client.status(),
        #[cfg(test)]
        RunningCore::Fixture(runtime) => Ok(runtime.lock().unwrap().status.clone()),
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
        // Only an owner outcome can finish a recovery. A metadata publication
        // carrying an older native snapshot is not an operation completion.
        if matches!(self.tun_device_recovery, TunDeviceRecoveryStatus::Idle)
            && matches!(
                current.tun_device_recovery,
                TunDeviceRecoveryStatus::Pending
            )
            && self.auto_tun_recovery_eligible
            && self.desired_running
            && self.tun
        {
            self.tun_device_recovery = TunDeviceRecoveryStatus::Pending;
        }
        let local_failure = self.control_state == "unreachable"
            && self
                .error_code
                .as_deref()
                .is_some_and(|code| code.starts_with("service_") || code == "io_error");
        let precise_failure = matches!(
            self.error_code.as_deref(),
            Some("tun_device_conflict" | "core_termination_unconfirmed")
        );
        let failure = (local_failure || precise_failure).then(|| {
            (
                self.phase,
                self.operation.clone(),
                self.error_code.clone(),
                self.last_error.clone(),
            )
        });
        if let Some(core) = &current.core_observation {
            self.copy_core_observation(core);
        } else {
            // Initial/default snapshots predate a native observation.
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
        }
        let newer_cleanup = self.error_code.as_deref() == Some("core_termination_unconfirmed");
        if !newer_cleanup {
            if let Some((phase, operation, code, message)) = failure {
                self.phase = phase;
                self.operation = operation;
                self.error_code = code;
                self.last_error = message;
                if local_failure {
                    self.control_state = "unreachable".into();
                    self.controller = None;
                }
            }
        }
        self.project_tun_device_recovery();
    }

    pub fn apply_core_observation(&mut self, core: &CoreStatus) {
        let cleanup_failure = (self.error_code.as_deref() == Some("core_termination_unconfirmed"))
            .then(|| self.last_error.clone());
        self.copy_core_observation(core);
        if let Some(message) = cleanup_failure {
            self.error_code = Some("core_termination_unconfirmed".into());
            self.last_error = message;
        }
        if matches!(
            self.tun_device_recovery,
            TunDeviceRecoveryStatus::Idle | TunDeviceRecoveryStatus::Succeeded
        ) && self.auto_tun_recovery_eligible
            && self.desired_running
            && self.tun
            && self.error_code.as_deref() == Some("tun_device_conflict")
            && matches!(self.process_state.as_str(), "alive" | "exited")
            && self.operation.is_none()
        {
            self.tun_device_recovery = TunDeviceRecoveryStatus::Pending;
        }
        self.project_tun_device_recovery();
    }

    fn copy_core_observation(&mut self, core: &CoreStatus) {
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
        self.core_observation = Some(core.clone());
    }

    fn project_tun_device_recovery(&mut self) {
        // Confirmed native uncertainty has precedence over the recovery UI.
        if self.error_code.as_deref() == Some("core_termination_unconfirmed") {
            self.retain_unconfirmed_cleanup();
            return;
        }
        match &self.tun_device_recovery {
            TunDeviceRecoveryStatus::Pending => {
                self.phase = Phase::Recovering;
                self.operation = Some(TUN_DEVICE_RECOVERY_OPERATION.into());
            }
            TunDeviceRecoveryStatus::Failed(error) => {
                self.error_code = Some(error.code.clone());
                self.last_error = Some(error.message.clone());
                if error.code == "core_termination_unconfirmed" {
                    self.retain_unconfirmed_cleanup();
                } else {
                    self.phase = Phase::Error;
                    self.operation = None;
                }
            }
            TunDeviceRecoveryStatus::Succeeded | TunDeviceRecoveryStatus::Idle => (),
        }
    }

    fn retain_unconfirmed_cleanup(&mut self) {
        self.phase = Phase::Stopping;
        self.operation = Some("stopping".into());
        self.control_state = "unknown".into();
        self.listeners_ready = None;
        self.controller = None;
    }
}

#[cfg(test)]
mod cleanup_observation_tests {
    use super::*;

    const SOURCE: &str = "# source must stay untouched\nmixed-port: 7890\ndns:\n  enable: true\ntun:\n  device: utun\n";

    struct Fixture {
        manager: DesktopManager,
        runtime: Arc<Mutex<TestRuntime>>,
        directory: PathBuf,
    }

    impl Fixture {
        fn new(source: &str) -> Self {
            let mut random = [0u8; 12];
            getrandom::getrandom(&mut random).unwrap();
            let directory = std::env::temp_dir().join(format!("tun-auto-{}", hex::encode(random)));
            let (config_path, logs) = config::data_paths(&directory);
            config::atomic_write(&config_path, source.as_bytes()).unwrap();
            let app = crate::host::test_context();
            let runtime = Arc::new(Mutex::new(TestRuntime::default()));
            let mut manager = DesktopManager {
                cancellation: app.cancellation(),
                app,
                directory: directory.clone(),
                config_path,
                logs,
                preferences: Preferences {
                    tun: true,
                    desired_running: true,
                    ..Preferences::default()
                },
                proxy: SystemProxy::new(directory.clone()),
                running: None,
                phase: Phase::Stopped,
                core_status: CoreStatus {
                    process_state: "exited".into(),
                    observed_at: core::observed_now(),
                    ..CoreStatus::default()
                },
                applied_proxy_port: None,
                last_error: None,
                dirty: false,
                exiting: false,
                updating: false,
                run_intent: true,
                resume_pending: None,
                communication_recovery: None,
                lease_recovery_pending: None,
                lease_restarts: VecDeque::new(),
                applied_config: None,
                applied_config_revision: None,
                tun_device: TunDeviceStatus::default(),
                source_tun_valid: false,
                attempted_config_revision: None,
                conflict_config_revision: None,
                tun_device_recovery: TunDeviceRecovery::default(),
                test_runtime: Some(runtime.clone()),
            };
            manager
                .remember_config(&config::read(&manager.config_path).unwrap())
                .unwrap();
            manager.persist().unwrap();
            manager.publish();
            Self {
                manager,
                runtime,
                directory,
            }
        }

        fn installed_runtime(&mut self, status: CoreStatus) {
            self.manager.applied_config =
                Some(config::read(&self.manager.config_path).unwrap().content);
            self.manager.applied_config_revision =
                Some(self.manager.tun_device.config_revision.clone());
            self.manager.attempted_config_revision = self.manager.applied_config_revision.clone();
            self.manager.phase = phase_for_core(&status);
            self.manager.core_status = status.clone();
            self.runtime.lock().unwrap().status = status;
            self.manager.running = Some(RunningCore::Fixture(self.runtime.clone()));
            self.manager.publish();
        }

        fn events(&self) -> Vec<serde_json::Value> {
            assert!(crate::diagnostics::flush(Duration::from_secs(2)));
            std::fs::read_to_string(self.manager.logs.join("recovery.log"))
                .unwrap_or_default()
                .lines()
                .filter_map(|line| serde_json::from_str(line).ok())
                .collect()
        }

        fn assert_recovered(&self) {
            assert!(self.manager.phase == Phase::Running);
            assert!(!self.manager.tun_device_recovery.active);
            assert!(self.manager.tun_device.managed);
            assert_eq!(
                self.manager.tun_device.effective_name.as_deref(),
                Some(config::MANAGED_TUN_DEVICE)
            );
            assert_eq!(
                std::fs::read_to_string(&self.manager.config_path).unwrap(),
                SOURCE
            );
            let stored = config::load_preferences(&self.directory).unwrap();
            assert_eq!(
                stored.tun_device_override,
                Some(config::TunDeviceOverride::managed(Some("utun".into())))
            );
            let events = self.events();
            let recovery = events
                .iter()
                .position(|event| event["event"] == "desktop_tun_recovery_started")
                .unwrap();
            let stop = events
                .iter()
                .position(|event| {
                    event["event"] == "desktop_stop_requested"
                        && event["context"]["cause"] == "automatic_tun_device_recovery"
                })
                .unwrap();
            assert!(
                recovery < stop,
                "the automatic recovery cause must precede its Stop"
            );
            assert_eq!(
                events[stop]["context"]["triggerCode"],
                "tun_device_conflict"
            );
            assert_eq!(events[stop]["data"]["persistIntent"], false);
            let attempts: Vec<_> = events
                .iter()
                .filter(|event| event["event"] == "tun_device_auto_recovery")
                .collect();
            let successes: Vec<_> = events
                .iter()
                .filter(|event| event["event"] == "tun_device_auto_recovery_succeeded")
                .collect();
            assert_eq!(attempts.len(), 1);
            assert_eq!(successes.len(), 1);
            assert_eq!(attempts[0]["data"]["attempt"], 1);
            assert_eq!(attempts[0]["data"]["previousExitConfirmed"], true);
            assert_eq!(
                attempts[0]["data"]["configRevision"],
                config::revision(SOURCE.as_bytes())
            );
            assert_eq!(
                successes[0]["data"]["corePid"],
                self.manager.core_status.pid.unwrap()
            );
        }
    }

    impl Drop for Fixture {
        fn drop(&mut self) {
            let _ = crate::diagnostics::flush(Duration::from_secs(2));
            let _ = std::fs::remove_dir_all(&self.directory);
        }
    }

    fn ready(pid: u32) -> CoreStatus {
        CoreStatus {
            running: true,
            process_state: "alive".into(),
            control_state: "ready".into(),
            listeners_ready: Some(true),
            pid: Some(pid),
            observed_at: core::observed_now(),
            version: Some("fixture-core".into()),
            controller: Some(Controller {
                url: "http://127.0.0.1:9090".into(),
                secret: "fixture".into(),
            }),
            ..CoreStatus::default()
        }
    }

    fn conflict(name: &str) -> AppError {
        AppError::new(
            "tun_device_conflict",
            format!("native ownership guard: {name}"),
        )
    }

    fn exited_conflict() -> CoreStatus {
        CoreStatus {
            process_state: "exited".into(),
            error_code: Some("tun_device_conflict".into()),
            error: Some(conflict("utun").message),
            observed_at: core::observed_now(),
            ..CoreStatus::default()
        }
    }

    #[test]
    fn start_recovers_once_without_publishing_a_transient_terminal_conflict() {
        let mut fixture = Fixture::new(SOURCE);
        fixture
            .runtime
            .lock()
            .unwrap()
            .starts
            .extend([Err(conflict("utun")), Ok(ready(222))]);
        // Inspect the exact handoff point used by start's wrapper.
        assert_eq!(
            fixture.manager.start_once(true, None).unwrap_err().code,
            "tun_device_conflict"
        );
        let pending = fixture.manager.status();
        assert!(pending.phase == Phase::Recovering);
        assert_eq!(
            pending.operation.as_deref(),
            Some(TUN_DEVICE_RECOVERY_OPERATION)
        );
        assert_eq!(pending.process_state, "exited");
        fixture.manager.recover_tun_device(true).unwrap().unwrap();
        fixture.assert_recovered();
        assert_eq!(
            fixture.runtime.lock().unwrap().actions,
            ["start:utun", "stop", "start:MihomoDesktop"]
        );
    }

    #[test]
    fn ordinary_start_and_tun_enable_return_only_the_final_recovery_result() {
        for enable in [false, true] {
            let mut fixture = Fixture::new(SOURCE);
            if enable {
                fixture.manager.preferences.tun = false;
                fixture.installed_runtime(ready(111));
            }
            fixture
                .runtime
                .lock()
                .unwrap()
                .starts
                .extend([Err(conflict("utun")), Ok(ready(222))]);
            if enable {
                fixture.manager.set_tun(true).unwrap();
            } else {
                fixture.manager.start(true).unwrap();
            }
            fixture.assert_recovered();
        }
    }

    #[test]
    fn reload_poll_and_lease_recovery_share_the_same_confirmed_stop_barrier() {
        for entry in ["reload", "poll", "lease"] {
            let mut fixture = Fixture::new(SOURCE);
            fixture.installed_runtime(ready(111));
            {
                let mut runtime = fixture.runtime.lock().unwrap();
                match entry {
                    "reload" => runtime.reloads.push_back(Err(conflict("utun"))),
                    "poll" => runtime.observations.push_back(Ok(exited_conflict())),
                    "lease" => runtime.starts.push_back(Err(conflict("utun"))),
                    _ => unreachable!(),
                }
                runtime.starts.push_back(Ok(ready(222)));
            }
            match entry {
                "reload" => fixture.manager.reload_config(true).unwrap(),
                "poll" => fixture.manager.poll(),
                "lease" => {
                    fixture.manager.core_status = CoreStatus {
                        process_state: "exited".into(),
                        stop_reason: Some(StopReason::LeaseExpired),
                        ..CoreStatus::default()
                    };
                    fixture.manager.lease_recovery_pending = Some(fixture.manager.app.generation());
                    fixture.manager.poll();
                }
                _ => unreachable!(),
            }
            fixture.assert_recovered();
            let runtime = fixture.runtime.lock().unwrap();
            let stop = runtime
                .actions
                .iter()
                .position(|action| action == "stop")
                .unwrap();
            let restart = runtime
                .actions
                .iter()
                .position(|action| action == "start:MihomoDesktop")
                .unwrap();
            assert!(
                stop < restart,
                "{entry} must stop its previous owner before the managed start"
            );
        }
    }

    #[test]
    fn collision_of_fixed_name_is_terminal_without_a_suffix_or_second_attempt() {
        for already_fixed in [false, true] {
            let source = if already_fixed {
                SOURCE.replace("utun", "mihomodesktop")
            } else {
                SOURCE.into()
            };
            let mut fixture = Fixture::new(&source);
            fixture
                .runtime
                .lock()
                .unwrap()
                .starts
                .push_back(Err(conflict(if already_fixed {
                    "mihomodesktop"
                } else {
                    "utun"
                })));
            if !already_fixed {
                fixture
                    .runtime
                    .lock()
                    .unwrap()
                    .starts
                    .push_back(Err(conflict("MihomoDesktop")));
            }
            let error = fixture.manager.start(true).unwrap_err();
            assert_eq!(error.code, "tun_device_conflict");
            assert!(error.message.contains(if already_fixed {
                "mihomodesktop"
            } else {
                "MihomoDesktop"
            }));
            assert!(fixture.manager.phase == Phase::Error);
            assert!(!fixture.manager.tun_device_recovery.active);
            let starts = fixture
                .runtime
                .lock()
                .unwrap()
                .actions
                .iter()
                .filter(|action| action.starts_with("start:"))
                .count();
            assert_eq!(starts, if already_fixed { 1 } else { 2 });
            for _ in 0..3 {
                fixture.manager.poll();
            }
            assert_eq!(
                fixture
                    .runtime
                    .lock()
                    .unwrap()
                    .actions
                    .iter()
                    .filter(|action| action.starts_with("start:"))
                    .count(),
                starts
            );
            assert_eq!(
                fixture
                    .events()
                    .iter()
                    .filter(|event| event["event"] == "tun_device_auto_recovery")
                    .count(),
                usize::from(!already_fixed)
            );
        }
    }

    #[test]
    fn stop_or_source_change_after_persist_rolls_back_without_late_start() {
        for cancel in [true, false] {
            let mut fixture = Fixture::new(SOURCE);
            fixture
                .runtime
                .lock()
                .unwrap()
                .starts
                .push_back(Err(conflict("utun")));
            let context = fixture.manager.app.clone();
            let path = fixture.manager.config_path.clone();
            fixture.runtime.lock().unwrap().checkpoint = Some(Box::new(move |stage| {
                if stage != "selected" {
                    return;
                }
                if cancel {
                    context.runtime_generation.fetch_add(1, Ordering::AcqRel);
                } else {
                    config::atomic_write(
                        &path,
                        SOURCE.replace("utun", "changed-device").as_bytes(),
                    )
                    .unwrap();
                }
            }));
            let error = fixture.manager.start(true).unwrap_err();
            assert_eq!(
                error.code,
                if cancel {
                    "operation_cancelled"
                } else {
                    "config_conflict"
                }
            );
            assert_eq!(
                fixture
                    .runtime
                    .lock()
                    .unwrap()
                    .actions
                    .iter()
                    .filter(|action| action.starts_with("start:"))
                    .count(),
                1
            );
            assert!(fixture.manager.preferences.tun_device_override.is_none());
            assert!(config::load_preferences(&fixture.directory)
                .unwrap()
                .tun_device_override
                .is_none());
            assert!(!fixture.manager.tun_device_recovery.active);
            assert!(!fixture
                .events()
                .iter()
                .any(|event| event["event"] == "tun_device_auto_recovery"));
        }
    }

    #[test]
    fn newer_poll_cleanup_uncertainty_outranks_an_earlier_auto_recovery_failure() {
        let mut fixture = Fixture::new(SOURCE);
        fixture.installed_runtime(ready(111));
        let original = AppError::new("config_conflict", "source changed during recovery");
        fixture.manager.tun_device_recovery.outcome = Some(Err(original.clone()));
        fixture.manager.core_status.error_code = Some(original.code);
        fixture.manager.last_error = Some(original.message);
        fixture.manager.publish();
        fixture
            .runtime
            .lock()
            .unwrap()
            .observations
            .push_back(Ok(CoreStatus {
                process_state: "unknown".into(),
                error_code: Some("core_termination_unconfirmed".into()),
                error: Some("later owned cleanup remains unconfirmed".into()),
                operation: Some("stopping".into()),
                ..ready(111)
            }));
        fixture.manager.poll();
        let status = fixture.manager.status();
        assert!(status.phase == Phase::Stopping);
        assert_eq!(status.operation.as_deref(), Some("stopping"));
        assert_eq!(
            status.error_code.as_deref(),
            Some("core_termination_unconfirmed")
        );
        assert_eq!(
            status.last_error.as_deref(),
            Some("later owned cleanup remains unconfirmed")
        );
        assert_eq!(status.process_state, "unknown");
        assert_eq!(status.pid, Some(111));
        assert!(status.controller.is_none());
    }

    #[test]
    fn unreadable_source_after_ready_stops_the_new_owner_and_rolls_back() {
        let mut fixture = Fixture::new(SOURCE);
        fixture
            .runtime
            .lock()
            .unwrap()
            .starts
            .extend([Err(conflict("utun")), Ok(ready(222))]);
        let path = fixture.manager.config_path.clone();
        fixture.runtime.lock().unwrap().checkpoint = Some(Box::new(move |stage| {
            if stage == "ready" {
                std::fs::remove_file(&path).unwrap();
            }
        }));
        let error = fixture.manager.start(true).unwrap_err();
        assert_eq!(error.code, "config_missing");
        assert!(error.message.contains("utun"));
        assert_eq!(
            fixture.runtime.lock().unwrap().actions,
            ["start:utun", "stop", "start:MihomoDesktop", "stop"]
        );
        assert!(fixture.manager.running.is_none());
        assert!(fixture.manager.preferences.tun_device_override.is_none());
        assert!(config::load_preferences(&fixture.directory)
            .unwrap()
            .tun_device_override
            .is_none());
        assert!(!fixture
            .events()
            .iter()
            .any(|event| event["event"] == "tun_device_auto_recovery_succeeded"));
        fixture.manager.poll();
        assert!(fixture.manager.status().phase == Phase::Error);
        assert_eq!(
            fixture.manager.status().error_code.as_deref(),
            Some("config_missing")
        );
    }

    #[test]
    fn stop_during_managed_start_retains_the_owner_and_never_retries_again() {
        use std::sync::atomic::AtomicUsize;
        let mut fixture = Fixture::new(SOURCE);
        fixture
            .runtime
            .lock()
            .unwrap()
            .starts
            .extend([Err(conflict("utun")), Ok(ready(222))]);
        let context = fixture.manager.app.clone();
        let starts = AtomicUsize::new(0);
        fixture.runtime.lock().unwrap().checkpoint = Some(Box::new(move |stage| {
            if stage == "start" && starts.fetch_add(1, Ordering::AcqRel) == 1 {
                let revision = context.runtime_generation.fetch_add(1, Ordering::AcqRel) + 1;
                let mut stopping = context.status().unwrap();
                stopping.intent_revision = revision;
                stopping.desired_running = false;
                stopping.phase = Phase::Stopping;
                stopping.operation = Some("stopping".into());
                stopping.tun_device_recovery = TunDeviceRecoveryStatus::Idle;
                stopping.auto_tun_recovery_eligible = false;
                context.publish_status(stopping);
            }
        }));
        assert_eq!(
            fixture.manager.start(true).unwrap_err().code,
            "operation_cancelled"
        );
        assert!(
            fixture.manager.running.is_some(),
            "the cancelled worker must retain its exact owner for Stop"
        );
        assert!(fixture.manager.preferences.tun_device_override.is_none());
        assert!(fixture.manager.status().phase == Phase::Stopping);
        fixture
            .manager
            .begin_operation(fixture.manager.app.generation());
        fixture.manager.stop(true).unwrap();
        assert!(fixture.manager.running.is_none());
        assert!(fixture.manager.status().phase == Phase::Stopped);
        assert_eq!(
            fixture
                .runtime
                .lock()
                .unwrap()
                .actions
                .iter()
                .filter(|action| action.starts_with("start:"))
                .count(),
            2
        );
        assert!(!fixture
            .events()
            .iter()
            .any(|event| event["event"] == "tun_device_auto_recovery_succeeded"));
    }

    #[test]
    fn unknown_cleanup_dirty_state_and_disabled_tun_never_choose_another_device() {
        for blocker in ["unknown", "dirty", "tun_disabled"] {
            let mut fixture = Fixture::new(SOURCE);
            fixture
                .runtime
                .lock()
                .unwrap()
                .starts
                .push_back(Err(conflict("utun")));
            match blocker {
                "unknown" => fixture
                    .runtime
                    .lock()
                    .unwrap()
                    .stops
                    .push_back(Err(AppError::new("service_timeout", "exit not confirmed"))),
                "dirty" => fixture.manager.dirty = true,
                "tun_disabled" => fixture.manager.preferences.tun = false,
                _ => unreachable!(),
            }
            let error = fixture.manager.start(true).unwrap_err();
            assert_eq!(
                error.code,
                if blocker == "unknown" {
                    "core_termination_unconfirmed"
                } else {
                    "tun_device_conflict"
                }
            );
            assert!(error.message.contains("utun"));
            assert!(fixture.manager.preferences.tun_device_override.is_none());
            assert!(fixture
                .manager
                .tun_device_recovery
                .attempted_revisions
                .is_empty());
            assert_eq!(
                fixture
                    .runtime
                    .lock()
                    .unwrap()
                    .actions
                    .iter()
                    .filter(|action| action.starts_with("start:"))
                    .count(),
                1
            );
        }
    }

    #[test]
    fn normal_start_never_renames_and_failed_persistence_cannot_loop_in_poll() {
        let mut ordinary = Fixture::new(SOURCE);
        ordinary
            .runtime
            .lock()
            .unwrap()
            .starts
            .push_back(Ok(ready(111)));
        ordinary.manager.start(true).unwrap();
        assert!(!ordinary.manager.tun_device.managed);
        assert!(!ordinary
            .events()
            .iter()
            .any(|event| event["event"] == "tun_device_auto_recovery"));
        assert_eq!(
            std::fs::read_to_string(&ordinary.manager.config_path).unwrap(),
            SOURCE
        );

        let mut failed = Fixture::new(SOURCE);
        let preferences = failed.directory.join("preferences.json");
        std::fs::remove_file(&preferences).unwrap();
        std::fs::create_dir(&preferences).unwrap();
        failed
            .runtime
            .lock()
            .unwrap()
            .starts
            .push_back(Err(conflict("utun")));
        assert!(failed.manager.start(true).is_err());
        assert_eq!(
            failed.manager.tun_device_recovery.attempted_revisions.len(),
            1
        );
        assert!(!failed.manager.tun_device_recovery.active);
        failed.manager.core_status = exited_conflict();
        failed.manager.attempted_config_revision =
            Some(failed.manager.tun_device.config_revision.clone());
        failed.manager.observe_tun_device_conflict();
        for _ in 0..3 {
            failed.manager.poll();
        }
        assert_eq!(
            failed
                .runtime
                .lock()
                .unwrap()
                .actions
                .iter()
                .filter(|action| action.starts_with("start:"))
                .count(),
            1
        );
        assert!(!failed
            .events()
            .iter()
            .any(|event| event["event"] == "tun_device_auto_recovery"));
    }

    #[test]
    fn recovery_attempts_are_bound_to_both_intent_and_source_revision() {
        let mut attempts = TunDeviceRecovery::default();
        assert!(attempts.claim(7, "source-a"));
        assert!(!attempts.claim(7, "source-a"));
        assert!(attempts.claim(7, "source-b"));
        assert!(!attempts.claim(7, "source-a"));
        assert!(attempts.claim(8, "source-a"));
    }

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
