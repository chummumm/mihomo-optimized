//! Durable replacement of one fixed executable. The journal contains hashes,
//! never paths; the process owner decides when all relevant children are gone.
use crate::{
    error::{AppError, Result},
    lifecycle::Cancellation,
};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    fs::{self, File, OpenOptions},
    io::{Read, Write},
    path::{Path, PathBuf},
};

const MAX_CORE_SIZE: u64 = 256 * 1024 * 1024;
const MAX_JOURNAL_SIZE: u64 = 4096;

#[derive(Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct Journal {
    previous_sha256: String,
    candidate_sha256: String,
    accepted: bool,
}

pub(super) struct CoreReplacement {
    executable: PathBuf,
    journal: Journal,
}

fn paths(executable: &Path) -> Result<(PathBuf, PathBuf)> {
    let name = executable
        .file_name()
        .and_then(|value| value.to_str())
        .ok_or_else(|| AppError::new("core_update_path", "内核路径无效"))?;
    Ok((
        executable.with_file_name(format!("{name}.previous")),
        executable.with_file_name(format!("{name}.update.json")),
    ))
}

fn normal_file(path: &Path, limit: u64) -> Result<File> {
    let metadata =
        fs::symlink_metadata(path).map_err(|error| AppError::io("无法检查内核更新文件", error))?;
    if !metadata.is_file() || metadata.len() > limit {
        return Err(AppError::new(
            "core_update_file",
            "内核更新文件类型或大小无效",
        ));
    }
    #[cfg(windows)]
    {
        crate::windows::reject_reparse_path(path)?;
        crate::windows::reject_hard_links(path)?;
    }
    #[cfg(unix)]
    {
        use std::os::unix::fs::MetadataExt;
        if metadata.nlink() != 1 {
            return Err(AppError::new(
                "core_update_file",
                "内核更新文件不能是硬链接",
            ));
        }
    }
    File::open(path).map_err(|error| AppError::io("无法读取内核更新文件", error))
}

fn digest(path: &Path) -> Result<String> {
    let mut reader = normal_file(path, MAX_CORE_SIZE)?.take(MAX_CORE_SIZE + 1);
    let mut digest = Sha256::new();
    let mut count = 0;
    let mut buffer = [0u8; 64 * 1024];
    loop {
        let read = reader
            .read(&mut buffer)
            .map_err(|error| AppError::io("无法校验内核更新文件", error))?;
        if read == 0 {
            break;
        }
        count += read as u64;
        if count > MAX_CORE_SIZE {
            return Err(AppError::new(
                "core_update_file",
                "内核更新文件超出大小限制",
            ));
        }
        digest.update(&buffer[..read]);
    }
    Ok(hex::encode(digest.finalize()))
}

fn require_digest(path: &Path, expected: &str) -> Result<()> {
    if digest(path)? != expected {
        return Err(AppError::new(
            "core_update_changed",
            "内核更新文件已发生变化，已停止替换",
        ));
    }
    Ok(())
}

fn exists(path: &Path) -> Result<bool> {
    match fs::symlink_metadata(path) {
        Ok(_) => Ok(true),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(false),
        Err(error) => Err(AppError::io("无法检查内核更新状态", error)),
    }
}

fn read_journal(marker: &Path) -> Result<Option<Journal>> {
    if !exists(marker)? {
        return Ok(None);
    }
    let reader = normal_file(marker, MAX_JOURNAL_SIZE)?;
    let journal: Journal = serde_json::from_reader(reader.take(MAX_JOURNAL_SIZE + 1))
        .map_err(|_| AppError::new("core_update_journal", "内核更新记录损坏，不能自动恢复"))?;
    for value in [&journal.previous_sha256, &journal.candidate_sha256] {
        if value.len() != 64 || !value.bytes().all(|c| c.is_ascii_hexdigit()) {
            return Err(AppError::new(
                "core_update_journal",
                "内核更新记录校验值无效",
            ));
        }
    }
    Ok(Some(journal))
}

fn sync_directory(executable: &Path) -> Result<()> {
    #[cfg(unix)]
    File::open(
        executable
            .parent()
            .ok_or_else(|| AppError::new("core_update_path", "内核目录无效"))?,
    )
    .and_then(|directory| directory.sync_all())
    .map_err(|error| AppError::io("无法保存内核更新目录", error))?;
    #[cfg(not(unix))]
    let _ = executable;
    Ok(())
}

impl CoreReplacement {
    pub(super) fn install(
        executable: &Path,
        candidate: &Path,
        candidate_sha256: &str,
        cancellation: &Cancellation,
    ) -> Result<Self> {
        cancellation.check()?;
        let (backup, marker) = paths(executable)?;
        if exists(&backup)? || exists(&marker)? {
            return Err(AppError::new(
                "core_update_pending",
                "上次内核替换尚未完成，请先恢复后重试",
            ));
        }
        require_digest(candidate, candidate_sha256)?;
        #[cfg(windows)]
        if crate::windows::current_sid()? == "S-1-5-18" {
            // SYSTEM staging is private. A same-volume rename preserves that
            // DACL, so publish only the verified executable with the installer's
            // ordinary-user read/execute rights; directories stay protected.
            crate::windows::apply_dacl(candidate, "O:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;BU)")?;
            crate::windows::require_admin_protected(candidate)?;
        }
        let journal = Journal {
            previous_sha256: digest(executable)?,
            candidate_sha256: candidate_sha256.into(),
            accepted: false,
        };
        let mut file = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&marker)
            .map_err(|error| AppError::io("无法创建内核更新记录", error))?;
        let encoded = serde_json::to_vec(&journal)
            .map_err(|_| AppError::new("core_update_journal", "无法保存内核更新记录"))?;
        if let Err(error) = file.write_all(&encoded).and_then(|_| file.sync_all()) {
            drop(file);
            let _ = fs::remove_file(&marker);
            return Err(AppError::io("无法保存内核更新记录", error));
        }
        drop(file);
        sync_directory(executable)?;
        let transaction = Self {
            executable: executable.into(),
            journal,
        };
        let install = (|| {
            cancellation.check()?;
            require_digest(executable, &transaction.journal.previous_sha256)?;
            fs::rename(executable, &backup)
                .map_err(|error| AppError::io("无法备份正在替换的内核", error))?;
            cancellation.check()?;
            // Stage and destination share an installation filesystem. A move
            // does not expose a partially copied executable.
            fs::rename(candidate, executable)
                .map_err(|error| AppError::io("无法切换新内核文件", error))?;
            sync_directory(executable)?;
            cancellation.check()
        })();
        if let Err(error) = install {
            if let Err(rollback) = transaction.rollback() {
                return Err(AppError::new(
                    "core_update_restore_failed",
                    format!("{}；恢复旧内核失败：{}", error.message, rollback.message),
                ));
            }
            return Err(error);
        }
        Ok(transaction)
    }

    /// The caller has confirmed that no new child remains alive. Never call
    /// from Drop: an unconfirmed Windows process must retain its ownership.
    pub(super) fn rollback(&self) -> Result<()> {
        let (backup, marker) = paths(&self.executable)?;
        if exists(&backup)? {
            require_digest(&backup, &self.journal.previous_sha256)?;
            if exists(&self.executable)? {
                require_digest(&self.executable, &self.journal.candidate_sha256)?;
                fs::remove_file(&self.executable)
                    .map_err(|error| AppError::io("无法移除待回滚的新内核", error))?;
            }
            fs::rename(&backup, &self.executable)
                .map_err(|error| AppError::io("无法恢复旧内核文件", error))?;
        } else {
            // Either the first rename never happened, or recovery already
            // restored the old file before a process interruption.
            require_digest(&self.executable, &self.journal.previous_sha256)?;
        }
        sync_directory(&self.executable)?;
        if exists(&marker)? {
            fs::remove_file(marker).map_err(|error| AppError::io("无法清理内核回滚记录", error))?;
        }
        sync_directory(&self.executable)
    }

    pub(super) fn accept(&mut self) -> Result<()> {
        let (backup, marker) = paths(&self.executable)?;
        require_digest(&self.executable, &self.journal.candidate_sha256)?;
        self.journal.accepted = true;
        // An interrupted cleanup must retain the verified new version instead
        // of undoing a successful, acknowledged startup.
        let encoded = serde_json::to_vec(&self.journal)
            .map_err(|_| AppError::new("core_update_journal", "无法保存内核更新结果"))?;
        crate::config::atomic_write(&marker, &encoded)?;
        // Windows FlushFileBuffers requires a writable handle. atomic_write
        // flushed the contents before rename; flush the published file too and
        // the containing directory on systems that expose that operation.
        OpenOptions::new()
            .write(true)
            .open(&marker)
            .map_err(|error| AppError::io("无法读取内核更新结果", error))?
            .sync_all()
            .map_err(|error| AppError::io("无法保存内核更新结果", error))?;
        sync_directory(&self.executable)?;
        // The acknowledgement is durable. A locked backup only delays cleanup;
        // it must not turn a successful update into an attempted rollback.
        let _ = self.cleanup_accepted(&backup, &marker);
        Ok(())
    }

    fn cleanup_accepted(&self, backup: &Path, marker: &Path) -> Result<()> {
        require_digest(&self.executable, &self.journal.candidate_sha256)?;
        if exists(backup)? {
            require_digest(backup, &self.journal.previous_sha256)?;
            fs::remove_file(backup)
                .map_err(|error| AppError::io("无法清理已完成的内核备份", error))?;
        }
        fs::remove_file(marker)
            .map_err(|error| AppError::io("无法清理已完成的内核更新记录", error))?;
        sync_directory(&self.executable)
    }

    pub(super) fn has_unconfirmed(executable: &Path) -> Result<bool> {
        let (_, marker) = paths(executable)?;
        Ok(read_journal(&marker)?.is_some_and(|journal| !journal.accepted))
    }

    pub(super) fn needs_recovery(executable: &Path) -> Result<bool> {
        let (_, marker) = paths(executable)?;
        exists(&marker)
    }

    pub(super) fn recover(executable: &Path, restore_unconfirmed: bool) -> Result<()> {
        let (backup, marker) = paths(executable)?;
        let Some(journal) = read_journal(&marker)? else {
            return Ok(());
        };
        let transaction = Self {
            executable: executable.into(),
            journal,
        };
        if transaction.journal.accepted {
            transaction.cleanup_accepted(&backup, &marker)
        } else if restore_unconfirmed || !exists(executable)? || !exists(&backup)? {
            transaction.rollback()
        } else {
            // Service restart may coincide with a direct child still owned by
            // its authenticated host. Only repair the rename gap at startup;
            // the next confirmed lifecycle boundary performs full rollback.
            require_digest(executable, &transaction.journal.candidate_sha256)
        }
    }
}

pub(super) fn journal_paths(executable: &Path) -> Result<(PathBuf, PathBuf)> {
    paths(executable)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::{atomic::AtomicU64, Arc};

    struct Fixture {
        directory: PathBuf,
        executable: PathBuf,
        candidate: PathBuf,
        cancellation: Cancellation,
    }

    impl Fixture {
        fn new() -> Self {
            let mut random = [0u8; 12];
            getrandom::getrandom(&mut random).unwrap();
            let directory =
                std::env::temp_dir().join(format!("core-replace-{}", hex::encode(random)));
            fs::create_dir(&directory).unwrap();
            let executable = directory.join("mihomo-core.exe");
            let candidate = directory.join("candidate.exe");
            fs::write(&executable, b"old verified core").unwrap();
            fs::write(&candidate, b"new verified core").unwrap();
            Self {
                directory,
                executable,
                candidate,
                cancellation: Cancellation::new(Arc::new(AtomicU64::new(1)), 1),
            }
        }
        fn install(&self) -> CoreReplacement {
            CoreReplacement::install(
                &self.executable,
                &self.candidate,
                &digest(&self.candidate).unwrap(),
                &self.cancellation,
            )
            .unwrap()
        }
        fn bytes(&self) -> Vec<u8> {
            fs::read(&self.executable).unwrap()
        }
    }
    impl Drop for Fixture {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.directory);
        }
    }

    #[test]
    fn recovery_presence_check_does_not_interpret_the_privileged_journal() {
        let fixture = Fixture::new();
        assert!(!CoreReplacement::needs_recovery(&fixture.executable).unwrap());
        let (_, marker) = paths(&fixture.executable).unwrap();
        fs::write(marker, b"unreadable-as-a-journal").unwrap();
        assert!(CoreReplacement::needs_recovery(&fixture.executable).unwrap());
        assert!(CoreReplacement::recover(&fixture.executable, false).is_err());
        assert_eq!(fixture.bytes(), b"old verified core");
    }

    #[test]
    fn replacement_keeps_rollback_until_readiness_acknowledgement() {
        let fixture = Fixture::new();
        let transaction = fixture.install();
        let (backup, marker) = paths(&fixture.executable).unwrap();
        assert_eq!(fixture.bytes(), b"new verified core");
        assert_eq!(fs::read(&backup).unwrap(), b"old verified core");
        assert!(marker.exists());
        transaction.rollback().unwrap();
        assert_eq!(fixture.bytes(), b"old verified core");
        assert!(!backup.exists() && !marker.exists());
    }

    #[test]
    fn acknowledged_replacement_survives_recovery() {
        let fixture = Fixture::new();
        let mut transaction = fixture.install();
        transaction.accept().unwrap();
        CoreReplacement::recover(&fixture.executable, true).unwrap();
        assert_eq!(fixture.bytes(), b"new verified core");
        let (backup, marker) = paths(&fixture.executable).unwrap();
        assert!(!backup.exists() && !marker.exists());
    }

    #[test]
    fn service_startup_preserves_unconfirmed_external_process_image() {
        let fixture = Fixture::new();
        let _transaction = fixture.install();
        CoreReplacement::recover(&fixture.executable, false).unwrap();
        assert_eq!(fixture.bytes(), b"new verified core");
        assert!(CoreReplacement::has_unconfirmed(&fixture.executable).unwrap());
        CoreReplacement::recover(&fixture.executable, true).unwrap();
        assert_eq!(fixture.bytes(), b"old verified core");
    }

    #[test]
    fn interrupted_rename_gap_restores_old_image_before_start() {
        let fixture = Fixture::new();
        let _transaction = fixture.install();
        fs::remove_file(&fixture.executable).unwrap();
        CoreReplacement::recover(&fixture.executable, false).unwrap();
        assert_eq!(fixture.bytes(), b"old verified core");
        assert!(!CoreReplacement::needs_recovery(&fixture.executable).unwrap());
    }

    #[test]
    fn interrupted_pre_rename_journal_is_cleaned_without_replacing_old_image() {
        let fixture = Fixture::new();
        let (_, marker) = paths(&fixture.executable).unwrap();
        let journal = Journal {
            previous_sha256: digest(&fixture.executable).unwrap(),
            candidate_sha256: digest(&fixture.candidate).unwrap(),
            accepted: false,
        };
        fs::write(&marker, serde_json::to_vec(&journal).unwrap()).unwrap();
        CoreReplacement::recover(&fixture.executable, false).unwrap();
        assert_eq!(fixture.bytes(), b"old verified core");
        assert!(!marker.exists());
        assert!(fixture.candidate.exists());
    }

    #[test]
    fn interrupted_acknowledged_cleanup_keeps_new_image() {
        let fixture = Fixture::new();
        let mut transaction = fixture.install();
        transaction.journal.accepted = true;
        let (backup, marker) = paths(&fixture.executable).unwrap();
        fs::write(&marker, serde_json::to_vec(&transaction.journal).unwrap()).unwrap();
        CoreReplacement::recover(&fixture.executable, true).unwrap();
        assert_eq!(fixture.bytes(), b"new verified core");
        assert!(!backup.exists() && !marker.exists());
    }

    #[test]
    fn corrupted_backup_cannot_overwrite_current_core() {
        let fixture = Fixture::new();
        let transaction = fixture.install();
        let (backup, marker) = paths(&fixture.executable).unwrap();
        fs::write(&backup, b"substituted backup").unwrap();
        assert!(transaction.rollback().is_err());
        assert_eq!(fixture.bytes(), b"new verified core");
        assert!(marker.exists());
    }

    #[test]
    fn changed_candidate_is_rejected_before_any_replacement() {
        let fixture = Fixture::new();
        let expected = digest(&fixture.candidate).unwrap();
        fs::write(&fixture.candidate, b"substituted candidate").unwrap();
        assert!(CoreReplacement::install(
            &fixture.executable,
            &fixture.candidate,
            &expected,
            &fixture.cancellation
        )
        .is_err());
        assert_eq!(fixture.bytes(), b"old verified core");
        let (backup, marker) = paths(&fixture.executable).unwrap();
        assert!(!backup.exists() && !marker.exists());
    }

    #[test]
    fn canceled_intent_never_moves_either_binary() {
        let fixture = Fixture::new();
        let cancellation = Cancellation::new(Arc::new(AtomicU64::new(2)), 1);
        assert!(CoreReplacement::install(
            &fixture.executable,
            &fixture.candidate,
            &digest(&fixture.candidate).unwrap(),
            &cancellation
        )
        .is_err());
        assert_eq!(fixture.bytes(), b"old verified core");
        assert!(fixture.candidate.exists());
        assert!(!CoreReplacement::needs_recovery(&fixture.executable).unwrap());
    }

    #[test]
    fn recovery_record_cannot_select_a_different_path() {
        let fixture = Fixture::new();
        let (_, marker) = paths(&fixture.executable).unwrap();
        let payload = serde_json::json!({
            "previous_sha256": digest(&fixture.executable).unwrap(),
            "candidate_sha256": digest(&fixture.candidate).unwrap(),
            "accepted": false, "path": "elsewhere.exe"
        });
        fs::write(&marker, serde_json::to_vec(&payload).unwrap()).unwrap();
        assert!(CoreReplacement::recover(&fixture.executable, true).is_err());
        assert_eq!(fixture.bytes(), b"old verified core");
        assert!(marker.exists());
    }

    #[cfg(unix)]
    #[test]
    fn a_linked_journal_is_not_read_or_removed() {
        let fixture = Fixture::new();
        let (_, marker) = paths(&fixture.executable).unwrap();
        std::os::unix::fs::symlink(&fixture.candidate, &marker).unwrap();
        assert!(CoreReplacement::recover(&fixture.executable, true).is_err());
        assert_eq!(fixture.bytes(), b"old verified core");
        assert_eq!(fs::read(&fixture.candidate).unwrap(), b"new verified core");
        assert!(fs::symlink_metadata(marker)
            .unwrap()
            .file_type()
            .is_symlink());
    }
}
