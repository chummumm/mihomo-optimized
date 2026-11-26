#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

#[cfg(windows)]
fn main() {
    use std::{fs::OpenOptions, io::Write};

    let args: Vec<String> = std::env::args().skip(1).collect();
    let (mut error_report, args) = match args.as_slice() {
        [option, path, args @ ..] if option == "--nsis-error-file" => {
            // NSIS supplies a fresh path in its administrator-only maintenance
            // directory. Open before maintenance, and never overwrite a file.
            let report = match OpenOptions::new().write(true).create_new(true).open(path) {
                Ok(report) => report,
                Err(error) => {
                    eprintln!(
                        "nsis_error_report_open_failed: kind={:?}, os_error={:?}",
                        error.kind(),
                        error.raw_os_error()
                    );
                    std::process::exit(1);
                }
            };
            (Some(report), args)
        }
        args => (None, args),
    };

    if let Err(error) = mihomo_desktop::service::entry(args) {
        if let Some(report) = &mut error_report {
            // nsExec treats narrow pipe output as the Windows ANSI code page,
            // not UTF-8. FileReadUTF16LE gives NSIS an explicit Unicode boundary.
            let bytes: Vec<u8> = std::iter::once(0xfeff)
                .chain(error.to_string().encode_utf16())
                .flat_map(u16::to_le_bytes)
                .collect();
            if let Err(write_error) = report.write_all(&bytes) {
                eprintln!(
                    "nsis_error_report_write_failed: kind={:?}, os_error={:?}, helper={}",
                    write_error.kind(),
                    write_error.raw_os_error(),
                    error.code
                );
            }
        } else {
            // Ordinary CLI and diagnostic callers retain their UTF-8 contract.
            eprintln!("{error}");
        }
        std::process::exit(1);
    }
}

#[cfg(not(windows))]
fn main() {
    eprintln!("Windows service is only supported on Windows.");
    std::process::exit(1);
}
