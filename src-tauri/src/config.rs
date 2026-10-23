use crate::error::{AppError, Result};
use serde::{Deserialize, Serialize};
use serde_yaml::{Mapping, Value};
use sha2::{Digest, Sha256};
use std::{
    fs,
    io::{Read, Write},
    path::{Path, PathBuf},
    time::Duration,
};

pub const MAX_CONFIG_SIZE: usize = 4 * 1024 * 1024;
pub const DEFAULT_RESOURCE_TIMEOUT: Duration = Duration::from_secs(180);
// Go's time.Duration is a signed count of nanoseconds, including on 32-bit builds.
const MAX_RESOURCE_TIMEOUT_SECONDS: u64 = 9_223_372_036;
pub const INITIAL_CONFIG: &str = "# 导入你的 Clash / Mihomo YAML 配置后启动。\n# 本地端口、系统代理和 TUN 可以在桌面设置中管理。\nmixed-port: 7890\nallow-lan: false\nmode: rule\nlog-level: info\nipv6: false\ndns:\n  enable: true\n  nameserver:\n    - 1.1.1.1\n    - 8.8.8.8\nproxies: []\nproxy-groups: []\nrules:\n  - MATCH,DIRECT\n";

#[derive(Clone, Debug, Serialize)]
pub struct ConfigDocument {
    pub path: String,
    pub content: String,
    pub revision: String,
    pub max_size: usize,
    pub writable: bool,
}

#[derive(Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Preferences {
    pub desired_running: bool,
    pub system_proxy: bool,
    pub tun: bool,
}

pub fn revision(content: &[u8]) -> String {
    hex::encode(Sha256::digest(content))
}

pub fn parse(content: &str) -> Result<Mapping> {
    if content.len() > MAX_CONFIG_SIZE {
        return Err(AppError::new("config_too_large", "配置不能超过 4 MiB"));
    }
    if content.trim().is_empty() {
        return Err(AppError::new("config_invalid", "配置不能为空"));
    }
    let value: Value = serde_yaml::from_str(content).map_err(|error| {
        // Parser diagnostics can contain subscription secrets or a complete source line.
        let position = error
            .location()
            .map(|loc| format!("（第 {} 行，第 {} 列）", loc.line(), loc.column()))
            .unwrap_or_default();
        AppError::new("config_invalid", format!("YAML 格式无效{position}"))
    })?;
    match value {
        Value::Mapping(mapping) => Ok(mapping),
        _ => Err(AppError::new(
            "config_invalid",
            "配置根节点必须是 YAML 对象",
        )),
    }
}

pub fn resource_bootstrap_timeout(content: &str) -> Result<Duration> {
    let document = parse(content)?;
    let Some(bootstrap) = document.get("resource-bootstrap") else {
        return Ok(DEFAULT_RESOURCE_TIMEOUT);
    };
    if bootstrap.is_null() {
        return Ok(DEFAULT_RESOURCE_TIMEOUT);
    }
    let invalid = || {
        AppError::new(
            "config_invalid",
            "resource-bootstrap.timeout 必须是正整数秒，且不超过 9223372036",
        )
    };
    let bootstrap = bootstrap.as_mapping().ok_or_else(invalid)?;
    let Some(timeout) = bootstrap.get("timeout") else {
        return Ok(DEFAULT_RESOURCE_TIMEOUT);
    };
    let seconds = timeout
        .as_u64()
        .filter(|seconds| *seconds > 0 && *seconds <= MAX_RESOURCE_TIMEOUT_SECONDS)
        .ok_or_else(invalid)?;
    Ok(Duration::from_secs(seconds))
}

pub fn read(path: &Path) -> Result<ConfigDocument> {
    let content = read_limited(path)?;
    let writable = fs::metadata(path)
        .map(|m| !m.permissions().readonly())
        .unwrap_or(false);
    Ok(ConfigDocument {
        path: path.display().to_string(),
        revision: revision(content.as_bytes()),
        content,
        max_size: MAX_CONFIG_SIZE,
        writable,
    })
}

pub fn read_limited(path: &Path) -> Result<String> {
    let mut file = fs::File::open(path).map_err(|e| {
        if e.kind() == std::io::ErrorKind::NotFound {
            AppError::new("config_missing", "尚未创建本地配置")
        } else {
            AppError::io("无法读取配置", e)
        }
    })?;
    let mut bytes = Vec::new();
    Read::by_ref(&mut file)
        .take((MAX_CONFIG_SIZE + 1) as u64)
        .read_to_end(&mut bytes)
        .map_err(|e| AppError::io("无法读取配置", e))?;
    if bytes.len() > MAX_CONFIG_SIZE {
        return Err(AppError::new("config_too_large", "配置不能超过 4 MiB"));
    }
    String::from_utf8(bytes).map_err(|_| AppError::new("config_invalid", "配置必须使用 UTF-8 编码"))
}

pub fn save(path: &Path, content: &str, expected: Option<&str>) -> Result<ConfigDocument> {
    parse(content)?;
    if let Some(expected) = expected {
        let current = read_limited(path)?;
        if revision(current.as_bytes()) != expected {
            return Err(AppError::new(
                "config_conflict",
                "磁盘配置已被修改，请重新读取后再保存",
            ));
        }
    }
    if fs::metadata(path).is_ok_and(|m| m.permissions().readonly()) {
        return Err(AppError::new("config_read_only", "配置文件是只读的"));
    }
    atomic_write(path, content.as_bytes())?;
    read(path)
}

/// Import preserves the previous local document before replacement. A stale editor
/// revision cannot replace either the active configuration or its existing backup.
pub fn import(path: &Path, content: &str, expected: Option<&str>) -> Result<ConfigDocument> {
    parse(content)?;
    let previous = read_limited(path)?;
    let current_revision = revision(previous.as_bytes());
    if expected.is_some_and(|expected| expected != current_revision) {
        return Err(AppError::new(
            "config_conflict",
            "磁盘配置已被修改，请重新读取后再导入",
        ));
    }
    if fs::metadata(path).is_ok_and(|m| m.permissions().readonly()) {
        return Err(AppError::new("config_read_only", "配置文件是只读的"));
    }
    atomic_write(&path.with_extension("yaml.bak"), previous.as_bytes())?;
    save(path, content, Some(&current_revision))
}

pub fn atomic_write(path: &Path, bytes: &[u8]) -> Result<()> {
    let parent = path
        .parent()
        .ok_or_else(|| AppError::new("invalid_path", "缺少配置目录"))?;
    fs::create_dir_all(parent).map_err(|e| AppError::io("无法创建配置目录", e))?;
    let mut nonce = [0_u8; 12];
    getrandom::getrandom(&mut nonce)
        .map_err(|_| AppError::new("random_failed", "系统随机数不可用"))?;
    let temporary = parent.join(format!(".write-{}", hex::encode(nonce)));
    let result = (|| {
        let mut file = fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&temporary)
            .map_err(|e| AppError::io("无法创建临时配置", e))?;
        file.write_all(bytes)
            .and_then(|_| file.sync_all())
            .map_err(|e| AppError::io("无法保存配置", e))?;
        drop(file);
        fs::rename(&temporary, path).map_err(|e| AppError::io("无法替换配置文件", e))?;
        Ok(())
    })();
    if result.is_err() {
        let _ = fs::remove_file(temporary);
    }
    result
}

pub fn load_preferences(directory: &Path) -> Result<Preferences> {
    let path = directory.join("preferences.json");
    match fs::read(&path) {
        Ok(bytes) => serde_json::from_slice(&bytes).map_err(|_| {
            AppError::new(
                "preferences_invalid",
                "桌面设置文件损坏，请在配置目录中检查 preferences.json",
            )
        }),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(Preferences::default()),
        Err(error) => Err(AppError::io("无法读取桌面设置", error)),
    }
}

pub fn save_preferences(directory: &Path, preferences: &Preferences) -> Result<()> {
    let bytes = serde_json::to_vec_pretty(preferences)
        .map_err(|_| AppError::new("serialize_failed", "无法序列化桌面设置"))?;
    atomic_write(&directory.join("preferences.json"), &bytes)
}

fn key(value: &str) -> Value {
    Value::String(value.into())
}

pub fn runtime_config(content: &str, tun: bool, port: u16) -> Result<String> {
    let mut document = parse(content)?;
    if tun
        && document
            .get("dns")
            .and_then(|value| value.get("enable"))
            .and_then(Value::as_bool)
            != Some(true)
    {
        return Err(AppError::new("tun_dns_required", "TUN 需要启用内核 DNS。请在配置中设置 dns.enable: true 和所需的 nameserver，再启用 TUN；现有 DNS 策略不会被自动改写"));
    }
    for field in [
        "external-controller-unix",
        "external-controller-pipe",
        "external-controller-tls",
        "external-ui",
        "external-ui-url",
        "external-ui-name",
        "secret",
        "tls",
    ] {
        document.remove(key(field));
    }
    document.insert(
        key("external-controller"),
        key(&format!("127.0.0.1:{port}")),
    );
    // The WPF client uses authenticated native HTTP; no browser origin needs
    // access to this private, ephemeral controller.
    document.insert(
        key("external-controller-cors"),
        serde_yaml::to_value(
            serde_json::json!({"allow-origins": [], "allow-private-network": false}),
        )
        .map_err(|_| AppError::new("config_invalid", "无法设置本地控制接口"))?,
    );
    let tun_config = document
        .entry(key("tun"))
        .or_insert(Value::Mapping(Mapping::new()));
    if !tun_config.is_mapping() {
        *tun_config = Value::Mapping(Mapping::new());
    }
    let tun_mapping = tun_config.as_mapping_mut().expect("mapping created above");
    tun_mapping.insert(key("enable"), Value::Bool(tun));
    if tun {
        tun_mapping.insert(key("auto-route"), Value::Bool(true));
        tun_mapping.insert(key("auto-detect-interface"), Value::Bool(true));
        tun_mapping.entry(key("stack")).or_insert(key("mixed"));
        tun_mapping.entry(key("dns-hijack")).or_insert(
            serde_yaml::to_value(["any:53"])
                .map_err(|_| AppError::new("config_invalid", "无法设置 TUN DNS"))?,
        );
    }
    serde_yaml::to_string(&document)
        .map_err(|_| AppError::new("config_invalid", "无法生成运行配置"))
}

pub fn mixed_port(content: &str) -> Result<u16> {
    let document = parse(content)?;
    for field in ["mixed-port", "port"] {
        if let Some(port) = document
            .get(key(field))
            .and_then(Value::as_u64)
            .filter(|n| *n > 0 && *n <= 65535)
        {
            return Ok(port as u16);
        }
    }
    Err(AppError::new(
        "proxy_port_missing",
        "请在配置中设置有效的 mixed-port 或 HTTP port，再开启系统代理",
    ))
}

pub fn patch(
    path: &Path,
    updates: serde_json::Map<String, serde_json::Value>,
) -> Result<ConfigDocument> {
    let document = read(path)?;
    let mut parsed = parse(&document.content)?;
    for (name, value) in &updates {
        let valid = match name.as_str() {
            "port" | "socks-port" | "mixed-port" => value.as_u64().is_some_and(|n| n <= 65535),
            "mode" => value
                .as_str()
                .is_some_and(|v| ["rule", "global", "direct"].contains(&v)),
            "allow-lan" => value.is_boolean(),
            "log-level" => value
                .as_str()
                .is_some_and(|v| ["silent", "error", "warning", "info", "debug"].contains(&v)),
            _ => false,
        };
        if !valid {
            return Err(AppError::new("config_invalid", "不支持的设置值"));
        }
        parsed.insert(
            key(name),
            serde_yaml::to_value(value)
                .map_err(|_| AppError::new("config_invalid", "无法生成设置"))?,
        );
    }
    // Preserve comments and formatting when conventional unquoted root keys are used.
    // Unusual YAML spellings fall back to serialization so the semantic change is exact.
    let mut content = document.content.clone();
    let mut preserve = true;
    for (name, value) in &updates {
        let replacement = format!("{name}: {value}");
        let mut found = 0;
        let lines: Vec<String> = content
            .lines()
            .map(|line| {
                if line.starts_with(&format!("{name}:")) {
                    found += 1;
                    replacement.clone()
                } else {
                    line.to_string()
                }
            })
            .collect();
        content = lines.join("\n") + "\n";
        if found == 0 {
            content.push_str(&format!("{replacement}\n"));
        }
        if found > 1 {
            preserve = false;
        }
    }
    if !preserve || parse(&content).ok().as_ref() != Some(&parsed) {
        content = serde_yaml::to_string(&parsed)
            .map_err(|_| AppError::new("config_invalid", "无法生成设置"))?;
    }
    save(path, &content, Some(&document.revision))
}

pub fn data_paths(directory: &Path) -> (PathBuf, PathBuf) {
    (directory.join("config.yaml"), directory.join("logs"))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn resource_budget_uses_positive_integer_seconds_without_overflow() {
        for content in [
            "mode: rule",
            "resource-bootstrap: {}",
            "resource-bootstrap: null",
        ] {
            assert_eq!(
                resource_bootstrap_timeout(content).unwrap(),
                DEFAULT_RESOURCE_TIMEOUT
            );
        }
        assert_eq!(
            resource_bootstrap_timeout("resource-bootstrap:\n  timeout: 1").unwrap(),
            Duration::from_secs(1)
        );
        assert_eq!(
            resource_bootstrap_timeout("resource-bootstrap:\n  timeout: 9223372036").unwrap(),
            Duration::from_secs(9_223_372_036)
        );
        for value in ["0", "-1", "1.5", "true", "null", "'180'", "9223372037"] {
            let content = format!("resource-bootstrap:\n  timeout: {value}");
            assert_eq!(
                resource_bootstrap_timeout(&content).unwrap_err().code,
                "config_invalid",
                "{value}"
            );
        }
        assert!(resource_bootstrap_timeout("resource-bootstrap: []").is_err());
    }
    #[test]
    fn invalid_yaml_does_not_echo_credentials() {
        let error = parse("password: [very-secret-value").unwrap_err();
        assert_eq!(error.code, "config_invalid");
        assert!(!error.message.contains("very-secret-value"));
    }
    #[test]
    fn runtime_has_no_user_controller_or_secret() {
        let value = runtime_config("mixed-port: 7890\nsecret: original-secret\nexternal-controller: 0.0.0.0:9090\nexternal-ui-url: https://example.com\ntun:\n  enable: true\n", false, 43210).unwrap();
        let map = parse(&value).unwrap();
        assert!(!map.contains_key(key("secret")));
        assert!(!map.contains_key(key("external-ui-url")));
        assert_eq!(
            map.get(key("external-controller")),
            Some(&key("127.0.0.1:43210"))
        );
        assert_eq!(
            map.get(key("tun"))
                .and_then(|v| v.get("enable"))
                .and_then(Value::as_bool),
            Some(false)
        );
    }
    #[test]
    fn root_sequence_and_oversized_source_are_rejected() {
        assert_eq!(parse("- item").unwrap_err().code, "config_invalid");
        assert_eq!(
            parse(&"x".repeat(MAX_CONFIG_SIZE + 1)).unwrap_err().code,
            "config_too_large"
        );
    }
    #[test]
    fn tun_rejects_disabled_dns_without_changing_user_policy() {
        let source = "mixed-port: 7890\ndns:\n  enable: false\n";
        assert_eq!(
            runtime_config(source, true, 9090).unwrap_err().code,
            "tun_dns_required"
        );
        assert!(runtime_config(source, false, 9090).is_ok());
        assert!(runtime_config(INITIAL_CONFIG, true, 9090).is_ok());
    }
    #[test]
    fn overwrite_is_atomic_and_stale_revision_is_rejected() {
        let mut random = [0_u8; 12];
        getrandom::getrandom(&mut random).unwrap();
        let directory =
            std::env::temp_dir().join(format!("mihomo-config-test-{}", hex::encode(random)));
        fs::create_dir(&directory).unwrap();
        let path = directory.join("config.yaml");
        atomic_write(&path, INITIAL_CONFIG.as_bytes()).unwrap();
        let original = read(&path).unwrap();
        let replacement = "mixed-port: 8888\nmode: direct\n";
        let saved = save(&path, replacement, Some(&original.revision)).unwrap();
        assert_eq!(saved.content, replacement);
        assert_ne!(saved.revision, original.revision);
        let error = save(&path, "mode: global\n", Some(&original.revision)).unwrap_err();
        assert_eq!(error.code, "config_conflict");
        assert_eq!(read(&path).unwrap().content, replacement);
        let imported = import(&path, INITIAL_CONFIG, Some(&saved.revision)).unwrap();
        assert_eq!(
            fs::read_to_string(path.with_extension("yaml.bak")).unwrap(),
            replacement
        );
        let error = import(&path, "mode: global\n", Some(&saved.revision)).unwrap_err();
        assert_eq!(error.code, "config_conflict");
        assert_eq!(read(&path).unwrap().revision, imported.revision);
        assert_eq!(
            fs::read_to_string(path.with_extension("yaml.bak")).unwrap(),
            replacement
        );
        fs::remove_dir_all(directory).unwrap();
    }
}
