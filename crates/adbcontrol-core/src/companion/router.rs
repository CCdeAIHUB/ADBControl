use std::collections::HashMap;

use serde::{Deserialize, Serialize};
use serde_json::{json, Value};

use crate::error::AppError;

use super::protocol::{
    CompanionCommandRequest, QuicChannel, QuicEnvelope, QuicMessageKind, COMPANION_PROTOCOL,
    COMPANION_PROTOCOL_VERSION,
};

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CompanionCommandDispatch {
    pub request_id: String,
    pub device_id: String,
    pub capability_id: String,
    pub operation: String,
    #[serde(default)]
    pub args: Value,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CompanionCommandResponse {
    pub request_id: String,
    pub device_id: String,
    pub capability_id: String,
    pub operation: String,
    pub status: CompanionCommandStatus,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub envelope: Option<QuicEnvelope>,
    #[serde(default)]
    pub result: Value,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum CompanionCommandStatus {
    Dispatched,
    Completed,
}

pub trait CompanionCommandRouter: Send + Sync {
    fn dispatch(&self, command: CompanionCommandDispatch) -> Result<CompanionCommandResponse, AppError>;
}

#[derive(Debug, Default, Clone, Copy)]
pub struct DisconnectedCompanionCommandRouter;

impl CompanionCommandRouter for DisconnectedCompanionCommandRouter {
    fn dispatch(&self, command: CompanionCommandDispatch) -> Result<CompanionCommandResponse, AppError> {
        Err(AppError::new(
            "COMPANION_SESSION_NOT_CONNECTED",
            format!(
                "Android companion QUIC session is not connected for device {}.",
                command.device_id
            ),
            "companion.router",
            true,
        )
        .with_suggestion("Complete companion pairing and QUIC hello/helloAck before invoking capabilities."))
    }
}

#[derive(Debug, Clone, Default)]
pub struct InMemoryCompanionCommandRouter {
    sessions: HashMap<String, InMemoryCompanionSession>,
}

impl InMemoryCompanionCommandRouter {
    pub fn new(sessions: Vec<InMemoryCompanionSession>) -> Self {
        let sessions = sessions
            .into_iter()
            .map(|session| (session.device_id.clone(), session))
            .collect();
        Self { sessions }
    }
}

impl CompanionCommandRouter for InMemoryCompanionCommandRouter {
    fn dispatch(&self, command: CompanionCommandDispatch) -> Result<CompanionCommandResponse, AppError> {
        let session = self.sessions.get(&command.device_id).ok_or_else(|| {
            AppError::new(
                "COMPANION_SESSION_NOT_CONNECTED",
                format!(
                    "Android companion QUIC session is not connected for device {}.",
                    command.device_id
                ),
                "companion.router",
                true,
            )
            .with_suggestion("Complete companion pairing and QUIC hello/helloAck before invoking capabilities.")
        })?;

        Ok(session.dispatch(command))
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct InMemoryCompanionSession {
    pub device_id: String,
    pub dispatch_mode: InMemoryDispatchMode,
}

impl InMemoryCompanionSession {
    pub fn dispatch_only(device_id: impl Into<String>) -> Self {
        Self {
            device_id: device_id.into(),
            dispatch_mode: InMemoryDispatchMode::DispatchOnly,
        }
    }

    pub fn complete_immediately(device_id: impl Into<String>) -> Self {
        Self {
            device_id: device_id.into(),
            dispatch_mode: InMemoryDispatchMode::CompleteImmediately,
        }
    }

    fn dispatch(&self, command: CompanionCommandDispatch) -> CompanionCommandResponse {
        let envelope = build_command_request_envelope(&command);
        let status = match self.dispatch_mode {
            InMemoryDispatchMode::DispatchOnly => CompanionCommandStatus::Dispatched,
            InMemoryDispatchMode::CompleteImmediately => CompanionCommandStatus::Completed,
        };
        let result = match self.dispatch_mode {
            InMemoryDispatchMode::DispatchOnly => json!({
                "dispatched": true,
                "transport": "quic-control",
                "messageId": envelope.message_id,
            }),
            InMemoryDispatchMode::CompleteImmediately => json!({
                "dispatched": true,
                "completed": true,
                "transport": "in-memory-companion-session",
            }),
        };

        CompanionCommandResponse {
            request_id: command.request_id,
            device_id: command.device_id,
            capability_id: command.capability_id,
            operation: command.operation,
            status,
            envelope: Some(envelope),
            result,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum InMemoryDispatchMode {
    DispatchOnly,
    CompleteImmediately,
}

pub fn build_command_request_envelope(command: &CompanionCommandDispatch) -> QuicEnvelope {
    let payload = serde_json::to_value(CompanionCommandRequest {
        request_id: command.request_id.clone(),
        capability_id: command.capability_id.clone(),
        operation: command.operation.clone(),
        args: command.args.clone(),
    })
    .unwrap_or_else(|error| {
        json!({
            "serializationError": error.to_string(),
        })
    });

    // The router is the Core boundary between IPC and QUIC. It must produce the
    // same commandRequest envelope that the Android Companion service consumes,
    // so later network transport can be added without changing IPC semantics.
    QuicEnvelope {
        protocol: String::from(COMPANION_PROTOCOL),
        version: COMPANION_PROTOCOL_VERSION,
        message_id: format!("command-{}", command.request_id),
        trace_id: Some(command.request_id.clone()),
        device_id: Some(command.device_id.clone()),
        channel: QuicChannel::Control,
        kind: QuicMessageKind::CommandRequest,
        payload,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn disconnected_router_returns_recoverable_session_error() {
        // 场景：设备已注册但 QUIC session 未连接时，Core 必须明确告诉前端需要先配对连接。
        let router = DisconnectedCompanionCommandRouter;
        let error = router
            .dispatch(CompanionCommandDispatch {
                request_id: String::from("invoke-1"),
                device_id: String::from("android-companion-sample"),
                capability_id: String::from("android.volume.media"),
                operation: String::from("volume.set"),
                args: json!({"level": 5}),
            })
            .expect_err("missing session should fail");

        assert_eq!(error.error_code, "COMPANION_SESSION_NOT_CONNECTED");
        assert!(error.recoverable);
    }

    #[test]
    fn connected_router_builds_command_request_envelope() {
        // 场景：设备 session 已连接时，Core 必须生成 Android Companion 可消费的 QUIC commandRequest envelope。
        let router = InMemoryCompanionCommandRouter::new(vec![
            InMemoryCompanionSession::dispatch_only("android-companion-sample"),
        ]);

        let response = router
            .dispatch(CompanionCommandDispatch {
                request_id: String::from("invoke-1"),
                device_id: String::from("android-companion-sample"),
                capability_id: String::from("android.volume.media"),
                operation: String::from("volume.set"),
                args: json!({"level": 5}),
            })
            .expect("connected session should dispatch");
        let envelope = response.envelope.expect("envelope should be present");

        assert_eq!(response.status, CompanionCommandStatus::Dispatched);
        assert_eq!(envelope.kind, QuicMessageKind::CommandRequest);
        assert_eq!(envelope.payload["requestId"], "invoke-1");
        assert_eq!(envelope.payload["capabilityId"], "android.volume.media");
        assert_eq!(envelope.payload["operation"], "volume.set");
        assert_eq!(envelope.payload["args"]["level"], 5);
    }
}
