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
        ServiceAccess, ServiceControl, ServiceControlAccept, ServiceErrorControl, ServiceExitCode,
        ServiceInfo, ServiceStartType, ServiceState, ServiceStatus, ServiceType,
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
    let directory = windows::protected_install_dir()?;
    let manager = ServiceManager::local_computer(None::<&str>, ServiceManagerAccess::CONNECT)
        .map_err(service_error)?;
    let service = match manager.open_service(
        SERVICE_NAME,
        ServiceAccess::STOP | ServiceAccess::START | ServiceAccess::QUERY_STATUS,
    ) {
        Ok(service) => service,
        Err(windows_service::Error::Winapi(error)) if error.raw_os_error() == Some(1060) => {
            return Ok(())
        }
        Err(error) => return Err(service_error(error)),
    };
    if running {
        windows::configure_tun_firewall(&directory, true)?;
    }
    let current = service.query_status().map_err(service_error)?.current_state;
    if running && current == ServiceState::Stopped {
        service.start::<&str>(&[]).map_err(service_error)?;
    }
    if !running && current != ServiceState::Stopped {
        service.stop().map_err(service_error)?;
    }
    let expected = if running {
        ServiceState::Running
    } else {
        ServiceState::Stopped
    };
    let started = Instant::now();
    while service.query_status().map_err(service_error)?.current_state != expected {
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

fn uninstall() -> Result<()> {
    let directory = windows::protected_install_dir()?;
    let manager = ServiceManager::local_computer(None::<&str>, ServiceManagerAccess::CONNECT)
        .map_err(service_error)?;
    let service = match manager.open_service(
        SERVICE_NAME,
        ServiceAccess::STOP | ServiceAccess::DELETE | ServiceAccess::QUERY_STATUS,
    ) {
        Ok(service) => Some(service),
        Err(windows_service::Error::Winapi(error)) if error.raw_os_error() == Some(1060) => None,
        Err(error) => return Err(service_error(error)),
    };
    if let Some(service) = &service {
        if service.query_status().map_err(service_error)?.current_state != ServiceState::Stopped {
            service.stop().map_err(service_error)?;
            let started = Instant::now();
            while service.query_status().map_err(service_error)?.current_state
                != ServiceState::Stopped
            {
                if started.elapsed() > Duration::from_secs(40) {
                    return Err(AppError::new(
                        "service_stop_timeout",
                        "服务尚未停止，请稍后重试卸载",
                    ));
                }
                thread::sleep(Duration::from_millis(100));
            }
        }
    }
    // A previous partial uninstall may have already removed the SCM entry.
    // Always remove this installation's firewall rules before clearing ownership.
    windows::configure_tun_firewall(&directory, false)?;
    if let Some(service) = service {
        service.delete().map_err(service_error)?;
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
