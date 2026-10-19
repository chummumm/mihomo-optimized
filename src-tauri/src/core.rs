use crate::{
    config,
    error::{AppError, Result},
};
use reqwest::blocking::Client;
use serde::{Deserialize, Serialize};
use std::{
    collections::VecDeque,
    fs,
    net::TcpListener,
    path::{Path, PathBuf},
    process::{Child, Command, Stdio},
    thread,
    time::{Duration, Instant},
};

const START_TIMEOUT: Duration = Duration::from_secs(20);
const STOP_TIMEOUT: Duration = Duration::from_secs(8);
const RESTART_WINDOW: Duration = Duration::from_secs(60);
const MAX_RESTARTS: usize = 3;

#[derive(Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct Controller {
    pub url: String,
    pub secret: String,
}

#[derive(Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CoreStatus {
    pub running: bool,
    pub controller: Option<Controller>,
    pub version: Option<String>,
    pub error: Option<String>,
    pub restart_count: usize,
}

pub struct CoreSupervisor {
    executable: PathBuf,
    home: PathBuf,
    logs: PathBuf,
    child: Option<Child>,
    #[cfg(windows)]
    job: Option<std::os::windows::io::OwnedHandle>,
    controller: Option<Controller>,
    version: Option<String>,
    source: Option<String>,
    tun: bool,
    desired_running: bool,
    error: Option<String>,
    restarts: VecDeque<Instant>,
    restart_at: Option<Instant>,
}

pub fn http_client() -> Result<Client> {
    Client::builder()
        .no_proxy()
        .timeout(Duration::from_secs(3))
        .build()
        .map_err(|_| AppError::new("http_init_failed", "无法初始化本地控制连接"))
}

pub fn fixed_core_path() -> Result<PathBuf> {
    let current = std::env::current_exe().map_err(|e| AppError::io("无法定位程序", e))?;
    let parent = current
        .parent()
        .ok_or_else(|| AppError::new("core_missing", "找不到程序目录"))?;
    let installed = parent.join(if cfg!(windows) {
        "mihomo-core.exe"
    } else {
        "mihomo-core"
    });
    if installed.is_file() {
        return Ok(installed);
    }
    #[cfg(debug_assertions)]
    {
        let dev = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("binaries")
            .join(if cfg!(windows) {
                "mihomo-core-x86_64-pc-windows-msvc.exe"
            } else {
                "mihomo-core-x86_64-unknown-linux-gnu"
            });
        if dev.is_file() {
            return Ok(dev);
        }
    }
    Err(AppError::new(
        "core_missing",
        "未找到配套内核，请重新安装完整程序",
    ))
}

pub fn quiet_command(executable: &Path) -> Command {
    #[allow(unused_mut)]
    let mut command = Command::new(executable);
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        command.creation_flags(windows_sys::Win32::System::Threading::CREATE_NO_WINDOW);
    }
    command
}

impl CoreSupervisor {
    pub fn new(executable: PathBuf, home: PathBuf, logs: PathBuf) -> Self {
        Self {
            executable,
            home,
            logs,
            child: None,
            #[cfg(windows)]
            job: None,
            controller: None,
            version: None,
            source: None,
            tun: false,
            desired_running: false,
            error: None,
            restarts: VecDeque::new(),
            restart_at: None,
        }
    }

    pub fn status(&self) -> CoreStatus {
        CoreStatus {
            running: self.child.is_some(),
            controller: self.controller.clone(),
            version: self.version.clone(),
            error: self.error.clone(),
            restart_count: self.restarts.len(),
        }
    }

    pub fn start(&mut self, source: String, tun: bool) -> Result<CoreStatus> {
        self.stop()?;
        self.source = Some(source);
        self.tun = tun;
        self.restarts.clear();
        self.restart_at = None;
        self.desired_running = true;
        self.error = None;
        if let Err(error) = self.spawn() {
            let _ = self.terminate_child();
            self.controller = None;
            #[cfg(windows)]
            {
                self.job = None;
            }
            self.desired_running = false;
            self.error = Some(error.message.clone());
            return Err(error);
        }
        Ok(self.status())
    }

    fn spawn(&mut self) -> Result<()> {
        fs::create_dir_all(&self.home).map_err(|e| AppError::io("无法创建运行目录", e))?;
        fs::create_dir_all(&self.logs).map_err(|e| AppError::io("无法创建日志目录", e))?;
        let listener = TcpListener::bind(("127.0.0.1", 0))
            .map_err(|e| AppError::io("无法分配本地控制端口", e))?;
        let port = listener
            .local_addr()
            .map_err(|e| AppError::io("无法读取控制端口", e))?
            .port();
        let mut random = [0_u8; 32];
        getrandom::getrandom(&mut random)
            .map_err(|_| AppError::new("random_failed", "系统随机数不可用"))?;
        let controller = Controller {
            url: format!("http://127.0.0.1:{port}"),
            secret: hex::encode(random),
        };
        let source = self
            .source
            .as_deref()
            .ok_or_else(|| AppError::new("config_missing", "没有可运行的配置"))?;
        let runtime = config::runtime_config(source, self.tun, port)?;
        config::atomic_write(&self.home.join("config.yaml"), runtime.as_bytes())?;
        self.validate(&controller)?;
        let log_path = self.logs.join("core.log");
        if fs::metadata(&log_path).is_ok_and(|m| m.len() > 10 * 1024 * 1024) {
            let old = self.logs.join("core.previous.log");
            if old.exists() {
                fs::remove_file(&old).map_err(|e| AppError::io("无法轮换日志", e))?;
            }
            fs::rename(&log_path, old).map_err(|e| AppError::io("无法轮换日志", e))?;
        }
        let stdout = fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(log_path)
            .map_err(|e| AppError::io("无法打开内核日志", e))?;
        let stderr = stdout
            .try_clone()
            .map_err(|e| AppError::io("无法打开内核日志", e))?;
        let mut command = self.command(&controller);
        command
            .stdin(Stdio::null())
            .stdout(Stdio::from(stdout))
            .stderr(Stdio::from(stderr));
        drop(listener);
        let child = command
            .spawn()
            .map_err(|e| AppError::io("无法启动配套内核", e))?;
        #[cfg(windows)]
        {
            match crate::windows::create_kill_job(&child) {
                Ok(job) => self.job = Some(job),
                Err(error) => {
                    let mut child = child;
                    let _ = child.kill();
                    let _ = child.wait();
                    return Err(error);
                }
            }
        }
        self.child = Some(child);
        self.controller = Some(controller.clone());
        let started = Instant::now();
        let client = http_client()?;
        while started.elapsed() < START_TIMEOUT {
            if let Some(exit) = self
                .child
                .as_mut()
                .expect("child stored above")
                .try_wait()
                .map_err(|e| AppError::io("无法读取内核状态", e))?
            {
                self.child = None;
                self.controller = None;
                return Err(AppError::new(
                    "core_start_failed",
                    format!(
                        "内核启动后退出（{}），请检查配置或日志",
                        exit.code()
                            .map_or_else(|| "已终止".into(), |n| n.to_string())
                    ),
                ));
            }
            if let Ok(response) = client
                .get(format!("{}/desktop", controller.url))
                .bearer_auth(&controller.secret)
                .send()
            {
                if response.status().is_success() {
                    if let Ok(info) = response.json::<serde_json::Value>() {
                        let expected_pid = self.child.as_ref().map(|child| child.id() as u64);
                        if info.get("managed").and_then(serde_json::Value::as_bool) == Some(true)
                            && info.get("api_version").and_then(serde_json::Value::as_u64)
                                == Some(1)
                            && info.get("state").and_then(serde_json::Value::as_str)
                                == Some("running")
                            && info.get("pid").and_then(serde_json::Value::as_u64) == expected_pid
                        {
                            self.verify_listeners(&controller, source)?;
                            self.version = client
                                .get(format!("{}/version", controller.url))
                                .bearer_auth(&controller.secret)
                                .send()
                                .ok()
                                .and_then(|r| r.json::<serde_json::Value>().ok())
                                .and_then(|v| {
                                    v.get("version")
                                        .and_then(|v| v.as_str())
                                        .map(str::to_string)
                                });
                            self.error = None;
                            return Ok(());
                        }
                    }
                }
            }
            thread::sleep(Duration::from_millis(150));
        }
        let _ = self.terminate_child();
        self.controller = None;
        Err(AppError::new(
            "core_start_timeout",
            "内核在 20 秒内没有就绪，请检查端口或配置日志",
        ))
    }

    fn command(&self, controller: &Controller) -> Command {
        let mut command = quiet_command(&self.executable);
        command
            .arg("-desktop-managed")
            .arg("-d")
            .arg(&self.home)
            .arg("-f")
            .arg(self.home.join("config.yaml"))
            .arg("-ext-ctl")
            .arg(controller.url.trim_start_matches("http://"))
            .env("CLASH_OVERRIDE_SECRET", &controller.secret)
            .current_dir(&self.home);
        command
    }

    fn validate(&self, controller: &Controller) -> Result<()> {
        let mut child = self
            .command(controller)
            .arg("-t")
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .map_err(|e| AppError::io("无法校验配置", e))?;
        #[cfg(windows)]
        let _job = match crate::windows::create_kill_job(&child) {
            Ok(job) => job,
            Err(error) => {
                let _ = child.kill();
                let _ = child.wait();
                return Err(error);
            }
        };
        let start = Instant::now();
        loop {
            match child
                .try_wait()
                .map_err(|e| AppError::io("无法读取配置校验结果", e))?
            {
                Some(status) if status.success() => return Ok(()),
                Some(_) => {
                    return Err(AppError::new(
                        "reload_invalid",
                        "内核拒绝此配置，请检查字段、规则和提供者路径",
                    ))
                }
                None if start.elapsed() >= Duration::from_secs(15) => {
                    let _ = child.kill();
                    let _ = child.wait();
                    return Err(AppError::new(
                        "config_validation_timeout",
                        "配置校验超时，当前运行状态未改变",
                    ));
                }
                None => thread::sleep(Duration::from_millis(50)),
            }
        }
    }

    fn verify_listeners(&self, controller: &Controller, source: &str) -> Result<()> {
        let expected = config::parse(source)?;
        let response = http_client()?
            .get(format!("{}/configs", controller.url))
            .bearer_auth(&controller.secret)
            .send()
            .map_err(|_| {
                AppError::new(
                    "core_listener_unverified",
                    "无法确认内核监听状态，系统代理未切换",
                )
            })?;
        if !response.status().is_success() {
            return Err(AppError::new(
                "core_listener_unverified",
                "无法鉴权读取内核监听状态",
            ));
        }
        let actual: serde_json::Value = response
            .json()
            .map_err(|_| AppError::new("core_listener_unverified", "内核返回无效监听状态"))?;
        // Mihomo reports these values from its actual TCP listeners, so a port bind
        // failure cannot be mistaken for a successful config application.
        for field in ["port", "socks-port", "mixed-port"] {
            let requested = expected
                .get(field)
                .and_then(serde_yaml::Value::as_u64)
                .unwrap_or(0);
            if actual.get(field).and_then(serde_json::Value::as_u64) != Some(requested) {
                return Err(AppError::new(
                    "core_listener_failed",
                    format!("内核未能监听配置的 {field}，请检查端口是否被占用"),
                ));
            }
        }
        Ok(())
    }

    fn apply_runtime(&self, controller: &Controller, source: &str) -> Result<()> {
        let response = http_client()?
            .put(format!("{}/configs?force=true", controller.url))
            .timeout(Duration::from_secs(20))
            .bearer_auth(&controller.secret)
            .json(&serde_json::json!({"path": self.home.join("config.yaml").display().to_string()}))
            .send()
            .map_err(|_| AppError::new("reload_failed", "运行配置重载超时或连接失败"))?;
        if !response.status().is_success() {
            return Err(AppError::new("reload_failed", "内核拒绝应用运行配置"));
        }
        self.verify_listeners(controller, source)
    }

    pub fn reload(&mut self, source: String) -> Result<CoreStatus> {
        config::parse(&source)?;
        if self.child.is_none() {
            self.source = Some(source);
            return Ok(self.status());
        }
        let controller = self
            .controller
            .clone()
            .ok_or_else(|| AppError::new("core_stopped", "内核未运行"))?;
        let port = controller
            .url
            .rsplit(':')
            .next()
            .and_then(|v| v.parse::<u16>().ok())
            .ok_or_else(|| AppError::new("controller_invalid", "本地控制接口无效"))?;
        let previous = self.source.clone().unwrap_or_default();
        let runtime = config::runtime_config(&source, self.tun, port)?;
        config::atomic_write(&self.home.join("config.yaml"), runtime.as_bytes())?;
        if let Err(error) = self.validate(&controller) {
            let old = config::runtime_config(&previous, self.tun, port)?;
            config::atomic_write(&self.home.join("config.yaml"), old.as_bytes())?;
            return Err(error);
        }
        if let Err(error) = self.apply_runtime(&controller, &source) {
            let rollback = (|| {
                let old = config::runtime_config(&previous, self.tun, port)?;
                config::atomic_write(&self.home.join("config.yaml"), old.as_bytes())?;
                self.apply_runtime(&controller, &previous)
            })();
            if rollback.is_ok() {
                return Err(AppError::new(
                    "reload_failed",
                    format!(
                        "配置已保存，但未能应用：{}。已恢复上次运行配置，请修正后重载",
                        error.message
                    ),
                ));
            }
            // A timed-out ApplyConfig may still be running. Terminating the process
            // also cancels any pending application before reporting a failed rollback.
            let _ = self.stop();
            let _ = self.terminate_child();
            self.controller = None;
            #[cfg(windows)]
            {
                self.job = None;
            }
            self.error =
                Some("配置已保存，重载和运行回滚均失败，内核已停止；请修正配置后重新启动".into());
            return Err(AppError::new(
                "reload_restore_failed",
                self.error.clone().unwrap_or_default(),
            ));
        }
        self.source = Some(source);
        self.error = None;
        Ok(self.status())
    }

    pub fn poll(&mut self) -> Result<CoreStatus> {
        if let Some(child) = self.child.as_mut() {
            if child
                .try_wait()
                .map_err(|e| AppError::io("无法读取内核状态", e))?
                .is_some()
            {
                self.child = None;
                self.controller = None;
                #[cfg(windows)]
                {
                    self.job = None;
                }
                self.error = Some("内核意外退出，正在准备恢复".into());
                self.restart_at = Some(Instant::now() + Duration::from_secs(1));
            }
        }
        while self
            .restarts
            .front()
            .is_some_and(|time| time.elapsed() > RESTART_WINDOW)
        {
            self.restarts.pop_front();
        }
        if self.desired_running
            && self.child.is_none()
            && self.restart_at.is_some_and(|time| Instant::now() >= time)
        {
            if self.restarts.len() >= MAX_RESTARTS {
                self.desired_running = false;
                self.restart_at = None;
                self.error = Some("内核在 60 秒内连续失败 3 次，已停止自动重启，请检查配置".into());
            } else {
                self.restarts.push_back(Instant::now());
                match self.spawn() {
                    Ok(()) => self.restart_at = None,
                    Err(error) => {
                        let _ = self.terminate_child();
                        self.controller = None;
                        self.error = Some(error.message);
                        self.restart_at =
                            Some(Instant::now() + Duration::from_secs(1 << self.restarts.len()));
                    }
                }
            }
        }
        Ok(self.status())
    }

    pub fn stop(&mut self) -> Result<()> {
        self.desired_running = false;
        self.restart_at = None;
        if self.child.is_some() {
            if let Some(controller) = &self.controller {
                if let Ok(client) = http_client() {
                    let _ = client
                        .post(format!("{}/desktop/shutdown", controller.url))
                        .bearer_auth(&controller.secret)
                        .send();
                }
            }
            let stop_started = Instant::now();
            while stop_started.elapsed() < STOP_TIMEOUT {
                let exited = self
                    .child
                    .as_mut()
                    .expect("child exists until cleanup")
                    .try_wait()
                    .map_err(|e| AppError::io("无法等待内核退出", e))?
                    .is_some();
                if exited {
                    self.child = None;
                    break;
                }
                thread::sleep(Duration::from_millis(75));
            }
            self.terminate_child()?;
        }
        self.controller = None;
        self.version = None;
        #[cfg(windows)]
        {
            self.job = None;
        }
        Ok(())
    }

    fn terminate_child(&mut self) -> Result<()> {
        if let Some(mut child) = self.child.take() {
            let _ = child.kill();
            child
                .wait()
                .map_err(|e| AppError::io("无法清理内核进程", e))?;
        }
        Ok(())
    }
}

impl Drop for CoreSupervisor {
    fn drop(&mut self) {
        let _ = self.stop();
    }
}
