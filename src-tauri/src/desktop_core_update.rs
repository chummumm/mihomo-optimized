//! Independent core updates run inside the existing desktop lifecycle owner.
//! Program Files writes belong to the authenticated service; portable builds
//! use the same verified transaction in their unprivileged supervisor.
use super::*;
use crate::core_update::{UpdateJob, UpdateOutcome};

impl DesktopManager {
    /// Only a newly accepted command may resume an interrupted cutover. The
    /// cancelled update must never borrow a fresh token to restart itself.
    pub fn resume_interrupted_core_update(&mut self) -> Result<()> {
        if !self.core_update_interrupted {
            return Ok(());
        }
        self.cancellation.check()?;
        if self.app.stop_pending.load(Ordering::Acquire) != 0 {
            return Ok(());
        }
        if !self.run_intent || self.exiting || self.core_status.running {
            self.core_update_interrupted = false;
            return Ok(());
        }
        if self.core_status.process_state != "exited" || self.dirty {
            return Ok(());
        }
        self.core_update_interrupted = false;
        self.start(false)
    }

    pub fn can_update_core(&self) -> bool {
        !self.updating
            && !self.exiting
            && !self.dirty
            && self.app.stop_pending.load(Ordering::Acquire) == 0
            && matches!(self.phase, Phase::Stopped | Phase::Running | Phase::Error)
            && self.core_status.error_code.as_deref() != Some("core_termination_unconfirmed")
            && (self.phase != Phase::Error || self.core_status.process_state == "exited")
    }

    /// Only called after the previous owned process has exited. A committed but
    /// unconfirmed file replacement must be resolved before another Start.
    pub(super) fn recover_core_update_before_start(&mut self) -> Result<()> {
        #[cfg(test)]
        if self.test_runtime.is_some() {
            return Ok(());
        }
        self.cancellation.check()?;
        if let Ok(directory) = windows::protected_install_dir() {
            // A stopped service is fine for an ordinary direct start, but only
            // that service may recover a privileged replacement.
            if service::status()?.running {
                let client = self.update_service_client()?;
                self.app.set_update_service(Some(client.clone()));
                let result = client.core_update_call(
                    Request::CompleteCoreUpdate { accept: false },
                    &self.cancellation,
                    None,
                );
                self.app.set_update_service(None);
                result?;
            } else if core::core_update_needs_recovery(&directory.join("mihomo-core.exe"))? {
                return Err(AppError::new(
                    "core_update_service_required",
                    "上次内核更新尚未完成恢复，请在设置中启动桌面服务后重试",
                ));
            }
        } else {
            let path = core::fixed_core_path().or_else(|_| {
                self.app
                    .application_path()
                    .parent()
                    .map(|directory| directory.join("mihomo-core.exe"))
                    .ok_or_else(|| AppError::new("core_missing", "找不到内核安装目录"))
            })?;
            core::recover_core_update(&path, true)?;
        }
        Ok(())
    }

    fn update_service_client(&self) -> Result<ServiceClient> {
        let client =
            self.app.service_client().map(Ok).unwrap_or_else(|| {
                ServiceClient::new(self.app.instance.clone(), self.logs.clone())
            })?;
        if self.app.service_client().is_none() {
            self.app.set_service_client(Some(client.clone()));
            self.app.pause_service_observation();
        }
        Ok(client)
    }

    pub fn update_core(&mut self, job: &UpdateJob) -> Result<UpdateOutcome> {
        let _scope = crate::diagnostics::scope(
            serde_json::json!({"cause":"core_update","updateRequestId":job.request_id}),
        );
        self.cancellation.check()?;
        self.require_idle_update()?;
        let was_running = self.phase == Phase::Running;
        let source = self.applied_config.clone();
        let proxy_port = if was_running {
            self.applied_proxy_port
        } else {
            None
        };
        let service_owned = matches!(self.running, Some(RunningCore::Service(_)));
        let needs_service = service_owned || windows::protected_install_dir().is_ok();
        if self.running.is_none() {
            self.recover_core_update_before_start()?;
        }
        let client = if needs_service {
            if !service::status()?.running {
                return Err(AppError::new(
                    "core_update_service_required",
                    "安装版独立更新内核需要桌面服务，请在设置中安装服务后重试",
                ));
            }
            let client = self.update_service_client()?;
            if !service_owned {
                self.app.set_update_service(Some(client.clone()));
            }
            Some(client)
        } else {
            None
        };
        if !needs_service && self.running.is_none() {
            let mut supervisor = CoreSupervisor::new(
                core::fixed_core_path()?,
                self.directory.join("runtime"),
                self.logs.clone(),
            );
            supervisor.set_cancellation(self.cancellation.clone());
            self.running = Some(RunningCore::Direct(Box::new(supervisor)));
        }
        self.updating = true;
        // Admission can succeed before its response is cancelled or lost.
        let mut prepare_attempted = false;
        let mut prepared = false;
        let mut switching = false;
        let result =
            (|| {
                prepare_attempted = true;
                let status = if let Some(client) = &client {
                    client.core_update_call(
                        Request::PrepareCoreUpdate { proxy_port },
                        &self.cancellation,
                        source.as_deref(),
                    )?
                } else {
                    self.direct_update_core()?
                        .prepare_core_update(proxy_port, job.generation)?
                };
                let update = status.core_update.as_ref().ok_or_else(|| {
                    AppError::new(
                        "core_update_protocol",
                        "后台未返回内核更新状态，请使用新版完整安装包",
                    )
                })?;
                let version = update
                    .version
                    .clone()
                    .ok_or_else(|| AppError::new("core_update_protocol", "后台未返回内核版本"))?;
                if update.phase == "up_to_date" {
                    prepare_attempted = false;
                    return Ok(UpdateOutcome {
                        current_version: version.clone(),
                        latest_version: version,
                        updated: false,
                    });
                }
                if update.phase != "prepared" {
                    return Err(AppError::new(
                        "core_update_protocol",
                        "内核候选尚未准备完成",
                    ));
                }
                prepared = true;
                self.wait_for_resource_updates()?;
                self.cancellation.check()?;
                self.app.core_updates.restarting(job, &version);
                // The proxy stays live through downloads and candidate checks.
                self.proxy.restore()?;
                switching = true;
                if client.is_some() && !service_owned {
                    if let Some(RunningCore::Direct(supervisor)) = &mut self.running {
                        supervisor.stop_for(StopReason::CoreUpdate)?;
                    }
                }
                let status = if let Some(client) = &client {
                    client.core_update_call(
                        Request::CommitCoreUpdate {},
                        &self.cancellation,
                        source.as_deref(),
                    )?
                } else {
                    self.direct_update_core()?
                        .commit_core_update(job.generation)?
                };
                self.cancellation.check()?;
                self.core_status = if client.is_some() && !service_owned && was_running {
                    let source = source.clone().ok_or_else(|| {
                        AppError::new("core_update_state", "缺少正在运行的内核配置")
                    })?;
                    self.direct_update_core()?
                        .start_after_core_update(source, false)?
                } else if client.is_some() && !service_owned {
                    self.core_status.clone()
                } else {
                    status
                };
                self.cancellation.check()?;
                if was_running
                    && (!self.core_status.running
                        || self.core_status.version.as_deref() != Some(version.as_str()))
                {
                    return Err(AppError::new(
                        "core_update_start_failed",
                        "新内核未通过启动和版本检查，正在恢复旧内核",
                    ));
                }
                if let Some(client) = &client {
                    client.core_update_call(
                        Request::CompleteCoreUpdate { accept: true },
                        &self.cancellation,
                        source.as_deref(),
                    )?;
                } else {
                    self.direct_update_core()?
                        .complete_core_update(true, job.generation)?;
                }
                prepared = false;
                prepare_attempted = false;
                // A proxy error after acceptance must not claim the binary rolled back.
                switching = false;
                if was_running && self.preferences.system_proxy {
                    self.proxy.enable(proxy_port.ok_or_else(|| {
                        AppError::new("core_update_state", "缺少运行中的代理端口")
                    })?)?;
                }
                self.phase = if was_running {
                    Phase::Running
                } else {
                    Phase::Stopped
                };
                self.last_error = None;
                self.publish();
                Ok(UpdateOutcome {
                    current_version: version.clone(),
                    latest_version: version,
                    updated: true,
                })
            })();
        let result = match result {
            Err(original) => {
                let rollback = if prepare_attempted || prepared || switching {
                    self.rollback_core_update(
                        client.as_ref(),
                        service_owned,
                        was_running,
                        switching,
                        source.as_deref(),
                        proxy_port,
                    )
                } else {
                    Ok(())
                };
                match rollback {
                    Ok(()) => Err(original),
                    Err(error) => Err(AppError::new(
                        "core_update_rollback_failed",
                        format!("{}；恢复旧内核失败：{}", original.message, error.message),
                    )),
                }
            }
            other => other,
        };
        self.app.set_update_service(None);
        self.updating = false;
        if let Some(version) = &self.core_status.version {
            self.app.core_updates.observe_version(version);
        }
        self.publish();
        result
    }

    fn direct_update_core(&mut self) -> Result<&mut CoreSupervisor> {
        match self.running.as_mut() {
            Some(RunningCore::Direct(supervisor)) => Ok(supervisor),
            _ => Err(AppError::new(
                "core_update_state",
                "当前内核不属于本地进程所有者",
            )),
        }
    }

    fn rollback_core_update(
        &mut self,
        client: Option<&ServiceClient>,
        service_owned: bool,
        was_running: bool,
        switching: bool,
        source: Option<&str>,
        proxy_port: Option<u16>,
    ) -> Result<()> {
        // A fresh token grants cleanup only. Restart requires the original token.
        let cleanup = self.app.cancellation();
        if switching && !service_owned {
            if let Some(RunningCore::Direct(supervisor)) = &mut self.running {
                supervisor.set_cancellation(cleanup.clone());
                supervisor.stop_for(StopReason::CoreUpdate)?;
            }
        }
        if let Some(client) = client {
            let status = client.core_update_call(
                Request::CompleteCoreUpdate { accept: false },
                &cleanup,
                source,
            )?;
            if service_owned {
                self.core_status = status;
            }
        } else {
            let supervisor = self.direct_update_core()?;
            supervisor.set_cancellation(cleanup.clone());
            self.core_status = supervisor.complete_core_update(false, cleanup.revision())?;
        }
        if self.cancellation.check().is_ok() && was_running && switching {
            if !service_owned {
                let token = self.cancellation.clone();
                let supervisor = self.direct_update_core()?;
                supervisor.set_cancellation(token);
                self.core_status = if !supervisor.status().running {
                    supervisor.start_after_core_update(
                        source
                            .ok_or_else(|| AppError::new("core_update_state", "缺少原内核配置"))?
                            .into(),
                        false,
                    )?
                } else {
                    supervisor.status()
                };
            }
            if self.core_status.running && self.preferences.system_proxy {
                self.proxy.enable(
                    proxy_port
                        .ok_or_else(|| AppError::new("core_update_state", "缺少原代理端口"))?,
                )?;
            }
        }
        if switching {
            if let Some(RunningCore::Direct(supervisor)) = &mut self.running {
                self.core_status = supervisor.status();
            }
            self.phase = if self.core_status.running {
                Phase::Running
            } else {
                Phase::Stopped
            };
            self.core_update_interrupted =
                was_running && !self.core_status.running && self.cancellation.check().is_err();
        }
        Ok(())
    }

    fn wait_for_resource_updates(&self) -> Result<()> {
        let Some(controller) = self.core_status.controller.clone() else {
            return Ok(());
        };
        let token = self.cancellation.clone();
        core::wait_cancellable(
            Some(&token),
            Instant::now() + Duration::from_secs(240),
            async move {
                let client = reqwest::Client::builder()
                    .no_proxy()
                    .timeout(Duration::from_secs(3))
                    .build()
                    .map_err(|_| AppError::new("core_update_wait", "无法读取内核资源更新状态"))?;
                loop {
                    let response = client
                        .get(format!(
                            "{}/upgrade/check",
                            controller.url.trim_end_matches('/')
                        ))
                        .bearer_auth(&controller.secret)
                        .send()
                        .await
                        .map_err(|_| {
                            AppError::new(
                                "core_update_wait",
                                "无法确认资源更新状态，已保留当前内核",
                            )
                        })?;
                    if response.status() == reqwest::StatusCode::NOT_FOUND {
                        return Ok(());
                    }
                    if !response.status().is_success() {
                        return Err(AppError::new(
                            "core_update_wait",
                            "内核资源状态暂不可用，已保留当前内核",
                        ));
                    }
                    let value: serde_json::Value = response
                        .json()
                        .await
                        .map_err(|_| AppError::new("core_update_wait", "内核资源状态格式无效"))?;
                    match value.get("state").and_then(serde_json::Value::as_str) {
                        Some("idle" | "completed" | "partial") => return Ok(()),
                        Some("running" | "restarting") => {
                            tokio::time::sleep(Duration::from_millis(200)).await
                        }
                        _ => return Err(AppError::new("core_update_wait", "内核资源状态格式无效")),
                    }
                }
            },
        )
    }
}
