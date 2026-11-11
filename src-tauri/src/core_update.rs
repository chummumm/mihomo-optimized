//! The desktop owns the core's update schedule, independently of UI navigation
//! and of the application installer. Timers use monotonic time; accepted jobs
//! retain the user's lifecycle generation until they finish or are cancelled.
use crate::{
    config,
    error::{AppError, Result},
};
use serde::Serialize;
use std::{
    collections::VecDeque,
    sync::{Arc, Mutex},
    time::{Duration, Instant},
};

const MAX_INTERVAL_HOURS: u64 = (i64::MAX as u64) / 3_600_000_000_000;
const RETAINED_REQUESTS: usize = 64;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Policy {
    pub enabled: bool,
    pub interval_hours: u64,
}
impl Default for Policy {
    fn default() -> Self {
        Self {
            enabled: false,
            interval_hours: 24,
        }
    }
}
impl Policy {
    pub fn from_config(source: &str) -> Result<Self> {
        use serde_yaml::Value;
        let mapping = config::parse(source)?;
        let enabled = match mapping.get("core-auto-update") {
            None | Some(Value::Null) => false,
            Some(Value::Bool(value)) => *value,
            _ => {
                return Err(AppError::new(
                    "config_invalid",
                    "core-auto-update 必须是布尔值",
                ))
            }
        };
        let interval_hours = match mapping.get("core-update-interval") {
            None | Some(Value::Null) => 24,
            Some(value) => value
                .as_u64()
                .filter(|hours| (1..=MAX_INTERVAL_HOURS).contains(hours))
                .ok_or_else(|| {
                    AppError::new(
                        "config_invalid",
                        "core-update-interval 必须是有效的正整数小时数",
                    )
                })?,
        };
        Ok(Self {
            enabled,
            interval_hours,
        })
    }
    fn interval(self) -> Duration {
        Duration::from_secs(self.interval_hours * 3600)
    }
}

#[derive(Clone, Debug, Serialize)]
pub struct UpdateView {
    pub id: &'static str,
    pub kind: &'static str,
    pub name: &'static str,
    pub status: String,
    pub current_version: String,
    pub latest_version: String,
    pub auto_update: bool,
    pub update_interval: u64,
    pub interval_unit: &'static str,
    pub auto_update_state: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub next_check_in_ms: Option<u64>,
    pub request_id: String,
    pub error: String,
    pub reason: String,
}
impl Default for UpdateView {
    fn default() -> Self {
        Self {
            id: "kernel",
            kind: "kernel",
            name: "mihomo-optimized",
            status: "skipped".into(),
            current_version: String::new(),
            latest_version: String::new(),
            auto_update: false,
            update_interval: 24,
            interval_unit: "hours",
            auto_update_state: "unscheduled".into(),
            next_check_in_ms: None,
            request_id: String::new(),
            error: String::new(),
            reason: String::new(),
        }
    }
}
#[derive(Clone, Debug)]
pub struct UpdateJob {
    pub request_id: String,
    pub generation: u64,
    automatic: bool,
}
pub struct UpdateOutcome {
    pub current_version: String,
    pub latest_version: String,
    pub updated: bool,
}
#[derive(Default)]
struct Schedule {
    policy: Policy,
    configured: bool,
    immediate: bool,
    next: Option<Instant>,
    pending: Option<UpdateJob>,
    active: Option<UpdateJob>,
    view: UpdateView,
    completed: VecDeque<(String, UpdateView)>,
    sequence: u64,
}
#[derive(Clone, Default)]
pub struct CoreUpdates(Arc<Mutex<Schedule>>);
impl CoreUpdates {
    pub fn configure(&self, policy: Policy) {
        self.configure_at(policy, Instant::now());
    }
    fn configure_at(&self, policy: Policy, now: Instant) {
        let mut state = self.0.lock().unwrap_or_else(|error| error.into_inner());
        if state.configured && state.policy == policy {
            return;
        }
        let immediate = policy.enabled && (!state.configured || !state.policy.enabled);
        state.policy = policy;
        state.configured = true;
        state.immediate = immediate;
        state.next = policy.enabled.then(|| now + policy.interval());
        if state.active.is_none() && state.pending.is_none() {
            state.view.auto_update_state = if immediate {
                "waiting"
            } else if policy.enabled {
                "scheduled"
            } else {
                "unscheduled"
            }
            .into();
        }
    }
    pub fn observe_version(&self, version: &str) {
        if !version.is_empty() {
            self.0
                .lock()
                .unwrap_or_else(|error| error.into_inner())
                .view
                .current_version = version.into();
        }
    }
    pub fn snapshot(&self) -> UpdateView {
        self.snapshot_at(Instant::now())
    }
    fn snapshot_at(&self, now: Instant) -> UpdateView {
        let state = self.0.lock().unwrap_or_else(|error| error.into_inner());
        snapshot(&state, now)
    }
    /// Replayed IDs only return their retained result; a second accepted request
    /// cannot duplicate an in-flight download or restart.
    pub fn request(&self, request_id: &str, generation: u64) -> Result<UpdateView> {
        if request_id.is_empty()
            || request_id.len() > 64
            || !request_id
                .bytes()
                .all(|byte| byte.is_ascii_alphanumeric() || byte == b'-' || byte == b'_')
        {
            return Err(AppError::new("invalid_arguments", "内核更新请求编号无效"));
        }
        let mut state = self.0.lock().unwrap_or_else(|error| error.into_inner());
        if let Some((_, view)) = state.completed.iter().find(|(id, _)| id == request_id) {
            return Ok(view.clone());
        }
        if let Some(job) = state.active.as_ref().or(state.pending.as_ref()) {
            return if job.request_id == request_id {
                Ok(snapshot(&state, Instant::now()))
            } else {
                Err(AppError::new(
                    "core_update_busy",
                    "已有内核更新任务正在进行",
                ))
            };
        }
        state.pending = Some(UpdateJob {
            request_id: request_id.into(),
            generation,
            automatic: false,
        });
        state.view.status = "pending".into();
        state.view.auto_update_state = "waiting".into();
        state.view.request_id = request_id.into();
        state.view.error.clear();
        state.view.reason.clear();
        Ok(snapshot(&state, Instant::now()))
    }
    pub fn take_due(&self, generation: u64) -> Option<UpdateJob> {
        self.take_due_at(generation, Instant::now())
    }
    fn take_due_at(&self, generation: u64, now: Instant) -> Option<UpdateJob> {
        let mut state = self.0.lock().unwrap_or_else(|error| error.into_inner());
        if state.active.is_some() {
            return None;
        }
        let job = if let Some(job) = state.pending.take() {
            if job.generation != generation {
                state.view.status = "skipped".into();
                state.view.auto_update_state = if state.policy.enabled {
                    "scheduled"
                } else {
                    "unscheduled"
                }
                .into();
                state.view.reason = "operation_cancelled".into();
                state.view.error = "内核更新已被更新的用户操作取消".into();
                retain(&mut state, &job.request_id, now);
                return None;
            }
            job
        } else {
            if !state.policy.enabled
                || !(state.immediate || state.next.is_some_and(|next| next <= now))
            {
                return None;
            }
            state.immediate = false;
            state.next = Some(now + state.policy.interval());
            state.sequence += 1;
            UpdateJob {
                request_id: format!("automatic-{}", state.sequence),
                generation,
                automatic: true,
            }
        };
        // An explicit check satisfies an already-due automatic check too.
        if state.immediate || state.next.is_some_and(|next| next <= now) {
            state.immediate = false;
            state.next = state.policy.enabled.then(|| now + state.policy.interval());
        }
        state.view.status = "checking".into();
        state.view.auto_update_state = "checking".into();
        state.view.request_id = job.request_id.clone();
        state.view.error.clear();
        state.view.reason.clear();
        state.active = Some(job.clone());
        Some(job)
    }
    pub fn restarting(&self, job: &UpdateJob, version: &str) {
        let mut state = self.0.lock().unwrap_or_else(|error| error.into_inner());
        if state
            .active
            .as_ref()
            .is_some_and(|active| active.request_id == job.request_id)
        {
            state.view.status = "restarting".into();
            state.view.latest_version = version.into();
        }
    }
    pub fn finish(&self, job: &UpdateJob, outcome: Result<UpdateOutcome>) {
        let mut state = self.0.lock().unwrap_or_else(|error| error.into_inner());
        if !state
            .active
            .as_ref()
            .is_some_and(|active| active.request_id == job.request_id)
        {
            return;
        }
        state.active = None;
        state.view.auto_update_state = if state.policy.enabled {
            "scheduled"
        } else {
            "unscheduled"
        }
        .into();
        match outcome {
            Ok(result) => {
                state.view.current_version = result.current_version;
                state.view.latest_version = result.latest_version;
                state.view.status = if result.updated {
                    "updated"
                } else {
                    "up_to_date"
                }
                .into();
                state.view.error.clear();
                state.view.reason.clear();
            }
            Err(error) => {
                state.view.status = if error.code == "operation_cancelled" {
                    "skipped"
                } else {
                    "error"
                }
                .into();
                state.view.reason = error.code;
                state.view.error = error.message;
                if job.automatic && state.policy.enabled {
                    state.view.auto_update_state = "retrying".into();
                }
            }
        }
        retain(&mut state, &job.request_id, Instant::now());
    }
}
fn snapshot(state: &Schedule, now: Instant) -> UpdateView {
    let mut view = state.view.clone();
    view.auto_update = state.policy.enabled;
    view.update_interval = state.policy.interval_hours;
    view.next_check_in_ms = state.next.map(|next| {
        next.saturating_duration_since(now)
            .as_millis()
            .min(u64::MAX as u128) as u64
    });
    if !state.policy.enabled && state.active.is_none() && state.pending.is_none() {
        view.auto_update_state = "unscheduled".into();
    }
    view
}
fn retain(state: &mut Schedule, request_id: &str, now: Instant) {
    state
        .completed
        .push_back((request_id.into(), snapshot(state, now)));
    while state.completed.len() > RETAINED_REQUESTS {
        state.completed.pop_front();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn policy_matches_core_defaults_and_rejects_invalid_intervals() {
        assert_eq!(
            Policy::from_config("mode: rule\n").unwrap(),
            Policy::default()
        );
        assert_eq!(
            Policy::from_config("core-auto-update: true\ncore-update-interval: 2\n").unwrap(),
            Policy {
                enabled: true,
                interval_hours: 2
            }
        );
        for value in ["0", "-1", "1.5", "\"24\"", "true", "2562048"] {
            assert!(
                Policy::from_config(&format!("core-update-interval: {value}\n")).is_err(),
                "{value}"
            );
        }
        assert!(Policy::from_config("core-auto-update: 'true'\n").is_err());
    }
    #[test]
    fn activation_is_immediate_and_same_policy_keeps_its_deadline() {
        let schedule = CoreUpdates::default();
        let now = Instant::now();
        let policy = Policy {
            enabled: true,
            interval_hours: 24,
        };
        schedule.configure_at(policy, now);
        let job = schedule.take_due_at(1, now).unwrap();
        assert!(schedule.take_due_at(1, now).is_none());
        schedule.finish(
            &job,
            Ok(UpdateOutcome {
                current_version: "1.0.29".into(),
                latest_version: "1.0.29".into(),
                updated: false,
            }),
        );
        schedule.configure_at(policy, now + Duration::from_secs(60));
        assert_eq!(schedule.snapshot_at(now).next_check_in_ms, Some(86_400_000));
        assert!(schedule
            .take_due_at(1, now + Duration::from_secs(86_399))
            .is_none());
        assert!(schedule
            .take_due_at(1, now + Duration::from_secs(86_400))
            .is_some());
    }
    #[test]
    fn changed_interval_reschedules_without_an_extra_activation() {
        let schedule = CoreUpdates::default();
        let now = Instant::now();
        schedule.configure_at(
            Policy {
                enabled: true,
                interval_hours: 24,
            },
            now,
        );
        let job = schedule.take_due_at(1, now).unwrap();
        schedule.finish(&job, Err(AppError::new("download_failed", "网络不可用")));
        schedule.configure_at(
            Policy {
                enabled: true,
                interval_hours: 2,
            },
            now + Duration::from_secs(30),
        );
        assert!(schedule
            .take_due_at(1, now + Duration::from_secs(30))
            .is_none());
        assert_eq!(
            schedule
                .snapshot_at(now + Duration::from_secs(30))
                .next_check_in_ms,
            Some(7_200_000)
        );
        schedule.configure_at(Policy::default(), now);
        assert!(schedule
            .take_due_at(1, now + Duration::from_secs(100_000))
            .is_none());
        schedule.configure_at(
            Policy {
                enabled: true,
                interval_hours: 2,
            },
            now,
        );
        assert!(schedule.take_due_at(1, now).is_some());
    }
    #[test]
    fn manual_request_is_idempotent_and_works_with_automatic_updates_disabled() {
        let schedule = CoreUpdates::default();
        schedule.request("request-1", 7).unwrap();
        schedule.request("request-1", 7).unwrap();
        let job = schedule.take_due(7).unwrap();
        assert_eq!(
            schedule.request("different", 7).unwrap_err().code,
            "core_update_busy"
        );
        schedule.finish(
            &job,
            Ok(UpdateOutcome {
                current_version: "1.0.29".into(),
                latest_version: "1.0.29".into(),
                updated: true,
            }),
        );
        assert_eq!(schedule.request("request-1", 8).unwrap().status, "updated");
        assert!(schedule.take_due(8).is_none());
    }
    #[test]
    fn a_later_user_intent_cancels_an_accepted_but_not_started_request() {
        let schedule = CoreUpdates::default();
        schedule.request("request-1", 7).unwrap();
        assert!(schedule.take_due(8).is_none());
        assert_eq!(schedule.snapshot().reason, "operation_cancelled");
        assert_eq!(schedule.request("request-1", 8).unwrap().status, "skipped");
        assert!(schedule.take_due(8).is_none());
    }
    #[test]
    fn manual_check_consumes_initial_automatic_check() {
        let schedule = CoreUpdates::default();
        let now = Instant::now();
        schedule.configure_at(
            Policy {
                enabled: true,
                interval_hours: 24,
            },
            now,
        );
        schedule.request("manual-first", 1).unwrap();
        let job = schedule.take_due_at(1, now).unwrap();
        assert!(!job.automatic);
        schedule.finish(
            &job,
            Ok(UpdateOutcome {
                current_version: "1.0.29".into(),
                latest_version: "1.0.29".into(),
                updated: false,
            }),
        );
        assert!(schedule
            .take_due_at(1, now + Duration::from_secs(1))
            .is_none());
        assert!(schedule
            .take_due_at(1, now + Duration::from_secs(86_400))
            .is_some());
    }
}
