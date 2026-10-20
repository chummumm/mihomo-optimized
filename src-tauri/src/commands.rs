use crate::{
    config::{ConfigDocument, MAX_CONFIG_SIZE},
    desktop::{DesktopManager, DesktopStatus, HostState, PublishedDesktopStatus},
    error::{AppError, Result},
    windows,
};
use std::{io::Read, sync::TryLockError};
use tauri::State;

async fn with_manager<T, F>(state: State<'_, HostState>, operation: F) -> Result<T>
where
    T: Send + 'static,
    F: FnOnce(&mut DesktopManager) -> Result<T> + Send + 'static,
{
    let state = state.inner().clone();
    tauri::async_runtime::spawn_blocking(move || {
        let mut manager = state
            .0
            .lock()
            .map_err(|_| AppError::new("state_unavailable", "桌面状态不可用，请重新启动程序"))?;
        operation(&mut manager)
    })
    .await
    .map_err(|_| AppError::new("task_failed", "桌面操作未能完成"))?
}

#[tauri::command]
pub async fn desktop_status(
    state: State<'_, HostState>,
    published: State<'_, PublishedDesktopStatus>,
) -> Result<DesktopStatus> {
    let state = state.inner().clone();
    let published = published.inner().clone();
    tauri::async_runtime::spawn_blocking(move || {
        // Refresh OS-backed switches while idle, but never queue a status read
        // behind resource preparation or another long-running core operation.
        let result = match state.0.try_lock() {
            Ok(manager) => Ok(manager.status()),
            Err(TryLockError::WouldBlock) => Ok(published.read()),
            Err(TryLockError::Poisoned(_)) => Err(AppError::new(
                "state_unavailable",
                "桌面状态不可用，请重新启动程序",
            )),
        };
        result
    })
    .await
    .map_err(|_| AppError::new("task_failed", "桌面操作未能完成"))?
}
#[tauri::command]
pub async fn desktop_start(state: State<'_, HostState>) -> Result<DesktopStatus> {
    with_manager(state, |manager| {
        manager.start(true)?;
        Ok(manager.status())
    })
    .await
}
#[tauri::command]
pub async fn desktop_stop(state: State<'_, HostState>) -> Result<DesktopStatus> {
    with_manager(state, |manager| {
        manager.stop(true)?;
        Ok(manager.status())
    })
    .await
}
#[tauri::command]
pub async fn desktop_restart(state: State<'_, HostState>) -> Result<DesktopStatus> {
    with_manager(state, |manager| {
        manager.restart()?;
        Ok(manager.status())
    })
    .await
}
#[tauri::command]
pub async fn desktop_set_system_proxy(
    state: State<'_, HostState>,
    enabled: bool,
) -> Result<DesktopStatus> {
    with_manager(state, move |manager| {
        manager.set_system_proxy(enabled)?;
        Ok(manager.status())
    })
    .await
}
#[tauri::command]
pub async fn desktop_set_tun(state: State<'_, HostState>, enabled: bool) -> Result<DesktopStatus> {
    with_manager(state, move |manager| {
        manager.set_tun(enabled)?;
        Ok(manager.status())
    })
    .await
}
#[tauri::command]
pub async fn desktop_set_autostart(
    state: State<'_, HostState>,
    enabled: bool,
) -> Result<DesktopStatus> {
    with_manager(state, move |manager| {
        windows::set_autostart(enabled)?;
        manager.publish();
        Ok(manager.status())
    })
    .await
}
#[tauri::command]
pub async fn desktop_install_service(state: State<'_, HostState>) -> Result<DesktopStatus> {
    with_manager(state, |manager| {
        manager.install_service()?;
        Ok(manager.status())
    })
    .await
}
#[tauri::command]
pub async fn desktop_uninstall_service(state: State<'_, HostState>) -> Result<DesktopStatus> {
    with_manager(state, |manager| {
        manager.uninstall_service()?;
        Ok(manager.status())
    })
    .await
}
#[tauri::command]
pub async fn desktop_read_config(state: State<'_, HostState>) -> Result<ConfigDocument> {
    with_manager(state, |manager| manager.read_config()).await
}
#[tauri::command]
pub async fn desktop_save_config(
    state: State<'_, HostState>,
    content: String,
    expected_revision: String,
) -> Result<ConfigDocument> {
    with_manager(state, move |manager| {
        manager.save_config(&content, Some(&expected_revision))
    })
    .await
}
#[tauri::command]
pub async fn desktop_import_config(
    state: State<'_, HostState>,
    content: String,
    expected_revision: Option<String>,
) -> Result<ConfigDocument> {
    with_manager(state, move |manager| {
        manager.import_config(&content, expected_revision.as_deref())
    })
    .await
}
#[tauri::command]
pub async fn desktop_reload_config(state: State<'_, HostState>, force: bool) -> Result<()> {
    with_manager(state, move |manager| manager.reload_config(force)).await
}
#[tauri::command]
pub async fn desktop_patch_config(
    state: State<'_, HostState>,
    patch: serde_json::Map<String, serde_json::Value>,
) -> Result<()> {
    with_manager(state, move |manager| manager.patch_config(patch)).await
}
#[tauri::command]
pub async fn desktop_set_dirty(state: State<'_, HostState>, dirty: bool) -> Result<()> {
    with_manager(state, move |manager| {
        manager.dirty = dirty;
        Ok(())
    })
    .await
}
#[tauri::command]
pub async fn desktop_open_directory(state: State<'_, HostState>, kind: String) -> Result<()> {
    with_manager(state, move |manager| manager.open_directory(&kind)).await
}

#[tauri::command]
pub async fn desktop_open_external(url: String) -> Result<()> {
    let parsed =
        reqwest::Url::parse(&url).map_err(|_| AppError::new("invalid_url", "无效的网址"))?;
    if !["http", "https"].contains(&parsed.scheme())
        || parsed.host_str().is_none()
        || !parsed.username().is_empty()
        || parsed.password().is_some()
    {
        return Err(AppError::new(
            "invalid_url",
            "只允许打开普通 HTTP 或 HTTPS 网页",
        ));
    }
    tauri::async_runtime::spawn_blocking(move || windows::shell_open(parsed.as_str()))
        .await
        .map_err(|_| AppError::new("open_failed", "无法打开网页"))?
}

#[tauri::command]
pub async fn desktop_import_config_url(
    state: State<'_, HostState>,
    url: String,
    expected_revision: Option<String>,
) -> Result<ConfigDocument> {
    let host = state.inner().clone();
    let (proxy, captured_revision) = with_manager(state, |manager| {
        Ok((manager.update_proxy_url(), manager.read_config()?.revision))
    })
    .await?;
    let content = tauri::async_runtime::spawn_blocking(move || -> Result<String> {
        let parsed =
            reqwest::Url::parse(&url).map_err(|_| AppError::new("invalid_url", "订阅地址无效"))?;
        if !["http", "https"].contains(&parsed.scheme())
            || parsed.host_str().is_none()
            || !parsed.username().is_empty()
            || parsed.password().is_some()
        {
            return Err(AppError::new(
                "invalid_url",
                "订阅必须使用 HTTP 或 HTTPS 地址",
            ));
        }
        let mut builder = reqwest::blocking::Client::builder()
            .no_proxy()
            .timeout(std::time::Duration::from_secs(30))
            .redirect(reqwest::redirect::Policy::limited(5));
        if let Some(proxy) = proxy {
            builder = builder.proxy(
                reqwest::Proxy::all(proxy)
                    .map_err(|_| AppError::new("proxy_invalid", "本地代理不可用"))?,
            );
        }
        let client = builder
            .build()
            .map_err(|_| AppError::new("download_failed", "无法初始化订阅下载"))?;
        let response = client
            .get(parsed)
            .header("User-Agent", "clash.meta/MihomoOptimizedDesktop")
            .send()
            .map_err(|_| AppError::new("download_failed", "订阅下载失败，请检查地址和网络"))?;
        if !response.status().is_success() {
            return Err(AppError::new(
                "download_failed",
                format!("订阅服务器返回 HTTP {}", response.status().as_u16()),
            ));
        }
        if response
            .content_length()
            .is_some_and(|length| length > MAX_CONFIG_SIZE as u64)
        {
            return Err(AppError::new("config_too_large", "订阅不能超过 4 MiB"));
        }
        let mut bytes = Vec::new();
        response
            .take((MAX_CONFIG_SIZE + 1) as u64)
            .read_to_end(&mut bytes)
            .map_err(|_| AppError::new("download_failed", "订阅下载不完整"))?;
        if bytes.len() > MAX_CONFIG_SIZE {
            return Err(AppError::new("config_too_large", "订阅不能超过 4 MiB"));
        }
        String::from_utf8(bytes)
            .map_err(|_| AppError::new("config_invalid", "订阅必须返回 UTF-8 YAML"))
    })
    .await
    .map_err(|_| AppError::new("download_failed", "订阅下载操作被中断"))??;
    tauri::async_runtime::spawn_blocking(move || {
        let mut manager = host
            .0
            .lock()
            .map_err(|_| AppError::new("state_unavailable", "桌面状态不可用"))?;
        manager.import_config(
            &content,
            Some(expected_revision.as_deref().unwrap_or(&captured_revision)),
        )
    })
    .await
    .map_err(|_| AppError::new("task_failed", "无法保存订阅"))?
}

#[tauri::command]
pub async fn desktop_exit(
    state: State<'_, HostState>,
    app: tauri::AppHandle,
    force: Option<bool>,
) -> Result<()> {
    let should_exit = with_manager(state, move |manager| {
        manager.request_exit(force.unwrap_or(false))
    })
    .await?;
    if should_exit {
        app.exit(0);
    }
    Ok(())
}
