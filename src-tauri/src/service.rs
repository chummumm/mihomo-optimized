use crate::{
    config,
    core::CoreSupervisor,
    core_runtime::{CoreRuntime, RuntimeRequest},
    error::{AppError, Result},
    lifecycle::{OwnerLease, PowerSignal, StopReason, SystemAwakeClock},
    service_ipc::{AuthenticatedOwner, AuthenticatedRequest, Reply, Request, Server},
    windows,
};
use serde::Serialize;
use std::{
    collections::VecDeque,
    ffi::OsString,
    fs,
    path::PathBuf,
    sync::{
        atomic::{AtomicBool, AtomicU8, Ordering},
        Arc, Mutex,
    },
    thread,
    time::{Duration, Instant},
};
use windows_service::{
    service::{
        PowerEventParam, Service, ServiceAccess, ServiceControl, ServiceControlAccept,
        ServiceErrorControl, ServiceExitCode, ServiceInfo, ServiceStartType, ServiceState,
        ServiceStatus, ServiceType,
    },
    service_control_handler::{self, ServiceControlHandlerResult},
    service_dispatcher,
    service_manager::{ServiceManager, ServiceManagerAccess},
};

pub const SERVICE_NAME: &str = "MihomoOptimizedService";

#[derive(Clone, Default, Serialize)]
pub struct ServiceStatusView {
    pub installed: bool,
    pub running: bool,
}

pub fn status() -> Result<ServiceStatusView> {
    let manager = ServiceManager::local_computer(None::<&str>, ServiceManagerAccess::CONNECT)
        .map_err(service_error)?;
    match manager.open_service(SERVICE_NAME, ServiceAccess::QUERY_STATUS) {
        Ok(service) => Ok(ServiceStatusView {
            installed: true,
            running: service.query_status().map_err(service_error)?.current_state
                == ServiceState::Running,
        }),
        Err(windows_service::Error::Winapi(error)) if error.raw_os_error() == Some(1060) => {
            Ok(ServiceStatusView::default())
        }
        Err(error) => Err(service_error(error)),
    }
}

fn service_error(error: windows_service::Error) -> AppError {
    match error {
        windows_service::Error::Winapi(error) => AppError::io("Windows 服务操作失败", error),
        _ => AppError::new("service_failed", "Windows 服务操作失败"),
    }
}

pub fn entry() -> Result<()> {
    let args: Vec<String> = std::env::args().skip(1).collect();
    match args.as_slice() {
        [action, marker, sid] if action == "--install" && marker == "--owner-sid" => install(sid),
        [action] if action == "--uninstall" => uninstall(),
        [action] if action == "--stop-for-update" => set_installed_running(false),
        [action] if action == "--start-if-installed" => set_installed_running(true),
        [action, pid, marker, instance] if action == "--watch-user" && marker == "--instance" => {
            crate::proxy::watch_user(
                pid.parse()
                    .map_err(|_| AppError::new("invalid_pid", "无效的主程序进程"))?,
                instance,
            )
        }
        [] => service_dispatcher::start(SERVICE_NAME, ffi_service_main).map_err(service_error),
        _ => Err(AppError::new(
            "invalid_service_action",
            "不支持的服务启动参数",
        )),
    }
}

fn valid_user_sid(sid: &str) -> bool {
    sid.starts_with("S-1-5-21-")
        && sid.len() < 180
        && sid
            .split('-')
            .skip(1)
            .all(|part| !part.is_empty() && part.bytes().all(|byte| byte.is_ascii_digit()))
}

fn install(owner_sid: &str) -> Result<()> {
    if !valid_user_sid(owner_sid) {
        return Err(AppError::new("invalid_owner", "服务所属用户标识无效"));
    }
    let directory = windows::protected_install_dir()?;
    // Both the installed executables and persistent service state are administrator-only
    // writable. Unprivileged clients supply configuration text, never filesystem paths.
    windows::apply_dacl(
        &directory,
        "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;GRGX;;;BU)",
    )?;
    for filename in [
        "mihomo-core.exe",
        "mihomo-optimized.exe",
        "mihomo-desktop-host.exe",
        "mihomo-desktop-service.exe",
        "wintun.dll",
    ] {
        let path = directory.join(filename);
        if path.exists() {
            windows::reject_reparse_path(&path)?;
            windows::reject_hard_links(&path)?;
            windows::apply_dacl(&path, "O:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;BU)")?;
        }
    }
    let data = windows::service_data_dir()?;
    if let Some(parent) = data.parent() {
        windows::secure_directory(parent, None)?;
    }
    windows::secure_directory(&data, None)?;
    let owner_file = data.join("owner.sid");
    if owner_file.exists() {
        windows::reject_reparse_path(&owner_file)?;
        windows::reject_hard_links(&owner_file)?;
        windows::apply_dacl(&owner_file, "O:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)")?;
    }
    if owner_file.exists()
        && fs::read_to_string(&owner_file)
            .map_err(|e| AppError::io("无法读取服务所有者", e))?
            .trim()
            != owner_sid
    {
        return Err(AppError::new(
            "service_owner_mismatch",
            "服务已由另一位用户安装，请先卸载原服务",
        ));
    }
    config::atomic_write(&owner_file, owner_sid.as_bytes())?;
    windows::secure_directory(&data.join("runtime"), None)?;
    windows::secure_directory(&data.join("logs"), None)?;
    windows::secure_admin_tree(&data.join("runtime"), None)?;
    windows::secure_admin_tree(&data.join("logs"), Some(owner_sid))?;
    let manager = ServiceManager::local_computer(
        None::<&str>,
        ServiceManagerAccess::CONNECT | ServiceManagerAccess::CREATE_SERVICE,
    )
    .map_err(service_error)?;
    let access = ServiceAccess::START | ServiceAccess::QUERY_STATUS | ServiceAccess::CHANGE_CONFIG;
    let service = match manager.open_service(SERVICE_NAME, access) {
        Ok(service) => service,
        Err(windows_service::Error::Winapi(error)) if error.raw_os_error() == Some(1060) => manager
            .create_service(
                &ServiceInfo {
                    name: SERVICE_NAME.into(),
                    display_name: "Mihomo Optimized TUN Service".into(),
                    service_type: ServiceType::OWN_PROCESS,
                    start_type: ServiceStartType::AutoStart,
                    error_control: ServiceErrorControl::Normal,
                    executable_path: directory.join("mihomo-desktop-service.exe"),
                    launch_arguments: vec![],
                    dependencies: vec![],
                    account_name: None,
                    account_password: None,
                },
                access,
            )
            .map_err(service_error)?,
        Err(error) => return Err(service_error(error)),
    };
    service.set_description("Runs only the bundled Mihomo core for the installed user's TUN mode; stops the core when the desktop session disconnects.").map_err(service_error)?;
    windows::configure_tun_firewall(&directory, true)?;
    if service.query_status().map_err(service_error)?.current_state == ServiceState::Stopped {
        service.start::<&str>(&[]).map_err(service_error)?;
    }
    let started = Instant::now();
    while started.elapsed() < Duration::from_secs(15) {
        if service.query_status().map_err(service_error)?.current_state == ServiceState::Running {
            return Ok(());
        }
        thread::sleep(Duration::from_millis(100));
    }
    Err(AppError::new(
        "service_start_failed",
        "服务已安装但未能启动，请检查 Windows 服务状态",
    ))
}

fn set_installed_running(running: bool) -> Result<()> {
    // An old uninstall may leave only this helper behind. Stopping the fixed SCM
    // service must not require the application/core files that were just removed.
    let directory = if running {
        // The privileged maintenance start must repair the same fixed rename
        // gap as SCM startup before requiring the core executable to exist.
        Some(windows::protected_install_dir_for_core_recovery()?)
    } else {
        None
    };
    let manager = ServiceManager::local_computer(None::<&str>, ServiceManagerAccess::CONNECT)
        .map_err(service_error)?;
    let access = ServiceAccess::QUERY_STATUS
        | if running {
            ServiceAccess::START
        } else {
            ServiceAccess::STOP
        };
    let service = match open_maintenance_service(&manager, access)? {
        Some(service) => service,
        None => return Ok(()),
    };
    if let Some(directory) = directory {
        windows::configure_tun_firewall(&directory, true)?;
    } else {
        return stop_service(&service);
    }
    let current = service.query_status().map_err(service_error)?.current_state;
    if current == ServiceState::Stopped {
        service.start::<&str>(&[]).map_err(service_error)?;
    }
    let started = Instant::now();
    while service.query_status().map_err(service_error)?.current_state != ServiceState::Running {
        if started.elapsed() > Duration::from_secs(40) {
            return Err(AppError::new(
                "service_transition_timeout",
                "等待服务切换状态超时",
            ));
        }
        thread::sleep(Duration::from_millis(100));
    }
    Ok(())
}

fn has_service_error(error: &windows_service::Error, code: i32) -> bool {
    matches!(error, windows_service::Error::Winapi(error) if error.raw_os_error() == Some(code))
}

fn open_maintenance_service(
    manager: &ServiceManager,
    access: ServiceAccess,
) -> Result<Option<Service>> {
    match manager.open_service(SERVICE_NAME, access) {
        Ok(service) => Ok(Some(service)),
        Err(error) if has_service_error(&error, 1060) => Ok(None),
        Err(error) if has_service_error(&error, 1072) => {
            // Marked for deletion does not prove that the service has stopped.
            wait_for_service_deleted(manager)?;
            Ok(None)
        }
        Err(error) => Err(service_error(error)),
    }
}

fn maintenance_wait(started: Instant) -> bool {
    if started.elapsed() >= Duration::from_secs(40) {
        return false;
    }
    thread::sleep(Duration::from_millis(100));
    true
}

fn stop_service(service: &Service) -> Result<()> {
    let started = Instant::now();
    stop_service_with(
        || service.query_status().map(|status| status.current_state),
        || service.stop().map(|_| ()),
        || maintenance_wait(started),
    )
}

fn stop_service_with(
    mut query: impl FnMut() -> windows_service::Result<ServiceState>,
    mut stop: impl FnMut() -> windows_service::Result<()>,
    mut wait: impl FnMut() -> bool,
) -> Result<()> {
    let mut stop_requested = false;
    let mut last_error = None;
    loop {
        let state = query().map_err(service_error)?;
        if state == ServiceState::Stopped {
            return Ok(());
        }
        // Pending states need time to finish. A successful STOP must not be
        // repeated even if QueryServiceStatusEx still reports the previous state.
        if !stop_requested && matches!(state, ServiceState::Running | ServiceState::Paused) {
            match stop() {
                Ok(()) => stop_requested = true,
                Err(error)
                    if has_service_error(&error, 1061) || has_service_error(&error, 1062) =>
                {
                    // A concurrent stop/start can race the status query. Requery
                    // before deciding whether another STOP is appropriate.
                    last_error = Some(service_error(error));
                }
                Err(error) => return Err(service_error(error)),
            }
        }
        if !wait() {
            return Err(maintenance_timeout(
                "service_stop_timeout",
                format!("等待服务停止超时（当前状态：{state:?}）"),
                last_error,
            ));
        }
    }
}

fn wait_for_service_deleted(manager: &ServiceManager) -> Result<()> {
    let started = Instant::now();
    wait_for_service_deleted_with(
        // Do not keep a probe handle open: it would itself defer deletion.
        || {
            manager
                .open_service(SERVICE_NAME, ServiceAccess::QUERY_STATUS)
                .map(drop)
        },
        || maintenance_wait(started),
    )
}

fn wait_for_service_deleted_with(
    mut open: impl FnMut() -> windows_service::Result<()>,
    mut wait: impl FnMut() -> bool,
) -> Result<()> {
    let mut last_error = None;
    loop {
        match open() {
            Ok(()) => (),
            Err(error) if has_service_error(&error, 1060) => return Ok(()),
            Err(error) if has_service_error(&error, 1072) => {
                last_error = Some(service_error(error));
            }
            Err(error) => return Err(service_error(error)),
        }
        if !wait() {
            return Err(maintenance_timeout(
                "service_delete_timeout",
                "等待服务从 Windows 服务列表移除超时，请关闭服务管理窗口后重试".into(),
                last_error,
            ));
        }
    }
}

fn maintenance_timeout(code: &str, message: String, last_error: Option<AppError>) -> AppError {
    AppError::new(
        code,
        match last_error {
            Some(error) => format!("{message}；上次操作错误：{}", error.message),
            None => message,
        },
    )
}

fn uninstall() -> Result<()> {
    let directory = windows::protected_install_dir()?;
    let manager = ServiceManager::local_computer(None::<&str>, ServiceManagerAccess::CONNECT)
        .map_err(service_error)?;
    let service = open_maintenance_service(
        &manager,
        ServiceAccess::STOP | ServiceAccess::DELETE | ServiceAccess::QUERY_STATUS,
    )?;
    if let Some(service) = &service {
        stop_service(service)?;
    }
    // A previous partial uninstall may have already removed the SCM entry.
    // Always remove this installation's firewall rules before clearing ownership.
    windows::configure_tun_firewall(&directory, false)?;
    if let Some(service) = service {
        match service.delete() {
            Ok(()) => (),
            Err(error) if has_service_error(&error, 1072) => (),
            Err(error) => return Err(service_error(error)),
        }
        // Another administrator may have started the service between the first
        // stop and DeleteService. A marked entry cannot be started again; confirm
        // it has stopped before closing our handle and waiting for actual removal.
        stop_service(&service)?;
        drop(service);
        wait_for_service_deleted(&manager)?;
    }
    let owner = windows::service_data_dir()?.join("owner.sid");
    if owner.exists() {
        fs::remove_file(owner).map_err(|e| AppError::io("无法清理服务所有者", e))?;
    }
    Ok(())
}

windows_service::define_windows_service!(ffi_service_main, service_main);

fn service_main(_arguments: Vec<OsString>) {
    if let Err(error) = run_service() {
        if let Ok(data) = windows::service_data_dir() {
            let _ = config::atomic_write(
                &data.join("service-error.txt"),
                error.to_string().as_bytes(),
            );
        }
    }
}

fn report_status(
    handle: &service_control_handler::ServiceStatusHandle,
    state: ServiceState,
) -> Result<()> {
    handle
        .set_service_status(ServiceStatus {
            service_type: ServiceType::OWN_PROCESS,
            current_state: state,
            controls_accepted: if state == ServiceState::Running {
                ServiceControlAccept::STOP
                    | ServiceControlAccept::SHUTDOWN
                    | ServiceControlAccept::POWER_EVENT
            } else {
                ServiceControlAccept::empty()
            },
            exit_code: ServiceExitCode::Win32(0),
            checkpoint: if state == ServiceState::StopPending {
                1
            } else {
                0
            },
            wait_hint: Duration::from_secs(45),
            process_id: None,
        })
        .map_err(service_error)
}

fn run_service() -> Result<()> {
    let stopping = Arc::new(AtomicBool::new(false));
    let stop_handler = stopping.clone();
    let stop_signal = Arc::new(AtomicU8::new(0));
    let stop_signal_handler = stop_signal.clone();
    let power = Arc::new(PowerSignal::default());
    let power_handler = power.clone();
    let handle = service_control_handler::register(SERVICE_NAME, move |control| match control {
        control @ (ServiceControl::Stop | ServiceControl::Shutdown) => {
            let signal = if matches!(control, ServiceControl::Shutdown) {
                2
            } else {
                1
            };
            let _ = stop_signal_handler.compare_exchange(
                0,
                signal,
                Ordering::Relaxed,
                Ordering::Relaxed,
            );
            stop_handler.store(true, Ordering::SeqCst);
            ServiceControlHandlerResult::NoError
        }
        ServiceControl::Interrogate => ServiceControlHandlerResult::NoError,
        ServiceControl::PowerEvent(event) => {
            match event {
                PowerEventParam::Suspend => power_handler.suspend(),
                PowerEventParam::ResumeAutomatic | PowerEventParam::ResumeCritical => {
                    power_handler.resume(true)
                }
                PowerEventParam::ResumeSuspend => power_handler.resume(false),
                _ => {}
            }
            ServiceControlHandlerResult::NoError
        }
        _ => ServiceControlHandlerResult::NotImplemented,
    })
    .map_err(service_error)?;
    let result = (|| {
        let directory = windows::protected_install_dir_for_core_recovery()?;
        let data = windows::service_data_dir()?;
        for path in [
            data.parent().unwrap_or(&data).to_path_buf(),
            data.clone(),
            data.join("runtime"),
            data.join("logs"),
            data.join("owner.sid"),
        ] {
            windows::reject_reparse_path(&path)?;
            windows::require_admin_protected(&path)?;
        }
        let owner = fs::read_to_string(data.join("owner.sid"))
            .map_err(|e| AppError::io("无法读取服务所有者", e))?;
        let owner = owner.trim();
        if !valid_user_sid(owner) {
            return Err(AppError::new("invalid_owner", "服务所有者记录损坏"));
        }
        windows::allow_service_identity_query(owner)?;
        let host_path = directory.join("mihomo-desktop-host.exe");
        let runtime = CoreRuntime::spawn(CoreSupervisor::new(
            directory.join("mihomo-core.exe"),
            data.join("runtime"),
            data.join("logs"),
        ))?;
        let session = Arc::new(Mutex::new(ServiceSession {
            owner: None,
            id: None,
            intent_revision: 0,
            request_sequence: 0,
            lease: OwnerLease::new(SystemAwakeClock)?,
            closing: false,
            retired: VecDeque::new(),
            shutting_down: false,
            cleanup_sequence: 0,
            diagnostic_directory: data.join("logs"),
        }));
        crate::diagnostics::record(
            &data.join("logs"),
            "service_started",
            serde_json::json!({"serviceInstance":runtime.status().service_instance}),
        );
        // The work pipe carries bounded config frames. Three separately accepted
        // control instances retain capacity for Stop/heartbeat when a work peer
        // is slow; none executes lifecycle work on its connection thread.
        let mut servers = Vec::new();
        for (bulk, first) in [(false, true), (false, false), (false, false), (true, true)] {
            servers.push(Server::new(
                owner.into(),
                host_path.clone(),
                bulk,
                first,
                data.join("logs"),
            )?);
        }
        for server in servers {
            let runtime = runtime.clone();
            let session = session.clone();
            let stopping = stopping.clone();
            let logs = data.join("logs");
            thread::Builder::new()
                .name("service-control".into())
                .spawn(move || {
                    let mut receive_failure = None;
                    while !stopping.load(Ordering::Acquire) {
                        match server.receive() {
                            Ok(Some(request)) => {
                                receive_failure = None;
                                let _scope =
                                    crate::diagnostics::scope(request.envelope.diagnostic_fields());
                                if let Err(error) =
                                    server.respond(handle_request(&runtime, &session, request))
                                {
                                    crate::diagnostics::record(
                                        &logs,
                                        "service_response_failed",
                                        serde_json::json!({"code":error.code}),
                                    );
                                }
                            }
                            Ok(None) => thread::sleep(Duration::from_millis(10)),
                            Err(error) => {
                                if receive_failure.as_deref() != Some(error.code.as_str()) {
                                    crate::diagnostics::record(
                                        &logs,
                                        "service_receive_failed",
                                        serde_json::json!({"code":error.code}),
                                    );
                                }
                                receive_failure = Some(error.code);
                                thread::sleep(Duration::from_millis(25));
                            }
                        }
                    }
                })
                .map_err(|error| AppError::io("无法启动服务控制通道", error))?;
        }
        let mut power_generation = power.generation();
        report_status(&handle, ServiceState::Running)?;
        while !stopping.load(Ordering::Acquire) {
            let resumed = power.generation() != power_generation;
            if resumed {
                power_generation = power.generation();
            }
            let mut cleanup_event = None;
            {
                let mut session = session.lock().unwrap_or_else(|error| error.into_inner());
                if let Some(authenticated) = session.owner.as_ref() {
                    let owner_pid = authenticated.pid;
                    let owner_alive = authenticated.alive();
                    let identity_error = if resumed {
                        authenticated.verify(owner, &host_path).err()
                    } else {
                        None
                    };
                    let valid = if resumed {
                        identity_error.is_none()
                    } else {
                        owner_alive
                    };
                    if !session.closing {
                        if valid && resumed {
                            session.lease.resume_verified()?;
                        }
                        let reason = if !valid {
                            Some(StopReason::OwnerLost)
                        } else if session.lease.expired()? {
                            Some(StopReason::LeaseExpired)
                        } else {
                            None
                        };
                        if let Some(reason) = reason {
                            let owner_session = session.id.clone();
                            let intent_revision = session.intent_revision;
                            let lease_age = session.lease.elapsed().ok().map(|age| age.as_millis());
                            begin_owner_cleanup(&runtime, &mut session, reason)?;
                            cleanup_event = Some(
                                serde_json::json!({"ownerPid":owner_pid,"reason":reason,"generation":power_generation,"ownerAlive":owner_alive,"identityRechecked":resumed,"identityErrorCode":identity_error.as_ref().map(|error|&error.code),"ownerValid":valid,"ownerSession":owner_session,"intentRevision":intent_revision,"leaseAgeMs":lease_age,"cleanupSequence":session.cleanup_sequence}),
                            );
                        } else if resumed {
                            runtime.reconcile(power_generation);
                        }
                    }
                    // Never transfer ownership while an old process/operation
                    // still owns resources, even if the owner PID has vanished.
                    let status = runtime.status();
                    if session.closing
                        && status.pid.is_none()
                        && status.operation.is_none()
                        && !status.desired_running
                    {
                        crate::diagnostics::record(
                            &data.join("logs"),
                            "service_owner_released",
                            serde_json::json!({
                                "ownerSession":session.id,"ownerPid":owner_pid,
                                "intentRevision":session.intent_revision,"exitConfirmed":true,
                            }),
                        );
                        if let Some(id) = session.id.take() {
                            let order = (id, session.intent_revision, session.request_sequence);
                            session.retired.push_back(order);
                            while session.retired.len() > 64 {
                                session.retired.pop_front();
                            }
                        }
                        session.owner = None;
                        session.closing = false;
                        session.intent_revision = 0;
                        session.request_sequence = 0;
                    }
                }
            }
            if let Some(event) = cleanup_event {
                crate::diagnostics::record(&data.join("logs"), "service_owner_cleanup", event);
            }
            thread::sleep(Duration::from_millis(25));
        }
        // Closing admission uses the same mutex as authenticated requests.
        // A frame already being read cannot overtake the SCM shutdown barrier.
        session
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .shutting_down = true;
        report_status(&handle, ServiceState::StopPending)?;
        let _scope = crate::diagnostics::scope(
            serde_json::json!({"cause":"service_control_shutdown","serviceInstance":runtime.status().service_instance}),
        );
        crate::diagnostics::record(
            &data.join("logs"),
            "service_shutdown_started",
            serde_json::json!({
                "control":match stop_signal.load(Ordering::Relaxed) { 1 => "stop", 2 => "shutdown", _ => "unknown" },
            }),
        );
        let shutdown = runtime.shutdown();
        crate::diagnostics::record(
            &data.join("logs"),
            "service_shutdown_finished",
            serde_json::json!({"code":shutdown.as_ref().err().map(|error| &error.code)}),
        );
        shutdown?;
        Ok(())
    })();
    let final_status = report_status(&handle, ServiceState::Stopped);
    let _ = crate::diagnostics::flush(Duration::from_millis(200));
    result.and(final_status)
}

struct ServiceSession {
    owner: Option<AuthenticatedOwner>,
    id: Option<String>,
    intent_revision: u64,
    request_sequence: u64,
    lease: OwnerLease<SystemAwakeClock>,
    closing: bool,
    retired: VecDeque<(String, u64, u64)>,
    shutting_down: bool,
    cleanup_sequence: u64,
    diagnostic_directory: PathBuf,
}

fn begin_owner_cleanup(
    runtime: &CoreRuntime,
    session: &mut ServiceSession,
    reason: StopReason,
) -> Result<()> {
    session.cleanup_sequence += 1;
    let operation_id = format!(
        "owner-cleanup-{}-{}",
        session.id.as_deref().unwrap_or("none"),
        session.cleanup_sequence
    );
    let _scope = crate::diagnostics::scope(serde_json::json!({
        "cause":reason,"ownerSession":session.id,"intentRevision":session.intent_revision,
        "operationId":operation_id,
    }));
    crate::diagnostics::record(
        &session.diagnostic_directory,
        "service_owner_stop_requested",
        serde_json::json!({
            "operationId":operation_id,"reason":reason,
        }),
    );
    let submitted = runtime.submit(
        operation_id,
        session.intent_revision,
        RuntimeRequest::Stop(reason),
    );
    crate::diagnostics::record(
        &session.diagnostic_directory,
        "service_owner_stop_admission",
        serde_json::json!({
            "accepted":submitted.is_ok(),"code":submitted.as_ref().err().map(|error| &error.code),
            "operationId":submitted.as_ref().ok().map(|operation| &operation.id),
        }),
    );
    submitted?;
    session.closing = true;
    Ok(())
}

fn handle_request(
    runtime: &CoreRuntime,
    session: &Mutex<ServiceSession>,
    authenticated: AuthenticatedRequest,
) -> Reply {
    let lifecycle = authenticated.envelope.request.lifecycle_request();
    let _scope = crate::diagnostics::scope(authenticated.envelope.diagnostic_fields());
    let _owner_scope = crate::diagnostics::scope(serde_json::json!({
        "ownerPid":authenticated.owner.pid,"serviceInstance":runtime.status().service_instance,
    }));
    let mut session = session.lock().unwrap_or_else(|error| error.into_inner());
    let logs = session.diagnostic_directory.clone();
    if lifecycle {
        crate::diagnostics::record(
            &logs,
            "service_request_received",
            authenticated.envelope.diagnostic_fields(),
        );
    }
    let reply = handle_session_request(runtime, &mut session, authenticated);
    drop(session);
    if lifecycle || reply.error.is_some() {
        crate::diagnostics::record(
            &logs,
            "service_request_finished",
            serde_json::json!({
                "admission":if reply.error.is_none() { "accepted" } else { "rejected" },
                "code":reply.error.as_ref().map(|error| &error.code),
                "operationId":reply.operation.as_ref().map(|operation| &operation.id),
                "operationPhase":reply.operation.as_ref().map(|operation| &operation.phase),
            }),
        );
    }
    reply
}

fn handle_session_request(
    runtime: &CoreRuntime,
    session: &mut ServiceSession,
    authenticated: AuthenticatedRequest,
) -> Reply {
    if session.shutting_down {
        return Reply::from_result(Err(AppError::new(
            "service_stopping",
            "TUN 服务正在关闭，不能接收新的内核操作",
        )));
    }
    if !authenticated.owner.alive() {
        return Reply::from_result(Err(AppError::new(
            "service_owner_lost",
            "发送请求的桌面后台已经退出",
        )));
    }
    let envelope = authenticated.envelope;
    let is_owner = session
        .owner
        .as_ref()
        .is_some_and(|owner| owner.alive() && owner.pid == authenticated.owner.pid)
        && session.id.as_deref() == Some(envelope.owner_session.as_str());
    if session.owner.is_some() && (!is_owner || session.closing) {
        let cleaning =
            session.closing || session.owner.as_ref().is_some_and(|owner| !owner.alive());
        return Reply::from_result(Err(AppError::new(
            if cleaning {
                "service_owner_cleanup"
            } else {
                "service_owner_busy"
            },
            if cleaning {
                "服务正在清理上一桌面实例的内核"
            } else {
                "现有桌面后台仍拥有服务，请先完全退出旧实例"
            },
        )));
    }
    let key = format!("{}:{}", envelope.owner_session, envelope.request_id);
    match envelope.request {
        Request::Status => Reply::from_result(Ok(runtime.status())),
        Request::Heartbeat if is_owner => {
            Reply::from_result(session.lease.renew().map(|_| runtime.status()))
        }
        Request::Heartbeat => Reply::from_result(Ok(runtime.status())),
        Request::Operation { id }
            if is_owner && id.starts_with(&format!("{}:", envelope.owner_session)) =>
        {
            Reply::operation(runtime.operation(&id))
        }
        Request::Reconcile { generation } if is_owner => {
            runtime.reconcile(generation);
            Reply::from_result(Ok(runtime.status()))
        }
        request @ (Request::Start { .. }
        | Request::Stop
        | Request::Reload { .. }
        | Request::PrepareCoreUpdate { .. }
        | Request::CommitCoreUpdate {}
        | Request::CompleteCoreUpdate { .. }) => {
            if !is_owner
                && matches!(
                    request,
                    Request::Reload { .. }
                        | Request::CommitCoreUpdate {}
                        | Request::CompleteCoreUpdate { accept: true }
                )
            {
                return Reply::from_result(Err(AppError::new(
                    "service_owner_required",
                    "只有当前桌面后台可以重载内核",
                )));
            }
            let operation = match request {
                Request::Start { config } => RuntimeRequest::Start(config),
                Request::Reload { config } => RuntimeRequest::Reload(config),
                Request::Stop => RuntimeRequest::Stop(StopReason::UserStop),
                Request::PrepareCoreUpdate { proxy_port } => {
                    if proxy_port == Some(0) {
                        return Reply::from_result(Err(AppError::new(
                            "invalid_arguments",
                            "内核更新代理端口无效",
                        )));
                    }
                    RuntimeRequest::PrepareCoreUpdate { proxy_port }
                }
                Request::CommitCoreUpdate {} => RuntimeRequest::CommitCoreUpdate,
                Request::CompleteCoreUpdate { accept } => {
                    RuntimeRequest::CompleteCoreUpdate { accept }
                }
                _ => unreachable!(),
            };
            // Replaying an accepted ID returns its retained outcome; an unseen
            // stale intent/sequence can never cancel newer work or resurrect it.
            if runtime.operation(&key).is_ok() {
                return Reply::operation(runtime.submit(key, envelope.intent_revision, operation));
            }
            let (previous_intent, previous_sequence) =
                if session.id.as_deref() == Some(envelope.owner_session.as_str()) {
                    (session.intent_revision, session.request_sequence)
                } else {
                    session
                        .retired
                        .iter()
                        .rev()
                        .find(|(id, _, _)| id == &envelope.owner_session)
                        .map(|(_, intent, sequence)| (*intent, *sequence))
                        .unwrap_or((0, 0))
                };
            if envelope.intent_revision < previous_intent
                || envelope.intent_revision == previous_intent
                    && envelope.request_sequence <= previous_sequence
            {
                return Reply::from_result(Err(AppError::new(
                    "operation_cancelled",
                    "服务已接收更新的用户指令",
                )));
            }
            let start = matches!(
                operation,
                RuntimeRequest::Start(_)
                    | RuntimeRequest::PrepareCoreUpdate { .. }
                    | RuntimeRequest::CommitCoreUpdate
                    | RuntimeRequest::CompleteCoreUpdate { .. }
            );
            let result = runtime.submit(key, envelope.intent_revision, operation);
            if result.is_ok() {
                session.intent_revision = envelope.intent_revision;
                session.request_sequence = envelope.request_sequence;
                if session.owner.is_none() {
                    // An authenticated Stop on an empty runtime still records a
                    // session tombstone, so a delayed bulk Start cannot overtake it.
                    session.owner = Some(authenticated.owner);
                    session.id = Some(envelope.owner_session);
                    match OwnerLease::new(SystemAwakeClock) {
                        Ok(lease) => session.lease = lease,
                        Err(error) => return Reply::from_result(Err(error)),
                    }
                }
                if start {
                    if let Err(error) = session.lease.renew() {
                        return Reply::from_result(Err(error));
                    }
                }
            }
            Reply::operation(result)
        }
        _ => Reply::from_result(Err(AppError::new(
            "service_owner_required",
            "只有当前桌面后台可以查询或恢复其内核操作",
        ))),
    }
}

#[cfg(test)]
mod maintenance_tests {
    use super::*;

    fn winapi_error(code: i32) -> windows_service::Error {
        windows_service::Error::Winapi(std::io::Error::from_raw_os_error(code))
    }

    #[test]
    fn stop_handles_concurrent_transitions_without_repeated_controls() {
        use ServiceState::{Running, StartPending, StopPending, Stopped};
        for (states, controls) in [
            (vec![StopPending, Stopped], vec![]),
            (vec![StartPending, Running, Stopped], vec![None]),
            (vec![Running, Running, StopPending, Stopped], vec![None]),
            (vec![Running, StopPending, Stopped], vec![Some(1061)]),
            (vec![Running, Stopped], vec![Some(1062)]),
        ] {
            let mut states = states.into_iter();
            let mut controls = controls.into_iter();
            stop_service_with(
                || Ok(states.next().expect("unexpected extra status query")),
                || match controls.next().expect("unexpected repeated STOP") {
                    Some(code) => Err(winapi_error(code)),
                    None => Ok(()),
                },
                || true,
            )
            .unwrap();
            assert!(states.next().is_none());
            assert!(controls.next().is_none());
        }
    }

    #[test]
    fn stop_preserves_query_and_control_access_errors() {
        let query_error = stop_service_with(
            || Err(winapi_error(5)),
            || panic!("must not stop after a failed status query"),
            || panic!("access denied is not a retryable transition"),
        )
        .unwrap_err();
        let control_error = stop_service_with(
            || Ok(ServiceState::Running),
            || Err(winapi_error(5)),
            || panic!("access denied is not a retryable transition"),
        )
        .unwrap_err();
        for error in [query_error, control_error] {
            assert_eq!(error.code, "io_error");
            assert!(error.message.contains("系统错误：5"));
        }
    }

    #[test]
    fn stop_timeout_preserves_the_last_transition_error() {
        let error = stop_service_with(
            || Ok(ServiceState::Running),
            || Err(winapi_error(1061)),
            || false,
        )
        .unwrap_err();
        assert_eq!(error.code, "service_stop_timeout");
        assert!(error.message.contains("系统错误：1061"));
        let pending = stop_service_with(
            || Ok(ServiceState::StopPending),
            || panic!("must not repeat STOP while StopPending"),
            || false,
        )
        .unwrap_err();
        assert_eq!(pending.code, "service_stop_timeout");
    }

    #[test]
    fn deletion_waits_until_the_service_is_absent() {
        let mut results = [Some(1072), None, Some(1060)].into_iter();
        wait_for_service_deleted_with(
            || match results.next().expect("unexpected extra service probe") {
                Some(code) => Err(winapi_error(code)),
                None => Ok(()),
            },
            || true,
        )
        .unwrap();
        assert!(results.next().is_none());
    }

    #[test]
    fn deletion_does_not_report_pending_or_access_denied_as_success() {
        let pending =
            wait_for_service_deleted_with(|| Err(winapi_error(1072)), || false).unwrap_err();
        assert_eq!(pending.code, "service_delete_timeout");
        assert!(pending.message.contains("系统错误：1072"));
        let denied = wait_for_service_deleted_with(
            || Err(winapi_error(5)),
            || panic!("access denied is not a pending deletion"),
        )
        .unwrap_err();
        assert_eq!(denied.code, "io_error");
        assert!(denied.message.contains("系统错误：5"));
    }
}

#[cfg(test)]
mod control_tests {
    use super::*;
    use crate::service_ipc::{Envelope, PROTOCOL_VERSION};

    fn session() -> Mutex<ServiceSession> {
        Mutex::new(ServiceSession {
            owner: None,
            id: None,
            intent_revision: 0,
            request_sequence: 0,
            lease: OwnerLease::new(SystemAwakeClock).unwrap(),
            closing: false,
            retired: VecDeque::new(),
            shutting_down: false,
            cleanup_sequence: 0,
            diagnostic_directory: PathBuf::new(),
        })
    }

    fn request(id: &str, intent: u64, sequence: u64, request: Request) -> AuthenticatedRequest {
        AuthenticatedRequest {
            owner: AuthenticatedOwner::current_for_test(),
            envelope: Envelope {
                protocol_version: PROTOCOL_VERSION,
                owner_session: "00000000000000000000000000000001".into(),
                request_id: id.into(),
                intent_revision: intent,
                request_sequence: sequence,
                request,
            },
        }
    }

    #[test]
    fn core_update_commit_requires_owner_and_cannot_overtake_stop() {
        let runtime = CoreRuntime::queued_for_test();
        let session = session();
        let unauthorized = handle_request(
            &runtime,
            &session,
            request("commit", 1, 1, Request::CommitCoreUpdate {}),
        );
        assert_eq!(unauthorized.error.unwrap().code, "service_owner_required");
        assert!(handle_request(
            &runtime,
            &session,
            request(
                "prepare",
                1,
                2,
                Request::PrepareCoreUpdate { proxy_port: None }
            )
        )
        .error
        .is_none());
        assert!(
            handle_request(&runtime, &session, request("stop", 2, 3, Request::Stop))
                .error
                .is_none()
        );
        let stale = handle_request(
            &runtime,
            &session,
            request("late-commit", 1, 4, Request::CommitCoreUpdate {}),
        );
        assert_eq!(stale.error.unwrap().code, "operation_cancelled");
        let rollback = handle_request(
            &runtime,
            &session,
            request(
                "rollback",
                2,
                5,
                Request::CompleteCoreUpdate { accept: false },
            ),
        );
        assert!(rollback.error.is_none());
        assert!(!runtime.status().desired_running);
    }

    #[test]
    fn invalid_update_proxy_does_not_acquire_service_or_cancel_work() {
        let runtime = CoreRuntime::queued_for_test();
        let session = session();
        let invalid = handle_request(
            &runtime,
            &session,
            request(
                "prepare",
                1,
                1,
                Request::PrepareCoreUpdate {
                    proxy_port: Some(0),
                },
            ),
        );
        assert_eq!(invalid.error.unwrap().code, "invalid_arguments");
        assert!(session.lock().unwrap().owner.is_none());
        assert!(runtime.status().operation.is_none());
    }

    #[test]
    fn admission_logs_correlate_accepted_and_rejected_requests_without_config() {
        let runtime = CoreRuntime::queued_for_test();
        let session = session();
        let mut nonce = [0u8; 12];
        getrandom::getrandom(&mut nonce).unwrap();
        let logs = std::env::temp_dir().join(format!("service-origin-{}", hex::encode(nonce)));
        session.lock().unwrap().diagnostic_directory = logs.clone();
        assert!(
            handle_request(&runtime, &session, request("stop", 2, 20, Request::Stop))
                .error
                .is_none()
        );
        let stale = handle_request(
            &runtime,
            &session,
            request(
                "stale-start",
                1,
                19,
                Request::Start {
                    config: "secret: must-not-be-logged".into(),
                },
            ),
        );
        assert_eq!(stale.error.unwrap().code, "operation_cancelled");
        assert!(crate::diagnostics::flush(Duration::from_secs(2)));
        let contents = fs::read_to_string(logs.join("recovery.log")).unwrap();
        assert!(!contents.contains("must-not-be-logged"));
        let events: Vec<serde_json::Value> = contents
            .lines()
            .map(|line| serde_json::from_str(line).unwrap())
            .collect();
        let admissions: Vec<_> = events
            .iter()
            .filter(|event| event["event"] == "service_request_finished")
            .collect();
        assert_eq!(admissions.len(), 2);
        assert_eq!(admissions[0]["data"]["admission"], "accepted");
        assert_eq!(admissions[0]["data"]["operationPhase"], "queued");
        assert_eq!(admissions[0]["context"]["serviceRequestId"], "stop");
        assert_eq!(
            admissions[0]["context"]["operationId"],
            "00000000000000000000000000000001:stop"
        );
        assert_eq!(admissions[1]["data"]["admission"], "rejected");
        assert_eq!(admissions[1]["data"]["code"], "operation_cancelled");
        assert_eq!(admissions[1]["context"]["serviceRequestId"], "stale-start");
        assert_eq!(admissions[1]["context"]["intentRevision"], 1);
        assert_eq!(admissions[1]["context"]["requestSequence"], 19);
        assert_eq!(admissions[1]["context"]["ownerPid"], std::process::id());
        fs::remove_dir_all(logs).unwrap();
    }

    #[test]
    fn empty_runtime_stop_records_ordering_before_a_delayed_bulk_start() {
        let runtime = CoreRuntime::queued_for_test();
        let session = session();
        let stop = handle_request(&runtime, &session, request("stop", 2, 20, Request::Stop));
        assert!(stop.error.is_none());
        let stale = handle_request(
            &runtime,
            &session,
            request(
                "delayed-start",
                1,
                19,
                Request::Start {
                    config: "mixed-port: 7890".into(),
                },
            ),
        );
        assert_eq!(stale.error.unwrap().code, "operation_cancelled");
        assert!(!runtime.status().desired_running);
        // Highwater survives owner retirement rather than reverting to zero.
        let mut owner = session.lock().unwrap();
        let id = owner.id.take().unwrap();
        let order = (id, owner.intent_revision, owner.request_sequence);
        owner.retired.push_back(order);
        owner.owner = None;
        owner.intent_revision = 0;
        owner.request_sequence = 0;
        drop(owner);
        let stale = handle_request(
            &runtime,
            &session,
            request(
                "delayed-after-retire",
                1,
                19,
                Request::Start {
                    config: "mixed-port: 7890".into(),
                },
            ),
        );
        assert_eq!(stale.error.unwrap().code, "operation_cancelled");
        assert!(!runtime.status().desired_running);
    }

    #[test]
    fn shutdown_gate_rejects_a_frame_that_finished_after_scm_stop() {
        let runtime = CoreRuntime::queued_for_test();
        let session = session();
        let delayed = request(
            "late",
            99,
            99,
            Request::Start {
                config: "mixed-port: 7890".into(),
            },
        );
        session.lock().unwrap().shutting_down = true;
        let reply = handle_request(&runtime, &session, delayed);
        assert_eq!(reply.error.unwrap().code, "service_stopping");
        assert!(!runtime.status().desired_running);
        assert!(runtime.status().operation.is_none());
    }

    #[test]
    fn repeated_lease_cleanup_uses_a_new_operation_after_same_session_reacquires() {
        let runtime = CoreRuntime::queued_for_test();
        let session = session();
        handle_request(
            &runtime,
            &session,
            request(
                "start1",
                1,
                1,
                Request::Start {
                    config: "mixed-port: 7890".into(),
                },
            ),
        );
        begin_owner_cleanup(
            &runtime,
            &mut session.lock().unwrap(),
            StopReason::LeaseExpired,
        )
        .unwrap();
        session.lock().unwrap().closing = false;
        handle_request(
            &runtime,
            &session,
            request(
                "start2",
                1,
                2,
                Request::Start {
                    config: "mixed-port: 7890".into(),
                },
            ),
        );
        assert!(runtime.status().desired_running);
        begin_owner_cleanup(
            &runtime,
            &mut session.lock().unwrap(),
            StopReason::LeaseExpired,
        )
        .unwrap();
        assert!(!runtime.status().desired_running);
        assert_eq!(session.lock().unwrap().cleanup_sequence, 2);
        let id = "00000000000000000000000000000001";
        assert_eq!(
            runtime
                .operation(&format!("owner-cleanup-{id}-1"))
                .unwrap()
                .phase,
            "cancelled"
        );
        assert_eq!(
            runtime
                .operation(&format!("owner-cleanup-{id}-2"))
                .unwrap()
                .phase,
            "queued"
        );
    }

    #[test]
    fn delayed_start_cannot_override_stop_but_accepted_id_remains_queryable() {
        let runtime = CoreRuntime::queued_for_test();
        let session = session();
        let start = handle_request(
            &runtime,
            &session,
            request(
                "start",
                1,
                10,
                Request::Start {
                    config: "mixed-port: 7890".into(),
                },
            ),
        );
        assert!(start.error.is_none());
        let stop = handle_request(&runtime, &session, request("stop", 2, 11, Request::Stop));
        assert!(stop.error.is_none());
        let stale = handle_request(
            &runtime,
            &session,
            request(
                "late-start",
                1,
                12,
                Request::Start {
                    config: "mixed-port: 7890".into(),
                },
            ),
        );
        assert_eq!(stale.error.unwrap().code, "operation_cancelled");
        assert!(!runtime.status().desired_running);
        let replay = handle_request(
            &runtime,
            &session,
            request(
                "start",
                1,
                10,
                Request::Start {
                    config: "mixed-port: 7890".into(),
                },
            ),
        );
        assert_eq!(replay.operation.unwrap().phase, "cancelled");
        assert!(!runtime.status().desired_running);
        let older_step = handle_request(
            &runtime,
            &session,
            request(
                "old-step",
                2,
                10,
                Request::Start {
                    config: "mixed-port: 7890".into(),
                },
            ),
        );
        assert_eq!(older_step.error.unwrap().code, "operation_cancelled");
        let later_start = handle_request(
            &runtime,
            &session,
            request(
                "new-start",
                3,
                13,
                Request::Start {
                    config: "mixed-port: 7890".into(),
                },
            ),
        );
        assert!(later_start.error.is_none());
        assert!(runtime.status().desired_running);
    }

    #[test]
    fn status_is_not_a_heartbeat_and_same_pid_cannot_change_owner_session() {
        let runtime = CoreRuntime::queued_for_test();
        let session = session();
        handle_request(
            &runtime,
            &session,
            request(
                "start",
                1,
                1,
                Request::Start {
                    config: "mixed-port: 7890".into(),
                },
            ),
        );
        thread::sleep(Duration::from_millis(30));
        let age = session.lock().unwrap().lease.elapsed().unwrap();
        let before = runtime.status().status_sequence;
        for number in 0..5 {
            let reply = handle_request(
                &runtime,
                &session,
                request(&format!("status{number}"), 0, 2 + number, Request::Status),
            );
            assert!(reply.error.is_none());
        }
        assert!(session.lock().unwrap().lease.elapsed().unwrap() >= age);
        assert_eq!(runtime.status().status_sequence, before);
        let mut foreign = request("foreign", 2, 99, Request::Stop);
        foreign.envelope.owner_session = "00000000000000000000000000000002".into();
        let reply = handle_request(&runtime, &session, foreign);
        assert_eq!(reply.error.unwrap().code, "service_owner_busy");
        assert!(runtime.status().desired_running);
    }
}
