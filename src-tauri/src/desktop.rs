use crate::{
    config::{self, ConfigDocument, Preferences},
    core::{self, Controller, CoreStatus, CoreSupervisor, OperationFailure},
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
    time::{Duration, Instant},
};

#[path = "desktop_core_update.rs"]
mod core_update;

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
    pub last_operation_failure: Option<OperationFailure>,
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
    #[serde(skip)]
    pub(crate) core_observation: Option<CoreStatus>,
    // A switch from the unprivileged owner to the service spans two runtimes.
    // Its rollback outcome belongs to the host transaction, not either child.
    #[serde(skip)]
    pub(crate) host_operation_failure: Option<OperationFailure>,
}

enum RunningCore {
    Direct(Box<CoreSupervisor>),
    Service(ServiceClient),
    #[cfg(test)]
    Fixture(Arc<Mutex<TestRuntime>>),
}

impl RunningCore {
    fn service_owned(&self) -> bool {
        match self {
            Self::Service(_) => true,
            Self::Direct(_) => false,
            #[cfg(test)]
            Self::Fixture(runtime) => runtime.lock().unwrap().service_owned,
        }
    }
}

fn prefer_installed_service(trusted_install: bool, service: &ServiceStatusView) -> bool {
    trusted_install && service.installed && service.running
}

#[cfg(test)]
type RecoveryCheckpoint = Box<dyn FnMut(&str) + Send>;

/// The desktop tests script owned lifecycle results; no test starts a service,
/// changes a real adapter, or replaces the source revision/cancellation checks.
#[cfg(test)]
#[derive(Default)]
struct TestRuntime {
    service_owned: bool,
    service_available: bool,
    status: CoreStatus,
    starts: VecDeque<Result<CoreStatus>>,
    reloads: VecDeque<Result<CoreStatus>>,
    observations: VecDeque<Result<CoreStatus>>,
    stops: VecDeque<Result<()>>,
    uninstalls: VecDeque<Result<()>>,
    actions: Vec<String>,
    tun_requests: Vec<bool>,
    tun_change_requests: Vec<bool>,
    status_errors: VecDeque<AppError>,
    checkpoint: Option<RecoveryCheckpoint>,
}

#[cfg(test)]
impl TestRuntime {
    fn checkpoint(&mut self, stage: &str) {
        if let Some(checkpoint) = &mut self.checkpoint {
            checkpoint(stage);
        }
    }

    fn start(&mut self, source: &str, tun: bool) -> Result<CoreStatus> {
        self.tun_requests.push(tun);
        self.actions.push(format!(
            "start:{}",
            config::configured_tun_device(source)?.unwrap_or_default()
        ));
        self.checkpoint("start");
        let result = self
            .starts
            .pop_front()
            .expect("unexpected extra start")
            .map(|mut status| {
                status.tun = tun;
                status
            });
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
        self.checkpoint("started");
        result
    }

    fn reload(&mut self, source: &str, tun: bool, tun_change: bool) -> Result<CoreStatus> {
        self.tun_requests.push(tun);
        self.tun_change_requests.push(tun_change);
        self.actions.push(format!(
            "reload:{}",
            config::configured_tun_device(source)?.unwrap_or_default()
        ));
        self.checkpoint("reload");
        let result = self
            .reloads
            .pop_front()
            .expect("unexpected reload")
            .map(|mut status| {
                status.tun = tun;
                if !tun_change
                    && self
                        .status
                        .last_operation_failure
                        .as_ref()
                        .is_some_and(|failure| failure.operation == "tun_change")
                {
                    status.last_operation_failure = self.status.last_operation_failure.clone();
                }
                status
            });
        match &result {
            Ok(status) => self.status = status.clone(),
            Err(error) => {
                if error.code == "operation_cancelled" {
                    self.status = CoreStatus {
                        tun: self.status.tun,
                        process_state: "exited".into(),
                        stop_reason: Some(StopReason::UserStop),
                        observed_at: core::observed_now(),
                        ..CoreStatus::default()
                    };
                }
                if error.code == "operation_cancelled"
                    || error.code == "core_termination_unconfirmed"
                {
                    self.status.error_code = Some(error.code.clone());
                    self.status.error = Some(error.message.clone());
                } else {
                    self.status.last_operation_failure = Some(OperationFailure::new(
                        if tun_change {
                            "tun_change"
                        } else {
                            "config_reload"
                        },
                        error,
                        tun,
                        self.status.tun,
                        0,
                    ));
                }
            }
        }
        self.checkpoint("reloaded");
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
    last_runtime_service: bool,
    phase: Phase,
    core_status: CoreStatus,
    applied_proxy_port: Option<u16>,
    last_error: Option<String>,
    host_operation_failure: Option<OperationFailure>,
    pub dirty: bool,
    pub exiting: bool,
    updating: bool,
    core_update_interrupted: bool,
    interrupted_runtime_revision: Option<u64>,
    run_intent: bool,
    resume_pending: Option<u64>,
    communication_recovery: Option<RecoveryWindow>,
    lease_recovery_pending: Option<u64>,
    lease_restarts: VecDeque<Duration>,
    applied_config: Option<String>,
    applied_config_revision: Option<String>,
    cancellation: Cancellation,
    tun_device: TunDeviceStatus,
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
            last_runtime_service: false,
            phase: Phase::Stopped,
            core_status: CoreStatus::default(),
            applied_proxy_port: None,
            last_error,
            host_operation_failure: None,
            dirty: false,
            exiting: false,
            updating: false,
            core_update_interrupted: false,
            interrupted_runtime_revision: None,
            run_intent,
            resume_pending: None,
            communication_recovery: None,
            lease_recovery_pending: None,
            lease_restarts: VecDeque::new(),
            applied_config: None,
            applied_config_revision: None,
            cancellation,
            tun_device: TunDeviceStatus::default(),
            #[cfg(test)]
            test_runtime: None,
        };
        match config::read(&manager.config_path)
            .and_then(|document| manager.remember_config(&document))
        {
            Ok(()) => (),
            Err(error) => manager.last_error = Some(error.message),
        }
        if let Ok(document) = config::read(&manager.config_path) {
            match crate::core_update::Policy::from_config(&document.content) {
                Ok(policy) => manager.app.core_updates.configure(policy),
                Err(error) => manager.last_error = Some(error.message),
            }
        }
        if let Ok(executable) = core::fixed_core_path() {
            if let Ok(version) =
                crate::core_update_release::executable_version(&executable, &manager.cancellation)
            {
                manager.app.core_updates.observe_version(&version);
            }
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
        let failure = latest_operation_failure(
            self.core_status.last_operation_failure.as_ref(),
            self.host_operation_failure.as_ref(),
        );
        let tun = if self.core_status.pid.is_some() || failure.is_some() {
            self.core_status.tun
        } else {
            self.preferences.tun
        };
        DesktopStatus {
            phase: self.phase,
            system_proxy: self.proxy.owned().unwrap_or(false),
            tun,
            autostart: windows::autostart_enabled().unwrap_or(false),
            controller: if self.core_status.process_state == "alive"
                && matches!(self.core_status.control_state.as_str(), "ready" | "busy")
            {
                self.core_status.controller.clone()
            } else {
                None
            },
            last_error: self.last_error.clone(),
            last_operation_failure: failure.map(|mut failure| {
                failure.applied_tun = tun;
                failure
            }),
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
            tun_device: self.tun_device.clone(),
            core_observation: Some(self.core_status.clone()),
            host_operation_failure: self.host_operation_failure.clone(),
        }
    }

    pub fn publish(&self) {
        self.app.publish_status(self.build_status());
    }

    fn active_log_directory(&self) -> PathBuf {
        if self
            .running
            .as_ref()
            .map_or(self.last_runtime_service, RunningCore::service_owned)
        {
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
                self.tun_device.configured_name = None;
                self.tun_device.effective_name = None;
                self.tun_device.managed = false;
                return Ok(());
            }
        };
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

    /// Retire the old automatic alias only after this owner's observation proves
    /// there is no active TUN to rename. Source edits still take the normal
    /// serialized reconfiguration path; no new override is ever created.
    fn retire_legacy_tun_device(&mut self) -> Result<bool> {
        let no_process = self.core_status.process_state == "exited"
            && self.core_status.pid.is_none()
            && self.core_status.operation.is_none();
        let disabled = self.core_status.process_state == "alive"
            && self.core_status.control_state == "ready"
            && !self.core_status.tun
            && self.core_status.tun_health.as_deref() == Some("disabled")
            && self.core_status.observed_at != 0
            && core::observed_now().saturating_sub(self.core_status.observed_at) <= 30_000
            && self.core_status.operation.is_none();
        if (!no_process && !disabled) || self.preferences.tun_device_override.is_none() {
            return Ok(false);
        }
        self.cancellation.check()?;
        let previous = self.preferences.tun_device_override.take();
        if let Err(error) = self.persist() {
            self.preferences.tun_device_override = previous;
            return Err(error);
        }
        self.remember_config(&config::read(&self.config_path)?)?;
        self.record_lifecycle("tun_device_legacy_mapping_retired", serde_json::json!({
            "configuredName":config::diagnostic_tun_device(self.tun_device.configured_name.as_deref()),
            "noProcess":no_process,"tunDisabled":disabled,
        }));
        Ok(true)
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
        let result = self.start_once(persist, None);
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
        self.core_update_interrupted = false;
        self.interrupted_runtime_revision = None;
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
        }
        self.publish();
        let mut confirmed_start = false;
        let mut result = (|| {
            let (mut content, mut revision) = self.read_runtime_source(expected_revision)?;
            let policy = crate::core_update::Policy::from_config(&content)?;
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
            self.recover_core_update_before_start()?;
            self.running = Some(self.new_running_core()?);
            if self.preferences.tun_device_override.is_some() {
                // A new service client must observe the authenticated runtime;
                // absence of a local child does not establish service cleanup.
                self.core_status =
                    running_status(self.running.as_mut().expect("runtime set above"))?;
                if self.retire_legacy_tun_device()? {
                    (content, revision) = self.read_runtime_source(expected_revision)?;
                }
            }
            self.last_runtime_service = self
                .running
                .as_ref()
                .is_some_and(RunningCore::service_owned);
            self.publish();
            self.core_status = match self.running.as_mut().expect("runtime set above") {
                RunningCore::Service(client) => client.call(
                    Request::Start {
                        config: content.clone(),
                        tun: self.preferences.tun,
                    },
                    &self.cancellation,
                )?,
                RunningCore::Direct(supervisor) => supervisor.start(content.clone(), false)?,
                #[cfg(test)]
                RunningCore::Fixture(runtime) => runtime
                    .lock()
                    .unwrap()
                    .start(&content, self.preferences.tun)?,
            };
            if !self.core_status.running {
                return Err(AppError::new("core_start_failed", "内核尚未就绪"));
            }
            // A newer UI intent can arrive just after the service confirms
            // completion. Retain those applied facts before observing it.
            self.preferences.tun = self.core_status.tun;
            self.applied_config = Some(content.clone());
            self.applied_config_revision = Some(revision);
            self.app.core_updates.configure(policy);
            if let Some(version) = &self.core_status.version {
                self.app.core_updates.observe_version(version);
            }
            self.applied_proxy_port = config::mixed_port(&content).ok();
            self.phase = phase_for_core(&self.core_status);
            confirmed_start = true;
            self.cancellation.check()?;
            if self.preferences.system_proxy {
                self.proxy.enable(config::mixed_port(&content)?)?;
            }
            self.cancellation.check()?;
            self.phase = Phase::Running;
            self.host_operation_failure = None;
            if persist {
                self.preferences.desired_running = true;
                self.persist()?;
            }
            Ok(())
        })();
        if let Some(mut error) = result.as_ref().err().cloned() {
            if !confirmed_start
                && self.cancellation.check().is_err()
                && matches!(
                    error.code.as_str(),
                    "operation_cancelled" | "core_termination_unconfirmed"
                )
            {
                self.observe_interrupted_runtime(&error);
            }
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
                self.host_operation_failure = Some(OperationFailure::new(
                    "start",
                    &error,
                    self.preferences.tun,
                    self.core_status.tun,
                    self.cancellation.revision(),
                ));
                result = Err(error);
            }
        }
        self.publish();
        result
    }

    pub fn stop(&mut self, persist: bool) -> Result<()> {
        let started = Instant::now();
        self.core_update_interrupted = false;
        self.interrupted_runtime_revision = None;
        self.record_lifecycle(
            "desktop_stop_requested",
            serde_json::json!({"persistIntent":persist}),
        );
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
        self.host_operation_failure = None;
        let migration_error = self.retire_legacy_tun_device().err();
        let error = persist_error.or(proxy_error).or(migration_error);
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
        self.cancellation.check()?;
        let failed_switch = latest_operation_failure(
            self.core_status.last_operation_failure.as_ref(),
            self.host_operation_failure.as_ref(),
        )
        .as_ref()
        .is_some_and(|failure| matches!(failure.operation.as_str(), "tun_change" | "start"));
        if self.preferences.tun == enabled && !failed_switch {
            return Ok(());
        }
        let service_owned = self
            .running
            .as_ref()
            .is_some_and(RunningCore::service_owned);
        if enabled {
            config::runtime_config(&self.read_runtime_source(None)?.0, true, 9090)?;
            if !service_owned {
                self.ensure_service()?;
            }
        }
        let was_running = self.run_intent;
        let previous = self.preferences.tun;
        if was_running && service_owned && self.core_status.pid.is_some() {
            let (content, revision) = self.read_runtime_source(None)?;
            let result = self.apply_runtime_config(content, revision, enabled, true);
            // Only confirmed application changes the stored mode. A failed
            // transaction retains the previous mode and its owned process.
            if self.cancellation.check().is_ok() {
                self.persist()?;
            }
            self.publish();
            return result;
        }
        if was_running {
            self.stop(false)?;
        }
        self.preferences.tun = enabled;
        if was_running {
            if let Err(error) = self.start(false) {
                if error.code == "operation_cancelled" {
                    self.preferences.tun = if self.core_status.running {
                        self.core_status.tun
                    } else {
                        previous
                    };
                    return Err(error);
                }
                self.preferences.tun = previous;
                let _rollback_scope = crate::diagnostics::scope(
                    serde_json::json!({"cause":"tun_mode_rollback","triggerCode":error.code}),
                );
                self.record_lifecycle("desktop_mode_rollback_requested", serde_json::json!({}));
                let recovery = self.start(false);
                self.cancellation.check()?;
                self.host_operation_failure = Some(OperationFailure::new(
                    "tun_change",
                    &error,
                    enabled,
                    self.core_status.tun,
                    self.cancellation.revision(),
                ));
                self.last_error = if recovery.is_ok() {
                    self.core_status.error.clone()
                } else {
                    Some(format!("{}；恢复先前模式失败，请检查配置", error.message))
                };
                self.publish();
                return Err(error);
            }
        }
        self.host_operation_failure = None;
        self.persist()?;
        self.publish();
        Ok(())
    }

    fn ensure_service(&self) -> Result<()> {
        #[cfg(test)]
        if let Some(runtime) = &self.test_runtime {
            runtime.lock().unwrap().service_available = true;
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
            let mut state = runtime.lock().unwrap();
            state.service_owned = self.preferences.tun || state.service_available;
            return Ok(RunningCore::Fixture(runtime.clone()));
        }
        let use_service = if self.preferences.tun {
            self.ensure_service()?;
            true
        } else if windows::protected_install_dir().is_ok() {
            // Ordinary proxy mode can reuse an already running trusted owner,
            // but must never install/start a service or prompt for elevation.
            prefer_installed_service(true, &service::status()?)
        } else {
            false
        };
        if use_service {
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
        self.cancellation.check()?;
        let service_owner = match self.running.as_ref() {
            Some(RunningCore::Service(client)) => Some(RunningCore::Service(client.clone())),
            #[cfg(test)]
            Some(RunningCore::Fixture(runtime)) if runtime.lock().unwrap().service_owned => {
                Some(RunningCore::Fixture(runtime.clone()))
            }
            _ => None,
        };
        let resume_direct = service_owner.is_some() && self.run_intent;
        // The service may also own an ordinary proxy core. Confirm its exit
        // before unregistering that process owner, regardless of the TUN mode.
        if service_owner.is_some() {
            self.stop(false)?;
        }
        if let Err(error) = self.remove_service_registration() {
            // Uninstallation can be rejected after Stop succeeded. Retain the
            // same authenticated owner and its confirmed stopped observation.
            if let Some(owner) = service_owner {
                self.running = Some(owner);
                if let Some(RunningCore::Service(client)) = &self.running {
                    self.app.set_service_client(Some(client.clone()));
                }
            }
            self.last_error = Some(error.message.clone());
            self.publish();
            return Err(error);
        }
        self.cancellation.check()?;
        self.app.set_service_client(None);
        self.preferences.tun = false;
        self.persist()?;
        if resume_direct {
            self.start(false)?;
        }
        self.publish();
        Ok(())
    }

    fn remove_service_registration(&self) -> Result<()> {
        #[cfg(test)]
        if let Some(runtime) = &self.test_runtime {
            let mut runtime = runtime.lock().unwrap();
            runtime.actions.push("uninstall_service".into());
            let result = runtime.uninstalls.pop_front().unwrap_or(Ok(()));
            if result.is_ok() {
                runtime.service_available = false;
                runtime.service_owned = false;
            }
            return result;
        }
        windows::run_elevated_service_action_cancellable("uninstall", Some(&self.cancellation))
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
        if self
            .interrupted_runtime_revision
            .is_some_and(|revision| revision < self.cancellation.revision())
            && self.run_intent
            && self.core_status.pid.is_none()
            && self.core_status.process_state == "exited"
        {
            self.cancellation.check()?;
            return self.start(false);
        }
        if self.phase != Phase::Running {
            self.app
                .core_updates
                .configure(crate::core_update::Policy::from_config(&content)?);
            return Ok(());
        }
        self.apply_runtime_config(content, revision, self.preferences.tun, false)
    }

    fn apply_runtime_config(
        &mut self,
        content: String,
        revision: String,
        tun: bool,
        tun_change: bool,
    ) -> Result<()> {
        self.cancellation.check()?;
        let policy = crate::core_update::Policy::from_config(&content)?;
        if self.preferences.system_proxy {
            config::mixed_port(&content)?;
        }
        self.publish();
        let result = match self.running.as_mut() {
            Some(RunningCore::Direct(core)) => {
                core.reconfigure_request(content.clone(), tun, tun_change)
            }
            Some(RunningCore::Service(client)) => client.call(
                Request::Reload {
                    config: content.clone(),
                    tun,
                    tun_change,
                },
                &self.cancellation,
            ),
            #[cfg(test)]
            Some(RunningCore::Fixture(runtime)) => {
                runtime.lock().unwrap().reload(&content, tun, tun_change)
            }
            None => return Ok(()),
        };
        let status = match result {
            Ok(status) => status,
            Err(error) => {
                if matches!(
                    error.code.as_str(),
                    "operation_cancelled" | "core_termination_unconfirmed"
                ) {
                    self.observe_interrupted_runtime(&error);
                    self.publish();
                    return Err(error);
                }
                self.core_status = self
                    .running
                    .as_mut()
                    .and_then(|running| running_status(running).ok())
                    .unwrap_or_else(|| self.core_status.clone());
                self.phase = phase_for_core(&self.core_status);
                if self.core_status.last_operation_failure.is_some() {
                    self.last_error = self.core_status.error.clone();
                } else {
                    self.last_error = Some(error.message.clone());
                    self.core_status.error_code = Some(error.code.clone());
                }
                if self.core_status.process_state == "exited"
                    || self.core_status.listeners_ready == Some(false)
                {
                    let _ = self.proxy.restore();
                }
                self.publish();
                return Err(error);
            }
        };
        // Preserve a confirmed result even when a later UI action supersedes
        // this request before its caller can publish or change the proxy.
        self.core_status = status;
        self.preferences.tun = self.core_status.tun;
        self.applied_config = Some(content.clone());
        self.applied_config_revision = Some(revision);
        self.app.core_updates.configure(policy);
        self.phase = phase_for_core(&self.core_status);
        self.last_error = None;
        if tun_change {
            self.host_operation_failure = None;
        }
        self.applied_proxy_port = config::mixed_port(&content).ok();
        self.cancellation.check()?;
        if !self.core_status.tun {
            self.retire_legacy_tun_device()?;
        }
        if self.preferences.system_proxy {
            self.proxy.enable(config::mixed_port(&content)?)?;
        }
        self.publish();
        Ok(())
    }

    fn observe_interrupted_runtime(&mut self, error: &AppError) {
        let revision = self.cancellation.revision();
        let observed = match self.running.as_mut() {
            Some(RunningCore::Service(client)) => client.confirmed_cancelled_stop(revision),
            Some(running) => running_status(running),
            None => Err(AppError::new(
                "core_termination_unconfirmed",
                "缺少自有内核停止结果",
            )),
        };
        self.interrupted_runtime_revision = None;
        match observed {
            Ok(status)
                if status.pid.is_none()
                    && status.process_state == "exited"
                    && status.operation.is_none() =>
            {
                self.core_status = status;
                self.core_status.error_code = Some("operation_cancelled".into());
                self.interrupted_runtime_revision = Some(revision);
                self.phase = phase_for_core(&self.core_status);
            }
            _ => {
                self.core_status.running = false;
                self.core_status.process_state = "unknown".into();
                self.core_status.control_state = "unknown".into();
                self.core_status.operation = Some("stopping".into());
                self.core_status.listeners_ready = None;
                self.core_status.error_code = Some("core_termination_unconfirmed".into());
                self.phase = Phase::Stopping;
            }
        }
        self.core_status.controller = None;
        self.applied_proxy_port = None;
        let _ = self.proxy.restore();
        self.last_error = Some(error.message.clone());
        self.core_status.error = Some(error.message.clone());
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
            #[cfg(test)]
            Some(RunningCore::Fixture(runtime)) => runtime.lock().unwrap().poll(),
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
        } else {
            self.publish();
        }
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
            self.publish();
            let status = match self.running.as_ref() {
                Some(RunningCore::Service(client)) => client.call(
                    Request::Start {
                        config: source,
                        tun: self.core_status.tun,
                    },
                    &self.cancellation,
                )?,
                #[cfg(test)]
                Some(RunningCore::Fixture(runtime)) => runtime
                    .lock()
                    .unwrap()
                    .start(&source, self.core_status.tun)?,
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
        RunningCore::Fixture(runtime) => {
            let mut runtime = runtime.lock().unwrap();
            runtime
                .status_errors
                .pop_front()
                .map_or_else(|| Ok(runtime.status.clone()), Err)
        }
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

fn latest_operation_failure(
    core: Option<&OperationFailure>,
    host: Option<&OperationFailure>,
) -> Option<OperationFailure> {
    [core, host]
        .into_iter()
        .flatten()
        .max_by_key(|failure| (failure.intent_revision, failure.occurred_at))
        .cloned()
}

impl DesktopStatus {
    pub fn preserve_core_observation(&mut self, current: &Self) {
        let local_failure = self.control_state == "unreachable"
            && self
                .error_code
                .as_deref()
                .is_some_and(|code| code.starts_with("service_") || code == "io_error");
        let precise_failure = matches!(
            self.error_code.as_deref(),
            Some("core_termination_unconfirmed")
        ) || self.error_code.as_deref() == Some("tun_device_conflict")
            && !current
                .core_observation
                .as_ref()
                .is_some_and(|core| core.running && core.error_code.is_none());
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
            self.last_operation_failure = current.last_operation_failure.clone();
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
        if self.error_code.as_deref() == Some("core_termination_unconfirmed") {
            self.retain_unconfirmed_cleanup();
        }
    }

    pub fn apply_core_observation(&mut self, core: &CoreStatus) {
        let cleanup_failure = (self.error_code.as_deref() == Some("core_termination_unconfirmed"))
            .then(|| self.last_error.clone());
        self.copy_core_observation(core);
        if let Some(message) = cleanup_failure {
            self.error_code = Some("core_termination_unconfirmed".into());
            self.last_error = message;
            self.retain_unconfirmed_cleanup();
        }
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
        self.last_operation_failure = latest_operation_failure(
            core.last_operation_failure.as_ref(),
            self.host_operation_failure.as_ref(),
        );
        if core.pid.is_some() || self.last_operation_failure.is_some() {
            self.tun = core.tun;
        }
        if let Some(failure) = &mut self.last_operation_failure {
            failure.applied_tun = self.tun;
        }
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
            let runtime = Arc::new(Mutex::new(TestRuntime {
                service_owned: true,
                service_available: true,
                ..TestRuntime::default()
            }));
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
                last_runtime_service: true,
                phase: Phase::Stopped,
                core_status: CoreStatus {
                    process_state: "exited".into(),
                    observed_at: core::observed_now(),
                    ..CoreStatus::default()
                },
                applied_proxy_port: None,
                last_error: None,
                host_operation_failure: None,
                dirty: false,
                exiting: false,
                updating: false,
                core_update_interrupted: false,
                interrupted_runtime_revision: None,
                run_intent: true,
                resume_pending: None,
                communication_recovery: None,
                lease_recovery_pending: None,
                lease_restarts: VecDeque::new(),
                applied_config: None,
                applied_config_revision: None,
                tun_device: TunDeviceStatus::default(),
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

        fn installed_runtime(&mut self, mut status: CoreStatus) {
            status.tun = self.manager.preferences.tun;
            self.manager.last_runtime_service = self.runtime.lock().unwrap().service_owned;
            self.manager.applied_config =
                Some(config::read(&self.manager.config_path).unwrap().content);
            self.manager.applied_config_revision =
                Some(self.manager.tun_device.config_revision.clone());
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

    #[test]
    fn completed_core_update_replaces_finishing_heartbeat_before_success_is_published() {
        for tun in [false, true] {
            let mut fixture = Fixture::new(SOURCE);
            fixture.manager.preferences.tun = tun;
            let mut committed = ready(410);
            committed.service_instance = Some("update-service".into());
            committed.status_sequence = 10;
            committed.core_update = Some(core::CoreUpdateStatus {
                phase: "committed".into(),
                version: committed.version.clone(),
            });
            fixture.installed_runtime(committed);

            let mut finishing = fixture.manager.core_status.clone();
            finishing.status_sequence = 11;
            finishing.operation = Some("finishing_core_update".into());
            let mut heartbeat = fixture.manager.status();
            heartbeat.apply_core_observation(&finishing);
            fixture.manager.app.publish_status(heartbeat.clone());
            // Publishing the old commit cannot erase a newer service operation.
            fixture.manager.publish();
            let observed = fixture.manager.status();
            assert!(observed.phase == Phase::Recovering);
            assert_eq!(observed.service_sequence, 11);
            assert_eq!(observed.operation.as_deref(), Some("finishing_core_update"));

            let mut completed = finishing;
            completed.status_sequence = 12;
            completed.operation = None;
            completed.core_update.as_mut().unwrap().phase = "confirmed".into();
            fixture
                .manager
                .apply_core_update_completion(Ok(completed), false)
                .unwrap();
            fixture.manager.publish();
            let observed = fixture.manager.status();
            assert!(observed.phase == Phase::Running);
            assert!(observed.operation.is_none());
            assert_eq!(observed.service_sequence, 12);
            assert_eq!(observed.pid, Some(410));
            assert_eq!(observed.tun, tun);
            let update = fixture.manager.core_status.core_update.as_ref().unwrap();
            assert_eq!(update.phase, "confirmed");

            // The existing sequence barrier still rejects a delayed heartbeat.
            fixture.manager.app.publish_status(heartbeat);
            let observed = fixture.manager.status();
            assert!(observed.phase == Phase::Running);
            assert!(observed.operation.is_none());
            assert_eq!(observed.service_sequence, 12);
        }
    }

    #[test]
    fn installer_completion_preserves_the_local_running_or_stopped_owner() {
        for running in [false, true] {
            let mut fixture = Fixture::new(SOURCE);
            fixture.runtime.lock().unwrap().service_owned = false;
            fixture.manager.preferences.tun = false;
            fixture.manager.preferences.desired_running = running;
            fixture.manager.run_intent = running;
            let local = if running {
                ready(411)
            } else {
                CoreStatus {
                    process_state: "exited".into(),
                    control_state: "unknown".into(),
                    stop_reason: Some(StopReason::UserStop),
                    observed_at: core::observed_now(),
                    ..CoreStatus::default()
                }
            };
            fixture.installed_runtime(local);
            let before = serde_json::to_value(&fixture.manager.core_status).unwrap();
            let installer = CoreStatus {
                process_state: "exited".into(),
                control_state: "unknown".into(),
                stop_reason: Some(StopReason::CoreUpdate),
                service_instance: Some("installer-only".into()),
                status_sequence: 50,
                core_update: Some(core::CoreUpdateStatus {
                    phase: "confirmed".into(),
                    version: Some("fixture-update".into()),
                }),
                ..CoreStatus::default()
            };
            fixture
                .manager
                .apply_core_update_completion(Ok(installer), true)
                .unwrap();
            fixture.manager.publish();
            assert_eq!(
                serde_json::to_value(&fixture.manager.core_status).unwrap(),
                before
            );
            let observed = fixture.manager.status();
            let expected_phase = if running {
                Phase::Running
            } else {
                Phase::Stopped
            };
            assert!(observed.phase == expected_phase);
            assert!(observed.operation.is_none());
            assert_eq!(observed.pid, running.then_some(411));
            assert_eq!(observed.desired_running, running);
            assert!(observed.service_instance.is_none());
        }
    }

    #[test]
    fn failed_core_update_completion_does_not_replace_the_owner_observation() {
        for installer_only in [false, true] {
            let mut fixture = Fixture::new(SOURCE);
            fixture.installed_runtime(ready(412));
            let before = serde_json::to_value(&fixture.manager.core_status).unwrap();
            let error = fixture
                .manager
                .apply_core_update_completion(
                    Err(AppError::new("core_update_accept_failed", "fixture failure")),
                    installer_only,
                )
                .unwrap_err();
            assert_eq!(error.code, "core_update_accept_failed");
            assert_eq!(
                serde_json::to_value(&fixture.manager.core_status).unwrap(),
                before
            );
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
    fn ordinary_mode_only_prefers_an_installed_running_trusted_service() {
        for trusted in [false, true] {
            for installed in [false, true] {
                for running in [false, true] {
                    assert_eq!(
                        prefer_installed_service(
                            trusted,
                            &ServiceStatusView { installed, running }
                        ),
                        trusted && installed && running,
                    );
                }
            }
        }
    }

    #[test]
    fn native_failure_never_creates_a_new_alias_or_restarts_a_healthy_runtime() {
        for starting in [false, true] {
            let mut fixture = Fixture::new(SOURCE);
            if starting {
                fixture
                    .runtime
                    .lock()
                    .unwrap()
                    .starts
                    .push_back(Err(conflict("utun")));
                assert_eq!(
                    fixture.manager.start(false).unwrap_err().code,
                    "tun_device_conflict"
                );
                assert_eq!(
                    fixture.runtime.lock().unwrap().actions,
                    ["start:utun", "stop"]
                );
            } else {
                fixture.manager.preferences.tun = false;
                fixture.installed_runtime(ready(111));
                fixture
                    .runtime
                    .lock()
                    .unwrap()
                    .reloads
                    .push_back(Err(conflict("utun")));
                assert_eq!(
                    fixture.manager.set_tun(true).unwrap_err().code,
                    "tun_device_conflict"
                );
                let observed = fixture.manager.status();
                assert!(observed.phase == Phase::Running && !observed.tun);
                assert_eq!(observed.pid, Some(111));
                assert!(observed.last_error.is_none() && observed.error_code.is_none());
                assert_eq!(
                    observed.last_operation_failure.unwrap().operation,
                    "tun_change"
                );
                assert_eq!(fixture.runtime.lock().unwrap().actions, ["reload:utun"]);
            }
            assert!(fixture.manager.preferences.tun_device_override.is_none());
            assert_eq!(
                std::fs::read_to_string(&fixture.manager.config_path).unwrap(),
                SOURCE
            );
        }
    }

    #[test]
    fn legacy_alias_waits_for_a_fresh_confirmed_disabled_boundary() {
        let mut fixture = Fixture::new(SOURCE);
        fixture.manager.preferences.tun_device_override =
            Some(config::TunDeviceOverride::managed(Some("utun".into())));
        fixture
            .manager
            .remember_config(&config::read(&fixture.manager.config_path).unwrap())
            .unwrap();
        fixture.manager.persist().unwrap();
        fixture.installed_runtime(ready(111));
        assert!(!fixture.manager.retire_legacy_tun_device().unwrap());
        assert_eq!(
            fixture.manager.tun_device.effective_name.as_deref(),
            Some(config::MANAGED_TUN_DEVICE)
        );
        fixture.manager.core_status.tun = false;
        fixture.manager.core_status.tun_health = Some("disabled".into());
        fixture.manager.core_status.observed_at = core::observed_now().saturating_sub(30_001);
        assert!(
            !fixture.manager.retire_legacy_tun_device().unwrap(),
            "a stale disabled snapshot cannot retire a live mapping"
        );
        fixture.manager.core_status.observed_at = core::observed_now();
        fixture.manager.core_status.operation = Some("reloading".into());
        assert!(
            !fixture.manager.retire_legacy_tun_device().unwrap(),
            "a pending operation may already be recreating the adapter"
        );
        fixture.manager.core_status.operation = None;
        assert!(fixture.manager.retire_legacy_tun_device().unwrap());
        assert!(config::load_preferences(&fixture.directory)
            .unwrap()
            .tun_device_override
            .is_none());
        assert_eq!(
            fixture.manager.tun_device.effective_name.as_deref(),
            Some("utun")
        );
        assert_eq!(
            std::fs::read_to_string(&fixture.manager.config_path).unwrap(),
            SOURCE
        );
        assert!(fixture.runtime.lock().unwrap().actions.is_empty());
    }

    #[test]
    fn stopped_legacy_alias_is_retired_before_first_start_and_respects_utun() {
        let mut fixture = Fixture::new(SOURCE);
        fixture.manager.preferences.tun_device_override =
            Some(config::TunDeviceOverride::managed(Some("utun".into())));
        fixture
            .manager
            .remember_config(&config::read(&fixture.manager.config_path).unwrap())
            .unwrap();
        {
            let mut runtime = fixture.runtime.lock().unwrap();
            runtime.status = CoreStatus {
                process_state: "exited".into(),
                observed_at: core::observed_now(),
                ..CoreStatus::default()
            };
            runtime.starts.push_back(Ok(ready(222)));
        }
        fixture.manager.start(false).unwrap();
        assert!(fixture.manager.preferences.tun_device_override.is_none());
        assert_eq!(
            fixture.manager.tun_device.effective_name.as_deref(),
            Some("utun")
        );
        assert_eq!(fixture.runtime.lock().unwrap().actions, ["start:utun"]);
        assert_eq!(
            std::fs::read_to_string(&fixture.manager.config_path).unwrap(),
            SOURCE
        );
    }

    #[test]
    fn legacy_alias_retirement_keeps_the_choice_if_persistence_fails() {
        let mut fixture = Fixture::new(SOURCE);
        fixture.manager.preferences.tun_device_override =
            Some(config::TunDeviceOverride::managed(Some("utun".into())));
        fixture
            .manager
            .remember_config(&config::read(&fixture.manager.config_path).unwrap())
            .unwrap();
        let preferences = fixture.directory.join("preferences.json");
        std::fs::remove_file(&preferences).unwrap();
        std::fs::create_dir(&preferences).unwrap();
        assert!(fixture.manager.retire_legacy_tun_device().is_err());
        assert!(fixture.manager.preferences.tun_device_override.is_some());
        assert_eq!(
            fixture.manager.tun_device.effective_name.as_deref(),
            Some(config::MANAGED_TUN_DEVICE)
        );
        assert_eq!(
            std::fs::read_to_string(&fixture.manager.config_path).unwrap(),
            SOURCE
        );
    }

    #[test]
    fn ordinary_reload_keeps_failed_tun_intent_and_explicit_disable_acknowledges_it() {
        let mut fixture = Fixture::new(SOURCE);
        fixture.manager.preferences.tun = false;
        fixture.installed_runtime(ready(111));
        fixture.runtime.lock().unwrap().reloads.extend([
            Err(conflict("utun")),
            Ok(ready(111)),
            Ok(ready(111)),
        ]);
        assert!(fixture.manager.set_tun(true).is_err());
        let failure = fixture.manager.status().last_operation_failure;
        fixture.manager.reload_config(false).unwrap();
        assert_eq!(fixture.manager.status().last_operation_failure, failure);
        fixture.manager.set_tun(false).unwrap();
        assert!(fixture.manager.status().last_operation_failure.is_none());
        assert_eq!(
            fixture.runtime.lock().unwrap().tun_change_requests,
            [true, false, true]
        );
        assert_eq!(fixture.manager.status().pid, Some(111));
    }

    #[test]
    fn direct_to_service_rollback_preserves_failed_request_through_healthy_polls() {
        let mut fixture = Fixture::new(SOURCE);
        fixture.manager.preferences.tun = false;
        fixture.runtime.lock().unwrap().service_owned = false;
        fixture.installed_runtime(ready(111));
        fixture.runtime.lock().unwrap().starts.extend([
            Err(AppError::new(
                "tun_device_busy",
                "owned adapter is still active",
            )),
            Ok(ready(222)),
        ]);
        assert_eq!(
            fixture.manager.set_tun(true).unwrap_err().code,
            "tun_device_busy"
        );
        let restored = fixture.manager.status();
        assert!(restored.phase == Phase::Running && !restored.tun);
        assert_eq!(restored.pid, Some(222));
        assert!(restored.last_error.is_none() && restored.error_code.is_none());
        let failure = restored.last_operation_failure.unwrap();
        assert!(failure.requested_tun && !failure.applied_tun);
        assert_eq!(failure.operation, "tun_change");
        fixture.manager.poll();
        assert_eq!(
            fixture.manager.status().last_operation_failure,
            Some(failure.clone())
        );
        fixture
            .runtime
            .lock()
            .unwrap()
            .reloads
            .push_back(Ok(ready(222)));
        fixture.manager.reload_config(false).unwrap();
        assert_eq!(
            fixture.manager.status().last_operation_failure,
            Some(failure)
        );
        fixture
            .runtime
            .lock()
            .unwrap()
            .reloads
            .push_back(Ok(ready(222)));
        fixture.manager.set_tun(false).unwrap();
        assert!(fixture.manager.status().last_operation_failure.is_none());
        assert!(fixture.manager.status().last_error.is_none());
    }

    #[test]
    fn service_tun_round_trip_reconfigures_the_same_owned_process() {
        let mut fixture = Fixture::new(SOURCE);
        fixture.manager.preferences.tun = false;
        fixture.installed_runtime(ready(111));
        fixture
            .runtime
            .lock()
            .unwrap()
            .reloads
            .extend([Ok(ready(111)), Ok(ready(111))]);
        fixture.manager.set_tun(true).unwrap();
        assert!(fixture.manager.preferences.tun);
        assert!(config::load_preferences(&fixture.directory).unwrap().tun);
        fixture.manager.set_tun(false).unwrap();
        assert!(!fixture.manager.preferences.tun);
        assert!(!config::load_preferences(&fixture.directory).unwrap().tun);
        assert_eq!(fixture.manager.core_status.pid, Some(111));
        assert!(!fixture.manager.core_status.tun);
        assert_eq!(fixture.manager.applied_config.as_deref(), Some(SOURCE));
        assert_eq!(
            fixture.manager.applied_config_revision.as_deref(),
            Some(config::revision(SOURCE.as_bytes()).as_str())
        );
        assert!(fixture.manager.running.as_ref().unwrap().service_owned());
        let runtime = fixture.runtime.lock().unwrap();
        assert_eq!(runtime.actions, ["reload:utun", "reload:utun"]);
        assert_eq!(runtime.tun_requests, [true, false]);
    }

    #[test]
    fn failed_service_tun_switch_retains_confirmed_mode_source_and_pid() {
        for previous in [false, true] {
            let mut fixture = Fixture::new(SOURCE);
            fixture.manager.preferences.tun = previous;
            fixture.manager.persist().unwrap();
            fixture.installed_runtime(ready(111));
            let revision = fixture.manager.applied_config_revision.clone();
            fixture
                .runtime
                .lock()
                .unwrap()
                .reloads
                .push_back(Err(AppError::new(
                    "reload_failed",
                    "old configuration restored",
                )));
            assert_eq!(
                fixture.manager.set_tun(!previous).unwrap_err().code,
                "reload_failed"
            );
            assert_eq!(fixture.manager.preferences.tun, previous);
            assert_eq!(
                config::load_preferences(&fixture.directory).unwrap().tun,
                previous
            );
            assert_eq!(fixture.manager.core_status.pid, Some(111));
            assert_eq!(fixture.manager.core_status.tun, previous);
            assert_eq!(fixture.manager.applied_config.as_deref(), Some(SOURCE));
            assert_eq!(fixture.manager.applied_config_revision, revision);
            assert_eq!(fixture.runtime.lock().unwrap().actions, ["reload:utun"]);
        }
    }

    #[test]
    fn cancelled_service_tun_switch_never_rolls_back_by_restarting() {
        let mut fixture = Fixture::new(SOURCE);
        fixture.manager.preferences.tun = false;
        fixture.manager.persist().unwrap();
        fixture.installed_runtime(ready(111));
        fixture
            .runtime
            .lock()
            .unwrap()
            .reloads
            .push_back(Err(AppError::new("operation_cancelled", "stop accepted")));
        assert_eq!(
            fixture.manager.set_tun(true).unwrap_err().code,
            "operation_cancelled"
        );
        assert!(!fixture.manager.preferences.tun);
        assert!(!config::load_preferences(&fixture.directory).unwrap().tun);
        assert!(fixture.manager.core_status.pid.is_none());
        assert!(fixture.manager.core_status.controller.is_none());
        assert!(fixture.manager.applied_proxy_port.is_none());
        assert_eq!(
            fixture.manager.interrupted_runtime_revision,
            Some(fixture.manager.cancellation.revision())
        );
        assert_eq!(fixture.runtime.lock().unwrap().actions, ["reload:utun"]);
    }

    #[test]
    fn unconfirmed_cancel_withdraws_old_readiness_and_keeps_the_owner() {
        let mut fixture = Fixture::new(SOURCE);
        fixture.manager.preferences.tun = false;
        fixture.installed_runtime(ready(111));
        fixture.manager.applied_proxy_port = Some(7890);
        {
            let mut runtime = fixture.runtime.lock().unwrap();
            runtime
                .reloads
                .push_back(Err(AppError::new("operation_cancelled", "cancelled")));
            runtime
                .status_errors
                .push_back(AppError::new("service_unavailable", "query failed"));
        }
        assert!(fixture.manager.set_tun(true).is_err());
        assert!(fixture.manager.phase == Phase::Stopping);
        assert_eq!(fixture.manager.core_status.process_state, "unknown");
        assert_eq!(
            fixture.manager.core_status.error_code.as_deref(),
            Some("core_termination_unconfirmed")
        );
        assert!(fixture.manager.core_status.controller.is_none());
        assert!(fixture.manager.applied_proxy_port.is_none());
        assert!(fixture.manager.interrupted_runtime_revision.is_none());
        assert!(fixture.manager.running.as_ref().unwrap().service_owned());
        assert_eq!(fixture.runtime.lock().unwrap().actions, ["reload:utun"]);
    }

    #[test]
    fn completed_start_or_reload_keeps_applied_facts_when_the_next_intent_arrives() {
        for start in [false, true] {
            let mut fixture = Fixture::new(SOURCE);
            if start {
                fixture
                    .runtime
                    .lock()
                    .unwrap()
                    .starts
                    .push_back(Ok(ready(111)));
            } else {
                fixture.manager.preferences.tun = false;
                fixture.installed_runtime(ready(111));
                fixture
                    .runtime
                    .lock()
                    .unwrap()
                    .reloads
                    .push_back(Ok(ready(111)));
            }
            let context = fixture.manager.app.clone();
            fixture.runtime.lock().unwrap().checkpoint = Some(Box::new(move |stage| {
                if stage == if start { "started" } else { "reloaded" } {
                    context.runtime_generation.fetch_add(1, Ordering::AcqRel);
                }
            }));
            let result = if start {
                fixture.manager.start(false)
            } else {
                fixture.manager.set_tun(true)
            };
            assert_eq!(result.unwrap_err().code, "operation_cancelled");
            assert!(fixture.manager.core_status.running);
            assert_eq!(fixture.manager.core_status.pid, Some(111));
            assert!(fixture.manager.core_status.tun);
            assert!(fixture.manager.preferences.tun);
            assert!(fixture.manager.phase == Phase::Running);
            assert_eq!(fixture.manager.applied_config.as_deref(), Some(SOURCE));
            assert_eq!(fixture.manager.applied_proxy_port, Some(7890));
            assert!(fixture.manager.interrupted_runtime_revision.is_none());
            assert_eq!(
                fixture.runtime.lock().unwrap().actions,
                [if start { "start:utun" } else { "reload:utun" }]
            );
        }
    }

    #[test]
    fn new_reload_resumes_a_confirmed_cancel_even_after_status_clears_its_error() {
        let mut fixture = Fixture::new(SOURCE);
        fixture.manager.preferences.tun = false;
        fixture.installed_runtime(ready(111));
        fixture
            .runtime
            .lock()
            .unwrap()
            .reloads
            .push_back(Err(AppError::new("operation_cancelled", "cancelled")));
        assert!(fixture.manager.set_tun(true).is_err());
        fixture.runtime.lock().unwrap().status.error_code = None;
        fixture.manager.poll();
        assert!(fixture.manager.core_status.error_code.is_none());
        let generation = fixture
            .manager
            .app
            .runtime_generation
            .fetch_add(1, Ordering::AcqRel)
            + 1;
        fixture.manager.begin_operation(generation);
        fixture
            .runtime
            .lock()
            .unwrap()
            .starts
            .push_back(Ok(ready(222)));
        fixture.manager.reload_config(false).unwrap();
        assert_eq!(fixture.manager.core_status.pid, Some(222));
        assert!(!fixture.manager.core_status.tun);
        assert!(fixture.manager.interrupted_runtime_revision.is_none());
        assert_eq!(
            fixture.runtime.lock().unwrap().actions,
            ["reload:utun", "poll", "stop", "start:utun"]
        );
    }

    #[test]
    fn stop_consumes_cancel_recovery_and_reload_does_not_start_a_stopped_core() {
        let mut fixture = Fixture::new(SOURCE);
        fixture.manager.preferences.tun = false;
        fixture.installed_runtime(ready(111));
        fixture
            .runtime
            .lock()
            .unwrap()
            .reloads
            .push_back(Err(AppError::new("operation_cancelled", "cancelled")));
        assert!(fixture.manager.set_tun(true).is_err());
        fixture.manager.stop(true).unwrap();
        let generation = fixture
            .manager
            .app
            .runtime_generation
            .fetch_add(1, Ordering::AcqRel)
            + 1;
        fixture.manager.begin_operation(generation);
        fixture.manager.reload_config(false).unwrap();
        assert!(!fixture.manager.run_intent);
        assert!(fixture.manager.interrupted_runtime_revision.is_none());
        assert_eq!(
            fixture.runtime.lock().unwrap().actions,
            ["reload:utun", "stop"]
        );
    }

    #[test]
    fn log_owner_survives_stop_and_failed_start_until_the_owner_changes() {
        let mut fixture = Fixture::new(SOURCE);
        fixture.manager.preferences.tun = false;
        fixture.installed_runtime(ready(111));
        fixture.manager.stop(false).unwrap();
        assert!(fixture.manager.last_runtime_service);
        fixture
            .runtime
            .lock()
            .unwrap()
            .starts
            .push_back(Err(AppError::new("config_invalid", "invalid")));
        assert!(fixture.manager.start(false).is_err());
        assert!(fixture.manager.running.is_none());
        assert!(fixture.manager.last_runtime_service);
        {
            let mut runtime = fixture.runtime.lock().unwrap();
            runtime.service_available = false;
            runtime.starts.push_back(Ok(ready(222)));
        }
        fixture.manager.start(false).unwrap();
        assert!(!fixture.manager.last_runtime_service);
        fixture.manager.stop(false).unwrap();
        assert!(!fixture.manager.last_runtime_service);
    }

    #[test]
    fn uninstall_stops_service_owned_ordinary_core_before_direct_restart() {
        for tun in [false, true] {
            let mut fixture = Fixture::new(SOURCE);
            fixture.manager.preferences.tun = tun;
            fixture.installed_runtime(ready(111));
            fixture
                .runtime
                .lock()
                .unwrap()
                .starts
                .push_back(Ok(ready(222)));
            fixture.manager.uninstall_service().unwrap();
            assert_eq!(
                fixture.runtime.lock().unwrap().actions,
                ["stop", "uninstall_service", "start:utun"]
            );
            assert_eq!(fixture.manager.core_status.pid, Some(222));
            assert!(!fixture.manager.preferences.tun);
            assert!(!fixture.manager.running.as_ref().unwrap().service_owned());
            assert_eq!(fixture.runtime.lock().unwrap().tun_requests, [false]);
        }
    }

    #[test]
    fn uninstall_never_abandons_an_unconfirmed_owned_process() {
        let mut fixture = Fixture::new(SOURCE);
        fixture.manager.preferences.tun = false;
        fixture.installed_runtime(ready(111));
        fixture
            .runtime
            .lock()
            .unwrap()
            .stops
            .push_back(Err(AppError::new(
                "core_termination_unconfirmed",
                "still owned",
            )));
        assert_eq!(
            fixture.manager.uninstall_service().unwrap_err().code,
            "core_termination_unconfirmed"
        );
        assert_eq!(fixture.runtime.lock().unwrap().actions, ["stop"]);
        assert!(fixture.manager.running.as_ref().unwrap().service_owned());
    }

    #[test]
    fn failed_uninstall_keeps_old_intent_and_the_confirmed_stopped_state() {
        let mut fixture = Fixture::new(SOURCE);
        fixture.installed_runtime(ready(111));
        fixture
            .runtime
            .lock()
            .unwrap()
            .uninstalls
            .push_back(Err(AppError::new("elevation_cancelled", "cancelled")));
        assert_eq!(
            fixture.manager.uninstall_service().unwrap_err().code,
            "elevation_cancelled"
        );
        assert!(fixture.manager.preferences.tun);
        assert!(fixture.manager.run_intent);
        assert!(fixture.manager.core_status.pid.is_none());
        assert!(fixture.manager.running.as_ref().unwrap().service_owned());
        assert_eq!(
            fixture.runtime.lock().unwrap().actions,
            ["stop", "uninstall_service"]
        );
    }

    #[test]
    fn service_uninstall_never_drops_a_direct_core() {
        for rejected in [false, true] {
            let mut fixture = Fixture::new(SOURCE);
            fixture.manager.preferences.tun = false;
            fixture.runtime.lock().unwrap().service_owned = false;
            fixture.installed_runtime(ready(111));
            if rejected {
                fixture
                    .runtime
                    .lock()
                    .unwrap()
                    .uninstalls
                    .push_back(Err(AppError::new("elevation_cancelled", "cancelled")));
            }
            assert_eq!(fixture.manager.uninstall_service().is_err(), rejected);
            assert_eq!(fixture.manager.core_status.pid, Some(111));
            assert!(!fixture.manager.running.as_ref().unwrap().service_owned());
            assert_eq!(
                fixture.runtime.lock().unwrap().actions,
                ["uninstall_service"]
            );
        }
    }

    #[test]
    fn ordinary_service_lease_recovery_keeps_the_applied_tun_mode() {
        let mut fixture = Fixture::new(SOURCE);
        fixture.manager.preferences.tun = false;
        fixture.installed_runtime(CoreStatus {
            process_state: "exited".into(),
            stop_reason: Some(StopReason::LeaseExpired),
            ..CoreStatus::default()
        });
        fixture
            .runtime
            .lock()
            .unwrap()
            .starts
            .push_back(Ok(ready(222)));
        fixture
            .manager
            .recover_lease(fixture.manager.app.generation());
        assert!(fixture.manager.core_status.running);
        assert!(!fixture.manager.core_status.tun);
        assert_eq!(fixture.runtime.lock().unwrap().tun_requests, [false]);
    }

    #[test]
    fn newer_reload_resumes_an_interrupted_core_update_with_the_new_config() {
        let mut fixture = Fixture::new(SOURCE);
        fixture.installed_runtime(CoreStatus {
            process_state: "exited".into(),
            stop_reason: Some(StopReason::CoreUpdate),
            ..CoreStatus::default()
        });
        fixture.manager.core_update_interrupted = true;
        let revised = SOURCE.replace("device: utun", "device: revised");
        config::atomic_write(&fixture.manager.config_path, revised.as_bytes()).unwrap();
        fixture
            .runtime
            .lock()
            .unwrap()
            .starts
            .push_back(Ok(ready(223)));
        fixture.manager.reload_config(false).unwrap();
        assert!(!fixture.manager.core_status.running);
        fixture.manager.resume_interrupted_core_update().unwrap();
        assert!(fixture.manager.core_status.running);
        assert_eq!(
            fixture.manager.applied_config.as_deref(),
            Some(revised.as_str())
        );
        assert_eq!(
            fixture.runtime.lock().unwrap().actions,
            ["stop", "start:revised"]
        );
        assert!(!fixture.manager.core_update_interrupted);
    }

    #[test]
    fn explicit_stop_consumes_interrupted_update_without_a_late_restart() {
        let mut fixture = Fixture::new(SOURCE);
        fixture.installed_runtime(CoreStatus {
            process_state: "exited".into(),
            ..CoreStatus::default()
        });
        fixture.manager.core_update_interrupted = true;
        fixture.manager.stop(true).unwrap();
        fixture.manager.resume_interrupted_core_update().unwrap();
        assert!(!fixture.manager.run_intent);
        assert!(!fixture.manager.core_status.running);
        assert_eq!(fixture.runtime.lock().unwrap().actions, ["stop"]);
    }

    #[test]
    fn cancelled_command_cannot_resume_interrupted_update_under_a_new_intent() {
        let mut fixture = Fixture::new(SOURCE);
        fixture.installed_runtime(CoreStatus {
            process_state: "exited".into(),
            ..CoreStatus::default()
        });
        fixture.manager.core_update_interrupted = true;
        fixture
            .manager
            .app
            .runtime_generation
            .fetch_add(1, Ordering::AcqRel);
        assert_eq!(
            fixture
                .manager
                .resume_interrupted_core_update()
                .unwrap_err()
                .code,
            "operation_cancelled"
        );
        assert!(fixture.runtime.lock().unwrap().actions.is_empty());
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
