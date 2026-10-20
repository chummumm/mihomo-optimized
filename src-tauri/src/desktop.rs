use crate::{
    config::{self, ConfigDocument, Preferences},
    core::{self, Controller, CoreStatus, CoreSupervisor},
    error::{AppError, Result},
    proxy::SystemProxy,
    service::{self, ServiceStatusView},
    service_ipc::{Client as ServiceClient, Request},
    windows,
};
use serde::Serialize;
use std::{
    path::PathBuf,
    sync::{Arc, Mutex},
    thread,
    time::Duration,
};
use tauri::{Emitter, Manager};

#[derive(Clone)]
pub struct HostState(pub Arc<Mutex<DesktopManager>>);

#[derive(Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Phase {
    Stopped,
    Starting,
    Running,
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
}

/// Startup can hold the operation mutex while downloading resources. Keep the
/// last published view independently readable, including for a WebView that
/// subscribes after the initial Starting event. This lock only copies a view;
/// it is never held across core, service, registry or event operations.
#[derive(Clone)]
pub struct PublishedDesktopStatus(Arc<Mutex<DesktopStatus>>);

impl PublishedDesktopStatus {
    fn new(status: DesktopStatus) -> Self {
        Self(Arc::new(Mutex::new(status)))
    }

    pub fn read(&self) -> DesktopStatus {
        self.0
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .clone()
    }

    fn replace(&self, status: DesktopStatus) {
        *self
            .0
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner()) = status;
    }
}

enum RunningCore {
    Direct(CoreSupervisor),
    Service(ServiceClient),
}

pub struct DesktopManager {
    app: tauri::AppHandle,
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
}

impl DesktopManager {
    fn new(app: tauri::AppHandle) -> Result<Self> {
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
        })
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
        }
    }

    pub fn publish(&self) {
        let status = self.status();
        self.app
            .state::<PublishedDesktopStatus>()
            .replace(status.clone());
        let _ = self.app.emit("desktop-status", status);
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

    fn poll(&mut self) {
        if self.updating || self.exiting {
            return;
        }
        let result = match self.running.as_mut() {
            Some(RunningCore::Direct(core)) => core.poll(),
            Some(RunningCore::Service(client)) => client.call(&Request::Status),
            None => return,
        };
        match result {
            Ok(status) => {
                // A failed service query preserves the last process status, but
                // withdraws the controller and restores our system proxy. The
                // next confirmed Running observation must also restore the user's
                // preference when that last process status was already running.
                let recovered = self.phase != Phase::Running && status.running;
                self.phase = if status.running {
                    Phase::Running
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
                self.phase = Phase::Error;
                self.last_error = Some(error.message);
                self.core_status.controller = None;
                let _ = self.proxy.restore();
            }
        }
        self.publish();
    }

    pub fn request_exit(&mut self, force: bool) -> Result<bool> {
        if self.dirty && !force {
            show_main(&self.app);
            let _ = self.app.emit("desktop-exit-requested", ());
            return Ok(false);
        }
        self.stop(false)?;
        self.exiting = true;
        Ok(true)
    }
}

fn stop_running(running: &mut RunningCore) -> Result<()> {
    match running {
        RunningCore::Direct(core) => core.stop(),
        RunningCore::Service(client) => client.call(&Request::Stop).map(|_| ()),
    }
}

pub fn show_main(app: &tauri::AppHandle) {
    if let Some(window) = app.get_webview_window("main") {
        let _ = window.unminimize();
        let _ = window.show();
        let _ = window.set_focus();
        let _ = app.emit("desktop-visibility", serde_json::json!({"visible":true}));
    }
}

fn tray_exit(app: &tauri::AppHandle) {
    let app = app.clone();
    thread::spawn(move || {
        let state = app.state::<HostState>();
        let should_exit = if let Ok(mut manager) = state.0.lock() {
            match manager.request_exit(false) {
                Ok(value) => value,
                Err(error) => {
                    manager.last_error = Some(error.message);
                    manager.publish();
                    show_main(&app);
                    false
                }
            }
        } else {
            false
        };
        if should_exit {
            app.exit(0);
        }
    });
}

pub fn run() {
    let builder = tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, _, _| {
            show_main(app)
        }))
        .plugin(
            tauri::plugin::Builder::<tauri::Wry>::new("local-navigation")
                .on_navigation(|_, url| {
                    (url.scheme() == "tauri" && url.host_str() == Some("localhost"))
                        || (["http", "https"].contains(&url.scheme())
                            && url.host_str() == Some("tauri.localhost")
                            && url.port().is_none())
                        || (cfg!(debug_assertions)
                            && url.scheme() == "http"
                            && url.host_str() == Some("localhost")
                            && url.port() == Some(3000))
                })
                .build(),
        )
        .invoke_handler(tauri::generate_handler![
            crate::commands::desktop_status,
            crate::commands::desktop_start,
            crate::commands::desktop_stop,
            crate::commands::desktop_restart,
            crate::commands::desktop_set_system_proxy,
            crate::commands::desktop_set_tun,
            crate::commands::desktop_set_autostart,
            crate::commands::desktop_read_config,
            crate::commands::desktop_save_config,
            crate::commands::desktop_import_config,
            crate::commands::desktop_import_config_url,
            crate::commands::desktop_reload_config,
            crate::commands::desktop_patch_config,
            crate::commands::desktop_set_dirty,
            crate::commands::desktop_open_directory,
            crate::commands::desktop_open_external,
            crate::commands::desktop_install_service,
            crate::commands::desktop_uninstall_service,
            crate::commands::desktop_exit,
            crate::update::desktop_check_update,
            crate::update::desktop_install_update
        ])
        .setup(|app| {
            let mut manager = DesktopManager::new(app.handle().clone())?;
            let should_start = manager.preferences.desired_running;
            if should_start {
                manager.phase = Phase::Starting;
                manager.last_error = None;
            }
            app.manage(PublishedDesktopStatus::new(manager.status()));
            let state = HostState(Arc::new(Mutex::new(manager)));
            app.manage(state.clone());
            let show =
                tauri::menu::MenuItem::with_id(app, "show", "打开主界面", true, None::<&str>)?;
            let stop = tauri::menu::MenuItem::with_id(
                app,
                "stop",
                "停止代理并恢复网络",
                true,
                None::<&str>,
            )?;
            let quit = tauri::menu::MenuItem::with_id(app, "quit", "退出", true, None::<&str>)?;
            let menu = tauri::menu::Menu::with_items(app, &[&show, &stop, &quit])?;
            let mut tray = tauri::tray::TrayIconBuilder::with_id("main-tray")
                .tooltip("Mihomo Optimized")
                .menu(&menu)
                .show_menu_on_left_click(false)
                .on_tray_icon_event(|tray, event| {
                    if matches!(
                        event,
                        tauri::tray::TrayIconEvent::Click {
                            button: tauri::tray::MouseButton::Left,
                            button_state: tauri::tray::MouseButtonState::Up,
                            ..
                        }
                    ) {
                        show_main(tray.app_handle());
                    }
                })
                .on_menu_event(|app, event| match event.id().as_ref() {
                    "show" => show_main(app),
                    "quit" => tray_exit(app),
                    "stop" => {
                        let app = app.clone();
                        thread::spawn(move || {
                            let state = app.state::<HostState>();
                            if let Ok(mut manager) = state.0.lock() {
                                if let Err(error) = manager.stop(true) {
                                    manager.last_error = Some(error.message);
                                    manager.publish();
                                    show_main(&app);
                                }
                            };
                        });
                    }
                    _ => {}
                });
            if let Some(icon) = app.default_window_icon() {
                tray = tray.icon(icon.clone());
            }
            tray.build(app)?;
            if !std::env::args().any(|argument| argument == "--tray") {
                show_main(app.handle());
            }
            thread::spawn(move || {
                if should_start {
                    if let Ok(mut manager) = state.0.lock() {
                        let _ = manager.start(false);
                    }
                }
                if let Some(message) = crate::update::take_install_result() {
                    if let Ok(mut manager) = state.0.lock() {
                        manager.last_error = Some(message);
                        manager.publish();
                    }
                }
                loop {
                    thread::sleep(Duration::from_secs(2));
                    if let Ok(mut manager) = state.0.try_lock() {
                        if manager.exiting {
                            break;
                        }
                        manager.poll();
                    }
                }
            });
            Ok(())
        })
        .on_window_event(|window, event| match event {
            tauri::WindowEvent::CloseRequested { api, .. } => {
                api.prevent_close();
                let _ = window.hide();
                let _ = window
                    .app_handle()
                    .emit("desktop-visibility", serde_json::json!({"visible":false}));
            }
            tauri::WindowEvent::Focused(_) | tauri::WindowEvent::Resized(_) => {
                let visible =
                    window.is_visible().unwrap_or(false) && !window.is_minimized().unwrap_or(false);
                let _ = window
                    .app_handle()
                    .emit("desktop-visibility", serde_json::json!({"visible":visible}));
            }
            _ => {}
        });
    match builder.build(tauri::generate_context!()) {
        Ok(app) => app.run(|handle, event| {
            if let tauri::RunEvent::ExitRequested { api, .. } = event {
                let state = handle.state::<HostState>();
                let exiting = state
                    .0
                    .lock()
                    .map(|manager| manager.exiting)
                    .unwrap_or(false);
                if !exiting {
                    api.prevent_exit();
                    tray_exit(handle);
                }
            }
        }),
        Err(error) => {
            use windows_sys::Win32::UI::WindowsAndMessaging::{MessageBoxW, MB_ICONERROR, MB_OK};
            let message = windows::wide(format!("桌面程序无法启动：{error}"));
            let title = windows::wide("Mihomo Optimized");
            unsafe {
                MessageBoxW(
                    std::ptr::null_mut(),
                    message.as_ptr(),
                    title.as_ptr(),
                    MB_OK | MB_ICONERROR,
                );
            }
        }
    }
}
