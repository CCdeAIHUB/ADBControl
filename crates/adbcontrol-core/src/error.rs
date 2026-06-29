use serde::{Deserialize, Serialize};

/// Unified error shape for every core failure path.
///
/// The frontend must never need to parse raw OS, IPC or ADB spawn errors.
/// This structure keeps module ownership, recovery semantics and optional
/// diagnostic context explicit across IPC boundaries.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct AppError {
    #[serde(rename = "errorCode")]
    pub error_code: String,
    pub message: String,
    pub module: String,
    pub recoverable: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub cause: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub suggestion: Option<String>,
    #[serde(rename = "traceId", skip_serializing_if = "Option::is_none")]
    pub trace_id: Option<String>,
}

impl AppError {
    pub fn new(
        error_code: impl Into<String>,
        message: impl Into<String>,
        module: impl Into<String>,
        recoverable: bool,
    ) -> Self {
        Self {
            error_code: error_code.into(),
            message: message.into(),
            module: module.into(),
            recoverable,
            cause: None,
            suggestion: None,
            trace_id: None,
        }
    }

    pub fn with_cause(mut self, cause: impl ToString) -> Self {
        self.cause = Some(cause.to_string());
        self
    }

    pub fn with_suggestion(mut self, suggestion: impl Into<String>) -> Self {
        self.suggestion = Some(suggestion.into());
        self
    }

    pub fn with_trace_id(mut self, trace_id: impl Into<String>) -> Self {
        self.trace_id = Some(trace_id.into());
        self
    }
}

impl std::fmt::Display for AppError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(
            formatter,
            "{} [{}]: {}",
            self.module, self.error_code, self.message
        )
    }
}

impl std::error::Error for AppError {}
