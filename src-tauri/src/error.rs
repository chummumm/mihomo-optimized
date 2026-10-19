use serde::{Deserialize, Serialize};

pub type Result<T> = std::result::Result<T, AppError>;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AppError {
    pub code: String,
    pub message: String,
}

impl AppError {
    pub fn new(code: &str, message: impl Into<String>) -> Self {
        Self {
            code: code.into(),
            message: message.into(),
        }
    }

    pub fn io(operation: &str, error: std::io::Error) -> Self {
        Self::new(
            "io_error",
            format!(
                "{operation}（系统错误：{}）",
                error
                    .raw_os_error()
                    .map_or_else(|| format!("{:?}", error.kind()), |n| n.to_string())
            ),
        )
    }
}

impl std::fmt::Display for AppError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}: {}", self.code, self.message)
    }
}

impl std::error::Error for AppError {}
