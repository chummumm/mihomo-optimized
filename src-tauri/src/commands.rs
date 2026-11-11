//! Commands retained across the Tauri-to-native migration. Serialization is at
//! this boundary; the manager and service continue to enforce their invariants.
use crate::{
    config::MAX_CONFIG_SIZE,
    desktop::{DesktopManager, HostState},
    error::{AppError, Result},
    host::HostContext,
    windows,
};
use serde::{de::DeserializeOwned, Deserialize, Serialize};
use serde_json::{Map, Value};
use std::sync::TryLockError;

fn decode<T: DeserializeOwned>(params: Map<String, Value>) -> Result<T> {
    serde_json::from_value(Value::Object(params))
        .map_err(|_| AppError::new("invalid_arguments", "桌面操作参数无效"))
}

fn encode<T: Serialize>(value: T) -> Result<Value> {
    serde_json::to_value(value)
        .map_err(|_| AppError::new("response_invalid", "无法编码桌面操作结果"))
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Empty {}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Enabled {
    enabled: bool,
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Dirty {
    dirty: bool,
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Force {
    #[serde(default)]
    force: bool,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Save {
    content: String,
    expected_revision: String,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Import {
    content: String,
    expected_revision: Option<String>,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ImportUrl {
    url: String,
    expected_revision: Option<String>,
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Patch {
    patch: Map<String, Value>,
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Directory {
    kind: String,
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct ExternalUrl {
    url: String,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct CoreUpdateRequest {
    request_id: String,
}

/// Admission and status never wait for downloads or the lifecycle worker.
pub fn core_update_control(
    context: &HostContext,
    method: &str,
    params: Map<String, Value>,
) -> Result<Value> {
    match method {
        "desktop_core_update_status" => {
            decode::<Empty>(params)?;
            encode(context.core_updates.snapshot())
        }
        "desktop_update_core" => {
            let request: CoreUpdateRequest = decode(params)?;
            encode(
                context
                    .core_updates
                    .request(&request.request_id, context.generation())?,
            )
        }
        _ => Err(AppError::new("unknown_method", "不支持的内核更新操作")),
    }
}

/// Validate lifecycle arguments before an accepted request can cancel a worker.
pub fn validate_admission(method: &str, params: &Map<String, Value>) -> Result<()> {
    match method {
        "desktop_start"
        | "desktop_stop"
        | "desktop_restart"
        | "desktop_install_update"
        | "desktop_uninstall_service" => {
            decode::<Empty>(params.clone())?;
        }
        "desktop_set_tun" | "desktop_set_system_proxy" => {
            decode::<Enabled>(params.clone())?;
        }
        "desktop_reload_config" | "desktop_exit" => {
            decode::<Force>(params.clone())?;
        }
        "desktop_patch_config" => {
            decode::<Patch>(params.clone())?;
        }
        _ => (),
    }
    Ok(())
}

struct BoundState<'a> {
    state: &'a HostState,
    generation: u64,
}

fn with_manager<T>(
    state: &BoundState<'_>,
    operation: impl FnOnce(&mut DesktopManager) -> Result<T>,
) -> Result<T> {
    let mut manager = state
        .state
        .0
        .lock()
        .map_err(|_| AppError::new("state_unavailable", "桌面状态不可用，请重新启动程序"))?;
    // Binding and execution share the same manager lock. A background poll can
    // never upgrade an old command to a newer accepted user's revision.
    manager.begin_operation(state.generation);
    manager.operation_cancellation().check()?;
    operation(&mut manager)
}

pub fn status(
    state: &HostState,
    context: &HostContext,
    params: Map<String, Value>,
) -> Result<Value> {
    decode::<Empty>(params)?;
    // External service and login-startup changes must be visible while idle.
    // Never wait behind resource preparation or another long core operation.
    match state.0.try_lock() {
        Ok(manager) => encode(manager.status()),
        Err(TryLockError::WouldBlock) => encode(context.status()?),
        Err(TryLockError::Poisoned(_)) => Err(AppError::new(
            "state_unavailable",
            "桌面状态不可用，请重新启动程序",
        )),
    }
}

fn core_action(
    state: &BoundState<'_>,
    params: Map<String, Value>,
    action: impl FnOnce(&mut DesktopManager) -> Result<()>,
) -> Result<Value> {
    decode::<Empty>(params)?;
    with_manager(state, |manager| {
        action(manager)?;
        encode(manager.status())
    })
}

pub fn dispatch(
    state: &HostState,
    context: &HostContext,
    method: &str,
    params: Map<String, Value>,
    generation: u64,
) -> Result<Value> {
    let state = &BoundState { state, generation };
    let cancellation =
        crate::lifecycle::Cancellation::new(context.runtime_generation.clone(), generation);
    match method {
        "desktop_start" => core_action(state, params, |manager| manager.start(true)),
        "desktop_stop" => core_action(state, params, |manager| manager.stop(true)),
        "desktop_restart" => core_action(state, params, DesktopManager::restart),
        "desktop_install_service" => {
            core_action(state, params, |manager| manager.install_service())
        }
        "desktop_uninstall_service" => {
            core_action(state, params, DesktopManager::uninstall_service)
        }
        "desktop_set_system_proxy" | "desktop_set_tun" | "desktop_set_autostart" => {
            let args: Enabled = decode(params)?;
            with_manager(state, |manager| {
                match method {
                    "desktop_set_system_proxy" => manager.set_system_proxy(args.enabled)?,
                    "desktop_set_tun" => manager.set_tun(args.enabled)?,
                    _ => {
                        windows::set_autostart(args.enabled)?;
                        manager.publish();
                    }
                }
                encode(manager.status())
            })
        }
        "desktop_read_config" => {
            decode::<Empty>(params)?;
            with_manager(state, |manager| encode(manager.read_config()?))
        }
        "desktop_save_config" => {
            let args: Save = decode(params)?;
            with_manager(state, |manager| {
                encode(manager.save_config(&args.content, Some(&args.expected_revision))?)
            })
        }
        "desktop_import_config" => {
            let args: Import = decode(params)?;
            with_manager(state, |manager| {
                encode(manager.import_config(&args.content, args.expected_revision.as_deref())?)
            })
        }
        "desktop_import_config_url" => import_url(state, decode(params)?),
        "desktop_reload_config" => {
            let args: Force = decode(params)?;
            with_manager(state, |manager| manager.reload_config(args.force))?;
            Ok(Value::Null)
        }
        "desktop_patch_config" => {
            let args: Patch = decode(params)?;
            with_manager(state, |manager| manager.patch_config(args.patch))?;
            Ok(Value::Null)
        }
        "desktop_set_dirty" => {
            let args: Dirty = decode(params)?;
            with_manager(state, |manager| {
                manager.dirty = args.dirty;
                Ok(())
            })?;
            Ok(Value::Null)
        }
        "desktop_open_directory" => {
            let args: Directory = decode(params)?;
            with_manager(state, |manager| manager.open_directory(&args.kind))?;
            Ok(Value::Null)
        }
        "desktop_open_external" => {
            let args: ExternalUrl = decode(params)?;
            let parsed = ordinary_http_url(
                &args.url,
                "invalid_url",
                "只允许打开普通 HTTP 或 HTTPS 网页",
            )?;
            windows::shell_open(parsed.as_str())?;
            Ok(Value::Null)
        }
        "desktop_exit" => {
            let args: Force = decode(params)?;
            let exiting = with_manager(state, |manager| manager.request_exit(args.force))?;
            Ok(serde_json::json!({"exiting":exiting}))
        }
        "desktop_check_update" => {
            decode::<Empty>(params)?;
            encode(crate::update::desktop_check_update(
                context,
                state.state,
                &cancellation,
            )?)
        }
        "desktop_install_update" => {
            decode::<Empty>(params)?;
            crate::update::desktop_install_update(context, state.state, &cancellation)?;
            Ok(serde_json::json!({"exiting":true}))
        }
        _ => Err(AppError::new("unknown_method", "不支持的桌面操作")),
    }
}

fn ordinary_http_url(input: &str, code: &str, message: &str) -> Result<reqwest::Url> {
    let parsed = reqwest::Url::parse(input).map_err(|_| AppError::new(code, message))?;
    if !["http", "https"].contains(&parsed.scheme())
        || parsed.host_str().is_none()
        || !parsed.username().is_empty()
        || parsed.password().is_some()
    {
        return Err(AppError::new(code, message));
    }
    Ok(parsed)
}

fn import_url(state: &BoundState<'_>, args: ImportUrl) -> Result<Value> {
    let (proxy, captured_revision, cancellation) = with_manager(state, |manager| {
        Ok((
            manager.update_proxy_url(),
            manager.read_config()?.revision,
            manager.operation_cancellation(),
        ))
    })?;
    cancellation.check()?;
    let parsed = ordinary_http_url(&args.url, "invalid_url", "订阅必须使用 HTTP 或 HTTPS 地址")?;
    let deadline = std::time::Instant::now() + std::time::Duration::from_secs(30);
    let bytes = crate::core::wait_cancellable(Some(&cancellation), deadline, async {
        let mut builder = reqwest::Client::builder()
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
        let mut response = client
            .get(parsed)
            .header("User-Agent", "clash.meta/MihomoOptimizedDesktop")
            .send()
            .await
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
        while let Some(chunk) = response
            .chunk()
            .await
            .map_err(|_| AppError::new("download_failed", "订阅下载不完整"))?
        {
            if bytes.len().saturating_add(chunk.len()) > MAX_CONFIG_SIZE {
                return Err(AppError::new("config_too_large", "订阅不能超过 4 MiB"));
            }
            bytes.extend_from_slice(&chunk);
        }
        Ok(bytes)
    })?;
    let content = String::from_utf8(bytes)
        .map_err(|_| AppError::new("config_invalid", "订阅必须返回 UTF-8 YAML"))?;
    with_manager(state, |manager| {
        cancellation.check()?;
        encode(
            manager.import_config(
                &content,
                Some(
                    args.expected_revision
                        .as_deref()
                        .unwrap_or(&captured_revision),
                ),
            )?,
        )
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn command_arguments_reject_silent_misspellings_and_wrong_types() {
        assert!(decode::<Enabled>(
            serde_json::from_value(serde_json::json!({"enabled":"true"})).unwrap()
        )
        .is_err());
        assert!(decode::<Save>(
            serde_json::from_value(
                serde_json::json!({"content":"rules: []", "expected_revision":"old"})
            )
            .unwrap()
        )
        .is_err());
        assert!(decode::<Empty>(
            serde_json::from_value(serde_json::json!({"force":true})).unwrap()
        )
        .is_err());
        let args: Save = decode(
            serde_json::from_value(
                serde_json::json!({"content":"rules: []", "expectedRevision":"old"}),
            )
            .unwrap(),
        )
        .unwrap();
        assert_eq!(args.expected_revision, "old");
    }

    #[test]
    fn external_links_cannot_launch_file_shell_or_embedded_credentials() {
        for url in [
            "file:///C:/Windows/notepad.exe",
            "javascript:alert(1)",
            "https://user:password@example.com/",
            "shell:AppsFolder",
        ] {
            assert!(ordinary_http_url(url, "invalid_url", "invalid").is_err());
        }
        assert!(ordinary_http_url("https://example.com/help", "invalid_url", "invalid").is_ok());
    }
}
