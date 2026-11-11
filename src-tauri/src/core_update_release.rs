//! Fetch and validate one immutable core release. The caller owns installation
//! and process replacement; this module never stops the user's running core.
use crate::{
    core::{quiet_command, wait_cancellable, CoreSupervisor},
    error::{AppError, Result},
    lifecycle::Cancellation,
};
use reqwest::{Client, Url};
use semver::Version;
use sha2::{Digest, Sha256};
use std::{
    fs::{self, File, OpenOptions},
    io::{Read, Seek, SeekFrom, Write},
    path::{Path, PathBuf},
    process::Stdio,
    thread,
    time::{Duration, Instant},
};

const RELEASE_ROOT: &str = "https://github.com/chummumm/mihomo-optimized/releases/";
const VERSION_PREFIX: &str = "mihomo-optimized-v";
const ARCHIVE_MEMBER: &str = "mihomo-optimized-windows-amd64.exe";
const MAX_METADATA: u64 = 1024 * 1024;
const MAX_ARCHIVE: u64 = 64 * 1024 * 1024;
const MAX_EXECUTABLE: u64 = 256 * 1024 * 1024;
const MAX_VERSION_OUTPUT: u64 = 16 * 1024;
const CHECK_TIMEOUT: Duration = Duration::from_secs(30);
const DOWNLOAD_TIMEOUT: Duration = Duration::from_secs(600);
const VERSION_TIMEOUT: Duration = Duration::from_secs(6);

const PROBE_CONFIG: &str = "mixed-port: 0\nport: 0\nsocks-port: 0\nallow-lan: false\nbind-address: 127.0.0.1\nmode: direct\nlog-level: error\nipv6: false\nfind-process-mode: off\ndns:\n  enable: false\ntun:\n  enable: false\ncore-auto-update: false\nexternal-ui-auto-update: false\ngeo-auto-update: false\nprofile:\n  store-selected: false\n  store-fake-ip: false\nresource-bootstrap:\n  timeout: 3\nrules: []\n";

/// A receipt can only be created after a fixed-source download and executable
/// probe. It is deliberately not serializable across the privileged IPC boundary.
#[derive(Debug)]
pub struct PreparedCoreUpdate {
    workspace: Workspace,
    executable: PathBuf,
    version: String,
    sha256: String,
}

impl PreparedCoreUpdate {
    pub fn version(&self) -> &str {
        &self.version
    }
    pub fn executable(&self) -> &Path {
        &self.executable
    }
    pub fn directory(&self) -> &Path {
        &self.workspace.directory
    }
    pub fn expected_sha256(&self) -> &str {
        &self.sha256
    }

    /// Recheck immediately before committing. The service keeps this receipt in
    /// its protected process; clients cannot replace its expected digest.
    pub fn verify(&self, cancellation: &Cancellation) -> Result<()> {
        cancellation.check()?;
        ensure_regular_file(&self.executable)?;
        ensure_windows_x64(&self.executable)?;
        if file_sha256(&self.executable, MAX_EXECUTABLE, cancellation)? != self.sha256 {
            return Err(AppError::new(
                "core_update_candidate_changed",
                "已验证的内核候选发生变化，更新已取消",
            ));
        }
        Ok(())
    }
}

#[derive(Debug)]
struct Workspace {
    directory: PathBuf,
}

impl Workspace {
    fn new(parent: &Path, prefix: &str) -> Result<Self> {
        if !parent.is_absolute() || !fs::metadata(parent).is_ok_and(|m| m.is_dir()) {
            return Err(AppError::new(
                "core_update_path_invalid",
                "内核更新目录无效",
            ));
        }
        #[cfg(windows)]
        crate::windows::reject_reparse_path(parent)?;
        #[cfg(not(windows))]
        if fs::symlink_metadata(parent).is_ok_and(|m| m.file_type().is_symlink()) {
            return Err(AppError::new(
                "core_update_path_invalid",
                "内核更新目录不能是链接",
            ));
        }
        let mut random = [0u8; 16];
        getrandom::getrandom(&mut random)
            .map_err(|_| AppError::new("random_failed", "无法创建内核更新目录"))?;
        let directory = parent.join(format!("{prefix}{}", hex::encode(random)));
        fs::create_dir(&directory).map_err(|e| AppError::io("无法创建内核更新目录", e))?;
        let workspace = Self { directory };
        #[cfg(windows)]
        {
            let sid = crate::windows::current_sid()?;
            crate::windows::secure_directory(
                &workspace.directory,
                (sid != "S-1-5-18").then_some(sid.as_str()),
            )?;
        }
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            fs::set_permissions(&workspace.directory, fs::Permissions::from_mode(0o700))
                .map_err(|e| AppError::io("无法保护内核更新目录", e))?;
        }
        Ok(workspace)
    }
}

impl Drop for Workspace {
    fn drop(&mut self) {
        // Only this freshly created random directory belongs to this receipt.
        // Rollback copies must live beside the installed executable, not here.
        let _ = fs::remove_dir_all(&self.directory);
    }
}

/// The service must pass its own fixed executable path, never a path from an
/// IPC request. An optional proxy is restricted to a literal loopback HTTP port.
pub fn prepare(
    current_executable: &Path,
    proxy: Option<&str>,
    cancellation: &Cancellation,
) -> Result<Option<PreparedCoreUpdate>> {
    cancellation.check()?;
    let current = executable_version(current_executable, cancellation)?;
    let current = parse_version(&current, true)?;
    let client = download_client(proxy)?;
    let metadata_deadline = Instant::now() + CHECK_TIMEOUT;
    let latest = fetch_bytes(
        &client,
        &format!("{RELEASE_ROOT}latest/download/version.txt"),
        256,
        metadata_deadline,
        cancellation,
    )?;
    let latest = std::str::from_utf8(&latest)
        .map_err(|_| metadata_error())?
        .trim();
    let latest_version = parse_version(latest, false)?;
    if latest_version <= current {
        return Ok(None);
    }
    let archive_name = format!("{latest}-windows-amd64.zip");
    let release = format!("{RELEASE_ROOT}download/{latest}/");
    let manifest = fetch_bytes(
        &client,
        &format!("{release}SHA256SUMS"),
        MAX_METADATA,
        metadata_deadline,
        cancellation,
    )?;
    let expected = checksum_for(&manifest, &archive_name)?;
    let parent = current_executable
        .parent()
        .ok_or_else(|| AppError::new("core_update_path_invalid", "当前内核路径无效"))?;
    let workspace = Workspace::new(parent, ".mihomo-core-update-")?;
    let archive = workspace.directory.join("core.zip");
    download_archive(
        &client,
        &format!("{release}{archive_name}"),
        &archive,
        &expected,
        cancellation,
    )?;
    let executable = workspace.directory.join("candidate.exe");
    extract_executable(&archive, &executable, cancellation)?;
    fs::remove_file(&archive).map_err(|e| AppError::io("无法清理内核下载包", e))?;
    let sha256 = file_sha256(&executable, MAX_EXECUTABLE, cancellation)?;
    let prepared = PreparedCoreUpdate {
        workspace,
        executable,
        version: latest.to_owned(),
        sha256,
    };
    prepared.verify(cancellation)?;
    if executable_version(prepared.executable(), cancellation)? != prepared.version {
        return Err(AppError::new(
            "core_update_version_mismatch",
            "内核候选报告的版本与正式发布不一致",
        ));
    }
    probe_managed_core(&prepared, cancellation)?;
    cancellation.check()?;
    Ok(Some(prepared))
}

fn metadata_error() -> AppError {
    AppError::new("core_update_metadata_invalid", "内核发布信息无效或不完整")
}

fn parse_version(input: &str, desktop: bool) -> Result<Version> {
    let number = input
        .strip_prefix(VERSION_PREFIX)
        .ok_or_else(metadata_error)?;
    let number = if desktop {
        number.strip_suffix("-desktop").unwrap_or(number)
    } else {
        number
    };
    let parsed = Version::parse(number).map_err(|_| metadata_error())?;
    if !parsed.pre.is_empty()
        || !parsed.build.is_empty()
        || format!("{}.{}.{}", parsed.major, parsed.minor, parsed.patch) != number
    {
        return Err(metadata_error());
    }
    Ok(parsed)
}

fn checksum_for(manifest: &[u8], name: &str) -> Result<String> {
    let text = std::str::from_utf8(manifest).map_err(|_| metadata_error())?;
    let mut checksum = None;
    for line in text.lines().filter(|line| !line.is_empty()) {
        if line.len() < 67 || !line.as_bytes()[..64].iter().all(u8::is_ascii_hexdigit) {
            return Err(metadata_error());
        }
        let (hash, rest) = line.split_at(64);
        let filename = rest
            .strip_prefix("  ")
            .or_else(|| rest.strip_prefix(" *"))
            .ok_or_else(metadata_error)?;
        if filename.is_empty()
            || !filename
                .bytes()
                .all(|b| b.is_ascii_alphanumeric() || b"-_.".contains(&b))
            || filename == "."
            || filename == ".."
        {
            return Err(metadata_error());
        }
        if filename == name {
            if checksum.is_some() {
                return Err(metadata_error());
            }
            checksum = Some(hash.to_ascii_lowercase());
        }
    }
    checksum.ok_or_else(metadata_error)
}

fn allowed_download(url: &Url) -> bool {
    url.scheme() == "https"
        && url.username().is_empty()
        && url.password().is_none()
        && url.port_or_known_default() == Some(443)
        && url.fragment().is_none()
        && matches!(
            url.host_str(),
            Some(
                "github.com"
                    | "release-assets.githubusercontent.com"
                    | "objects.githubusercontent.com"
            )
        )
}

fn loopback_proxy(value: &str) -> Result<Url> {
    let url = Url::parse(value)
        .map_err(|_| AppError::new("core_update_proxy_invalid", "内核更新代理地址无效"))?;
    let valid = url.scheme() == "http"
        && url.username().is_empty()
        && url.password().is_none()
        && url.query().is_none()
        && url.fragment().is_none()
        && url.path() == "/"
        && url.port_or_known_default().is_some_and(|port| port != 0)
        && url
            .host_str()
            .and_then(|host| {
                host.trim_matches(['[', ']'])
                    .parse::<std::net::IpAddr>()
                    .ok()
            })
            .is_some_and(|address| address.is_loopback());
    if !valid {
        return Err(AppError::new(
            "core_update_proxy_invalid",
            "内核更新只接受本机 HTTP 代理",
        ));
    }
    Ok(url)
}

fn download_client(proxy: Option<&str>) -> Result<Client> {
    let mut builder = Client::builder()
        .no_proxy()
        .https_only(true)
        .user_agent(concat!(
            "mihomo-desktop-core-update/",
            env!("CARGO_PKG_VERSION")
        ))
        .connect_timeout(Duration::from_secs(15))
        .timeout(DOWNLOAD_TIMEOUT)
        .redirect(reqwest::redirect::Policy::custom(|attempt| {
            if attempt.previous().len() < 5 && allowed_download(attempt.url()) {
                attempt.follow()
            } else {
                attempt.error("unexpected core release redirect")
            }
        }));
    if let Some(proxy) = proxy {
        builder = builder.proxy(
            reqwest::Proxy::all(loopback_proxy(proxy)?.as_str())
                .map_err(|_| AppError::new("core_update_proxy_invalid", "本地更新代理不可用"))?,
        );
    }
    builder
        .build()
        .map_err(|_| AppError::new("core_update_network_failed", "无法初始化内核更新连接"))
}

fn fetch_bytes(
    client: &Client,
    url: &str,
    maximum: u64,
    deadline: Instant,
    cancellation: &Cancellation,
) -> Result<Vec<u8>> {
    wait_cancellable(Some(cancellation), deadline, async {
        let mut response = client
            .get(url)
            .send()
            .await
            .and_then(reqwest::Response::error_for_status)
            .map_err(|_| AppError::new("core_update_check_failed", "无法读取正式内核发布信息"))?;
        if response.content_length().is_some_and(|size| size > maximum) {
            return Err(metadata_error());
        }
        let mut result = Vec::new();
        while let Some(chunk) = response
            .chunk()
            .await
            .map_err(|_| AppError::new("core_update_check_failed", "内核发布信息下载中断"))?
        {
            if (result.len() as u64).saturating_add(chunk.len() as u64) > maximum {
                return Err(metadata_error());
            }
            result.extend_from_slice(&chunk);
        }
        cancellation.check()?;
        Ok(result)
    })
}

fn download_archive(
    client: &Client,
    url: &str,
    destination: &Path,
    expected: &str,
    cancellation: &Cancellation,
) -> Result<()> {
    wait_cancellable(
        Some(cancellation),
        Instant::now() + DOWNLOAD_TIMEOUT,
        async {
            let mut response = client
                .get(url)
                .send()
                .await
                .and_then(reqwest::Response::error_for_status)
                .map_err(|_| AppError::new("core_update_download_failed", "正式内核下载失败"))?;
            if response
                .content_length()
                .is_some_and(|size| size == 0 || size > MAX_ARCHIVE)
            {
                return Err(AppError::new(
                    "core_update_size_invalid",
                    "内核下载包大小无效",
                ));
            }
            let mut file = OpenOptions::new()
                .write(true)
                .create_new(true)
                .open(destination)
                .map_err(|e| AppError::io("无法创建内核下载包", e))?;
            let mut hash = Sha256::new();
            let mut size = 0u64;
            while let Some(chunk) = response
                .chunk()
                .await
                .map_err(|_| AppError::new("core_update_download_failed", "内核下载中断"))?
            {
                size = size.saturating_add(chunk.len() as u64);
                if size > MAX_ARCHIVE {
                    return Err(AppError::new(
                        "core_update_size_invalid",
                        "内核下载包超过大小限制",
                    ));
                }
                cancellation.check()?;
                file.write_all(&chunk)
                    .map_err(|e| AppError::io("无法写入内核下载包", e))?;
                hash.update(&chunk);
            }
            if size == 0 || hex::encode(hash.finalize()) != expected {
                return Err(AppError::new(
                    "core_update_checksum_mismatch",
                    "内核下载包校验失败",
                ));
            }
            file.sync_all()
                .map_err(|e| AppError::io("无法保存内核下载包", e))?;
            cancellation.check()
        },
    )
}

fn archive_error() -> AppError {
    AppError::new("core_update_archive_invalid", "正式内核压缩包结构无效")
}

/// Core release ZIPs contain one ordinary x64 executable. Limit the directory
/// before parsing and never use archive paths or permissions for extraction.
fn extract_executable(
    archive: &Path,
    destination: &Path,
    cancellation: &Cancellation,
) -> Result<()> {
    cancellation.check()?;
    let mut source = File::open(archive).map_err(|e| AppError::io("无法读取内核下载包", e))?;
    let length = source
        .metadata()
        .map_err(|e| AppError::io("无法读取内核包大小", e))?
        .len();
    if !(22..=MAX_ARCHIVE).contains(&length) {
        return Err(archive_error());
    }
    source
        .seek(SeekFrom::End(-22))
        .map_err(|_| archive_error())?;
    let mut end = [0u8; 22];
    source.read_exact(&mut end).map_err(|_| archive_error())?;
    let u16_at = |n| u16::from_le_bytes([end[n], end[n + 1]]);
    let u32_at = |n| u32::from_le_bytes([end[n], end[n + 1], end[n + 2], end[n + 3]]);
    if &end[..4] != b"PK\x05\x06"
        || u16_at(4) != 0
        || u16_at(6) != 0
        || u16_at(8) != 1
        || u16_at(10) != 1
        || u16_at(20) != 0
        || u32_at(12) > 128 * 1024
        || u64::from(u32_at(12)) + u64::from(u32_at(16)) != length - 22
    {
        return Err(archive_error());
    }
    source.rewind().map_err(|_| archive_error())?;
    let mut archive = zip::ZipArchive::new(source).map_err(|_| archive_error())?;
    if archive.len() != 1 {
        return Err(archive_error());
    }
    let mut member = archive.by_index(0).map_err(|_| archive_error())?;
    if member.name_raw() != ARCHIVE_MEMBER.as_bytes()
        || !member.is_file()
        || member.encrypted()
        || member.unix_mode().is_some_and(|mode| {
            let kind = mode & 0o170000;
            kind != 0 && kind != 0o100000
        })
        || member.size() == 0
        || member.size() > MAX_EXECUTABLE
        || member.compressed_size() > MAX_ARCHIVE
        || !matches!(
            member.compression(),
            zip::CompressionMethod::Stored | zip::CompressionMethod::Deflated
        )
    {
        return Err(archive_error());
    }
    let expected_size = member.size();
    let mut output = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(destination)
        .map_err(|e| AppError::io("无法创建候选内核", e))?;
    let mut buffer = [0u8; 64 * 1024];
    let mut written = 0u64;
    loop {
        cancellation.check()?;
        let count = member.read(&mut buffer).map_err(|_| archive_error())?;
        if count == 0 {
            break;
        }
        written = written.saturating_add(count as u64);
        if written > expected_size || written > MAX_EXECUTABLE {
            return Err(archive_error());
        }
        output
            .write_all(&buffer[..count])
            .map_err(|e| AppError::io("无法写入候选内核", e))?;
    }
    if written != expected_size {
        return Err(archive_error());
    }
    output
        .sync_all()
        .map_err(|e| AppError::io("无法保存候选内核", e))?;
    drop(output);
    ensure_windows_x64(destination)
}

fn ensure_regular_file(path: &Path) -> Result<()> {
    #[cfg(windows)]
    {
        crate::windows::reject_reparse_path(path)?;
        crate::windows::reject_hard_links(path)?;
    }
    let metadata = fs::symlink_metadata(path).map_err(|e| AppError::io("无法读取内核文件", e))?;
    if !metadata.file_type().is_file() || metadata.len() == 0 || metadata.len() > MAX_EXECUTABLE {
        return Err(AppError::new(
            "core_update_file_invalid",
            "内核文件类型或大小无效",
        ));
    }
    #[cfg(unix)]
    {
        use std::os::unix::fs::MetadataExt;
        if metadata.nlink() != 1 {
            return Err(AppError::new(
                "core_update_file_invalid",
                "内核文件不能使用硬链接",
            ));
        }
    }
    Ok(())
}

fn ensure_windows_x64(path: &Path) -> Result<()> {
    let mut file = File::open(path).map_err(|e| AppError::io("无法检查候选内核", e))?;
    let mut header = [0u8; 64];
    file.read_exact(&mut header).map_err(|_| archive_error())?;
    if &header[..2] != b"MZ" {
        return Err(archive_error());
    }
    let offset = u32::from_le_bytes(header[60..64].try_into().expect("four-byte slice"));
    let length = file
        .metadata()
        .map_err(|e| AppError::io("无法读取候选内核大小", e))?
        .len();
    if offset < 64 || u64::from(offset) + 6 > length {
        return Err(archive_error());
    }
    file.seek(SeekFrom::Start(u64::from(offset)))
        .map_err(|_| archive_error())?;
    let mut pe = [0u8; 6];
    file.read_exact(&mut pe).map_err(|_| archive_error())?;
    if &pe[..4] != b"PE\0\0" || pe[4..] != [0x64, 0x86] {
        return Err(archive_error());
    }
    Ok(())
}

fn file_sha256(path: &Path, maximum: u64, cancellation: &Cancellation) -> Result<String> {
    let mut file = File::open(path).map_err(|e| AppError::io("无法校验内核文件", e))?;
    let mut digest = Sha256::new();
    let mut size = 0u64;
    let mut buffer = [0u8; 64 * 1024];
    loop {
        cancellation.check()?;
        let count = file
            .read(&mut buffer)
            .map_err(|e| AppError::io("无法校验内核文件", e))?;
        if count == 0 {
            break;
        }
        size = size.saturating_add(count as u64);
        if size > maximum {
            return Err(AppError::new(
                "core_update_size_invalid",
                "内核文件超过大小限制",
            ));
        }
        digest.update(&buffer[..count]);
    }
    Ok(hex::encode(digest.finalize()))
}

/// Read a short, bounded -v response without pipe-reader threads or a shell.
pub fn executable_version(executable: &Path, cancellation: &Cancellation) -> Result<String> {
    cancellation.check()?;
    ensure_regular_file(executable)?;
    let scratch = Workspace::new(&std::env::temp_dir(), "mihomo-core-version-")?;
    let log_path = scratch.directory.join("version.txt");
    let output = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&log_path)
        .map_err(|e| AppError::io("无法读取内核版本", e))?;
    let stderr = output
        .try_clone()
        .map_err(|e| AppError::io("无法读取内核版本", e))?;
    let mut child = quiet_command(executable)
        .arg("-v")
        .current_dir(&scratch.directory)
        .env_remove("CLASH_OVERRIDE_SECRET")
        .env_remove("CLASH_DESKTOP_MANAGED")
        .stdin(Stdio::null())
        .stdout(output)
        .stderr(stderr)
        .spawn()
        .map_err(|e| AppError::io("无法运行内核版本检查", e))?;
    #[cfg(windows)]
    let job = match crate::windows::create_kill_job(&child) {
        Ok(job) => Some(job),
        Err(error) => {
            let _ = child.kill();
            confirm_probe_exit(&mut child)?;
            return Err(error);
        }
    };
    let deadline = Instant::now() + VERSION_TIMEOUT;
    let result = (|| loop {
        cancellation.check()?;
        if fs::metadata(&log_path)
            .map_err(|e| AppError::io("无法读取内核版本", e))?
            .len()
            > MAX_VERSION_OUTPUT
        {
            return Err(AppError::new(
                "core_update_version_invalid",
                "内核版本输出过大",
            ));
        }
        if let Some(status) = child
            .try_wait()
            .map_err(|e| AppError::io("无法等待内核版本检查", e))?
        {
            return if status.success() {
                Ok(())
            } else {
                Err(AppError::new(
                    "core_update_version_invalid",
                    "内核版本检查失败",
                ))
            };
        }
        if Instant::now() >= deadline {
            return Err(AppError::new(
                "core_update_probe_timeout",
                "内核版本检查超时",
            ));
        }
        thread::sleep(Duration::from_millis(20));
    })();
    if result.is_err() {
        let _ = child.kill();
    }
    #[cfg(windows)]
    drop(job);
    confirm_probe_exit(&mut child)?;
    result?;
    let mut bytes = Vec::new();
    File::open(&log_path)
        .map_err(|e| AppError::io("无法读取内核版本", e))?
        .take(MAX_VERSION_OUTPUT + 1)
        .read_to_end(&mut bytes)
        .map_err(|e| AppError::io("无法读取内核版本", e))?;
    if bytes.len() as u64 > MAX_VERSION_OUTPUT {
        return Err(AppError::new(
            "core_update_version_invalid",
            "内核版本输出过大",
        ));
    }
    let output = std::str::from_utf8(&bytes)
        .map_err(|_| AppError::new("core_update_version_invalid", "内核版本输出无效"))?;
    let versions: Vec<_> = output
        .split_whitespace()
        .filter(|part| part.starts_with(VERSION_PREFIX))
        .collect();
    if versions.len() != 1 {
        return Err(AppError::new(
            "core_update_version_invalid",
            "无法识别本项目内核版本",
        ));
    }
    parse_version(versions[0], true)?;
    Ok(versions[0].to_owned())
}

fn confirm_probe_exit(child: &mut std::process::Child) -> Result<()> {
    let cleanup_deadline = Instant::now() + Duration::from_secs(2);
    loop {
        if child
            .try_wait()
            .map_err(|e| AppError::io("无法确认版本检查退出", e))?
            .is_some()
        {
            return Ok(());
        }
        if Instant::now() >= cleanup_deadline {
            return Err(AppError::new(
                "core_update_probe_cleanup_failed",
                "无法确认内核检查进程退出，更新已取消",
            ));
        }
        thread::sleep(Duration::from_millis(20));
    }
}

fn probe_managed_core(prepared: &PreparedCoreUpdate, cancellation: &Cancellation) -> Result<()> {
    let mut probe = CoreSupervisor::new(
        prepared.executable.clone(),
        prepared.directory().join("probe-home"),
        prepared.directory().join("probe-logs"),
    );
    probe.set_cancellation(cancellation.clone());
    let started = probe.start(PROBE_CONFIG.into(), false);
    // CoreSupervisor verifies authenticated API 1, its own PID and all disabled
    // listeners. Always confirm shutdown, including a failed/cancelled probe.
    let stopped = probe.stop();
    stopped.map_err(|error| {
        AppError::new(
            "core_update_probe_cleanup_failed",
            format!("候选内核检查未能完成清理：{}", error.message),
        )
    })?;
    let status = started?;
    if status.version.as_deref() != Some(prepared.version()) {
        return Err(AppError::new(
            "core_update_version_mismatch",
            "候选内核控制接口报告的版本不一致",
        ));
    }
    cancellation.check()
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{
        io::Cursor,
        net::TcpListener,
        sync::{
            atomic::{AtomicU64, Ordering},
            mpsc, Arc,
        },
    };

    fn token() -> Cancellation {
        Cancellation::new(Arc::new(AtomicU64::new(0)), 0)
    }

    fn workspace() -> Workspace {
        Workspace::new(&std::env::temp_dir(), "mihomo-core-update-test-").unwrap()
    }

    fn pe() -> Vec<u8> {
        let mut bytes = vec![0; 4096];
        bytes[..2].copy_from_slice(b"MZ");
        bytes[60..64].copy_from_slice(&64u32.to_le_bytes());
        bytes[64..70].copy_from_slice(b"PE\0\0\x64\x86");
        bytes
    }

    fn zip_bytes(entries: &[(&str, &[u8])]) -> Vec<u8> {
        let mut writer = zip::ZipWriter::new(Cursor::new(Vec::new()));
        for (name, bytes) in entries {
            writer
                .start_file(
                    *name,
                    zip::write::SimpleFileOptions::default()
                        .compression_method(zip::CompressionMethod::Stored),
                )
                .unwrap();
            writer.write_all(bytes).unwrap();
        }
        writer.finish().unwrap().into_inner()
    }

    fn extract_fixture(bytes: &[u8]) -> Result<Vec<u8>> {
        let directory = workspace();
        let archive = directory.directory.join("fixture.zip");
        let output = directory.directory.join("candidate.exe");
        fs::write(&archive, bytes).unwrap();
        extract_executable(&archive, &output, &token())?;
        Ok(fs::read(output).unwrap())
    }

    #[test]
    fn release_versions_are_canonical_and_desktop_suffix_does_not_downgrade() {
        let desktop = parse_version("mihomo-optimized-v1.0.29-desktop", true).unwrap();
        assert_eq!(
            desktop,
            parse_version("mihomo-optimized-v1.0.29", false).unwrap()
        );
        assert!(parse_version("mihomo-optimized-v1.0.30", false).unwrap() > desktop);
        assert!(parse_version("mihomo-optimized-v2.0.0", false).unwrap() > desktop);
        for invalid in [
            "1.0.29",
            "mihomo-desktop-v1.0.29",
            "mihomo-optimized-v01.0.29",
            "mihomo-optimized-v1.0.29-alpha",
            "mihomo-optimized-v1.0.29+other",
            "mihomo-optimized-v1.0.29-desktop",
            "mihomo-optimized-v1.0",
            "mihomo-optimized-v1.0.29/evil",
            "mihomo-optimized-v18446744073709551616.0.0",
        ] {
            assert!(parse_version(invalid, false).is_err(), "{invalid}");
        }
    }

    #[test]
    fn managed_probe_configuration_is_bounded_and_does_not_enable_network_resources() {
        assert_eq!(
            crate::config::resource_bootstrap_timeout(PROBE_CONFIG).unwrap(),
            Duration::from_secs(3)
        );
        let runtime = crate::config::runtime_config(PROBE_CONFIG, false, 49123).unwrap();
        let document = crate::config::parse(&runtime).unwrap();
        for field in ["mixed-port", "port", "socks-port"] {
            assert_eq!(document[field].as_u64(), Some(0));
        }
        for field in [
            "core-auto-update",
            "external-ui-auto-update",
            "geo-auto-update",
        ] {
            assert_eq!(document[field].as_bool(), Some(false));
        }
        assert_eq!(document["dns"]["enable"].as_bool(), Some(false));
        assert_eq!(document["tun"]["enable"].as_bool(), Some(false));
        assert_eq!(document["rules"].as_sequence().unwrap().len(), 0);
        assert!(!document.contains_key("rule-providers"));
        assert!(!document.contains_key("proxy-providers"));
    }

    #[test]
    fn checksums_require_one_exact_filename() {
        let name = "mihomo-optimized-v1.0.29-windows-amd64.zip";
        let hash = "a".repeat(64);
        let manifest = format!("{}  BUILDINFO.json\n{hash}  {name}\n", "b".repeat(64));
        assert_eq!(checksum_for(manifest.as_bytes(), name).unwrap(), hash);
        assert_eq!(
            checksum_for(
                format!("{} *{name}\r\n", hash.to_uppercase()).as_bytes(),
                name
            )
            .unwrap(),
            hash
        );
        for invalid in [
            format!("{manifest}{hash}  {name}\n"),
            format!("{hash}  ../{name}\n"),
            format!("{hash}  {name}:payload\n"),
            format!("{hash} {name}\n"),
            format!("{}  {name}\n", "z".repeat(64)),
            format!("{hash}  other.zip\n"),
        ] {
            assert!(checksum_for(invalid.as_bytes(), name).is_err());
        }
    }

    #[test]
    fn download_and_proxy_urls_do_not_accept_external_or_credentialled_origins() {
        for url in [
            "https://github.com/chummumm/mihomo-optimized/releases/latest/download/version.txt",
            "https://release-assets.githubusercontent.com/path?signature=value",
        ] {
            assert!(allowed_download(&Url::parse(url).unwrap()));
        }
        for url in [
            "http://github.com/a",
            "https://evil.example/a",
            "https://github.com.evil.example/a",
            "https://user@github.com/a",
            "https://github.com:444/a",
            "https://github.com/a#fragment",
        ] {
            assert!(!allowed_download(&Url::parse(url).unwrap()), "{url}");
        }
        for proxy in ["http://127.0.0.1:7890", "http://[::1]:7890"] {
            assert!(loopback_proxy(proxy).is_ok());
        }
        for proxy in [
            "http://localhost:7890",
            "http://192.0.2.1:7890",
            "http://user:password@127.0.0.1:7890",
            "http://127.0.0.1:7890/path",
            "http://127.0.0.1:7890/?url=x",
            "socks5://127.0.0.1:7890",
            "http://127.0.0.1:0",
        ] {
            assert!(loopback_proxy(proxy).is_err(), "{proxy}");
        }
    }

    #[test]
    fn extracts_only_one_exact_x64_executable() {
        let binary = pe();
        assert_eq!(
            extract_fixture(&zip_bytes(&[(ARCHIVE_MEMBER, &binary)])).unwrap(),
            binary
        );
        let mut compressed = zip::ZipWriter::new(Cursor::new(Vec::new()));
        compressed
            .start_file(
                ARCHIVE_MEMBER,
                zip::write::SimpleFileOptions::default()
                    .compression_method(zip::CompressionMethod::Deflated),
            )
            .unwrap();
        compressed.write_all(&binary).unwrap();
        assert_eq!(
            extract_fixture(&compressed.finish().unwrap().into_inner()).unwrap(),
            binary
        );
        for invalid in [
            zip_bytes(&[("../mihomo-optimized-windows-amd64.exe", &binary)]),
            zip_bytes(&[("directory/mihomo-optimized-windows-amd64.exe", &binary)]),
            zip_bytes(&[("another.exe", &binary)]),
            zip_bytes(&[(ARCHIVE_MEMBER, &binary), ("another.exe", &binary)]),
            zip_bytes(&[(ARCHIVE_MEMBER, b"not an executable")]),
            zip_bytes(&[(ARCHIVE_MEMBER, b"")]),
        ] {
            assert!(extract_fixture(&invalid).is_err());
        }
        let mut wrong_architecture = binary;
        wrong_architecture[68..70].copy_from_slice(&0x014cu16.to_le_bytes());
        assert!(extract_fixture(&zip_bytes(&[(ARCHIVE_MEMBER, &wrong_architecture)])).is_err());
    }

    #[test]
    fn rejects_links_crc_damage_and_unbounded_directory_before_extracting() {
        let binary = pe();
        let ordinary = zip_bytes(&[(ARCHIVE_MEMBER, &binary)]);
        let end = ordinary.len() - 22;
        let central = u32::from_le_bytes(ordinary[end + 16..end + 20].try_into().unwrap()) as usize;
        let mut linked = ordinary.clone();
        // The creator OS determines how ZIP external attributes are interpreted.
        // ZipWriter defaults to DOS on Windows, so make this a Unix link on every host.
        linked[central + 5] = zip::System::Unix as u8;
        linked[central + 38..central + 42].copy_from_slice(&(0o120777u32 << 16).to_le_bytes());
        {
            let mut archive = zip::ZipArchive::new(Cursor::new(&linked)).unwrap();
            let member = archive.by_index(0).unwrap();
            assert_eq!(member.unix_mode(), Some(0o120777));
            assert!(
                member.is_symlink(),
                "fixture must encode an actual ZIP symlink"
            );
        }
        assert!(extract_fixture(&linked).is_err());
        let mut corrupt = ordinary.clone();
        let payload = corrupt
            .windows(binary.len())
            .position(|window| window == binary)
            .unwrap();
        corrupt[payload + 80] ^= 1;
        assert!(extract_fixture(&corrupt).is_err());
        let mut entries = ordinary.clone();
        entries[end + 8..end + 12].copy_from_slice(&[0xff, 0xff, 0xff, 0xff]);
        assert!(extract_fixture(&entries).is_err());
        let mut oversized = ordinary;
        oversized[central + 24..central + 28]
            .copy_from_slice(&((MAX_EXECUTABLE + 1) as u32).to_le_bytes());
        assert!(extract_fixture(&oversized).is_err());
    }

    #[test]
    fn prepared_receipt_detects_candidate_substitution_and_cleans_its_own_stage() {
        let directory = workspace();
        let sibling = directory.directory.join("unrelated.txt");
        fs::write(&sibling, b"keep").unwrap();
        let stage = Workspace::new(&directory.directory, ".mihomo-core-update-").unwrap();
        let executable = stage.directory.join("candidate.exe");
        fs::write(&executable, pe()).unwrap();
        let prepared = PreparedCoreUpdate {
            workspace: stage,
            executable,
            version: "mihomo-optimized-v1.0.29".into(),
            sha256: hex::encode(Sha256::digest(pe())),
        };
        prepared.verify(&token()).unwrap();
        let mut changed = pe();
        changed[80] = 1;
        fs::write(prepared.executable(), changed).unwrap();
        assert_eq!(
            prepared.verify(&token()).unwrap_err().code,
            "core_update_candidate_changed"
        );
        let stage = prepared.directory().to_owned();
        drop(prepared);
        assert!(!stage.exists());
        assert_eq!(fs::read(sibling).unwrap(), b"keep");
    }

    #[test]
    fn cancelled_preparation_never_creates_a_candidate_or_changes_the_current_file() {
        let directory = workspace();
        let current = directory.directory.join("current.exe");
        fs::write(&current, b"unchanged current executable").unwrap();
        let revision = Arc::new(AtomicU64::new(1));
        let cancellation = Cancellation::new(revision, 0);
        assert_eq!(
            prepare(&current, None, &cancellation).unwrap_err().code,
            "operation_cancelled"
        );
        assert_eq!(fs::read(&current).unwrap(), b"unchanged current executable");
        assert_eq!(fs::read_dir(&directory.directory).unwrap().count(), 1);
    }

    fn local_response(body: Vec<u8>, declared: Option<u64>) -> (String, thread::JoinHandle<()>) {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let url = format!("http://{}/fixture", listener.local_addr().unwrap());
        let server = thread::spawn(move || {
            let (mut stream, _) = listener.accept().unwrap();
            stream
                .set_read_timeout(Some(Duration::from_secs(3)))
                .unwrap();
            let mut request = [0; 4096];
            let _ = stream.read(&mut request);
            let header = match declared {
                Some(length) => format!(
                    "HTTP/1.1 200 OK\r\nContent-Length: {length}\r\nConnection: close\r\n\r\n"
                ),
                None => "HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n".into(),
            };
            let _ = stream.write_all(header.as_bytes());
            let _ = stream.write_all(&body);
        });
        (url, server)
    }

    #[test]
    fn downloaded_bytes_must_match_the_pinned_digest() {
        let client = Client::builder().no_proxy().build().unwrap();
        let bytes = b"the immutable release archive".to_vec();
        let expected = hex::encode(Sha256::digest(&bytes));
        let directory = workspace();
        let (url, server) = local_response(bytes.clone(), Some(bytes.len() as u64));
        let destination = directory.directory.join("verified.zip");
        download_archive(&client, &url, &destination, &expected, &token()).unwrap();
        server.join().unwrap();
        assert_eq!(fs::read(destination).unwrap(), bytes);
        let (url, server) = local_response(bytes.clone(), Some(bytes.len() as u64));
        let error = download_archive(
            &client,
            &url,
            &directory.directory.join("invalid.zip"),
            &"0".repeat(64),
            &token(),
        )
        .unwrap_err();
        server.join().unwrap();
        assert_eq!(error.code, "core_update_checksum_mismatch");
    }

    #[test]
    fn metadata_limits_are_enforced_before_allocating_the_response() {
        let client = Client::builder().no_proxy().build().unwrap();
        for (body, declared) in [(Vec::new(), Some(MAX_METADATA + 1)), (vec![b'x'; 65], None)] {
            let (url, server) = local_response(body, declared);
            let error = fetch_bytes(
                &client,
                &url,
                64,
                Instant::now() + Duration::from_secs(3),
                &token(),
            )
            .unwrap_err();
            server.join().unwrap();
            assert_eq!(error.code, "core_update_metadata_invalid");
        }
    }

    #[test]
    fn cancellation_drops_a_stalled_download_without_waiting_for_network_timeout() {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let url = format!("http://{}/stalled", listener.local_addr().unwrap());
        let (ready_tx, ready_rx) = mpsc::channel();
        let (release_tx, release_rx) = mpsc::channel();
        let server = thread::spawn(move || {
            let (mut stream, _) = listener.accept().unwrap();
            stream
                .set_read_timeout(Some(Duration::from_secs(3)))
                .unwrap();
            let mut request = [0; 4096];
            let _ = stream.read(&mut request);
            stream
                .write_all(b"HTTP/1.1 200 OK\r\nContent-Length: 64\r\n\r\nx")
                .unwrap();
            ready_tx.send(()).unwrap();
            let _ = release_rx.recv_timeout(Duration::from_secs(3));
        });
        let directory = workspace();
        let destination = directory.directory.join("stalled.zip");
        let revision = Arc::new(AtomicU64::new(0));
        let cancellation = Cancellation::new(revision.clone(), 0);
        let worker = thread::spawn(move || {
            let client = Client::builder().no_proxy().build().unwrap();
            download_archive(&client, &url, &destination, &"0".repeat(64), &cancellation)
        });
        ready_rx.recv_timeout(Duration::from_secs(3)).unwrap();
        let started = Instant::now();
        revision.store(1, Ordering::Release);
        assert_eq!(
            worker.join().unwrap().unwrap_err().code,
            "operation_cancelled"
        );
        assert!(started.elapsed() < Duration::from_secs(2));
        let _ = release_tx.send(());
        server.join().unwrap();
    }

    #[cfg(unix)]
    #[test]
    fn version_probe_is_bounded_and_cancellable() {
        use std::os::unix::fs::PermissionsExt;
        let directory = workspace();
        let executable = directory.directory.join("fake-core");
        fs::write(
            &executable,
            "#!/bin/sh\nprintf 'Mihomo Meta mihomo-optimized-v1.0.29-desktop linux amd64\\n'\n",
        )
        .unwrap();
        fs::set_permissions(&executable, fs::Permissions::from_mode(0o700)).unwrap();
        assert_eq!(
            executable_version(&executable, &token()).unwrap(),
            "mihomo-optimized-v1.0.29-desktop"
        );
        fs::write(
            &executable,
            format!(
                "#!/bin/sh\nprintf '%s' '{}'\n",
                "x".repeat(MAX_VERSION_OUTPUT as usize + 1)
            ),
        )
        .unwrap();
        assert_eq!(
            executable_version(&executable, &token()).unwrap_err().code,
            "core_update_version_invalid"
        );
        fs::write(&executable, "#!/bin/sh\nwhile :; do :; done\n").unwrap();
        let revision = Arc::new(AtomicU64::new(0));
        let cancellation = Cancellation::new(revision.clone(), 0);
        let worker = thread::spawn(move || executable_version(&executable, &cancellation));
        let started = Instant::now();
        thread::sleep(Duration::from_millis(60));
        revision.store(1, Ordering::Release);
        assert_eq!(
            worker.join().unwrap().unwrap_err().code,
            "operation_cancelled"
        );
        assert!(started.elapsed() < Duration::from_secs(2));
    }

    /// CI explicitly supplies a real core built with an older version string.
    /// Production release URLs and validation remain unchanged in this test.
    #[cfg(windows)]
    #[test]
    fn real_release_preparation_keeps_existing_core_running() {
        if std::env::var("MIHOMO_CORE_UPDATE_RELEASE_TEST").as_deref() != Ok("1") {
            eprintln!(
                "SKIP real core release preparation; CI enables MIHOMO_CORE_UPDATE_RELEASE_TEST"
            );
            return;
        }
        let source = PathBuf::from(
            std::env::var_os("MIHOMO_CORE_UPDATE_OLD_CORE")
                .expect("MIHOMO_CORE_UPDATE_OLD_CORE is required by the real release test"),
        );
        let directory = workspace();
        let current = directory.directory.join("mihomo-core.exe");
        fs::copy(&source, &current).unwrap();
        let old_hash = file_sha256(&current, MAX_EXECUTABLE, &token()).unwrap();
        let mut running = CoreSupervisor::new(
            current.clone(),
            directory.directory.join("current-home"),
            directory.directory.join("current-logs"),
        );
        let before = running.start(PROBE_CONFIG.into(), false).unwrap();
        let old_version = before
            .version
            .as_deref()
            .expect("current core must report a version");
        let prepared = prepare(&current, None, &token())
            .unwrap()
            .expect("CI fixture version must be older than the published core");
        assert!(
            parse_version(prepared.version(), false).unwrap()
                > parse_version(old_version, true).unwrap()
        );
        prepared.verify(&token()).unwrap();
        let stage = prepared.directory().to_path_buf();
        let after = running.poll().unwrap();
        assert!(after.running);
        assert_eq!(
            after.pid, before.pid,
            "preparing an update stopped the current core"
        );
        assert_eq!(after.version, before.version);
        assert_eq!(
            file_sha256(&current, MAX_EXECUTABLE, &token()).unwrap(),
            old_hash
        );
        drop(prepared);
        assert!(!stage.exists(), "prepared release was not cleaned up");

        let revision = Arc::new(AtomicU64::new(0));
        let cancellation = Cancellation::new(revision.clone(), 0);
        let current_copy = current.clone();
        let worker_token = cancellation.clone();
        let worker = thread::spawn(move || prepare(&current_copy, None, &worker_token));
        let deadline = Instant::now() + Duration::from_secs(45);
        let staged = loop {
            let found = fs::read_dir(&directory.directory)
                .unwrap()
                .flatten()
                .any(|entry| {
                    entry
                        .file_name()
                        .to_string_lossy()
                        .starts_with(".mihomo-core-update-")
                });
            if found {
                break true;
            }
            if worker.is_finished() || Instant::now() >= deadline {
                break false;
            }
            thread::sleep(Duration::from_millis(10));
        };
        revision.store(1, Ordering::Release);
        let result = worker.join().unwrap();
        if let Ok(Some(prepared)) = result {
            // A very fast runner may finish preparing before this observation.
            // Its receipt must still reject the superseded operation.
            assert_eq!(
                prepared.verify(&cancellation).unwrap_err().code,
                "operation_cancelled"
            );
            drop(prepared);
        } else {
            assert_eq!(result.unwrap_err().code, "operation_cancelled");
        }
        assert!(
            staged,
            "real cancellation test did not reach its staging boundary"
        );
        assert!(!fs::read_dir(&directory.directory)
            .unwrap()
            .flatten()
            .any(|entry| entry
                .file_name()
                .to_string_lossy()
                .starts_with(".mihomo-core-update-")));
        let after = running.poll().unwrap();
        assert!(after.running);
        assert_eq!(after.pid, before.pid);
        assert_eq!(
            file_sha256(&current, MAX_EXECUTABLE, &token()).unwrap(),
            old_hash
        );
        running.stop().unwrap();
    }
}
