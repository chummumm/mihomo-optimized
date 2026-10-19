#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

fn main() {
    #[cfg(windows)]
    if let Err(error) = mihomo_desktop::service::entry() {
        eprintln!("{error}");
        std::process::exit(1);
    }
    #[cfg(not(windows))]
    {
        eprintln!("Windows service is only supported on Windows.");
        std::process::exit(1);
    }
}
