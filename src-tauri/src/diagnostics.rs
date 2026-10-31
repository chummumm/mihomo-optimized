//! Bounded lifecycle evidence. Callers supply fixed categories and numeric IDs,
//! never controller credentials, configuration, subscription URLs or payloads.
use serde_json::Value;
use std::{fs, io::Write, path::Path, sync::Mutex, time::SystemTime};

static WRITE_LOCK: Mutex<()> = Mutex::new(());
const MAX_LOG_BYTES: u64 = 1024 * 1024;

pub fn record(directory: &Path, event: &str, fields: Value) {
    if directory.as_os_str().is_empty() {
        return;
    }
    let _guard = WRITE_LOCK.lock().unwrap_or_else(|error| error.into_inner());
    let _ = (|| -> std::io::Result<()> {
        fs::create_dir_all(directory)?;
        let path = directory.join("recovery.log");
        if fs::metadata(&path).is_ok_and(|metadata| metadata.len() >= MAX_LOG_BYTES) {
            let previous = directory.join("recovery.previous.log");
            if previous.exists() {
                fs::remove_file(&previous)?;
            }
            fs::rename(&path, previous)?;
        }
        let mut file = fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(path)?;
        let time = SystemTime::now()
            .duration_since(SystemTime::UNIX_EPOCH)
            .unwrap_or_default()
            .as_millis();
        writeln!(
            file,
            "{}",
            serde_json::json!({"timeMs":time,"event":event,"processId":std::process::id(),"data":fields})
        )
    })();
}
