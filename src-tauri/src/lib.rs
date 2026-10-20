#[cfg(all(windows, feature = "desktop"))]
mod commands;
pub mod config;
pub mod core;
#[cfg(all(windows, feature = "desktop"))]
pub mod desktop;
pub mod error;
#[cfg(windows)]
mod firewall;
#[cfg(windows)]
pub mod proxy;
#[cfg(windows)]
pub mod service;
#[cfg(windows)]
pub mod service_ipc;
#[cfg(all(windows, feature = "desktop"))]
mod update;
#[cfg(all(windows, feature = "desktop"))]
mod update_cache;
#[cfg(windows)]
pub mod windows;

#[cfg(all(windows, feature = "desktop"))]
pub fn run() {
    if update::run_monitor_if_requested() {
        return;
    }
    desktop::run();
}
#[cfg(all(not(windows), feature = "desktop"))]
pub fn run() {
    eprintln!("This desktop application currently supports Windows only.");
}
