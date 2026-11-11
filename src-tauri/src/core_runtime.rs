//! A short control boundary around one process owner. Status and lease traffic
//! never execute lifecycle work; the sole worker retains every Child/Job handle.
use crate::{
    core::{CoreStatus, CoreSupervisor},
    error::{AppError, Result},
    lifecycle::{Cancellation, StopReason},
};
use serde::{Deserialize, Serialize};
use std::{
    collections::VecDeque,
    sync::{
        atomic::{AtomicBool, AtomicU64, Ordering},
        Arc, Condvar, Mutex,
    },
    thread,
    time::{Duration, Instant},
};

const RETAINED_OPERATIONS: usize = 64;

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RuntimeOperation {
    pub id: String,
    pub phase: String,
    pub intent_revision: u64,
    pub status: CoreStatus,
    pub error: Option<AppError>,
}

#[derive(Clone)]
pub enum RuntimeRequest {
    Start(String),
    Reload(String),
    Stop(StopReason),
    PrepareCoreUpdate { proxy_port: Option<u16> },
    CommitCoreUpdate,
    CompleteCoreUpdate { accept: bool },
}

impl RuntimeRequest {
    fn name(&self) -> &'static str {
        match self {
            Self::Start(_) => "starting",
            Self::Reload(_) => "reloading",
            Self::Stop(_) => "stopping",
            Self::PrepareCoreUpdate { .. } => "preparing_core_update",
            Self::CommitCoreUpdate => "updating_core",
            Self::CompleteCoreUpdate { .. } => "finishing_core_update",
        }
    }
    fn fingerprint(&self) -> String {
        use sha2::{Digest, Sha256};
        let mut digest = Sha256::new();
        digest.update(self.name());
        match self {
            Self::Start(source) | Self::Reload(source) => digest.update(source),
            Self::Stop(reason) => digest.update(format!("{reason:?}")),
            Self::PrepareCoreUpdate { proxy_port } => digest.update(format!("{proxy_port:?}")),
            Self::CommitCoreUpdate => (),
            Self::CompleteCoreUpdate { accept } => digest.update([u8::from(*accept)]),
        }
        hex::encode(digest.finalize())
    }
}

struct Work {
    id: String,
    revision: u64,
    intent_revision: u64,
    request: RuntimeRequest,
}

struct Record {
    fingerprint: String,
    operation: RuntimeOperation,
}

struct State {
    status: CoreStatus,
    records: VecDeque<Record>,
    pending: Option<Work>,
    reconcile: Option<u64>,
    sequence: u64,
}

struct Shared {
    state: Mutex<State>,
    wake: Condvar,
    revision: Arc<AtomicU64>,
    shutdown: AtomicBool,
    instance: String,
}

#[derive(Clone)]
pub struct CoreRuntime {
    shared: Arc<Shared>,
}

impl CoreRuntime {
    #[cfg(test)]
    pub(crate) fn queued_for_test() -> Self {
        Self {
            shared: Arc::new(Shared {
                state: Mutex::new(State {
                    status: CoreStatus::default(),
                    records: VecDeque::new(),
                    pending: None,
                    reconcile: None,
                    sequence: 0,
                }),
                wake: Condvar::new(),
                revision: Arc::new(AtomicU64::new(0)),
                shutdown: AtomicBool::new(false),
                instance: "fixture".into(),
            }),
        }
    }
    pub fn spawn(core: CoreSupervisor) -> Result<Self> {
        let mut random = [0u8; 16];
        getrandom::getrandom(&mut random)
            .map_err(|_| AppError::new("random_failed", "无法建立服务实例编号"))?;
        let shared = Arc::new(Shared {
            state: Mutex::new(State {
                status: core.status(),
                records: VecDeque::new(),
                pending: None,
                reconcile: None,
                sequence: 0,
            }),
            wake: Condvar::new(),
            revision: Arc::new(AtomicU64::new(0)),
            shutdown: AtomicBool::new(false),
            instance: hex::encode(random),
        });
        let worker = shared.clone();
        thread::Builder::new()
            .name("service-core-owner".into())
            .spawn(move || run_worker(worker, core))
            .map_err(|error| AppError::io("无法启动内核生命周期线程", error))?;
        Ok(Self { shared })
    }

    pub fn status(&self) -> CoreStatus {
        self.shared
            .state
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .status
            .clone()
    }

    pub fn operation(&self, id: &str) -> Result<RuntimeOperation> {
        self.shared
            .state
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .records
            .iter()
            .find(|record| record.operation.id == id)
            .map(|record| record.operation.clone())
            .ok_or_else(|| {
                AppError::new(
                    "service_operation_missing",
                    "服务操作记录不存在或已过期，请重新读取状态",
                )
            })
    }

    pub fn submit(
        &self,
        id: String,
        intent_revision: u64,
        request: RuntimeRequest,
    ) -> Result<RuntimeOperation> {
        if self.shared.shutdown.load(Ordering::Acquire) {
            return Err(AppError::new("service_stopping", "TUN 服务已关闭操作通道"));
        }
        let fingerprint = request.fingerprint();
        let mut state = self.shared.state.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(record) = state
            .records
            .iter()
            .find(|record| record.operation.id == id)
        {
            return if record.fingerprint == fingerprint
                && record.operation.intent_revision == intent_revision
            {
                Ok(record.operation.clone())
            } else {
                Err(AppError::new(
                    "service_request_conflict",
                    "重复的服务请求编号对应了不同操作",
                ))
            };
        }
        // One replaceable pending slot bounds memory and reserves Stop capacity.
        // The previous worker token is cancelled before admitting its successor.
        let revision = self.shared.revision.fetch_add(1, Ordering::AcqRel) + 1;
        if let Some(previous) = state.pending.take() {
            if let Some(record) = state
                .records
                .iter_mut()
                .find(|record| record.operation.id == previous.id)
            {
                record.operation.phase = "cancelled".into();
                record.operation.error = Some(cancelled());
            }
        }
        state.status.operation = Some(request.name().into());
        state.status.intent_revision = intent_revision;
        if matches!(request, RuntimeRequest::Stop(_)) {
            state.status.desired_running = false;
            state.status.recovering = false;
            state.reconcile = None;
        } else if matches!(request, RuntimeRequest::Start(_)) {
            state.status.desired_running = true;
        }
        stamp(&self.shared, &mut state);
        let operation = RuntimeOperation {
            id: id.clone(),
            phase: "queued".into(),
            intent_revision,
            status: state.status.clone(),
            error: None,
        };
        state.records.push_back(Record {
            fingerprint,
            operation: operation.clone(),
        });
        while state.records.len() > RETAINED_OPERATIONS {
            if let Some(index) = state
                .records
                .iter()
                .position(|record| !matches!(record.operation.phase.as_str(), "queued" | "running"))
            {
                state.records.remove(index);
            } else {
                break;
            }
        }
        state.pending = Some(Work {
            id,
            revision,
            intent_revision,
            request,
        });
        self.shared.wake.notify_one();
        Ok(operation)
    }

    pub fn reconcile(&self, generation: u64) {
        let mut state = self.shared.state.lock().unwrap_or_else(|e| e.into_inner());
        if state.status.desired_running {
            state.reconcile = Some(state.reconcile.unwrap_or(0).max(generation));
            self.shared.wake.notify_one();
        }
    }

    pub fn shutdown(&self) -> Result<()> {
        let operation = self.submit(
            format!("shutdown-{}", self.shared.instance),
            self.status().intent_revision,
            RuntimeRequest::Stop(StopReason::Shutdown),
        )?;
        let deadline = Instant::now() + Duration::from_secs(18);
        while Instant::now() < deadline {
            let result = self.operation(&operation.id)?;
            if !matches!(result.phase.as_str(), "queued" | "running") {
                self.shared.shutdown.store(true, Ordering::Release);
                self.shared.wake.notify_one();
                return result.error.map_or(Ok(()), Err);
            }
            thread::sleep(Duration::from_millis(25));
        }
        Err(AppError::new(
            "core_termination_unconfirmed",
            "服务关闭时未能确认内核停止，保留进程所有权",
        ))
    }
}

fn cancelled() -> AppError {
    AppError::new("operation_cancelled", "此操作已被后续指令取消")
}

fn stamp(shared: &Shared, state: &mut State) {
    state.sequence += 1;
    state.status.service_instance = Some(shared.instance.clone());
    state.status.status_sequence = state.sequence;
}

fn run_worker(shared: Arc<Shared>, mut core: CoreSupervisor) {
    let _worker_scope = crate::diagnostics::scope(
        serde_json::json!({"component":"core_runtime","serviceInstance":shared.instance}),
    );
    core.record_lifecycle("core_runtime_started", serde_json::json!({}));
    let mut last_poll = Instant::now() - Duration::from_secs(2);
    while !shared.shutdown.load(Ordering::Acquire) {
        let (work, reconcile, revision, intent_revision, intended_running) = {
            let mut state = shared.state.lock().unwrap_or_else(|e| e.into_inner());
            if state.pending.is_none()
                && state.reconcile.is_none()
                && last_poll.elapsed() < Duration::from_secs(2)
            {
                state = shared
                    .wake
                    .wait_timeout(state, Duration::from_millis(100))
                    .unwrap_or_else(|e| e.into_inner())
                    .0;
            }
            let work = state.pending.take();
            if let Some(work) = &work {
                if let Some(record) = state
                    .records
                    .iter_mut()
                    .find(|record| record.operation.id == work.id)
                {
                    record.operation.phase = "running".into();
                }
            }
            (
                work,
                state.reconcile.take(),
                shared.revision.load(Ordering::Acquire),
                state.status.intent_revision,
                state.status.desired_running,
            )
        };
        if let Some(work) = work {
            let started = Instant::now();
            let _operation_scope = crate::diagnostics::scope(serde_json::json!({
                "source":"runtime_request","operationId":work.id,"intentRevision":work.intent_revision,
                "workerRevision":work.revision,"action":work.request.name(),
            }));
            core.record_lifecycle(
                "runtime_operation_begin",
                serde_json::json!({"corePid":core.status().pid}),
            );
            core.set_cancellation(Cancellation::new(shared.revision.clone(), work.revision));
            let result = if work.revision != shared.revision.load(Ordering::Acquire) {
                Err(cancelled())
            } else {
                match work.request {
                    RuntimeRequest::Start(source) => core.start(source, true),
                    RuntimeRequest::Reload(source) => {
                        // A superseded update may have stopped the old process
                        // before the new Reload arrived. Resume only under this
                        // newly admitted running intent; Stop remains final.
                        let observed = core.status();
                        if intended_running
                            && observed.pid.is_none()
                            && observed.process_state == "exited"
                        {
                            core.start(source, true)
                        } else {
                            core.reload(source)
                        }
                    }
                    RuntimeRequest::Stop(reason) => core.stop_for(reason).map(|_| core.status()),
                    RuntimeRequest::PrepareCoreUpdate { proxy_port } => {
                        core.prepare_core_update(proxy_port, work.intent_revision)
                    }
                    RuntimeRequest::CommitCoreUpdate => {
                        core.commit_core_update(work.intent_revision)
                    }
                    RuntimeRequest::CompleteCoreUpdate { accept } => {
                        core.complete_core_update(accept, work.intent_revision)
                    }
                }
            };
            let mut state = shared.state.lock().unwrap_or_else(|e| e.into_inner());
            let current = work.revision == shared.revision.load(Ordering::Acquire);
            let execution_error = result.as_ref().err().map(|error| error.code.clone());
            let result = if current { result } else { Err(cancelled()) };
            core.record_lifecycle("runtime_operation_result", serde_json::json!({
                "success":result.is_ok(),"superseded":!current,"durationMs":started.elapsed().as_millis(),
                "executionErrorCode":execution_error,"errorCode":result.as_ref().err().map(|error|&error.code),
                "corePid":core.status().pid,"processState":core.status().process_state,
            }));
            if current {
                state.status = core.status();
                state.status.intent_revision = work.intent_revision;
                stamp(&shared, &mut state);
            }
            let status = state.status.clone();
            if let Some(record) = state
                .records
                .iter_mut()
                .find(|record| record.operation.id == work.id)
            {
                record.operation.phase = match &result {
                    Ok(_) => "succeeded",
                    Err(error) if error.code == "operation_cancelled" => "cancelled",
                    Err(_) => "failed",
                }
                .into();
                record.operation.status = status;
                record.operation.error = result.err();
            }
            last_poll = Instant::now();
        } else if last_poll.elapsed() >= Duration::from_secs(2) || reconcile.is_some() {
            let _poll_scope = crate::diagnostics::scope(serde_json::json!({
                "source":if reconcile.is_some() { "resume_reconcile" } else { "background_health_poll" },
                "intentRevision":intent_revision,"workerRevision":revision,
            }));
            core.set_cancellation(Cancellation::new(shared.revision.clone(), revision));
            if let Some(generation) = reconcile {
                let _ = core.request_reconcile(generation);
            }
            let result = core.poll();
            let mut state = shared.state.lock().unwrap_or_else(|e| e.into_inner());
            if revision == shared.revision.load(Ordering::Acquire) {
                let intent_revision = state.status.intent_revision;
                state.status = core.status();
                state.status.intent_revision = intent_revision;
                if let Err(error) = result {
                    if error.code != "operation_cancelled" {
                        state.status.error = Some(error.message);
                        state.status.error_code = Some(error.code);
                    }
                }
                stamp(&shared, &mut state);
            }
            last_poll = Instant::now();
        }
    }
    core.record_lifecycle(
        "core_runtime_finished",
        serde_json::json!({"corePid":core.status().pid}),
    );
}

#[cfg(test)]
mod tests {
    use super::*;
    #[cfg(unix)]
    use std::path::PathBuf;

    fn queued_runtime() -> CoreRuntime {
        CoreRuntime::queued_for_test()
    }

    #[test]
    fn stop_cancels_queued_core_update_without_starting_a_stopped_core() {
        let runtime = queued_runtime();
        runtime
            .submit(
                "update:prepare".into(),
                7,
                RuntimeRequest::PrepareCoreUpdate {
                    proxy_port: Some(7890),
                },
            )
            .unwrap();
        assert!(!runtime.status().desired_running);
        let token = Cancellation::new(runtime.shared.revision.clone(), 1);
        runtime
            .submit(
                "update:stop".into(),
                8,
                RuntimeRequest::Stop(StopReason::UserStop),
            )
            .unwrap();
        assert!(token.check().is_err());
        let replay = runtime
            .submit(
                "update:prepare".into(),
                7,
                RuntimeRequest::PrepareCoreUpdate {
                    proxy_port: Some(7890),
                },
            )
            .unwrap();
        assert_eq!(replay.phase, "cancelled");
        assert!(!runtime.status().desired_running);
        assert_eq!(runtime.shared.revision.load(Ordering::Acquire), 2);
    }

    #[test]
    fn update_stage_replay_cannot_change_proxy_or_acknowledgement() {
        let runtime = queued_runtime();
        runtime
            .submit(
                "update:prepare".into(),
                7,
                RuntimeRequest::PrepareCoreUpdate {
                    proxy_port: Some(7890),
                },
            )
            .unwrap();
        assert_eq!(
            runtime
                .submit(
                    "update:prepare".into(),
                    7,
                    RuntimeRequest::PrepareCoreUpdate {
                        proxy_port: Some(8080)
                    }
                )
                .err()
                .unwrap()
                .code,
            "service_request_conflict"
        );
        runtime
            .submit(
                "update:complete".into(),
                7,
                RuntimeRequest::CompleteCoreUpdate { accept: false },
            )
            .unwrap();
        assert_eq!(
            runtime
                .submit(
                    "update:complete".into(),
                    7,
                    RuntimeRequest::CompleteCoreUpdate { accept: true }
                )
                .err()
                .unwrap()
                .code,
            "service_request_conflict"
        );
    }

    #[test]
    fn status_is_read_only_and_duplicate_acceptance_does_not_dispatch_twice() {
        let runtime = queued_runtime();
        let first = runtime
            .submit(
                "session:start".into(),
                1,
                RuntimeRequest::Start("mixed-port: 7890".into()),
            )
            .unwrap();
        let revision = runtime.shared.revision.load(Ordering::Acquire);
        let before = runtime.status();
        for _ in 0..100 {
            let status = runtime.status();
            assert_eq!(status.status_sequence, before.status_sequence);
            assert_eq!(status.observed_at, before.observed_at);
        }
        let replay = runtime
            .submit(
                "session:start".into(),
                1,
                RuntimeRequest::Start("mixed-port: 7890".into()),
            )
            .unwrap();
        assert_eq!(replay.id, first.id);
        assert_eq!(runtime.shared.revision.load(Ordering::Acquire), revision);
        assert_eq!(runtime.shared.state.lock().unwrap().records.len(), 1);
        assert_eq!(
            runtime
                .submit(
                    "session:start".into(),
                    1,
                    RuntimeRequest::Start("mixed-port: 7891".into())
                )
                .err()
                .unwrap()
                .code,
            "service_request_conflict"
        );
    }

    #[test]
    fn stop_cancels_queued_work_and_replay_cannot_resurrect_it() {
        let runtime = queued_runtime();
        runtime
            .submit(
                "session:start".into(),
                1,
                RuntimeRequest::Start("mixed-port: 7890".into()),
            )
            .unwrap();
        let token = Cancellation::new(runtime.shared.revision.clone(), 1);
        runtime
            .submit(
                "session:stop".into(),
                2,
                RuntimeRequest::Stop(StopReason::UserStop),
            )
            .unwrap();
        assert_eq!(token.check().unwrap_err().code, "operation_cancelled");
        assert!(!runtime.status().desired_running);
        assert_eq!(
            runtime.operation("session:start").unwrap().phase,
            "cancelled"
        );
        let replay = runtime
            .submit(
                "session:start".into(),
                1,
                RuntimeRequest::Start("mixed-port: 7890".into()),
            )
            .unwrap();
        assert_eq!(replay.phase, "cancelled");
        assert_eq!(
            runtime
                .shared
                .state
                .lock()
                .unwrap()
                .pending
                .as_ref()
                .unwrap()
                .id,
            "session:stop"
        );
    }

    #[cfg(unix)]
    #[test]
    fn replacement_reload_resumes_only_the_newly_accepted_running_intent() {
        use std::os::unix::fs::PermissionsExt;
        let mut nonce = [0u8; 8];
        getrandom::getrandom(&mut nonce).unwrap();
        let directory = std::env::temp_dir().join(format!("mihomo-reload-{}", hex::encode(nonce)));
        std::fs::create_dir_all(&directory).unwrap();
        let executable = directory.join("owned-core-fixture");
        std::fs::write(
            &executable,
            "#!/bin/sh\necho $$ >> validation.pids\nexec sleep 30\n",
        )
        .unwrap();
        std::fs::set_permissions(&executable, std::fs::Permissions::from_mode(0o700)).unwrap();
        let runtime = CoreRuntime::spawn(CoreSupervisor::new(
            executable,
            directory.join("runtime"),
            directory.join("logs"),
        ))
        .unwrap();
        let pid_file = directory.join("runtime/validation.pids");
        let pids = || -> Vec<u32> {
            std::fs::read_to_string(&pid_file)
                .unwrap_or_default()
                .lines()
                .filter_map(|line| line.parse().ok())
                .collect()
        };
        runtime
            .submit(
                "session:first".into(),
                41,
                RuntimeRequest::Start(crate::config::INITIAL_CONFIG.into()),
            )
            .unwrap();
        let deadline = Instant::now() + Duration::from_secs(3);
        while pids().is_empty() && Instant::now() < deadline {
            thread::sleep(Duration::from_millis(10));
        }
        assert_eq!(pids().len(), 1, "first owned validation is running");
        runtime
            .submit(
                "session:reload".into(),
                42,
                RuntimeRequest::Reload(crate::config::INITIAL_CONFIG.into()),
            )
            .unwrap();
        let deadline = Instant::now() + Duration::from_secs(3);
        while pids().len() < 2 && Instant::now() < deadline {
            thread::sleep(Duration::from_millis(10));
        }
        let children = pids();
        assert_eq!(
            children.len(),
            2,
            "Reload resumes the accepted running intent"
        );
        assert_ne!(children[0], children[1]);
        assert_eq!(
            runtime.operation("session:first").unwrap().phase,
            "cancelled"
        );
        assert!(runtime.status().desired_running);
        runtime
            .submit(
                "session:stop".into(),
                43,
                RuntimeRequest::Stop(StopReason::UserStop),
            )
            .unwrap();
        let deadline = Instant::now() + Duration::from_secs(3);
        while matches!(
            runtime.operation("session:stop").unwrap().phase.as_str(),
            "queued" | "running"
        ) && Instant::now() < deadline
        {
            thread::sleep(Duration::from_millis(10));
        }
        assert_eq!(
            runtime.operation("session:reload").unwrap().phase,
            "cancelled"
        );
        assert_eq!(
            runtime.operation("session:stop").unwrap().phase,
            "succeeded"
        );
        assert!(!runtime.status().desired_running);
        assert!(runtime.status().pid.is_none());
        for pid in children {
            assert!(!PathBuf::from(format!("/proc/{pid}")).exists());
        }
        runtime.shutdown().unwrap();
        let deadline = Instant::now() + Duration::from_secs(3);
        while Arc::strong_count(&runtime.shared) > 1 && Instant::now() < deadline {
            thread::sleep(Duration::from_millis(10));
        }
        assert_eq!(Arc::strong_count(&runtime.shared), 1);
        assert!(crate::diagnostics::flush(Duration::from_secs(2)));
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[cfg(unix)]
    #[test]
    fn stop_interrupts_a_real_owned_validation_process_while_status_stays_fast() {
        use std::os::unix::fs::PermissionsExt;
        let mut nonce = [0u8; 8];
        getrandom::getrandom(&mut nonce).unwrap();
        let directory = std::env::temp_dir().join(format!("mihomo-cancel-{}", hex::encode(nonce)));
        std::fs::create_dir_all(&directory).unwrap();
        let executable = directory.join("owned-core-fixture");
        // exec makes the blocked process itself the retained validation Child,
        // not an unowned shell descendant. No network/foreign process is touched.
        std::fs::write(
            &executable,
            "#!/bin/sh\necho $$ > validation.pid\nexec sleep 30\n",
        )
        .unwrap();
        std::fs::set_permissions(&executable, std::fs::Permissions::from_mode(0o700)).unwrap();
        let runtime = CoreRuntime::spawn(CoreSupervisor::new(
            executable,
            directory.join("runtime"),
            directory.join("logs"),
        ))
        .unwrap();
        runtime
            .submit(
                "session:start".into(),
                41,
                RuntimeRequest::Start(crate::config::INITIAL_CONFIG.into()),
            )
            .unwrap();
        let pid_file = directory.join("runtime/validation.pid");
        let deadline = Instant::now() + Duration::from_secs(3);
        while !pid_file.exists() && Instant::now() < deadline {
            thread::sleep(Duration::from_millis(10));
        }
        let pid: u32 = std::fs::read_to_string(&pid_file)
            .expect("validation child entered resource wait")
            .trim()
            .parse()
            .unwrap();
        let start = Instant::now();
        for _ in 0..20 {
            assert_eq!(runtime.status().operation.as_deref(), Some("starting"));
        }
        assert!(start.elapsed() < Duration::from_millis(100));
        runtime
            .submit(
                "session:stop".into(),
                42,
                RuntimeRequest::Stop(StopReason::UserStop),
            )
            .unwrap();
        let deadline = Instant::now() + Duration::from_secs(3);
        while matches!(
            runtime.operation("session:stop").unwrap().phase.as_str(),
            "queued" | "running"
        ) && Instant::now() < deadline
        {
            thread::sleep(Duration::from_millis(10));
        }
        assert_eq!(
            runtime.operation("session:stop").unwrap().phase,
            "succeeded"
        );
        assert_eq!(
            runtime.operation("session:start").unwrap().phase,
            "cancelled"
        );
        assert!(
            !PathBuf::from(format!("/proc/{pid}")).exists(),
            "cancelled validation child must be reaped"
        );
        assert!(!runtime.status().desired_running);
        thread::sleep(Duration::from_millis(150));
        assert!(runtime.status().pid.is_none());
        runtime.shutdown().unwrap();
        // Shutdown confirms the child first. Wait for the worker's supervisor
        // destructor (including its final diagnostic write) before removing logs.
        let deadline = Instant::now() + Duration::from_secs(3);
        while Arc::strong_count(&runtime.shared) > 1 && Instant::now() < deadline {
            thread::sleep(Duration::from_millis(10));
        }
        assert_eq!(Arc::strong_count(&runtime.shared), 1);
        assert!(crate::diagnostics::flush(Duration::from_secs(2)));
        let events: Vec<serde_json::Value> =
            std::fs::read_to_string(directory.join("logs/recovery.log"))
                .unwrap()
                .lines()
                .map(|line| serde_json::from_str(line).unwrap())
                .collect();
        for (operation, intent_revision, worker_revision, success) in [
            ("session:start", 41, 1, false),
            ("session:stop", 42, 2, true),
        ] {
            let begin = events
                .iter()
                .find(|event| {
                    event["event"] == "runtime_operation_begin"
                        && event["context"]["operationId"] == operation
                })
                .unwrap();
            let result = events
                .iter()
                .find(|event| {
                    event["event"] == "runtime_operation_result"
                        && event["context"]["operationId"] == operation
                })
                .unwrap();
            assert_eq!(begin["context"]["intentRevision"], intent_revision);
            assert_eq!(begin["context"]["workerRevision"], worker_revision);
            assert_eq!(result["context"], begin["context"]);
            assert_eq!(result["data"]["success"], success);
            assert_eq!(result["data"]["superseded"], !success);
            let stop_begin = events
                .iter()
                .find(|event| {
                    event["event"] == "core_stop" && event["context"]["operationId"] == operation
                })
                .unwrap();
            assert_eq!(stop_begin["context"]["intentRevision"], intent_revision);
            assert_eq!(stop_begin["data"]["cancellationRevision"], worker_revision);
            assert!(stop_begin["data"].get("intentRevision").is_none());
        }
        let stop = events
            .iter()
            .find(|event| {
                event["event"] == "core_stop_result"
                    && event["context"]["operationId"] == "session:stop"
            })
            .unwrap();
        assert_eq!(stop["data"]["cause"], "requested_stop");
        assert_eq!(stop["data"]["exitConfirmed"], true);
        let finished = events
            .iter()
            .find(|event| event["event"] == "core_runtime_finished")
            .unwrap();
        assert_eq!(finished["context"]["component"], "core_runtime");
        assert!(finished["context"].get("operationId").is_none());
        std::fs::remove_dir_all(directory).unwrap();
    }
}
