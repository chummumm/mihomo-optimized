//! Native desktop process boundary. One worker owns mutating operations; the
//! protocol thread can always return the last published status during startup.
use crate::{
    commands,
    core::{self, CoreStatus},
    desktop::{DesktopManager, DesktopStatus, HostState},
    error::{AppError, Result},
    lifecycle::{Cancellation, PowerSignal},
    protocol::{self, Request},
    service_ipc::Client as ServiceClient,
    windows,
};
use serde::Serialize;
use serde_json::Value;
use std::{
    collections::{HashSet, VecDeque},
    io::BufReader,
    os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle},
    path::{Path, PathBuf},
    sync::{
        atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering},
        mpsc::{self, Receiver, SyncSender},
        Arc, Condvar, Mutex,
    },
    thread,
    time::{Duration, Instant},
};
use windows_sys::Win32::{
    Foundation::{GetLastError, ERROR_ALREADY_EXISTS, INVALID_HANDLE_VALUE, WAIT_OBJECT_0},
    System::{
        Diagnostics::ToolHelp::{
            CreateToolhelp32Snapshot, Process32FirstW, Process32NextW, PROCESSENTRY32W,
            TH32CS_SNAPPROCESS,
        },
        Threading::{
            CreateMutexW, OpenProcess, WaitForSingleObject, PROCESS_QUERY_LIMITED_INFORMATION,
            PROCESS_SYNCHRONIZE,
        },
    },
};

const WORK_QUEUE_CAPACITY: usize = 4;
const OWNER_EXIT_GRACE: Duration = Duration::from_secs(12);

fn lifecycle_command(method: &str) -> Option<&'static str> {
    const COMMANDS: &[&str] = &[
        "desktop_start",
        "desktop_stop",
        "desktop_restart",
        "desktop_set_tun",
        "desktop_set_system_proxy",
        "desktop_reload_config",
        "desktop_patch_config",
        "desktop_install_update",
        "desktop_uninstall_service",
        "desktop_install_service",
        "desktop_exit",
    ];
    COMMANDS.iter().copied().find(|command| *command == method)
}

enum Message {
    Request(Request),
    Invalid(Option<i64>, AppError),
    Reply(i64, Result<Value>),
    Event(Value),
    InputClosed(&'static str),
    Exit,
}

pub struct UiProcess {
    pub pid: u32,
    pub application: PathBuf,
    handle: OwnedHandle,
}

impl UiProcess {
    fn exited(&self) -> bool {
        unsafe { WaitForSingleObject(self.handle.as_raw_handle(), 0) == WAIT_OBJECT_0 }
    }
}

#[derive(Clone)]
pub struct HostContext {
    output: SyncSender<Message>,
    published: Arc<Mutex<Option<DesktopStatus>>>,
    pub owner: Arc<UiProcess>,
    pub runtime_generation: Arc<AtomicU64>,
    pub stop_pending: Arc<AtomicUsize>,
    pub instance: String,
    sequence: Arc<AtomicU64>,
    service: Arc<Mutex<Option<ServiceClient>>>,
    service_active: Arc<AtomicBool>,
    service_epoch: Arc<AtomicU64>,
    service_stop: SyncSender<u64>,
    stop_intent_revision: Arc<AtomicU64>,
    diagnostic_directory: PathBuf,
    shutdown_cause: Arc<Mutex<Option<&'static str>>>,
}

impl HostContext {
    fn diagnostic_scope(&self, cause: &'static str) -> crate::diagnostics::DiagnosticScope {
        crate::diagnostics::scope(serde_json::json!({
            "hostInstance":self.instance,"uiPid":self.owner.pid,"cause":cause,
            "intentRevision":self.generation(),
        }))
    }

    fn record(&self, event: &str, data: Value) {
        crate::diagnostics::record(&self.diagnostic_directory, event, data);
    }

    fn record_shutdown(&self, cause: &'static str) {
        let mut first = self
            .shutdown_cause
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        if first.is_some() {
            return;
        }
        *first = Some(cause);
        drop(first);
        let _scope = self.diagnostic_scope(cause);
        self.record(
            "host_shutdown_requested",
            serde_json::json!({"uiExited":self.owner.exited()}),
        );
    }

    fn first_shutdown_cause(&self) -> &'static str {
        self.shutdown_cause
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .unwrap_or("shutdown_requested")
    }

    pub fn generation(&self) -> u64 {
        self.runtime_generation.load(Ordering::Acquire)
    }
    pub fn owner_alive(&self) -> bool {
        !self.owner.exited()
    }
    pub fn emit<T: Serialize>(&self, event: &str, data: T) {
        let _ = self.output.send(Message::Event(serde_json::json!({
            "event": event,
            "data": data,
        })));
    }

    pub fn cancellation(&self) -> Cancellation {
        Cancellation::new(self.runtime_generation.clone(), self.generation())
    }

    pub fn set_service_client(&self, client: Option<ServiceClient>) {
        let mut current = self.service.lock().unwrap_or_else(|e| e.into_inner());
        self.service_epoch.fetch_add(1, Ordering::AcqRel);
        self.service_active
            .store(client.is_some(), Ordering::Release);
        *current = client;
    }

    pub fn pause_service_observation(&self) {
        let _client = self.service.lock().unwrap_or_else(|e| e.into_inner());
        self.service_active.store(false, Ordering::Release);
        self.service_epoch.fetch_add(1, Ordering::AcqRel);
    }

    fn service_observer(&self) -> Option<(ServiceClient, u64)> {
        let client = self.service.lock().unwrap_or_else(|e| e.into_inner());
        self.service_active
            .load(Ordering::Acquire)
            .then(|| {
                client
                    .clone()
                    .map(|client| (client, self.service_epoch.load(Ordering::Acquire)))
            })
            .flatten()
    }

    pub fn service_client(&self) -> Option<ServiceClient> {
        self.service
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .clone()
    }

    pub fn publish_status(&self, mut status: DesktopStatus) {
        let mut published = self.published.lock().unwrap_or_else(|e| e.into_inner());
        if status.intent_revision != self.generation() {
            return;
        }
        if let Some(current) = published.as_ref() {
            if status.intent_revision == current.intent_revision
                && status.service_instance == current.service_instance
                && status.service_sequence < current.service_sequence
            {
                status.preserve_core_observation(current);
            }
        }
        self.stamp_status(&mut status);
        *published = Some(status.clone());
        drop(published);
        self.emit_status(status);
    }

    fn stamp_status(&self, status: &mut DesktopStatus) {
        status.status_instance = self.instance.clone();
        status.status_sequence = self.sequence.fetch_add(1, Ordering::AcqRel) + 1;
        status.status_stale = status.process_state != "exited"
            && (status.observed_at == 0
                || core::observed_now().saturating_sub(status.observed_at) > 30_000);
    }

    fn emit_status(&self, status: DesktopStatus) {
        // Status is also readable from the cache. A full event queue must never
        // block admission of Stop or the independent service heartbeat.
        let _ = self.output.try_send(Message::Event(
            serde_json::json!({"event":"desktop-status","data":status}),
        ));
    }

    fn publish_stop_intent(&self, revision: u64) {
        self.stop_intent_revision.store(revision, Ordering::Release);
        if let Ok(mut status) = self.status() {
            status.intent_revision = revision;
            status.desired_running = false;
            status.auto_tun_recovery_eligible = false;
            status.tun_device_recovery = crate::desktop::TunDeviceRecoveryStatus::Idle;
            status.phase = crate::desktop::Phase::Stopping;
            status.operation = Some("stopping".into());
            status.stop_reason = Some(crate::lifecycle::StopReason::UserStop);
            self.publish_status(status);
        }
        let _ = self.service_stop.try_send(revision);
    }

    fn publish_service_observation(&self, core: CoreStatus, epoch: u64) {
        let mut published = self.published.lock().unwrap_or_else(|e| e.into_inner());
        if core.intent_revision != self.generation()
            || !self.service_active.load(Ordering::Acquire)
            || epoch != self.service_epoch.load(Ordering::Acquire)
        {
            return;
        }
        let Some(status) = published.as_mut() else {
            return;
        };
        if status.service_instance == core.service_instance
            && core.status_sequence <= status.service_sequence
        {
            return;
        }
        status.apply_core_observation(&core);
        self.stamp_status(status);
        let status = status.clone();
        drop(published);
        self.emit_status(status);
    }

    pub fn status(&self) -> Result<DesktopStatus> {
        self.published
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .clone()
            .ok_or_else(|| AppError::new("desktop_starting", "桌面后台正在初始化"))
    }

    pub fn application_path(&self) -> &Path {
        &self.owner.application
    }

    pub fn resource_directory(&self) -> Result<PathBuf> {
        self.application_path()
            .parent()
            .map(|path| path.join("resources"))
            .ok_or_else(|| AppError::new("application_missing", "无法定位桌面资源目录"))
    }
}

/// Bind a supplied PID to the actual OS parent and fixed sibling UI image. A
/// same-user process cannot borrow another UI's PID to drive the privileged
/// service through a backend it spawned itself.
fn authenticate_parent(pid: u32) -> Result<UiProcess> {
    let invalid = || AppError::new("desktop_parent_invalid", "只能由配套原生桌面程序启动后台");
    let host = std::env::current_exe().map_err(|e| AppError::io("无法定位桌面后台", e))?;
    windows::reject_reparse_path(&host)?;
    if host.file_name().and_then(|name| name.to_str()) != Some(windows::HOST_EXE)
        || actual_parent_pid()? != pid
    {
        return Err(invalid());
    }
    let application = windows::application_path()?;
    let handle = unsafe {
        let raw = OpenProcess(
            PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SYNCHRONIZE,
            0,
            pid,
        );
        if raw.is_null() {
            return Err(invalid());
        }
        OwnedHandle::from_raw_handle(raw)
    };
    let (path, sid) = windows::process_handle_identity(&handle)?;
    if sid != windows::current_sid()?
        || path.canonicalize().map_err(|_| invalid())?
            != application.canonicalize().map_err(|_| invalid())?
        || unsafe { WaitForSingleObject(handle.as_raw_handle(), 0) } == WAIT_OBJECT_0
    {
        return Err(invalid());
    }
    Ok(UiProcess {
        pid,
        application,
        handle,
    })
}

pub(crate) fn actual_parent_pid() -> Result<u32> {
    unsafe {
        let raw = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if raw == INVALID_HANDLE_VALUE {
            return Err(windows::last_error("无法确认桌面后台的父进程"));
        }
        let snapshot = OwnedHandle::from_raw_handle(raw);
        let mut entry: PROCESSENTRY32W = std::mem::zeroed();
        entry.dwSize = std::mem::size_of::<PROCESSENTRY32W>() as u32;
        let mut found = Process32FirstW(snapshot.as_raw_handle(), &mut entry);
        while found != 0 {
            if entry.th32ProcessID == std::process::id() {
                return Ok(entry.th32ParentProcessID);
            }
            found = Process32NextW(snapshot.as_raw_handle(), &mut entry);
        }
    }
    Err(AppError::new(
        "desktop_parent_invalid",
        "无法确认桌面后台的父进程",
    ))
}

fn single_instance() -> Result<OwnedHandle> {
    let name = windows::wide(format!(
        "Local\\MihomoOptimized.NativeHost.{}",
        windows::current_sid()?
    ));
    unsafe {
        let raw = CreateMutexW(std::ptr::null(), 1, name.as_ptr());
        if raw.is_null() {
            return Err(windows::last_error("无法建立桌面后台实例锁"));
        }
        let handle = OwnedHandle::from_raw_handle(raw);
        if GetLastError() == ERROR_ALREADY_EXISTS {
            return Err(AppError::new(
                "desktop_already_running",
                "已有桌面后台正在运行",
            ));
        }
        Ok(handle)
    }
}

/// Older installation/maintenance callers may still target the application.
/// The native UI forwards only these exact argument forms to this entry point.
fn maintenance_command(args: &[String]) -> Option<Result<i32>> {
    let allowed = match args {
        [action, marker, _] => action == "--install" && marker == "--owner-sid",
        [action] => matches!(
            action.as_str(),
            "--uninstall" | "--stop-for-update" | "--start-if-installed"
        ),
        _ => false,
    };
    if !allowed {
        return None;
    }
    Some((|| {
        let helper = crate::proxy::helper_path()?;
        let result = crate::core::quiet_command(&helper)
            .args(args)
            .status()
            .map_err(|e| AppError::io("无法启动安装维护程序", e))?;
        Ok(result.code().unwrap_or(1))
    })())
}

pub fn run() -> Result<()> {
    let args: Vec<_> = std::env::args().skip(1).collect();
    if let Some(result) = maintenance_command(&args) {
        std::process::exit(result?);
    }
    let no_autostart = args
        .last()
        .is_some_and(|argument| argument == "--no-autostart");
    let runtime_args = if no_autostart {
        &args[..args.len() - 1]
    } else {
        &args[..]
    };
    let pid = match runtime_args {
        [mode, parent, pid] if mode == "--stdio" && parent == "--parent-pid" => pid
            .parse::<u32>()
            .map_err(|_| AppError::new("desktop_parent_invalid", "桌面父进程编号无效"))?,
        _ => {
            return Err(AppError::new(
                "desktop_arguments",
                "请打开 mihomo-optimized.exe 启动原生桌面程序",
            ))
        }
    };
    let owner = Arc::new(authenticate_parent(pid)?);
    let _instance = single_instance()?;
    let (output, messages) = mpsc::sync_channel(16);
    let (service_stop, service_stops) = mpsc::sync_channel(1);
    let mut random = [0u8; 16];
    getrandom::getrandom(&mut random)
        .map_err(|_| AppError::new("random_failed", "无法建立桌面会话编号"))?;
    let context = HostContext {
        output: output.clone(),
        published: Arc::new(Mutex::new(None)),
        owner,
        runtime_generation: Arc::new(AtomicU64::new(0)),
        stop_pending: Arc::new(AtomicUsize::new(0)),
        instance: hex::encode(random),
        sequence: Arc::new(AtomicU64::new(0)),
        service: Arc::new(Mutex::new(None)),
        service_active: Arc::new(AtomicBool::new(false)),
        service_epoch: Arc::new(AtomicU64::new(0)),
        service_stop,
        stop_intent_revision: Arc::new(AtomicU64::new(0)),
        diagnostic_directory: windows::user_data_dir()
            .map(|data| data.join("logs"))
            .unwrap_or_default(),
        shutdown_cause: Arc::new(Mutex::new(None)),
    };
    let power = Arc::new(PowerSignal::default());
    let _power_registration = crate::power::Registration::new(power.clone())?;
    let mut manager = DesktopManager::new(context.clone())?;
    let start = manager.prepare_startup(!no_autostart);
    {
        let _scope = context.diagnostic_scope("host_startup");
        context.record(
            "host_started",
            serde_json::json!({"allowAutostart":!no_autostart,"savedStartAccepted":start}),
        );
    }
    let state = HostState(Arc::new(Mutex::new(manager)));
    let work = Arc::new(WorkQueue::default());
    let requests = work.clone();
    let stopping = Arc::new(AtomicBool::new(false));
    let worker_state = state.clone();
    let worker_context = context.clone();
    let worker_stopping = stopping.clone();
    let poll_state = state.clone();
    let poll_stopping = stopping.clone();
    let poll_context = context.clone();
    let observation_context = context.clone();
    let heartbeat_stopping = stopping.clone();
    let heartbeat_power = power.clone();
    thread::Builder::new()
        .name("desktop-operations".into())
        .spawn(move || {
            operations(
                worker_state,
                worker_context,
                requests,
                worker_stopping,
                start,
            )
        })
        .map_err(|e| AppError::io("无法启动桌面操作线程", e))?;
    let control_context = context.clone();
    let control_stopping = stopping.clone();
    thread::Builder::new()
        .name("desktop-priority-stop".into())
        .spawn(move || {
            while !control_stopping.load(Ordering::Acquire) {
                match service_stops.recv_timeout(Duration::from_millis(100)) {
                    Ok(_) => {
                        let _scope = control_context.diagnostic_scope("priority_stop");
                        let revision = control_context.stop_intent_revision.load(Ordering::Acquire);
                        if revision == control_context.generation()
                            && control_context.stop_pending.load(Ordering::Acquire) != 0
                        {
                            if let Some((client, epoch)) = control_context.service_observer() {
                                if let Ok(status) = client.accept_stop(revision) {
                                    control_context.publish_service_observation(status, epoch);
                                }
                            }
                        }
                    }
                    Err(mpsc::RecvTimeoutError::Timeout) => (),
                    Err(mpsc::RecvTimeoutError::Disconnected) => break,
                }
            }
        })
        .map_err(|e| AppError::io("无法启动停止控制通道", e))?;
    // Heartbeats never acquire the manager or lifecycle-worker lock. Start,
    // reload and automatic resource preparation can all outlive the lease.
    thread::Builder::new()
        .name("desktop-heartbeat".into())
        .spawn(move || {
            let mut generation = heartbeat_power.generation();
            let mut last_failure = None;
            while !heartbeat_stopping.load(Ordering::Acquire) {
                for _ in 0..20 {
                    if heartbeat_power.generation() != generation {
                        break;
                    }
                    thread::sleep(Duration::from_millis(100));
                }
                if heartbeat_stopping.load(Ordering::Acquire) {
                    break;
                }
                let resumed = heartbeat_power.generation() != generation;
                if resumed {
                    generation = heartbeat_power.generation();
                }
                if !heartbeat_power.suspended() {
                    if let Some((client, epoch)) = poll_context.service_observer() {
                        let _scope = poll_context.diagnostic_scope("service_heartbeat");
                        match client.heartbeat() {
                            Ok(status) => {
                                if last_failure.take().is_some() {
                                    poll_context.record(
                                        "service_heartbeat_recovered",
                                        serde_json::json!({"corePid":status.pid}),
                                    );
                                }
                                poll_context.publish_service_observation(status, epoch);
                            }
                            Err(error) => {
                                if last_failure.as_deref() != Some(error.code.as_str()) {
                                    poll_context.record(
                                        "service_heartbeat_failed",
                                        serde_json::json!({"code":error.code}),
                                    );
                                }
                                last_failure = Some(error.code);
                            }
                        }
                        if resumed {
                            let _ = client.reconcile(generation);
                        }
                    }
                }
            }
        })
        .map_err(|e| AppError::io("无法启动服务心跳线程", e))?;
    thread::Builder::new()
        .name("desktop-observation".into())
        .spawn(move || {
            let mut generation = power.generation();
            while !poll_stopping.load(Ordering::Acquire) {
                for _ in 0..20 {
                    if power.generation() != generation {
                        break;
                    }
                    thread::sleep(Duration::from_millis(100));
                }
                if poll_stopping.load(Ordering::Acquire) {
                    break;
                }
                if !power.suspended() {
                    if let Ok(mut manager) = poll_state.0.try_lock() {
                        let _scope = observation_context.diagnostic_scope("background_observation");
                        if power.generation() != generation {
                            generation = power.generation();
                            manager.request_reconcile(generation);
                        }
                        manager.poll();
                    }
                }
            }
        })
        .map_err(|e| AppError::io("无法启动内核观察线程", e))?;
    thread::Builder::new()
        .name("desktop-input".into())
        .spawn(move || read_requests(output))
        .map_err(|e| AppError::io("无法启动桌面请求通道", e))?;
    crate::update_cache::start_cleanup_worker();
    let _scope = context.diagnostic_scope("host_protocol");
    let result = protocol_loop(state, context.clone(), messages, work, stopping);
    let _exit_scope = context.diagnostic_scope(context.first_shutdown_cause());
    context.record("host_finished", serde_json::json!({
        "cause":context.first_shutdown_cause(),"code":result.as_ref().err().map(|error| &error.code),
    }));
    let _ = crate::diagnostics::flush(Duration::from_millis(200));
    result
}

fn read_requests(output: SyncSender<Message>) {
    let mut input = BufReader::new(std::io::stdin());
    let close_cause = loop {
        let message = match protocol::read_frame(&mut input) {
            Ok(Some(bytes)) => match protocol::parse_request(&bytes) {
                Ok(request) => Message::Request(request),
                Err(error) => Message::Invalid(protocol::request_id(&bytes), error),
            },
            Ok(None) => break "input_closed",
            Err(error) => {
                let _ = output.send(Message::Invalid(None, error));
                break "protocol_read_failure";
            }
        };
        if output.send(message).is_err() {
            return;
        }
    };
    let _ = output.send(Message::InputClosed(close_cause));
}

fn reply(id: Option<i64>, result: Result<Value>) -> Value {
    match result {
        Ok(result) => serde_json::json!({"id":id,"result":result}),
        Err(error) => serde_json::json!({"id":id,"error":error}),
    }
}

fn protocol_loop(
    state: HostState,
    context: HostContext,
    messages: Receiver<Message>,
    work: Arc<WorkQueue>,
    stopping: Arc<AtomicBool>,
) -> Result<()> {
    let mut stdout = std::io::stdout();
    let mut pending = HashSet::new();
    let mut owner_lost_at = None;
    let mut output_open = true;
    loop {
        if context.owner.exited() && owner_lost_at.is_none() {
            context.record_shutdown("ui_process_exit");
            owner_lost_at = Some(Instant::now());
            context.runtime_generation.fetch_add(1, Ordering::AcqRel);
            stopping.store(true, Ordering::Release);
        }
        if owner_lost_at.is_some_and(|at: Instant| at.elapsed() >= OWNER_EXIT_GRACE) {
            // Kill only this host, never its descendants: the recovery helper
            // must outlive us to restore WinINet, and an update monitor may own
            // an installer. The existing core job closes with this process.
            context.record(
                "host_exit_deadline",
                serde_json::json!({"cause":context.first_shutdown_cause()}),
            );
            let _ = crate::diagnostics::flush(Duration::from_millis(100));
            std::process::exit(1);
        }
        let frame = match messages.recv_timeout(Duration::from_millis(100)) {
            Ok(Message::Request(request)) if owner_lost_at.is_none() => {
                if pending.contains(&request.id) {
                    context.record_shutdown("protocol_duplicate_id");
                    owner_lost_at = Some(Instant::now());
                    context.runtime_generation.fetch_add(1, Ordering::AcqRel);
                    stopping.store(true, Ordering::Release);
                    Some(reply(
                        Some(request.id),
                        Err(AppError::new("protocol_duplicate_id", "桌面请求编号重复")),
                    ))
                } else if request.method == "desktop_status" {
                    Some(reply(
                        Some(request.id),
                        commands::status(&state, &context, request.params),
                    ))
                } else {
                    let id = request.id;
                    let action = lifecycle_command(&request.method);
                    let _request_context = context.diagnostic_scope("ui_request");
                    let _scope = crate::diagnostics::scope(serde_json::json!({
                        "uiRequestId":id,"command":action,"hostInstance":context.instance,
                        "uiPid":context.owner.pid,
                    }));
                    match work.enqueue(&context, request) {
                        Ok(replaced) => {
                            pending.insert(id);
                            if let Some(replaced) = replaced {
                                context.record("host_request_superseded", serde_json::json!({"replacedUiRequestId":replaced,"replacementUiRequestId":id}));
                                pending.remove(&replaced);
                                Some(reply(
                                    Some(replaced),
                                    Err(AppError::new(
                                        "operation_cancelled",
                                        "此停止请求已合并到更新的停止指令",
                                    )),
                                ))
                            } else {
                                None
                            }
                        }
                        Err(error) => {
                            if action.is_some() {
                                context.record(
                                    "host_request_rejected",
                                    serde_json::json!({"code":error.code}),
                                );
                            }
                            Some(reply(Some(id), Err(error)))
                        }
                    }
                }
            }
            Ok(Message::Request(request)) => {
                if let Some(command) = lifecycle_command(&request.method) {
                    let _scope = context.diagnostic_scope(context.first_shutdown_cause());
                    context.record(
                        "host_request_rejected",
                        serde_json::json!({
                            "uiRequestId":request.id,"command":command,"code":"desktop_exiting",
                            "cause":context.first_shutdown_cause(),
                        }),
                    );
                }
                Some(reply(
                    Some(request.id),
                    Err(AppError::new("desktop_exiting", "桌面后台正在退出")),
                ))
            }
            Ok(Message::Invalid(id, error)) => {
                let _scope = context.diagnostic_scope("protocol_rejection");
                context.record(
                    "host_protocol_rejected",
                    serde_json::json!({"uiRequestId":id,"code":error.code}),
                );
                Some(reply(id, Err(error)))
            }
            Ok(Message::Reply(id, result)) => {
                pending.remove(&id);
                Some(reply(Some(id), result))
            }
            Ok(Message::Event(frame)) => Some(frame),
            Ok(Message::InputClosed(cause)) => {
                context.record_shutdown(cause);
                owner_lost_at.get_or_insert_with(Instant::now);
                context.runtime_generation.fetch_add(1, Ordering::AcqRel);
                stopping.store(true, Ordering::Release);
                None
            }
            Ok(Message::Exit) => return Ok(()),
            Err(mpsc::RecvTimeoutError::Timeout) => None,
            Err(mpsc::RecvTimeoutError::Disconnected) => return Ok(()),
        };
        if let Some(frame) = frame {
            if output_open && protocol::write_frame(&mut stdout, &frame).is_err() {
                context.record_shutdown("output_closed");
                output_open = false;
                owner_lost_at.get_or_insert_with(Instant::now);
                context.runtime_generation.fetch_add(1, Ordering::AcqRel);
                stopping.store(true, Ordering::Release);
            }
        }
    }
}

struct WorkRequest {
    request: Request,
    generation: u64,
    invalidates: bool,
    stops: bool,
}
#[derive(Default)]
struct Queue {
    normal: VecDeque<WorkRequest>,
    stop: Option<WorkRequest>,
}
#[derive(Default)]
struct WorkQueue {
    queue: Mutex<Queue>,
    wake: Condvar,
}

impl WorkQueue {
    fn enqueue(&self, context: &HostContext, request: Request) -> Result<Option<i64>> {
        commands::validate_admission(&request.method, &request.params)?;
        let stops = request.method == "desktop_stop"
            || request.method == "desktop_exit"
                && request.params.get("force").and_then(Value::as_bool) == Some(true);
        if request.method == "desktop_stop" && !request.params.is_empty() {
            return Err(AppError::new("invalid_arguments", "停止操作参数无效"));
        }
        let invalidates = stops
            || matches!(
                request.method.as_str(),
                "desktop_start"
                    | "desktop_restart"
                    | "desktop_set_tun"
                    | "desktop_set_system_proxy"
                    | "desktop_reload_config"
                    | "desktop_patch_config"
                    | "desktop_install_update"
                    | "desktop_uninstall_service"
            );
        let bound = invalidates
            || matches!(
                request.method.as_str(),
                "desktop_import_config_url"
                    | "desktop_check_update"
                    | "desktop_install_service"
                    | "desktop_exit"
            );
        let action = request.method.clone();
        let implicit_start = action == "desktop_set_system_proxy"
            && request.params.get("enabled").and_then(Value::as_bool) == Some(true);
        let mut queue = self.queue.lock().unwrap_or_else(|e| e.into_inner());
        if !stops && queue.normal.len() >= WORK_QUEUE_CAPACITY {
            return Err(AppError::new(
                "desktop_busy",
                "已有桌面操作正在进行，请稍后重试",
            ));
        }
        // Admission and generation publication are atomic with worker dequeue.
        // Rejected requests cannot invalidate a valid operation in flight.
        let generation = if invalidates {
            context.runtime_generation.fetch_add(1, Ordering::AcqRel) + 1
        } else {
            context.generation()
        };
        if let Some(command) = lifecycle_command(&action) {
            let _scope = context.diagnostic_scope("accepted_command");
            context.record(
                "host_request_accepted",
                serde_json::json!({
                    "uiRequestId":request.id,"command":command,"intentRevision":generation,
                }),
            );
        }
        let work = WorkRequest {
            request,
            generation,
            invalidates: bound,
            stops,
        };
        let replaced = if stops {
            context.stop_pending.fetch_add(1, Ordering::AcqRel);
            let previous = queue.stop.replace(work);
            if previous.is_some() {
                context.stop_pending.fetch_sub(1, Ordering::AcqRel);
            }
            previous.map(|work| work.request.id)
        } else {
            queue.normal.push_back(work);
            None
        };
        // Publish accepted intent while dequeue is still excluded. A fast Stop
        // completion must never be overwritten by its own later admission event.
        if stops {
            context.publish_stop_intent(generation);
        } else if invalidates {
            if let Ok(mut status) = context.status() {
                status.intent_revision = generation;
                status.tun_device_recovery = crate::desktop::TunDeviceRecoveryStatus::Idle;
                status.auto_tun_recovery_eligible = false;
                if matches!(action.as_str(), "desktop_start" | "desktop_restart") || implicit_start
                {
                    status.desired_running = true;
                    if action != "desktop_set_system_proxy" || status.process_state != "alive" {
                        status.phase = crate::desktop::Phase::Starting;
                        status.operation = Some(
                            if action == "desktop_restart" {
                                "restarting"
                            } else {
                                "starting"
                            }
                            .into(),
                        );
                    }
                }
                context.publish_status(status);
            }
        }
        self.wake.notify_one();
        Ok(replaced)
    }

    fn next(&self) -> Option<WorkRequest> {
        let mut queue = self.queue.lock().unwrap_or_else(|e| e.into_inner());
        if queue.stop.is_none() && queue.normal.is_empty() {
            queue = self
                .wake
                .wait_timeout(queue, Duration::from_millis(100))
                .unwrap_or_else(|e| e.into_inner())
                .0;
        }
        queue.stop.take().or_else(|| queue.normal.pop_front())
    }
}

fn operations(
    state: HostState,
    context: HostContext,
    requests: Arc<WorkQueue>,
    stopping: Arc<AtomicBool>,
    start: bool,
) {
    if start && !stopping.load(Ordering::Acquire) {
        let _scope = context.diagnostic_scope("saved_startup");
        context.record("desktop_autostart_requested", serde_json::json!({}));
        if let Ok(mut manager) = state.0.lock() {
            let result = manager.start(false);
            context.record(
                "desktop_autostart_finished",
                serde_json::json!({"code":result.as_ref().err().map(|error| &error.code)}),
            );
        }
    }
    if let Some(message) = crate::update::take_install_result() {
        if let Ok(mut manager) = state.0.lock() {
            manager.report_error(message);
        }
    }
    while !stopping.load(Ordering::Acquire) {
        if let Some(work) = requests.next() {
            if stopping.load(Ordering::Acquire) {
                break;
            }
            let request = work.request;
            let stops = work.stops;
            let action = lifecycle_command(&request.method);
            let _scope = context.diagnostic_scope("accepted_command");
            let _request_scope = crate::diagnostics::scope(serde_json::json!({
                "uiRequestId":request.id,"command":action,"intentRevision":work.generation,
            }));
            let started = Instant::now();
            if action.is_some() {
                context.record("host_request_started", serde_json::json!({}));
            }
            let result = if work.stops && work.generation != context.generation() {
                // A later Start may replace the desired state, but it cannot
                // bypass an already accepted Stop's ownership cleanup barrier.
                state
                    .0
                    .lock()
                    .map_err(|_| AppError::new("state_unavailable", "桌面状态不可用"))
                    .and_then(|mut manager| {
                        manager.begin_operation(context.generation());
                        manager.stop(false)?;
                        Err(AppError::new(
                            "operation_cancelled",
                            "停止清理已完成，运行意图已被后续指令更新",
                        ))
                    })
            } else if work.invalidates && work.generation != context.generation() {
                Err(AppError::new(
                    "operation_cancelled",
                    "此操作已被更新的用户指令取消",
                ))
            } else {
                commands::dispatch(
                    &state,
                    &context,
                    &request.method,
                    request.params,
                    if work.invalidates {
                        work.generation
                    } else {
                        context.generation()
                    },
                )
            };
            if stops {
                context.stop_pending.fetch_sub(1, Ordering::AcqRel);
            }
            let exit = result
                .as_ref()
                .ok()
                .and_then(|result| result.get("exiting"))
                .and_then(Value::as_bool)
                == Some(true);
            let update_exit = exit && request.method == "desktop_install_update";
            if action.is_some() {
                context.record(
                    "host_request_finished",
                    serde_json::json!({
                        "code":result.as_ref().err().map(|error| &error.code),
                        "elapsedMs":started.elapsed().as_millis(),"exiting":exit,
                    }),
                );
            }
            let _ = context.output.send(Message::Reply(request.id, result));
            if exit {
                context.record_shutdown(if update_exit {
                    "update_exit"
                } else {
                    "command_exit"
                });
                if update_exit {
                    context.emit(
                        "desktop-exit-requested",
                        serde_json::json!({"reason":"update","force":true}),
                    );
                }
                let _ = context.output.send(Message::Exit);
                return;
            }
        }
    }
    context.runtime_generation.fetch_add(1, Ordering::AcqRel);
    let _scope = context.diagnostic_scope(context.first_shutdown_cause());
    context.record("host_owner_cleanup_started", serde_json::json!({}));
    if let Ok(mut manager) = state.0.lock() {
        manager.begin_operation(context.generation());
        let result = manager.request_exit(true);
        context.record(
            "host_owner_cleanup_finished",
            serde_json::json!({"code":result.as_ref().err().map(|error| &error.code)}),
        );
    }
    let _ = context.output.send(Message::Exit);
}

#[cfg(test)]
pub(crate) fn test_context() -> HostContext {
    lifecycle_tests::context().0
}

#[cfg(test)]
mod lifecycle_tests {
    use super::*;
    use crate::desktop::{Phase, TunDeviceRecoveryStatus};

    pub(super) fn context() -> (HostContext, Receiver<Message>, Receiver<u64>) {
        let raw = unsafe {
            OpenProcess(
                PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SYNCHRONIZE,
                0,
                std::process::id(),
            )
        };
        assert!(!raw.is_null());
        let (output, messages) = mpsc::sync_channel(1024);
        let (service_stop, stops) = mpsc::sync_channel(1);
        let context = HostContext {
            output,
            published: Arc::new(Mutex::new(None)),
            owner: Arc::new(UiProcess {
                pid: std::process::id(),
                application: std::env::current_exe().unwrap(),
                handle: unsafe { OwnedHandle::from_raw_handle(raw) },
            }),
            runtime_generation: Arc::new(AtomicU64::new(0)),
            stop_pending: Arc::new(AtomicUsize::new(0)),
            instance: "00000000000000000000000000000001".into(),
            sequence: Arc::new(AtomicU64::new(0)),
            service: Arc::new(Mutex::new(None)),
            service_active: Arc::new(AtomicBool::new(false)),
            service_epoch: Arc::new(AtomicU64::new(0)),
            service_stop,
            stop_intent_revision: Arc::new(AtomicU64::new(0)),
            diagnostic_directory: PathBuf::new(),
            shutdown_cause: Arc::new(Mutex::new(None)),
        };
        context.publish_status(DesktopStatus {
            process_state: "exited".into(),
            control_state: "unknown".into(),
            ..DesktopStatus::default()
        });
        (context, messages, stops)
    }

    fn request(id: i64, method: &str, params: Value) -> Request {
        Request {
            id,
            method: method.into(),
            params: params.as_object().unwrap().clone(),
        }
    }

    #[test]
    fn shutdown_evidence_keeps_first_source_and_real_parent_state() {
        let (mut context, _messages, _stops) = context();
        let mut nonce = [0u8; 12];
        getrandom::getrandom(&mut nonce).unwrap();
        let logs = std::env::temp_dir().join(format!("host-origin-{}", hex::encode(nonce)));
        context.diagnostic_directory = logs.clone();
        context.record_shutdown("input_closed");
        context.record_shutdown("ui_process_exit");
        assert_eq!(context.first_shutdown_cause(), "input_closed");
        assert!(crate::diagnostics::flush(Duration::from_secs(2)));
        let events: Vec<Value> = std::fs::read_to_string(logs.join("recovery.log"))
            .unwrap()
            .lines()
            .map(|line| serde_json::from_str(line).unwrap())
            .collect();
        assert_eq!(events.len(), 1);
        assert_eq!(events[0]["event"], "host_shutdown_requested");
        assert_eq!(events[0]["context"]["cause"], "input_closed");
        assert_eq!(events[0]["context"]["hostInstance"], context.instance);
        assert_eq!(events[0]["data"]["uiExited"], false);
        std::fs::remove_dir_all(logs).unwrap();
    }

    #[test]
    fn full_normal_queue_cannot_reject_stop_or_run_an_older_implicit_start() {
        let (context, _messages, _stops) = context();
        let queue = WorkQueue::default();
        queue
            .enqueue(
                &context,
                request(
                    1,
                    "desktop_set_system_proxy",
                    serde_json::json!({"enabled":true}),
                ),
            )
            .unwrap();
        for id in 2..=4 {
            queue
                .enqueue(
                    &context,
                    request(
                        id,
                        "desktop_open_directory",
                        serde_json::json!({"kind":"logs"}),
                    ),
                )
                .unwrap();
        }
        let revision = context.generation();
        assert_eq!(
            queue
                .enqueue(&context, request(9, "desktop_start", serde_json::json!({})))
                .unwrap_err()
                .code,
            "desktop_busy"
        );
        assert_eq!(
            context.generation(),
            revision,
            "rejected work cannot cancel accepted work"
        );
        queue
            .enqueue(&context, request(5, "desktop_stop", serde_json::json!({})))
            .unwrap();
        assert!(!context.status().unwrap().desired_running);
        assert_eq!(queue.next().unwrap().request.method, "desktop_stop");
        let old_proxy = queue.next().unwrap();
        assert!(
            old_proxy.invalidates && old_proxy.generation < context.generation(),
            "old proxy enable must be cancelled, not rebound to Stop's generation"
        );
    }

    #[test]
    fn stop_intent_is_published_before_a_fast_worker_can_complete_it() {
        let (context, _messages, _stops) = context();
        let queue = Arc::new(WorkQueue::default());
        let worker_queue = queue.clone();
        let worker_context = context.clone();
        let worker = thread::spawn(move || {
            let stop = loop {
                if let Some(work) = worker_queue.next() {
                    break work;
                }
            };
            assert_eq!(
                worker_context.status().unwrap().phase as u8,
                Phase::Stopping as u8
            );
            let mut status = worker_context.status().unwrap();
            status.phase = Phase::Stopped;
            status.operation = None;
            status.intent_revision = stop.generation;
            worker_context.publish_status(status);
        });
        queue
            .enqueue(&context, request(1, "desktop_stop", serde_json::json!({})))
            .unwrap();
        worker.join().unwrap();
        assert_eq!(context.status().unwrap().phase as u8, Phase::Stopped as u8);
    }

    #[test]
    fn later_start_keeps_the_accepted_stop_cleanup_barrier() {
        let (context, _messages, _stops) = context();
        let queue = WorkQueue::default();
        queue
            .enqueue(&context, request(1, "desktop_start", serde_json::json!({})))
            .unwrap();
        queue
            .enqueue(&context, request(2, "desktop_stop", serde_json::json!({})))
            .unwrap();
        queue
            .enqueue(&context, request(3, "desktop_start", serde_json::json!({})))
            .unwrap();
        let barrier = queue.next().unwrap();
        assert!(barrier.stops && barrier.generation < context.generation());
        assert!(context.status().unwrap().desired_running);
        assert_eq!(
            context.stop_intent_revision.load(Ordering::Acquire),
            2,
            "old Stop must not be relabelled as the new Start by the service-control thread"
        );
        assert_eq!(queue.next().unwrap().request.id, 1);
        assert_eq!(queue.next().unwrap().request.id, 3);
    }

    #[test]
    fn cancelled_exit_confirmation_never_stops_the_runtime_at_admission() {
        let (context, _messages, _stops) = context();
        let queue = WorkQueue::default();
        queue
            .enqueue(
                &context,
                request(1, "desktop_exit", serde_json::json!({"force":false})),
            )
            .unwrap();
        assert_eq!(context.generation(), 0);
        assert_eq!(context.stop_pending.load(Ordering::Acquire), 0);
        assert!(!queue.next().unwrap().stops);
    }

    #[test]
    fn raw_conflict_and_metadata_publish_keep_recovery_until_the_owner_finishes() {
        let (context, _messages, _stops) = context();
        context.service_active.store(true, Ordering::Release);
        let mut metadata = context.status().unwrap();
        metadata.desired_running = true;
        metadata.tun = true;
        metadata.auto_tun_recovery_eligible = true;
        metadata.service_instance = Some("service".into());
        metadata.service_sequence = 1;
        metadata.phase = Phase::Running;
        context.publish_status(metadata.clone());
        context.publish_service_observation(
            CoreStatus {
                process_state: "exited".into(),
                error_code: Some("tun_device_conflict".into()),
                error: Some("native conflict".into()),
                service_instance: Some("service".into()),
                status_sequence: 2,
                observed_at: core::observed_now(),
                ..CoreStatus::default()
            },
            0,
        );
        let pending = context.status().unwrap();
        assert!(pending.phase == Phase::Recovering);
        assert_eq!(pending.operation.as_deref(), Some("recovering_tun_device"));
        assert!(pending.pid.is_none());
        // read_config/metadata has not observed or completed this recovery.
        context.publish_status(metadata);
        let pending = context.status().unwrap();
        assert!(pending.phase == Phase::Recovering);
        assert_eq!(pending.service_sequence, 2);

        let ready = CoreStatus {
            running: true,
            process_state: "alive".into(),
            control_state: "ready".into(),
            pid: Some(222),
            service_instance: Some("service".into()),
            status_sequence: 3,
            observed_at: core::observed_now(),
            ..CoreStatus::default()
        };
        context.publish_service_observation(ready.clone(), 0);
        assert!(context.status().unwrap().phase == Phase::Recovering);
        let mut completed = pending;
        completed.phase = Phase::Running;
        completed.operation = None;
        completed.error_code = None;
        completed.last_error = None;
        completed.auto_tun_recovery_eligible = false;
        completed.tun_device_recovery = TunDeviceRecoveryStatus::Succeeded;
        context.publish_status(completed);
        let completed = context.status().unwrap();
        assert!(completed.phase == Phase::Running);
        assert!(completed.operation.is_none());
        assert_eq!(completed.pid, Some(222));
        assert_eq!(completed.service_sequence, 3);
        context.publish_service_observation(
            CoreStatus {
                status_sequence: 4,
                ..ready
            },
            0,
        );
        assert!(context.status().unwrap().operation.is_none());
    }

    #[test]
    fn late_recovery_success_cannot_overwrite_a_newer_native_failure() {
        let (context, _messages, _stops) = context();
        context.service_active.store(true, Ordering::Release);
        let mut recovering = context.status().unwrap();
        recovering.desired_running = true;
        recovering.tun = true;
        recovering.service_instance = Some("service".into());
        recovering.service_sequence = 1;
        recovering.phase = Phase::Recovering;
        recovering.operation = Some("recovering_tun_device".into());
        recovering.tun_device_recovery = TunDeviceRecoveryStatus::Pending;
        context.publish_status(recovering.clone());
        context.publish_service_observation(
            CoreStatus {
                process_state: "exited".into(),
                error_code: Some("core_unexpected_exit".into()),
                error: Some("newer native exit".into()),
                service_instance: Some("service".into()),
                status_sequence: 2,
                observed_at: core::observed_now(),
                ..CoreStatus::default()
            },
            0,
        );
        recovering.phase = Phase::Running;
        recovering.operation = None;
        recovering.tun_device_recovery = TunDeviceRecoveryStatus::Succeeded;
        context.publish_status(recovering);
        let final_status = context.status().unwrap();
        assert!(final_status.phase == Phase::Error);
        assert_eq!(
            final_status.error_code.as_deref(),
            Some("core_unexpected_exit")
        );
        assert_eq!(
            final_status.last_error.as_deref(),
            Some("newer native exit")
        );
        assert_eq!(final_status.process_state, "exited");
        assert!(final_status.operation.is_none());
    }

    #[test]
    fn recovery_failure_survives_later_healthy_heartbeats_and_stop_cancels_its_overlay() {
        let (context, _messages, _stops) = context();
        context.service_active.store(true, Ordering::Release);
        let mut recovering = context.status().unwrap();
        recovering.desired_running = true;
        recovering.tun = true;
        recovering.service_instance = Some("service".into());
        recovering.service_sequence = 1;
        recovering.phase = Phase::Recovering;
        recovering.operation = Some("recovering_tun_device".into());
        recovering.tun_device_recovery = TunDeviceRecoveryStatus::Pending;
        context.publish_status(recovering.clone());
        let ready = CoreStatus {
            running: true,
            process_state: "alive".into(),
            control_state: "ready".into(),
            pid: Some(222),
            service_instance: Some("service".into()),
            status_sequence: 2,
            observed_at: core::observed_now(),
            ..CoreStatus::default()
        };
        context.publish_service_observation(ready.clone(), 0);
        recovering.phase = Phase::Error;
        recovering.operation = None;
        recovering.error_code = Some("config_conflict".into());
        recovering.last_error = Some("source changed during recovery".into());
        recovering.tun_device_recovery = TunDeviceRecoveryStatus::Failed(AppError::new(
            "config_conflict",
            "source changed during recovery",
        ));
        context.publish_status(recovering);
        for sequence in [3, 4] {
            context.publish_service_observation(
                CoreStatus {
                    status_sequence: sequence,
                    ..ready.clone()
                },
                0,
            );
            let failed = context.status().unwrap();
            assert!(failed.phase == Phase::Error);
            assert!(failed.operation.is_none());
            assert_eq!(failed.error_code.as_deref(), Some("config_conflict"));
            assert_eq!(
                failed.last_error.as_deref(),
                Some("source changed during recovery")
            );
            assert_eq!(failed.pid, Some(222));
        }
        let queue = WorkQueue::default();
        queue
            .enqueue(&context, request(1, "desktop_stop", serde_json::json!({})))
            .unwrap();
        context.publish_service_observation(
            CoreStatus {
                status_sequence: 5,
                ..ready
            },
            0,
        );
        let stopping = context.status().unwrap();
        assert!(!stopping.desired_running);
        assert!(stopping.phase == Phase::Stopping);
        assert_eq!(stopping.operation.as_deref(), Some("stopping"));
        assert!(matches!(
            stopping.tun_device_recovery,
            TunDeviceRecoveryStatus::Idle
        ));
    }

    #[test]
    fn confirmed_cleanup_does_not_erase_actionable_tun_conflict_or_age_it_out() {
        let (context, _messages, _stops) = context();
        let mut stopped = context.status().unwrap();
        stopped.service_instance = Some("service".into());
        stopped.service_sequence = 3;
        stopped.observed_at = 1;
        context.publish_status(stopped.clone());
        let mut failed = stopped;
        failed.phase = Phase::Error;
        failed.service_sequence = 2;
        failed.error_code = Some("tun_device_conflict".into());
        failed.last_error = Some("original native ownership guard".into());
        failed.tun_device.configured_name = Some("utun".into());
        failed.tun_device.effective_name = Some("utun".into());
        failed.tun_device.config_revision = "a".repeat(64);
        context.publish_status(failed);
        let observed = context.status().unwrap();
        assert!(observed.phase == Phase::Error);
        assert_eq!(observed.process_state, "exited");
        assert_eq!(observed.service_sequence, 3);
        assert_eq!(observed.error_code.as_deref(), Some("tun_device_conflict"));
        assert_eq!(
            observed.last_error.as_deref(),
            Some("original native ownership guard")
        );
        assert!(!observed.status_stale);
    }

    #[test]
    fn newer_heartbeats_do_not_release_unconfirmed_cleanup() {
        let (context, _messages, _stops) = context();
        context.service_active.store(true, Ordering::Release);
        let healthy = CoreStatus {
            running: true,
            process_state: "alive".into(),
            control_state: "ready".into(),
            listeners_ready: Some(true),
            pid: Some(111),
            controller: Some(crate::core::Controller {
                url: "http://127.0.0.1:9090".into(),
                secret: "fixture".into(),
            }),
            service_instance: Some("service".into()),
            status_sequence: 2,
            observed_at: core::observed_now(),
            ..CoreStatus::default()
        };
        context.publish_service_observation(healthy.clone(), 0);
        let mut failure = context.status().unwrap();
        failure.service_sequence = 1;
        failure.phase = Phase::Stopping;
        failure.operation = Some("stopping".into());
        failure.process_state = "unknown".into();
        failure.control_state = "unknown".into();
        failure.controller = None;
        failure.error_code = Some("core_termination_unconfirmed".into());
        failure.last_error = Some("original native conflict; cleanup exit unconfirmed".into());
        context.publish_status(failure);
        for sequence in [2, 3] {
            if sequence == 3 {
                context.publish_service_observation(
                    CoreStatus {
                        status_sequence: sequence,
                        ..healthy.clone()
                    },
                    0,
                );
            }
            let status = context.status().unwrap();
            assert_eq!(status.service_sequence, sequence);
            assert_eq!(status.pid, Some(111));
            assert_eq!(status.process_state, "alive");
            assert!(status.phase == Phase::Stopping);
            assert_eq!(status.operation.as_deref(), Some("stopping"));
            assert_eq!(status.control_state, "unknown");
            assert_eq!(
                status.error_code.as_deref(),
                Some("core_termination_unconfirmed")
            );
            assert_eq!(
                status.last_error.as_deref(),
                Some("original native conflict; cleanup exit unconfirmed")
            );
            assert!(status.controller.is_none());
        }

        // An explicit owner publication after confirming exit clears the
        // barrier; the heartbeat overlay must not latch the error forever.
        let mut confirmed = context.status().unwrap();
        confirmed.phase = Phase::Stopped;
        confirmed.process_state = "exited".into();
        confirmed.pid = None;
        confirmed.operation = None;
        confirmed.error_code = None;
        confirmed.last_error = None;
        context.publish_status(confirmed);
        let status = context.status().unwrap();
        assert!(status.phase == Phase::Stopped);
        assert!(status.operation.is_none());
        assert!(status.error_code.is_none());
    }

    #[test]
    fn newer_service_facts_cannot_overwrite_a_later_host_identity_failure() {
        let (context, _messages, _stops) = context();
        let mut healthy = context.status().unwrap();
        healthy.service_instance = Some("service".into());
        healthy.service_sequence = 2;
        healthy.process_state = "alive".into();
        healthy.control_state = "ready".into();
        healthy.phase = Phase::Running;
        healthy.pid = Some(111);
        healthy.controller = Some(crate::core::Controller {
            url: "http://127.0.0.1:9090".into(),
            secret: "fixture".into(),
        });
        healthy.observed_at = core::observed_now();
        context.publish_status(healthy.clone());
        let mut failure = healthy;
        failure.service_sequence = 1;
        failure.pid = None;
        failure.phase = Phase::Error;
        failure.control_state = "unreachable".into();
        failure.controller = None;
        failure.error_code = Some("service_identity_failed".into());
        failure.last_error = Some("无法保留 TUN 服务身份（系统错误：5）".into());
        failure.system_proxy = false;
        context.publish_status(failure);
        let status = context.status().unwrap();
        assert_eq!(status.service_sequence, 2);
        assert_eq!(status.pid, Some(111));
        assert_eq!(status.phase as u8, Phase::Error as u8);
        assert_eq!(status.control_state, "unreachable");
        assert_eq!(
            status.error_code.as_deref(),
            Some("service_identity_failed")
        );
        assert!(status.controller.is_none());
        assert!(!status.system_proxy);
    }

    #[test]
    fn retired_service_heartbeat_cannot_replace_a_live_direct_process() {
        let (context, _messages, _stops) = context();
        context.service_active.store(true, Ordering::Release);
        context.service_epoch.store(1, Ordering::Release);
        context.pause_service_observation();
        let mut direct = context.status().unwrap();
        direct.phase = Phase::Running;
        direct.process_state = "alive".into();
        direct.pid = Some(333);
        direct.observed_at = core::observed_now();
        context.publish_status(direct);
        let before = context.status().unwrap().status_sequence;
        let old = CoreStatus {
            process_state: "exited".into(),
            service_instance: Some("retired-service".into()),
            status_sequence: 999,
            ..CoreStatus::default()
        };
        context.publish_service_observation(old.clone(), 1);
        context.service_active.store(true, Ordering::Release);
        context.service_epoch.store(3, Ordering::Release);
        context.publish_service_observation(old, 1);
        let current = context.status().unwrap();
        assert_eq!(current.pid, Some(333));
        assert_eq!(current.status_sequence, before);
    }

    #[test]
    fn published_observations_are_monotonic_and_stopped_state_does_not_expire() {
        let (context, _messages, _stops) = context();
        assert!(!context.status().unwrap().status_stale);
        let mut workers = Vec::new();
        for range in [1..51, 51..101] {
            let context = context.clone();
            workers.push(thread::spawn(move || {
                for sequence in range {
                    let mut status = context.status().unwrap();
                    status.service_instance = Some("service".into());
                    status.service_sequence = sequence;
                    status.observed_at = sequence;
                    context.publish_status(status);
                }
            }));
        }
        for worker in workers {
            worker.join().unwrap();
        }
        let final_status = context.status().unwrap();
        assert_eq!(final_status.service_sequence, 100);
        assert_eq!(final_status.observed_at, 100);
        assert_eq!(final_status.status_sequence, 101);
        assert!(!final_status.status_stale);
    }
}
