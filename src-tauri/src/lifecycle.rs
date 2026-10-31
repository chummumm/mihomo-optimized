//! Small, testable policies shared by the host and privileged service. Deadlines
//! for user operations remain separate from the authenticated owner's lease.
use crate::error::{AppError, Result};
use serde::{Deserialize, Serialize};
use std::{
    sync::atomic::{AtomicBool, AtomicU64, Ordering},
    time::Duration,
};

pub const OWNER_LEASE: Duration = Duration::from_secs(20);
const RESUME_GRACE: Duration = Duration::from_secs(5);
pub const HEALTH_INTERVAL: Duration = Duration::from_secs(10);
pub const RECOVERY_WINDOW: Duration = Duration::from_secs(20);

pub trait AwakeClock {
    fn now(&self) -> Result<Duration>;
}

pub struct SystemAwakeClock;

impl AwakeClock for SystemAwakeClock {
    fn now(&self) -> Result<Duration> {
        awake_now()
    }
}

/// QPC/Instant includes Windows sleep. Only this lease/health clock excludes it;
/// resource downloads and explicit operation deadlines retain their own budget.
pub fn awake_now() -> Result<Duration> {
    #[cfg(windows)]
    {
        let mut ticks = 0;
        if unsafe {
            windows_sys::Win32::System::WindowsProgramming::QueryUnbiasedInterruptTime(&mut ticks)
        } == 0
        {
            return Err(crate::windows::last_error("无法读取系统清醒时间"));
        }
        Ok(
            Duration::from_secs(ticks / 10_000_000)
                + Duration::from_nanos(ticks % 10_000_000 * 100),
        )
    }
    #[cfg(not(windows))]
    {
        use std::sync::OnceLock;
        use std::time::Instant;
        static START: OnceLock<Instant> = OnceLock::new();
        Ok(START.get_or_init(Instant::now).elapsed())
    }
}

pub struct OwnerLease<C> {
    clock: C,
    last_seen: Duration,
    resume_grace_until: Option<Duration>,
}

impl<C: AwakeClock> OwnerLease<C> {
    pub fn new(clock: C) -> Result<Self> {
        let last_seen = clock.now()?;
        Ok(Self {
            clock,
            last_seen,
            resume_grace_until: None,
        })
    }

    /// Called only after an authenticated request has completed. Invalid peers,
    /// power notifications and cached UI status queries cannot renew this lease.
    pub fn renew(&mut self) -> Result<()> {
        self.last_seen = self.clock.now()?;
        self.resume_grace_until = None;
        Ok(())
    }

    /// Only after revalidating the retained authenticated owner handle. A
    /// resume does not count as a heartbeat, and duplicate signals cannot keep
    /// extending this scheduling allowance.
    pub fn resume_verified(&mut self) -> Result<()> {
        if self.resume_grace_until.is_none() {
            self.resume_grace_until = Some(self.clock.now()? + RESUME_GRACE);
        }
        Ok(())
    }

    pub fn elapsed(&self) -> Result<Duration> {
        self.clock
            .now()?
            .checked_sub(self.last_seen)
            .ok_or_else(|| {
                AppError::new(
                    "lease_clock_invalid",
                    "系统清醒时间发生异常，已停止特权路由",
                )
            })
    }

    pub fn expired(&self) -> Result<bool> {
        let expired = self.elapsed()? > OWNER_LEASE;
        let grace_elapsed = match self.resume_grace_until {
            Some(until) => self.clock.now()? >= until,
            None => true,
        };
        Ok(expired && grace_elapsed)
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum StopReason {
    UserStop,
    OwnerLost,
    LeaseExpired,
    UnexpectedExit,
    RecoveryExhausted,
    HealthFailure,
    Shutdown,
}

#[derive(Default)]
pub struct PowerSignal {
    suspended: AtomicBool,
    generation: AtomicU64,
}

impl PowerSignal {
    pub fn suspend(&self) {
        self.suspended.store(true, Ordering::Release);
    }
    pub fn resume(&self, automatic: bool) {
        // Windows normally sends Automatic followed by ResumeSuspend. Count
        // that pair once, while still accepting Automatic after a missed Suspend.
        let was_suspended = self.suspended.swap(false, Ordering::AcqRel);
        if automatic || was_suspended {
            self.generation.fetch_add(1, Ordering::AcqRel);
        }
    }
    pub fn generation(&self) -> u64 {
        self.generation.load(Ordering::Acquire)
    }
    pub fn suspended(&self) -> bool {
        self.suspended.load(Ordering::Acquire)
    }
}

/// Repeated notifications coalesce into the same bounded recovery window. They
/// never grant another window or reset the number of failed observations.
pub struct RecoveryWindow {
    pub generation: u64,
    started: Duration,
    failures: usize,
}

impl RecoveryWindow {
    pub fn new(generation: u64, now: Duration) -> Self {
        Self {
            generation,
            started: now,
            failures: 0,
        }
    }

    pub fn coalesce(&mut self, generation: u64) {
        self.generation = self.generation.max(generation);
    }

    pub fn failed(&mut self, now: Duration) -> bool {
        self.failures += 1;
        self.failures >= 3 || now.saturating_sub(self.started) >= RECOVERY_WINDOW
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{cell::Cell, rc::Rc};

    #[derive(Clone)]
    struct Clock(Rc<Cell<Duration>>);
    impl AwakeClock for Clock {
        fn now(&self) -> Result<Duration> {
            Ok(self.0.get())
        }
    }

    #[test]
    fn both_resume_orders_preserve_the_authenticated_lease() {
        for heartbeat_first in [false, true] {
            let awake = Clock(Rc::new(Cell::new(Duration::from_secs(100))));
            let mut lease = OwnerLease::new(awake.clone()).unwrap();
            // Ten hours of system sleep do not advance the injected awake clock.
            awake.0.set(Duration::from_secs(102));
            if heartbeat_first {
                lease.renew().unwrap();
            }
            assert!(!lease.expired().unwrap());
            if !heartbeat_first {
                lease.renew().unwrap();
            }
            awake.0.set(Duration::from_secs(122));
            assert!(!lease.expired().unwrap());
            awake.0.set(Duration::from_secs(123));
            assert!(
                lease.expired().unwrap(),
                "a truly absent awake owner must expire"
            );
        }
    }

    #[test]
    fn unauthenticated_activity_and_resume_do_not_renew_a_lease() {
        let awake = Clock(Rc::new(Cell::new(Duration::ZERO)));
        let lease = OwnerLease::new(awake.clone()).unwrap();
        awake.0.set(Duration::from_secs(21));
        assert!(lease.expired().unwrap());
        awake.0.set(Duration::from_secs(22));
        assert!(lease.expired().unwrap());
    }

    #[test]
    fn verified_resume_grace_is_short_and_duplicate_events_cannot_renew_it() {
        let awake = Clock(Rc::new(Cell::new(Duration::ZERO)));
        let mut lease = OwnerLease::new(awake.clone()).unwrap();
        awake.0.set(Duration::from_secs(19));
        lease.resume_verified().unwrap();
        awake.0.set(Duration::from_secs(22));
        assert!(!lease.expired().unwrap());
        lease.resume_verified().unwrap();
        awake.0.set(Duration::from_secs(24));
        assert!(
            lease.expired().unwrap(),
            "a live but silent owner must still expire"
        );
        lease.renew().unwrap();
        assert!(!lease.expired().unwrap());
    }

    #[test]
    fn a_regressing_clock_fails_closed_instead_of_extending_a_lease() {
        let awake = Clock(Rc::new(Cell::new(Duration::from_secs(100))));
        let lease = OwnerLease::new(awake.clone()).unwrap();
        awake.0.set(Duration::from_secs(99));
        assert!(lease.expired().is_err());
    }

    #[test]
    fn repeated_resume_cannot_extend_recovery() {
        let mut recovery = RecoveryWindow::new(1, Duration::ZERO);
        recovery.coalesce(9);
        assert_eq!(recovery.generation, 9);
        assert!(!recovery.failed(Duration::from_secs(1)));
        recovery.coalesce(10);
        assert!(recovery.failed(RECOVERY_WINDOW));
    }

    #[test]
    fn automatic_and_interactive_resume_are_one_generation() {
        let events = PowerSignal::default();
        events.suspend();
        events.resume(true);
        events.resume(false);
        assert_eq!(events.generation(), 1);
        events.suspend();
        events.resume(false);
        assert_eq!(events.generation(), 2);
    }
}
