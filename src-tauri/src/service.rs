use crate::{
    config,
    core::CoreSupervisor,
    error::{AppError, Result},
    service_ipc::{Reply, Request, Server},
    windows,
};
use serde::Serialize;
use std::{
    ffi::OsString,
    fs,
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc,
    },
    thread,
    time::{Duration, Instant},
};
use windows_service::{
    service::{
        Service, ServiceAccess, ServiceControl, ServiceControlAccept, ServiceErrorControl,
        ServiceExitCode, ServiceInfo, ServiceStartType, ServiceState, ServiceStatus, ServiceType,
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
        Some(windows::protected_install_dir()?)
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
                ServiceControlAccept::STOP | ServiceControlAccept::SHUTDOWN
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
    let handle = service_control_handler::register(SERVICE_NAME, move |control| match control {
        ServiceControl::Stop | ServiceControl::Shutdown => {
            stop_handler.store(true, Ordering::SeqCst);
            ServiceControlHandlerResult::NoError
        }
        ServiceControl::Interrogate => ServiceControlHandlerResult::NoError,
        _ => ServiceControlHandlerResult::NotImplemented,
    })
    .map_err(service_error)?;
    let result = (|| {
        let directory = windows::protected_install_dir()?;
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
        let server = Server::new(owner.into(), directory.join("mihomo-optimized.exe"))?;
        let mut core = CoreSupervisor::new(
            directory.join("mihomo-core.exe"),
            data.join("runtime"),
            data.join("logs"),
        );
        let mut last_seen = Instant::now();
        report_status(&handle, ServiceState::Running)?;
        while !stopping.load(Ordering::SeqCst) {
            if let Some(request) = server.receive()? {
                let reply = match request {
                    Request::Start { config } => core.start(config, true),
                    Request::Stop => core.stop().map(|_| core.status()),
                    Request::Reload { config } => core.reload(config),
                    Request::Status => core.poll(),
                };
                last_seen = Instant::now();
                let _ = server.respond(Reply::from_result(reply));
            }
            // A vanished or crashed UI must not leave privileged routing running forever.
            if last_seen.elapsed() > Duration::from_secs(20) {
                core.stop()?;
            }
            let _ = core.poll();
            thread::sleep(Duration::from_millis(25));
        }
        report_status(&handle, ServiceState::StopPending)?;
        core.stop()?;
        Ok(())
    })();
    let final_status = report_status(&handle, ServiceState::Stopped);
    result.and(final_status)
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
