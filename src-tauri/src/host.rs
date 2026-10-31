//! Native desktop process boundary. One worker owns mutating operations; the
//! protocol thread can always return the last published status during startup.
use crate::{
    commands,
    desktop::{DesktopManager, DesktopStatus, HostState},
    error::{AppError, Result},
    lifecycle::PowerSignal,
    protocol::{self, Request},
    windows,
};
use serde::Serialize;
use serde_json::Value;
use std::{
    collections::HashSet,
    io::BufReader,
    os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle},
    path::{Path, PathBuf},
    sync::{
        atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering},
        mpsc::{self, Receiver, SyncSender, TrySendError},
        Arc, Mutex,
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

enum Message {
    Request(Request),
    Invalid(Option<i64>, AppError),
    Reply(i64, Result<Value>),
    Event(Value),
    InputClosed,
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
}

impl HostContext {
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

    pub fn publish_status(&self, status: DesktopStatus) {
        *self.published.lock().unwrap_or_else(|e| e.into_inner()) = Some(status.clone());
        self.emit("desktop-status", status);
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
    let context = HostContext {
        output: output.clone(),
        published: Arc::new(Mutex::new(None)),
        owner,
        runtime_generation: Arc::new(AtomicU64::new(0)),
        stop_pending: Arc::new(AtomicUsize::new(0)),
    };
    let power = Arc::new(PowerSignal::default());
    let _power_registration = crate::power::Registration::new(power.clone())?;
    let mut manager = DesktopManager::new(context.clone())?;
    let start = manager.prepare_startup(!no_autostart);
    let state = HostState(Arc::new(Mutex::new(manager)));
    let (work, requests) = mpsc::sync_channel(WORK_QUEUE_CAPACITY);
    let stopping = Arc::new(AtomicBool::new(false));
    let worker_state = state.clone();
    let worker_context = context.clone();
    let worker_stopping = stopping.clone();
    let poll_state = state.clone();
    let poll_stopping = stopping.clone();
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
    // Subscription/update downloads deliberately release the manager lock.
    // Keep the authenticated TUN heartbeat alive during that network work;
    // otherwise the service correctly stops routing after 20 idle seconds.
    thread::Builder::new()
        .name("desktop-heartbeat".into())
        .spawn(move || {
            let mut generation = power.generation();
            while !poll_stopping.load(Ordering::Acquire) {
                // Frequent wake checks do not increase the normal heartbeat rate.
                for _ in 0..20 {
                    if power.generation() != generation {
                        break;
                    }
                    thread::sleep(Duration::from_millis(100));
                }
                if poll_stopping.load(Ordering::Acquire) {
                    break;
                }
                if let Ok(mut manager) = poll_state.0.try_lock() {
                    if power.generation() != generation {
                        generation = power.generation();
                        manager.request_reconcile(generation);
                    }
                    if !power.suspended() {
                        manager.poll();
                    }
                }
            }
        })
        .map_err(|e| AppError::io("无法启动内核心跳线程", e))?;
    thread::Builder::new()
        .name("desktop-input".into())
        .spawn(move || read_requests(output))
        .map_err(|e| AppError::io("无法启动桌面请求通道", e))?;
    crate::update_cache::start_cleanup_worker();
    protocol_loop(state, context, messages, work, stopping)
}

fn read_requests(output: SyncSender<Message>) {
    let mut input = BufReader::new(std::io::stdin());
    loop {
        let message = match protocol::read_frame(&mut input) {
            Ok(Some(bytes)) => match protocol::parse_request(&bytes) {
                Ok(request) => Message::Request(request),
                Err(error) => Message::Invalid(protocol::request_id(&bytes), error),
            },
            Ok(None) => break,
            Err(error) => {
                let _ = output.send(Message::Invalid(None, error));
                break;
            }
        };
        if output.send(message).is_err() {
            return;
        }
    }
    let _ = output.send(Message::InputClosed);
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
    work: SyncSender<Request>,
    stopping: Arc<AtomicBool>,
) -> Result<()> {
    let mut stdout = std::io::stdout();
    let mut pending = HashSet::new();
    let mut owner_lost_at = None;
    let mut output_open = true;
    loop {
        if context.owner.exited() && owner_lost_at.is_none() {
            owner_lost_at = Some(Instant::now());
            stopping.store(true, Ordering::Release);
        }
        if owner_lost_at.is_some_and(|at: Instant| at.elapsed() >= OWNER_EXIT_GRACE) {
            // Kill only this host, never its descendants: the recovery helper
            // must outlive us to restore WinINet, and an update monitor may own
            // an installer. The existing core job closes with this process.
            std::process::exit(1);
        }
        let frame = match messages.recv_timeout(Duration::from_millis(100)) {
            Ok(Message::Request(request)) if owner_lost_at.is_none() => {
                if pending.contains(&request.id) {
                    owner_lost_at = Some(Instant::now());
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
                    let stops = matches!(request.method.as_str(), "desktop_stop" | "desktop_exit");
                    let invalidates = stops
                        || matches!(
                            request.method.as_str(),
                            "desktop_start"
                                | "desktop_restart"
                                | "desktop_set_tun"
                                | "desktop_reload_config"
                                | "desktop_install_update"
                        );
                    if invalidates {
                        context.runtime_generation.fetch_add(1, Ordering::AcqRel);
                    }
                    if stops {
                        context.stop_pending.fetch_add(1, Ordering::AcqRel);
                    }
                    match work.try_send(request) {
                        Ok(()) => {
                            pending.insert(id);
                            None
                        }
                        Err(TrySendError::Full(_)) => {
                            if stops {
                                context.stop_pending.fetch_sub(1, Ordering::AcqRel);
                            }
                            Some(reply(
                                Some(id),
                                Err(AppError::new(
                                    "desktop_busy",
                                    "已有桌面操作正在进行，请稍后重试",
                                )),
                            ))
                        }
                        Err(TrySendError::Disconnected(_)) => {
                            return Err(AppError::new("state_unavailable", "桌面操作线程已退出"))
                        }
                    }
                }
            }
            Ok(Message::Request(request)) => Some(reply(
                Some(request.id),
                Err(AppError::new("desktop_exiting", "桌面后台正在退出")),
            )),
            Ok(Message::Invalid(id, error)) => Some(reply(id, Err(error))),
            Ok(Message::Reply(id, result)) => {
                pending.remove(&id);
                Some(reply(Some(id), result))
            }
            Ok(Message::Event(frame)) => Some(frame),
            Ok(Message::InputClosed) => {
                owner_lost_at.get_or_insert_with(Instant::now);
                stopping.store(true, Ordering::Release);
                None
            }
            Ok(Message::Exit) => return Ok(()),
            Err(mpsc::RecvTimeoutError::Timeout) => None,
            Err(mpsc::RecvTimeoutError::Disconnected) => return Ok(()),
        };
        if let Some(frame) = frame {
            if output_open && protocol::write_frame(&mut stdout, &frame).is_err() {
                output_open = false;
                owner_lost_at.get_or_insert_with(Instant::now);
                stopping.store(true, Ordering::Release);
            }
        }
    }
}

fn operations(
    state: HostState,
    context: HostContext,
    requests: Receiver<Request>,
    stopping: Arc<AtomicBool>,
    start: bool,
) {
    if start && !stopping.load(Ordering::Acquire) {
        if let Ok(mut manager) = state.0.lock() {
            let _ = manager.start(false);
        }
    }
    if let Some(message) = crate::update::take_install_result() {
        if let Ok(mut manager) = state.0.lock() {
            manager.report_error(message);
        }
    }
    while !stopping.load(Ordering::Acquire) {
        match requests.recv_timeout(Duration::from_millis(100)) {
            Ok(request) => {
                if stopping.load(Ordering::Acquire) {
                    break;
                }
                let result = commands::dispatch(&state, &context, &request.method, request.params);
                if matches!(request.method.as_str(), "desktop_stop" | "desktop_exit") {
                    context.stop_pending.fetch_sub(1, Ordering::AcqRel);
                }
                let exit = result
                    .as_ref()
                    .ok()
                    .and_then(|result| result.get("exiting"))
                    .and_then(Value::as_bool)
                    == Some(true);
                let update_exit = exit && request.method == "desktop_install_update";
                let _ = context.output.send(Message::Reply(request.id, result));
                if exit {
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
            Err(mpsc::RecvTimeoutError::Disconnected) => break,
            Err(mpsc::RecvTimeoutError::Timeout) => {}
        }
    }
    if let Ok(mut manager) = state.0.lock() {
        let _ = manager.request_exit(true);
    }
    let _ = context.output.send(Message::Exit);
}
