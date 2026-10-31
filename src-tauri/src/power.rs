//! No window or UI thread is needed for the Rust host's power notifications.
use crate::{
    error::{AppError, Result},
    lifecycle::PowerSignal,
};
use std::{ffi::c_void, sync::Arc};
use windows_sys::Win32::{
    System::Power::{
        PowerRegisterSuspendResumeNotification, PowerUnregisterSuspendResumeNotification,
        DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS, HPOWERNOTIFY,
    },
    UI::WindowsAndMessaging::{
        DEVICE_NOTIFY_CALLBACK, PBT_APMRESUMEAUTOMATIC, PBT_APMRESUMESUSPEND, PBT_APMSUSPEND,
    },
};

pub struct Registration {
    handle: HPOWERNOTIFY,
    context: *const PowerSignal,
}

impl Registration {
    pub fn new(signal: Arc<PowerSignal>) -> Result<Self> {
        let context = Arc::into_raw(signal);
        let mut parameters = DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS {
            Callback: Some(callback),
            Context: context.cast_mut().cast(),
        };
        let mut handle = std::ptr::null_mut();
        let status = unsafe {
            PowerRegisterSuspendResumeNotification(
                DEVICE_NOTIFY_CALLBACK,
                (&mut parameters as *mut DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS).cast(),
                &mut handle,
            )
        };
        // This API returns a DWORD status, not GetLastError's thread-local value.
        if status != 0 {
            unsafe {
                drop(Arc::from_raw(context));
            }
            return Err(AppError::new(
                "power_notification_failed",
                format!("无法注册系统恢复通知（系统错误：{status}）"),
            ));
        }
        // windows-sys exposes the register output as void**, but the unregister
        // input as pointer-sized HPOWERNOTIFY. Preserve the opaque bits once.
        Ok(Self {
            handle: handle as HPOWERNOTIFY,
            context,
        })
    }
}

unsafe extern "system" fn callback(
    context: *const c_void,
    event: u32,
    _setting: *const c_void,
) -> u32 {
    let signal = &*context.cast::<PowerSignal>();
    match event {
        PBT_APMSUSPEND => signal.suspend(),
        PBT_APMRESUMEAUTOMATIC => signal.resume(true),
        PBT_APMRESUMESUSPEND => signal.resume(false),
        _ => {}
    }
    0
}

impl Drop for Registration {
    fn drop(&mut self) {
        if unsafe { PowerUnregisterSuspendResumeNotification(self.handle) } == 0 {
            unsafe {
                drop(Arc::from_raw(self.context));
            }
        }
        // If unregister fails, retain the callback's Arc for process lifetime;
        // freeing it while Windows may invoke the callback would be unsafe.
    }
}
