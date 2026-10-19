//! Desktop releases are verified against GitHub's OIDC/Sigstore build identity.
//! No signing key or user GitHub token is shipped with the application.
use crate::{
    core::quiet_command,
    desktop::HostState,
    error::{AppError, Result},
};
use reqwest::{blocking::Client, Url};
use semver::Version;
use serde::{Deserialize, Serialize};
use std::{
    fs,
    io::{BufRead, Read, Write},
    path::{Path, PathBuf},
    process::Stdio,
    sync::{
        atomic::{AtomicBool, Ordering},
        Mutex, OnceLock,
    },
    thread,
    time::{Duration, Instant},
};
use tauri::{AppHandle, Emitter, Manager, State};

const REPOSITORY: &str = "chummumm/mihomo-optimized";
const REPOSITORY_ID: &str = "1410715865";
const TAG_PREFIX: &str = "mihomo-desktop-v";
const WORKFLOW: &str = "chummumm/mihomo-optimized/.github/workflows/windows-desktop.yml";
const SOURCE_REF: &str = "refs/heads/clash-dashboard";
const CHECK_TIMEOUT: Duration = Duration::from_secs(30);
const MAX_INSTALLER: u64 = 512 * 1024 * 1024;
const MAX_BUNDLE: u64 = 4 * 1024 * 1024;
const UNINSTALL_KEY: &str = r"Software\Microsoft\Windows\CurrentVersion\Uninstall\Mihomo Optimized";
static BUSY: AtomicBool = AtomicBool::new(false);
static CANDIDATE: OnceLock<Mutex<Option<Candidate>>> = OnceLock::new();

struct Operation;
impl Operation {
    fn acquire() -> Result<Self> {
        BUSY.compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
            .map_err(|_| AppError::new("update_busy", "已有更新操作正在进行"))?;
        Ok(Self)
    }
}
impl Drop for Operation {
    fn drop(&mut self) {
        BUSY.store(false, Ordering::Release);
    }
}

#[derive(Clone, Serialize, Deserialize)]
struct Asset {
    name: String,
    browser_download_url: String,
    size: u64,
}
#[derive(Deserialize)]
struct Release {
    tag_name: String,
    draft: bool,
    prerelease: bool,
    published_at: Option<String>,
    body: Option<String>,
    assets: Vec<Asset>,
}
#[derive(Clone, Serialize, Deserialize)]
struct Candidate {
    version: String,
    source_sha: String,
    installer: Asset,
    bundle: Asset,
}

#[derive(Serialize, Deserialize)]
struct MonitorPlan {
    parent_pid: u32,
    application: PathBuf,
    candidate: Candidate,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct UpdateInfo {
    current_version: String,
    available: bool,
    can_install: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    release_url: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    version: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    published_at: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    body: Option<String>,
}

/// The GUI first checks the protected executable layout. The monitor runs from a
/// staging copy, so it supplies the original, process-identity-checked application
/// path instead. Both paths must match the same administrator-owned NSIS record.
fn installed_update_dir(original_application: Option<&Path>) -> Result<PathBuf> {
    use winreg::enums::{HKEY_LOCAL_MACHINE, KEY_READ, KEY_WOW64_64KEY};
    let unavailable = || {
        AppError::new(
            "update_installation_required",
            "此程序路径没有匹配的 Windows 安装记录，请从可信发布页下载完整安装包并完成安装后再更新",
        )
    };
    let directory = if let Some(application) = original_application {
        crate::windows::reject_reparse_path(application)?;
        crate::windows::require_admin_protected(application)?;
        let directory = application.parent().ok_or_else(unavailable)?;
        crate::windows::require_admin_protected(directory)?;
        directory.canonicalize().map_err(|_| unavailable())?
    } else {
        crate::windows::protected_install_dir()?
    };
    // Pinned tauri-cli-v2.12.3 NSIS: perMachine selects HKLM, x64 selects the
    // 64-bit view, and InstallLocation is written as a quoted "$INSTDIR" string.
    let key = winreg::RegKey::predef(HKEY_LOCAL_MACHINE)
        .open_subkey_with_flags(UNINSTALL_KEY, KEY_READ | KEY_WOW64_64KEY)
        .map_err(|_| unavailable())?;
    let location: String = key
        .get_value("InstallLocation")
        .map_err(|_| unavailable())?;
    let binary: String = key.get_value("MainBinaryName").map_err(|_| unavailable())?;
    if binary != "mihomo-optimized.exe" {
        return Err(unavailable());
    }
    let location = location.trim();
    let unquoted = location
        .strip_prefix('"')
        .and_then(|value| value.strip_suffix('"'))
        .unwrap_or(location);
    let registered = Path::new(unquoted);
    if !registered.is_absolute() {
        return Err(unavailable());
    }
    crate::windows::reject_reparse_path(registered)?;
    if registered.canonicalize().map_err(|_| unavailable())? != directory {
        return Err(unavailable());
    }
    Ok(directory)
}

fn emit(app: &AppHandle, stage: &str, downloaded: Option<u64>, total: Option<u64>, message: &str) {
    let _ = app.emit(
        "desktop-update-progress",
        serde_json::json!({
            "stage": stage, "downloaded": downloaded, "total": total, "message": message
        }),
    );
}

fn allowed_host(url: &Url) -> bool {
    url.scheme() == "https"
        && url.username().is_empty()
        && url.password().is_none()
        && url.port_or_known_default() == Some(443)
        && matches!(
            url.host_str(),
            Some(
                "api.github.com"
                    | "github.com"
                    | "release-assets.githubusercontent.com"
                    | "objects.githubusercontent.com"
            )
        )
}

fn client(proxy: Option<String>) -> Result<Client> {
    let mut builder = Client::builder()
        .no_proxy()
        .user_agent(concat!("mihomo-desktop/", env!("CARGO_PKG_VERSION")))
        .connect_timeout(Duration::from_secs(15))
        .timeout(Duration::from_secs(600))
        .redirect(reqwest::redirect::Policy::custom(|attempt| {
            if attempt.previous().len() < 5 && allowed_host(attempt.url()) {
                attempt.follow()
            } else {
                attempt.error("unexpected download redirect")
            }
        }));
    if let Some(proxy) = proxy {
        builder = builder.proxy(
            reqwest::Proxy::all(proxy)
                .map_err(|_| AppError::new("update_proxy_invalid", "本地更新代理不可用"))?,
        );
    }
    builder
        .build()
        .map_err(|_| AppError::new("update_network_failed", "无法初始化更新连接"))
}

fn remaining_check_time(deadline: Instant) -> Result<Duration> {
    deadline
        .checked_duration_since(Instant::now())
        .filter(|remaining| !remaining.is_zero())
        .ok_or_else(|| {
            AppError::new(
                "update_check_failed",
                "检查桌面更新超时，请检查网络或稍后重试",
            )
        })
}

fn api(client: &Client, path: &str, deadline: Instant) -> Result<serde_json::Value> {
    let remaining = remaining_check_time(deadline)?;
    let response = client
        .get(format!("https://api.github.com/repos/{REPOSITORY}/{path}"))
        // Covers connection through the response body. Pages and tag lookups
        // share one deadline; installer downloads retain the client timeout.
        .timeout(remaining)
        .header("Accept", "application/vnd.github+json")
        .header("X-GitHub-Api-Version", "2022-11-28")
        .send()
        .and_then(reqwest::blocking::Response::error_for_status)
        .map_err(|_| {
            AppError::new(
                "update_check_failed",
                "无法读取 GitHub 更新信息，请检查网络或稍后重试",
            )
        })?;
    let mut bytes = Vec::new();
    response
        .take(8 * 1024 * 1024 + 1)
        .read_to_end(&mut bytes)
        .map_err(|_| AppError::new("update_check_failed", "更新信息下载中断"))?;
    if bytes.len() > 8 * 1024 * 1024 {
        return Err(AppError::new("update_metadata_invalid", "更新信息过大"));
    }
    serde_json::from_slice(&bytes)
        .map_err(|_| AppError::new("update_metadata_invalid", "更新信息格式无效"))
}

fn commit_for_tag(client: &Client, tag: &str, deadline: Instant) -> Result<String> {
    let mut value = api(client, &format!("git/ref/tags/{tag}"), deadline)?;
    for _ in 0..4 {
        let object = value
            .get("object")
            .ok_or_else(|| AppError::new("update_metadata_invalid", "更新标签缺少来源提交"))?;
        let sha = object
            .get("sha")
            .and_then(|v| v.as_str())
            .filter(|sha| sha.len() == 40 && sha.bytes().all(|b| b.is_ascii_hexdigit()))
            .ok_or_else(|| AppError::new("update_metadata_invalid", "更新来源提交无效"))?
            .to_owned();
        match object.get("type").and_then(|v| v.as_str()) {
            Some("commit") => return Ok(sha),
            Some("tag") => value = api(client, &format!("git/tags/{sha}"), deadline)?,
            _ => break,
        }
    }
    Err(AppError::new(
        "update_metadata_invalid",
        "无法解析更新来源提交",
    ))
}

fn asset(release: &Release, name: &str, maximum: u64) -> Result<Asset> {
    let matching: Vec<_> = release
        .assets
        .iter()
        .filter(|asset| asset.name == name)
        .collect();
    if matching.len() != 1 {
        return Err(AppError::new(
            "update_incomplete",
            "此版本缺少安装包或构建签名，请稍后重试",
        ));
    }
    let asset = matching[0];
    let expected = format!(
        "https://github.com/{REPOSITORY}/releases/download/{}/{name}",
        release.tag_name
    );
    if asset.browser_download_url != expected || asset.size == 0 || asset.size > maximum {
        return Err(AppError::new(
            "update_metadata_invalid",
            "更新文件地址或大小无效",
        ));
    }
    Ok(asset.clone())
}

fn check(app: &AppHandle, proxy: Option<String>) -> Result<UpdateInfo> {
    let deadline = Instant::now() + CHECK_TIMEOUT;
    *CANDIDATE
        .get_or_init(|| Mutex::new(None))
        .lock()
        .map_err(|_| AppError::new("update_state_failed", "更新状态不可用"))? = None;
    emit(app, "checking", None, None, "正在检查桌面版本");
    let client = client(proxy)?;
    let current = Version::parse(env!("CARGO_PKG_VERSION"))
        .map_err(|_| AppError::new("update_version_invalid", "当前版本号无效"))?;
    let mut releases = Vec::new();
    for page in 1..=3 {
        let value = api(
            &client,
            &format!("releases?per_page=100&page={page}"),
            deadline,
        )?;
        let page_releases: Vec<Release> = serde_json::from_value(value)
            .map_err(|_| AppError::new("update_metadata_invalid", "版本列表无效"))?;
        let finished = page_releases.len() < 100;
        releases.extend(page_releases);
        if finished {
            break;
        }
    }
    let latest = releases
        .iter()
        .filter(|release| !release.draft && !release.prerelease)
        .filter_map(|release| {
            release
                .tag_name
                .strip_prefix(TAG_PREFIX)
                .and_then(|s| Version::parse(s).ok())
                .map(|version| (version, release))
        })
        .filter(|(version, _)| {
            version.pre.is_empty() && version.build.is_empty() && *version > current
        })
        .max_by(|(left, _), (right, _)| left.cmp(right));
    let mut info = UpdateInfo {
        current_version: current.to_string(),
        available: false,
        can_install: installed_update_dir(None).is_ok(),
        release_url: None,
        version: None,
        published_at: None,
        body: None,
    };
    if let Some((version, release)) = latest {
        let name = format!("mihomo-desktop-{version}-windows-x64-setup.exe");
        let candidate = Candidate {
            version: version.to_string(),
            source_sha: commit_for_tag(&client, &release.tag_name, deadline)?,
            installer: asset(release, &name, MAX_INSTALLER)?,
            bundle: asset(release, &format!("{name}.sigstore.json"), MAX_BUNDLE)?,
        };
        info.available = true;
        info.version = Some(candidate.version.clone());
        info.release_url = Some(format!(
            "https://github.com/{REPOSITORY}/releases/tag/{TAG_PREFIX}{version}"
        ));
        info.published_at = release.published_at.clone();
        info.body = release
            .body
            .as_ref()
            .map(|body| body.chars().take(12000).collect());
        *CANDIDATE
            .get()
            .expect("initialized above")
            .lock()
            .map_err(|_| AppError::new("update_state_failed", "更新状态不可用"))? = Some(candidate);
    }
    emit(
        app,
        "ready",
        None,
        None,
        if info.available {
            "发现新的桌面版本"
        } else {
            "当前已是最新桌面版本"
        },
    );
    Ok(info)
}

fn download(
    app: &AppHandle,
    client: &Client,
    asset: &Asset,
    destination: &Path,
    show_progress: bool,
) -> Result<()> {
    let mut response = client
        .get(&asset.browser_download_url)
        .send()
        .and_then(reqwest::blocking::Response::error_for_status)
        .map_err(|_| AppError::new("update_download_failed", "更新文件下载失败"))?;
    if response
        .content_length()
        .is_some_and(|length| length != asset.size)
    {
        return Err(AppError::new(
            "update_size_mismatch",
            "更新文件大小与发布记录不一致",
        ));
    }
    let mut file = fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(destination)
        .map_err(|e| AppError::io("无法创建更新文件", e))?;
    let mut downloaded = 0_u64;
    let mut buffer = [0_u8; 64 * 1024];
    let mut last_emit = Instant::now();
    loop {
        let read = response
            .read(&mut buffer)
            .map_err(|_| AppError::new("update_download_failed", "更新下载中断，请重试"))?;
        if read == 0 {
            break;
        }
        downloaded += read as u64;
        if downloaded > asset.size {
            return Err(AppError::new(
                "update_size_mismatch",
                "更新文件超过发布记录大小",
            ));
        }
        file.write_all(&buffer[..read])
            .map_err(|e| AppError::io("无法写入更新文件", e))?;
        if show_progress && last_emit.elapsed() > Duration::from_millis(200) {
            emit(
                app,
                "downloading",
                Some(downloaded),
                Some(asset.size),
                "正在下载桌面更新",
            );
            last_emit = Instant::now();
        }
    }
    if downloaded != asset.size {
        return Err(AppError::new("update_size_mismatch", "更新文件不完整"));
    }
    file.sync_all()
        .map_err(|e| AppError::io("无法保存更新文件", e))?;
    Ok(())
}

fn verify(app: &AppHandle, directory: &Path, candidate: &Candidate) -> Result<()> {
    let executable = std::env::current_exe().map_err(|e| AppError::io("无法定位验证程序", e))?;
    let verifier = executable
        .parent()
        .ok_or_else(|| AppError::new("update_verifier_missing", "缺少更新验证程序"))?
        .join("mihomo-update-verify.exe");
    let trust = app
        .path()
        .resource_dir()
        .map_err(|_| AppError::new("update_verifier_missing", "无法定位验证证书"))?
        .join("resources/trusted-root.jsonl");
    verify_at(&verifier, &trust, directory, candidate)
}

fn verify_at(verifier: &Path, trust: &Path, directory: &Path, candidate: &Candidate) -> Result<()> {
    if !verifier.is_file() || !trust.is_file() {
        return Err(AppError::new(
            "update_verifier_missing",
            "安装缺少更新验证文件，请重新安装完整桌面版",
        ));
    }
    let result_path = directory.join("verification.json");
    let output = fs::File::create(&result_path).map_err(|e| AppError::io("无法记录验证结果", e))?;
    let mut command = quiet_command(&verifier);
    command
        .arg("attestation")
        .arg("verify")
        .arg(directory.join(&candidate.installer.name))
        .arg("--bundle")
        .arg(directory.join(&candidate.bundle.name))
        .arg("--repo")
        .arg(REPOSITORY)
        .arg("--signer-workflow")
        .arg(WORKFLOW)
        .arg("--source-ref")
        .arg(SOURCE_REF)
        .arg("--source-digest")
        .arg(&candidate.source_sha)
        .arg("--signer-digest")
        .arg(&candidate.source_sha)
        .arg("--cert-oidc-issuer")
        .arg("https://token.actions.githubusercontent.com")
        .arg("--deny-self-hosted-runners")
        .arg("--custom-trusted-root")
        .arg(trust)
        .arg("--format")
        .arg("json")
        .current_dir(directory)
        .env("GH_CONFIG_DIR", directory.join("gh-config"))
        .env("GH_PROMPT_DISABLED", "1")
        .env("GH_NO_UPDATE_NOTIFIER", "1")
        .stdin(Stdio::null())
        .stdout(Stdio::from(output))
        .stderr(Stdio::null());
    for variable in [
        "GH_TOKEN",
        "GITHUB_TOKEN",
        "GH_ENTERPRISE_TOKEN",
        "GITHUB_ENTERPRISE_TOKEN",
        "GH_HOST",
        "GH_DEBUG",
        "GH_HTTP_UNIX_SOCKET",
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "ALL_PROXY",
    ] {
        command.env_remove(variable);
    }
    let mut child = command
        .spawn()
        .map_err(|e| AppError::io("无法启动构建签名验证", e))?;
    let started = Instant::now();
    loop {
        match child
            .try_wait()
            .map_err(|e| AppError::io("无法读取验证结果", e))?
        {
            Some(status) if status.success() => {
                let mut bytes = Vec::new();
                fs::File::open(&result_path)
                    .map_err(|e| AppError::io("无法读取验证结果", e))?
                    .take(2 * 1024 * 1024 + 1)
                    .read_to_end(&mut bytes)
                    .map_err(|e| AppError::io("无法读取验证结果", e))?;
                let _ = fs::remove_file(&result_path);
                if bytes.len() > 2 * 1024 * 1024 || !verified_repository_id(&bytes) {
                    return Err(AppError::new(
                        "update_repository_mismatch",
                        "构建签名中的仓库身份不匹配，已拒绝安装",
                    ));
                }
                return Ok(());
            }
            Some(_) => {
                return Err(AppError::new(
                    "update_signature_invalid",
                    "更新文件未通过仓库、构建工作流和签名验证，已拒绝安装",
                ))
            }
            None if started.elapsed() > Duration::from_secs(45) => {
                let _ = child.kill();
                let _ = child.wait();
                return Err(AppError::new(
                    "update_verify_timeout",
                    "更新签名验证超时，未安装更新",
                ));
            }
            None => thread::sleep(Duration::from_millis(100)),
        }
    }
}

fn verified_repository_id(bytes: &[u8]) -> bool {
    serde_json::from_slice::<serde_json::Value>(bytes)
        .ok()
        .and_then(|value| value.as_array().cloned())
        .is_some_and(|results| {
            results.iter().any(|result| {
                result
                    .pointer("/verificationResult/signature/certificate/sourceRepositoryIdentifier")
                    .and_then(|id| id.as_str())
                    == Some(REPOSITORY_ID)
            })
        })
}

// The monitor executes from a private staging copy, so it never locks a file
// NSIS needs to replace. It survives the GUI and restarts it after either a
// successful installation or an installation failure.
fn handoff_to_monitor(directory: &Path, candidate: &Candidate) -> Result<()> {
    let application = std::env::current_exe().map_err(|e| AppError::io("无法定位桌面程序", e))?;
    let executable = directory.join("desktop-update-monitor.exe");
    fs::copy(&application, &executable).map_err(|e| AppError::io("无法准备更新恢复程序", e))?;
    let mut child = quiet_command(&executable)
        .arg("--desktop-update-monitor")
        .current_dir(directory)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .spawn()
        .map_err(|e| AppError::io("无法启动更新恢复程序", e))?;
    let plan = MonitorPlan {
        parent_pid: std::process::id(),
        application,
        candidate: candidate.clone(),
    };
    let input = serde_json::to_vec(&plan)
        .map_err(|_| AppError::new("update_monitor_failed", "无法生成更新恢复信息"))?;
    if let Some(mut stdin) = child.stdin.take() {
        stdin
            .write_all(&input)
            .map_err(|e| AppError::io("无法发送更新恢复信息", e))?;
    }
    let stdout = child
        .stdout
        .take()
        .ok_or_else(|| AppError::new("update_monitor_failed", "更新恢复程序未建立确认通道"))?;
    let mut response = String::new();
    std::io::BufReader::new(stdout)
        .take(16 * 1024)
        .read_line(&mut response)
        .map_err(|e| AppError::io("无法等待安装授权", e))?;
    let result: Result<()> = serde_json::from_str(&response)
        .map_err(|_| AppError::new("update_monitor_failed", "更新恢复程序没有确认安装状态"))?;
    if result.is_err() {
        let _ = child.wait();
    }
    result
}

fn launch_installer(installer: &Path) -> Result<std::os::windows::io::OwnedHandle> {
    use std::os::windows::io::FromRawHandle;
    use windows_sys::Win32::{
        Foundation::GetLastError,
        UI::Shell::{ShellExecuteExW, SEE_MASK_NOCLOSEPROCESS, SHELLEXECUTEINFOW},
    };
    let verb = crate::windows::wide("runas");
    let file = crate::windows::wide(installer);
    let arguments = crate::windows::wide("/P /UPDATE");
    unsafe {
        let mut info: SHELLEXECUTEINFOW = std::mem::zeroed();
        info.cbSize = std::mem::size_of_val(&info) as u32;
        info.fMask = SEE_MASK_NOCLOSEPROCESS;
        info.lpVerb = verb.as_ptr();
        info.lpFile = file.as_ptr();
        info.lpParameters = arguments.as_ptr();
        info.nShow = 1;
        if ShellExecuteExW(&mut info) == 0 {
            return Err(AppError::new(
                if GetLastError() == 1223 {
                    "update_cancelled"
                } else {
                    "update_launch_failed"
                },
                "安装授权未完成，已保留当前版本",
            ));
        }
        if info.hProcess.is_null() {
            return Err(AppError::new(
                "update_launch_failed",
                "安装程序未返回可监护的进程",
            ));
        }
        return Ok(std::os::windows::io::OwnedHandle::from_raw_handle(
            info.hProcess,
        ));
    }
}

/// Lock the actual files before the monitor's second verification. Read sharing
/// allows the verifier and Windows image loader to read them while denying any
/// concurrent writer or rename/delete. Directory handles also keep the pathname
/// components stable until the installer has exited.
fn lock_update_inputs(directory: &Path, candidate: &Candidate) -> Result<Vec<fs::File>> {
    use std::os::windows::fs::{MetadataExt, OpenOptionsExt};
    use windows_sys::Win32::Storage::FileSystem::{
        FILE_ATTRIBUTE_REPARSE_POINT, FILE_FLAG_BACKUP_SEMANTICS, FILE_FLAG_OPEN_REPARSE_POINT,
        FILE_SHARE_READ, FILE_SHARE_WRITE,
    };
    crate::windows::reject_reparse_path(directory)?;
    let mut handles = Vec::new();
    // Lock ancestors before children so a checked directory cannot subsequently
    // be redirected through a renamed parent or a directory junction.
    let ancestors: Vec<_> = directory.ancestors().collect();
    for path in ancestors.iter().rev() {
        let file = fs::OpenOptions::new()
            .read(true)
            .share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE)
            .custom_flags(FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT)
            .open(path)
            .map_err(|e| AppError::io("无法锁定更新目录", e))?;
        let metadata = file
            .metadata()
            .map_err(|e| AppError::io("无法检查更新目录", e))?;
        if !metadata.is_dir() || metadata.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0 {
            return Err(AppError::new(
                "update_unsafe_path",
                "更新目录不能使用符号链接或目录联接",
            ));
        }
        handles.push(file);
    }
    for name in [&candidate.installer.name, &candidate.bundle.name] {
        let path = directory.join(name);
        crate::windows::reject_reparse_path(&path)?;
        let file = fs::OpenOptions::new()
            .read(true)
            .share_mode(FILE_SHARE_READ)
            .custom_flags(FILE_FLAG_OPEN_REPARSE_POINT)
            .open(&path)
            .map_err(|e| AppError::io("无法锁定待验证的更新文件", e))?;
        let metadata = file
            .metadata()
            .map_err(|e| AppError::io("无法检查更新文件", e))?;
        if !metadata.is_file() || metadata.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0 {
            return Err(AppError::new(
                "update_unsafe_path",
                "更新文件不能使用符号链接",
            ));
        }
        crate::windows::reject_hard_links(&path)?;
        handles.push(file);
    }
    Ok(handles)
}

pub(crate) fn run_monitor_if_requested() -> bool {
    if std::env::args().nth(1).as_deref() != Some("--desktop-update-monitor") {
        return false;
    }
    let result = monitor();
    if let Err(error) = result {
        let _ = writeln!(
            std::io::stdout(),
            "{}",
            serde_json::to_string(&Err::<(), _>(error)).unwrap_or_default()
        );
        let _ = std::io::stdout().flush();
    }
    true
}

fn monitor() -> Result<()> {
    use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};
    use windows_sys::Win32::System::Threading::{
        GetExitCodeProcess, OpenProcess, WaitForSingleObject, PROCESS_QUERY_LIMITED_INFORMATION,
        PROCESS_SYNCHRONIZE,
    };
    let mut input = Vec::new();
    std::io::stdin()
        .take(16 * 1024 + 1)
        .read_to_end(&mut input)
        .map_err(|e| AppError::io("无法读取更新恢复信息", e))?;
    if input.len() > 16 * 1024 {
        return Err(AppError::new("update_monitor_invalid", "更新恢复信息过大"));
    }
    let plan: MonitorPlan = serde_json::from_slice(&input)
        .map_err(|_| AppError::new("update_monitor_invalid", "更新恢复信息无效"))?;
    let version = Version::parse(&plan.candidate.version)
        .map_err(|_| AppError::new("update_monitor_invalid", "更新版本无效"))?;
    let expected_installer = format!("mihomo-desktop-{version}-windows-x64-setup.exe");
    if !version.pre.is_empty()
        || !version.build.is_empty()
        || plan.candidate.installer.name != expected_installer
        || plan.candidate.bundle.name != format!("{expected_installer}.sigstore.json")
        || plan.candidate.source_sha.len() != 40
        || !plan
            .candidate
            .source_sha
            .bytes()
            .all(|b| b.is_ascii_hexdigit())
        || plan.application.file_name().and_then(|s| s.to_str()) != Some("mihomo-optimized.exe")
    {
        return Err(AppError::new("update_monitor_invalid", "更新目标无效"));
    }
    let parent = unsafe {
        let handle = OpenProcess(
            PROCESS_SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION,
            0,
            plan.parent_pid,
        );
        if handle.is_null() {
            return Err(AppError::new(
                "update_monitor_identity",
                "更新发起程序已经退出",
            ));
        }
        OwnedHandle::from_raw_handle(handle)
    };
    let (parent_path, parent_sid) = crate::windows::process_handle_identity(&parent)?;
    let expected_path = plan
        .application
        .canonicalize()
        .map_err(|_| AppError::new("update_monitor_identity", "更新发起程序路径无效"))?;
    let actual_path = parent_path
        .canonicalize()
        .map_err(|_| AppError::new("update_monitor_identity", "无法确认更新发起程序路径"))?;
    if parent_sid != crate::windows::current_sid()? || actual_path != expected_path {
        return Err(AppError::new(
            "update_monitor_identity",
            "无法验证更新发起程序",
        ));
    }
    let monitor_exe =
        std::env::current_exe().map_err(|e| AppError::io("无法定位更新恢复程序", e))?;
    let directory = monitor_exe
        .parent()
        .ok_or_else(|| AppError::new("update_monitor_invalid", "更新目录无效"))?;
    let application_directory = installed_update_dir(Some(&plan.application))?;
    let input_locks = lock_update_inputs(directory, &plan.candidate)?;
    verify_at(
        &application_directory.join("mihomo-update-verify.exe"),
        &application_directory.join("resources/trusted-root.jsonl"),
        directory,
        &plan.candidate,
    )?;
    let installer = launch_installer(&directory.join(expected_installer))?;
    let _ = writeln!(
        std::io::stdout(),
        "{}",
        serde_json::to_string(&Ok::<(), AppError>(())).unwrap_or_default()
    );
    let _ = std::io::stdout().flush();
    // UAC has succeeded. The parent now exits and NSIS can replace its files.
    if unsafe { WaitForSingleObject(parent.as_raw_handle(), u32::MAX) } != 0 {
        return Err(AppError::new(
            "update_monitor_wait_failed",
            "无法等待桌面程序退出，请检查安装进度",
        ));
    }
    if unsafe { WaitForSingleObject(installer.as_raw_handle(), u32::MAX) } != 0 {
        return Err(AppError::new(
            "update_monitor_wait_failed",
            "无法确认安装程序退出，请检查安装进度",
        ));
    }
    drop(input_locks);
    let mut exit_code = u32::MAX;
    unsafe {
        GetExitCodeProcess(installer.as_raw_handle(), &mut exit_code);
    }
    let outcome = serde_json::json!({"version":plan.candidate.version, "exitCode":exit_code, "success":exit_code == 0});
    let _ = crate::config::atomic_write(
        &crate::windows::user_data_dir()?.join("update-result.json"),
        outcome.to_string().as_bytes(),
    );
    // NSIS installation is not a transaction and may have replaced some files
    // before failing. Reopen the installed path if available to report the outcome;
    // this does not promise a complete rollback of the previous application files.
    quiet_command(&plan.application)
        .current_dir(&application_directory)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .map_err(|e| AppError::io("安装结束后无法重新启动桌面，请从开始菜单打开", e))?;
    // The current monitor is the only file still mapped from this directory.
    let _ = fs::remove_file(directory.join(&plan.candidate.installer.name));
    let _ = fs::remove_file(directory.join(&plan.candidate.bundle.name));
    Ok(())
}

pub(crate) fn take_install_result() -> Option<String> {
    let path = crate::windows::user_data_dir()
        .ok()?
        .join("update-result.json");
    let mut bytes = Vec::new();
    fs::File::open(&path)
        .ok()?
        .take(4096)
        .read_to_end(&mut bytes)
        .ok()?;
    let _ = fs::remove_file(path);
    let outcome: serde_json::Value = serde_json::from_slice(&bytes).ok()?;
    if outcome.get("success").and_then(|v| v.as_bool()) == Some(true) {
        None
    } else {
        Some(format!(
            "上次更新安装未完成（退出码 {}），已重新打开当前桌面程序。请重试更新或运行完整安装包。",
            outcome
                .get("exitCode")
                .and_then(|v| v.as_u64())
                .unwrap_or(u32::MAX as u64)
        ))
    }
}

#[tauri::command]
pub async fn desktop_check_update(
    app: AppHandle,
    state: State<'_, HostState>,
) -> Result<UpdateInfo> {
    let operation = Operation::acquire()?;
    let manager = state.0.clone();
    tauri::async_runtime::spawn_blocking(move || {
        let _operation = operation;
        let proxy = manager
            .lock()
            .map_err(|_| AppError::new("desktop_busy", "桌面状态不可用"))?
            .update_proxy_url();
        let result = check(&app, proxy);
        if let Err(error) = &result {
            emit(&app, "error", None, None, &error.message);
        }
        result
    })
    .await
    .map_err(|_| AppError::new("update_task_failed", "更新检查被中断"))?
}

#[tauri::command]
pub async fn desktop_install_update(app: AppHandle, state: State<'_, HostState>) -> Result<()> {
    let operation = Operation::acquire()?;
    let manager = state.0.clone();
    tauri::async_runtime::spawn_blocking(move || {
        let _operation = operation;
        if installed_update_dir(None).is_err() {
            return Err(AppError::new("update_installation_required", "便携版或未受保护的安装目录不能直接安装更新，请从可信发布页下载完整安装包并完成安装"));
        }
        let candidate = CANDIDATE.get_or_init(|| Mutex::new(None)).lock().map_err(|_| AppError::new("update_state_failed", "更新状态不可用"))?
            .clone().ok_or_else(|| AppError::new("update_check_required", "请先检查桌面更新"))?;
        let proxy = manager.lock().map_err(|_| AppError::new("desktop_busy", "桌面状态不可用"))?.update_proxy_url();
        let mut nonce = [0_u8; 16];
        getrandom::getrandom(&mut nonce).map_err(|_| AppError::new("random_failed", "系统随机数不可用"))?;
        let directory = crate::windows::user_data_dir()?.join("updates").join(hex::encode(nonce));
        crate::windows::secure_directory(&directory, Some(&crate::windows::current_sid()?))?;
        let result = (|| {
            let client = client(proxy)?;
            emit(&app, "downloading", Some(0), Some(candidate.installer.size), "正在下载桌面更新");
            download(&app, &client, &candidate.installer, &directory.join(&candidate.installer.name), true)?;
            download(&app, &client, &candidate.bundle, &directory.join(&candidate.bundle.name), false)?;
            emit(&app, "installing", None, None, "正在验证构建签名");
            verify(&app, &directory, &candidate)?;
            let was_running = manager.lock().map_err(|_| AppError::new("desktop_busy", "桌面状态不可用"))?.prepare_update()?;
            if let Err(error) = handoff_to_monitor(&directory, &candidate) {
                if let Err(recovery) = manager.lock().map_err(|_| AppError::new("desktop_busy", "桌面状态不可用"))?.resume_after_update(was_running) {
                    return Err(AppError::new("update_recovery_failed", format!("{}；{}", error.message, recovery.message)));
                }
                return Err(error);
            }
            emit(&app, "installing", None, None, "安装程序已启动，桌面程序即将退出");
            manager.lock().map_err(|_| AppError::new("desktop_busy", "桌面状态不可用"))?.exiting = true;
            app.exit(0);
            Ok(())
        })();
        if let Err(error) = &result {
            let _ = fs::remove_dir_all(&directory);
            emit(&app, "error", None, None, &error.message);
        }
        result
    }).await.map_err(|_| AppError::new("update_task_failed", "更新安装被中断"))?
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn metadata_requests_refuse_an_exhausted_shared_deadline() {
        let client = client(None).unwrap();
        let deadline = Instant::now();
        let page = api(&client, "releases?per_page=100&page=2", deadline).unwrap_err();
        let tag = commit_for_tag(&client, "mihomo-desktop-v9.0.0", deadline).unwrap_err();
        for error in [page, tag] {
            assert_eq!(error.code, "update_check_failed");
            assert_eq!(error.message, "检查桌面更新超时，请检查网络或稍后重试");
        }
    }

    #[test]
    fn metadata_requests_use_only_the_remaining_budget() {
        let budget = Duration::from_secs(2);
        let deadline = Instant::now() + budget;
        let remaining = remaining_check_time(deadline).unwrap();
        assert!(remaining <= budget);
        assert!(remaining < CHECK_TIMEOUT);
    }

    #[test]
    fn verified_installer_cannot_be_rewritten_or_renamed_while_locked() {
        let mut nonce = [0_u8; 12];
        getrandom::getrandom(&mut nonce).unwrap();
        let directory =
            std::env::temp_dir().join(format!("mihomo-update-lock-test-{}", hex::encode(nonce)));
        fs::create_dir(&directory).unwrap();
        let candidate = Candidate {
            version: "0.1.1".into(),
            source_sha: "0".repeat(40),
            installer: Asset {
                name: "setup.exe".into(),
                browser_download_url: String::new(),
                size: 4,
            },
            bundle: Asset {
                name: "setup.exe.sigstore.json".into(),
                browser_download_url: String::new(),
                size: 4,
            },
        };
        let installer = directory.join(&candidate.installer.name);
        fs::write(&installer, b"test").unwrap();
        fs::write(directory.join(&candidate.bundle.name), b"test").unwrap();
        let locks = lock_update_inputs(&directory, &candidate).unwrap();
        assert!(fs::OpenOptions::new().write(true).open(&installer).is_err());
        assert!(fs::rename(&installer, directory.join("replaced.exe")).is_err());
        assert_eq!(fs::read(&installer).unwrap(), b"test");
        drop(locks);
        assert!(fs::OpenOptions::new().write(true).open(&installer).is_ok());
        fs::remove_dir_all(directory).unwrap();
    }

    #[test]
    fn download_policy_rejects_insecure_hosts_and_credentials() {
        for url in [
            "http://github.com/a",
            "https://github.com.evil.example/a",
            "https://user@github.com/a",
            "https://github.com:444/a",
            "file:///tmp/a",
        ] {
            assert!(!allowed_host(&Url::parse(url).unwrap()));
        }
        assert!(allowed_host(
            &Url::parse("https://release-assets.githubusercontent.com/download").unwrap()
        ));
    }
    #[test]
    fn repository_name_alone_cannot_authorize_an_offline_update() {
        let result = |id: &str| {
            serde_json::json!([{"verificationResult":{"signature":{"certificate":{"sourceRepositoryIdentifier":id}}}}]).to_string()
        };
        assert!(verified_repository_id(result(REPOSITORY_ID).as_bytes()));
        assert!(!verified_repository_id(result("9999").as_bytes()));
        assert!(!verified_repository_id(
            b"[{\"repository\":\"chummumm/mihomo-optimized\"}]"
        ));
    }
}
