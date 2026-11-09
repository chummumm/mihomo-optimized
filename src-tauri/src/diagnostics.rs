//! Bounded desktop lifecycle evidence. No proxy payloads, configuration or
//! credentials belong here. Producers never wait for filesystem I/O.
use serde_json::{Map, Value};
use std::{
    cell::RefCell,
    fs,
    io::Write,
    marker::PhantomData,
    path::{Path, PathBuf},
    rc::Rc,
    sync::{
        atomic::{AtomicU64, Ordering},
        mpsc::{self, SyncSender, TrySendError},
        Arc, OnceLock,
    },
    thread,
    time::{Duration, Instant, SystemTime},
};

const MAX_LOG_BYTES: u64 = 1024 * 1024;
const MAX_EVENT_BYTES: usize = 16 * 1024;
const QUEUE_CAPACITY: usize = 256;
static WRITER: OnceLock<Option<Writer>> = OnceLock::new();
static PROCESS_INSTANCE: OnceLock<String> = OnceLock::new();
static STARTED: OnceLock<Instant> = OnceLock::new();
static SEQUENCE: AtomicU64 = AtomicU64::new(0);

thread_local! {
    static CONTEXT: RefCell<Map<String, Value>> = RefCell::new(Map::new());
}

/// A local execution context, not an authority or protocol field. It must stay
/// on its creating thread; nested scopes restore the previous context exactly.
pub struct DiagnosticScope {
    previous: Option<Map<String, Value>>,
    _thread_bound: PhantomData<Rc<()>>,
}

pub fn scope(fields: Value) -> DiagnosticScope {
    let previous = CONTEXT.with(|context| {
        let mut context = context.borrow_mut();
        let previous = context.clone();
        if let Value::Object(fields) = fields {
            context.extend(fields);
        }
        previous
    });
    DiagnosticScope {
        previous: Some(previous),
        _thread_bound: PhantomData,
    }
}

impl Drop for DiagnosticScope {
    fn drop(&mut self) {
        if let Some(previous) = self.previous.take() {
            CONTEXT.with(|context| *context.borrow_mut() = previous);
        }
    }
}

#[derive(Default)]
struct Counters {
    dropped: AtomicU64,
    write_failures: AtomicU64,
}

struct Entry {
    directory: PathBuf,
    line: Vec<u8>,
}

enum Message {
    Entry(Entry),
    Flush(SyncSender<()>),
}

struct Writer {
    sender: SyncSender<Message>,
    counters: Arc<Counters>,
}

impl Writer {
    fn start() -> Option<Self> {
        let (sender, receiver) = mpsc::sync_channel(QUEUE_CAPACITY);
        let counters = Arc::new(Counters::default());
        let worker_counters = counters.clone();
        thread::Builder::new()
            .name("desktop-diagnostics".into())
            .spawn(move || {
                for message in receiver {
                    match message {
                        Message::Entry(entry) => {
                            if write_entry(&entry).is_err() {
                                // The next accepted event exposes the cumulative
                                // failure count. Never echo paths or OS messages.
                                worker_counters
                                    .write_failures
                                    .fetch_add(1, Ordering::Relaxed);
                            }
                        }
                        Message::Flush(complete) => {
                            let _ = complete.try_send(());
                        }
                    }
                }
            })
            .ok()?;
        Some(Self { sender, counters })
    }

    fn enqueue(&self, entry: Entry) {
        if self.sender.try_send(Message::Entry(entry)).is_err() {
            self.counters.dropped.fetch_add(1, Ordering::Relaxed);
        }
    }
}

fn process_instance() -> &'static str {
    PROCESS_INSTANCE.get_or_init(|| {
        let mut random = [0u8; 16];
        if getrandom::getrandom(&mut random).is_ok() {
            hex::encode(random)
        } else {
            // A process-lifetime correlation key, never an authentication token.
            format!("{}-{}", std::process::id(), timestamp_ms())
        }
    })
}

fn timestamp_ms() -> u128 {
    SystemTime::now()
        .duration_since(SystemTime::UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis()
}

pub fn record(directory: &Path, event: &str, fields: Value) {
    if directory.as_os_str().is_empty() {
        return;
    }
    let Some(writer) = WRITER.get_or_init(Writer::start) else {
        return;
    };
    let context = CONTEXT.with(|context| context.borrow().clone());
    let value = serde_json::json!({
        "schemaVersion": 1,
        "timeMs": timestamp_ms(),
        "elapsedMs": STARTED.get_or_init(Instant::now).elapsed().as_millis(),
        "sequence": SEQUENCE.fetch_add(1, Ordering::Relaxed) + 1,
        "processId": std::process::id(),
        "processInstance": process_instance(),
        "desktopVersion": env!("CARGO_PKG_VERSION"),
        "event": event,
        "context": context,
        "data": fields,
        "diagnostics": {
            "droppedEvents": writer.counters.dropped.load(Ordering::Relaxed),
            "writeFailures": writer.counters.write_failures.load(Ordering::Relaxed),
        },
    });
    let Ok(mut line) = serde_json::to_vec(&value) else {
        writer.counters.dropped.fetch_add(1, Ordering::Relaxed);
        return;
    };
    if line.len() >= MAX_EVENT_BYTES {
        writer.counters.dropped.fetch_add(1, Ordering::Relaxed);
        return;
    }
    line.push(b'\n');
    writer.enqueue(Entry {
        directory: directory.into(),
        line,
    });
}

fn write_entry(entry: &Entry) -> std::io::Result<()> {
    fs::create_dir_all(&entry.directory)?;
    let path = entry.directory.join("recovery.log");
    if fs::metadata(&path).is_ok_and(|metadata| metadata.len() >= MAX_LOG_BYTES) {
        let previous = entry.directory.join("recovery.previous.log");
        if previous.exists() {
            fs::remove_file(&previous)?;
        }
        fs::rename(&path, previous)?;
    }
    fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(path)?
        .write_all(&entry.line)
}

/// Wait only at an outer shutdown boundary or before reading test evidence.
/// A stalled disk can consume at most this caller's budget, never a core budget.
pub fn flush(timeout: Duration) -> bool {
    let Some(Some(writer)) = WRITER.get() else {
        return true;
    };
    let Some(deadline) = Instant::now().checked_add(timeout) else {
        return false;
    };
    let (complete, receiver) = mpsc::sync_channel(1);
    let mut message = Message::Flush(complete);
    loop {
        match writer.sender.try_send(message) {
            Ok(()) => break,
            Err(TrySendError::Disconnected(_)) => return false,
            Err(TrySendError::Full(pending)) => {
                message = pending;
                let Some(left) = deadline.checked_duration_since(Instant::now()) else {
                    return false;
                };
                thread::sleep(left.min(Duration::from_millis(1)));
            }
        }
    }
    let Some(left) = deadline.checked_duration_since(Instant::now()) else {
        return false;
    };
    receiver.recv_timeout(left).is_ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn directory(label: &str) -> PathBuf {
        std::env::temp_dir().join(format!(
            "mihomo-diagnostics-{label}-{}-{}",
            std::process::id(),
            SEQUENCE.fetch_add(1, Ordering::Relaxed)
        ))
    }

    #[test]
    fn nested_context_and_enqueue_time_survive_async_writing() {
        let directory = directory("context");
        {
            let _outer = scope(serde_json::json!({"requestId": 7,"cause":"ui_command"}));
            record(&directory, "accepted", serde_json::json!({}));
            {
                let _inner =
                    scope(serde_json::json!({"cause":"start_replace","operationId":"op-1"}));
                record(&directory, "stop", serde_json::json!({"corePid":42}));
            }
            record(&directory, "result", serde_json::json!({}));
        }
        record(&directory, "poll", serde_json::json!({}));
        assert!(flush(Duration::from_secs(2)));
        let events: Vec<Value> = fs::read_to_string(directory.join("recovery.log"))
            .unwrap()
            .lines()
            .map(|line| serde_json::from_str(line).unwrap())
            .collect();
        assert_eq!(events.len(), 4);
        assert_eq!(events[1]["context"]["requestId"], 7);
        assert_eq!(events[1]["context"]["cause"], "start_replace");
        assert_eq!(events[2]["context"]["cause"], "ui_command");
        assert!(events[2]["context"].get("operationId").is_none());
        assert!(events[3]["context"].as_object().unwrap().is_empty());
        assert!(events
            .windows(2)
            .all(|pair| pair[0]["sequence"].as_u64() < pair[1]["sequence"].as_u64()));
        assert!(events.iter().all(|event| event["processInstance"]
            == events[0]["processInstance"]
            && event["desktopVersion"] == env!("CARGO_PKG_VERSION")));
        fs::remove_dir_all(directory).unwrap();
    }

    #[test]
    fn a_full_queue_drops_without_waiting_for_the_writer() {
        let (sender, _receiver) = mpsc::sync_channel(1);
        let writer = Writer {
            sender,
            counters: Arc::new(Counters::default()),
        };
        let entry = || Entry {
            directory: PathBuf::new(),
            line: b"{}\n".to_vec(),
        };
        writer.enqueue(entry());
        let started = Instant::now();
        for _ in 0..1000 {
            writer.enqueue(entry());
        }
        assert!(started.elapsed() < Duration::from_secs(1));
        assert_eq!(writer.counters.dropped.load(Ordering::Relaxed), 1000);
    }

    #[test]
    fn write_failure_does_not_block_later_evidence_and_rotation_is_bounded() {
        let directory = directory("failure");
        fs::write(&directory, b"a file cannot contain a log").unwrap();
        record(&directory, "write_failure_fixture", serde_json::json!({}));
        assert!(flush(Duration::from_secs(2)));
        fs::remove_file(&directory).unwrap();
        fs::create_dir(&directory).unwrap();
        fs::write(
            directory.join("recovery.log"),
            vec![b' '; MAX_LOG_BYTES as usize],
        )
        .unwrap();
        record(&directory, "healthy_again", serde_json::json!({}));
        assert!(flush(Duration::from_secs(2)));
        let event: Value =
            serde_json::from_str(&fs::read_to_string(directory.join("recovery.log")).unwrap())
                .unwrap();
        assert!(event["diagnostics"]["writeFailures"].as_u64().unwrap() >= 1);
        assert_eq!(
            fs::metadata(directory.join("recovery.previous.log"))
                .unwrap()
                .len(),
            MAX_LOG_BYTES
        );
        assert!(
            fs::metadata(directory.join("recovery.log")).unwrap().len() < MAX_EVENT_BYTES as u64
        );
        fs::remove_dir_all(directory).unwrap();
    }
}
