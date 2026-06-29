use serde_json::{json, Value};

use crate::error::AppError;

use super::{
    protocol::{
        validate_quic_envelope, QuicChannel, QuicEnvelope, QuicMessageKind, COMPANION_PROTOCOL,
        COMPANION_PROTOCOL_VERSION,
    },
    session::CompanionSessionManager,
};

#[derive(Debug, Default, Clone)]
pub struct CompanionIngress {
    session_manager: CompanionSessionManager,
    media_chunk_count: usize,
}

impl CompanionIngress {
    pub fn new(session_manager: CompanionSessionManager) -> Self {
        Self {
            session_manager,
            media_chunk_count: 0,
        }
    }

    pub fn handle_json_bytes(&mut self, bytes: &[u8]) -> Vec<u8> {
        let response = match serde_json::from_slice::<QuicEnvelope>(bytes) {
            Ok(envelope) => self.handle_envelope(envelope),
            Err(error) => Err(AppError::new(
                "COMPANION_INGRESS_INVALID_JSON",
                "Companion ingress received invalid JSON envelope.",
                "companion.ingress",
                false,
            )
            .with_cause(error)),
        };

        let envelope = match response {
            Ok(envelope) => envelope,
            Err(error) => error_envelope(None, None, None, error),
        };

        serde_json::to_vec(&envelope).unwrap_or_else(|error| {
            serde_json::to_vec(&json!({
                "protocol": COMPANION_PROTOCOL,
                "version": COMPANION_PROTOCOL_VERSION,
                "messageId": "ingress-serialization-error",
                "channel": "control",
                "kind": "error",
                "payload": {
                    "errorCode": "COMPANION_INGRESS_SERIALIZATION_FAILED",
                    "message": error.to_string(),
                    "module": "companion.ingress",
                    "recoverable": true
                }
            }))
            .expect("fallback error envelope should serialize")
        })
    }

    pub fn handle_envelope(&mut self, envelope: QuicEnvelope) -> Result<QuicEnvelope, AppError> {
        let trace_id = envelope.trace_id.clone();
        let device_id = envelope.device_id.clone();
        let message_id = envelope.message_id.clone();

        match self.dispatch_envelope(envelope) {
            Ok(response) => Ok(response),
            Err(error) => Ok(error_envelope(trace_id, device_id, Some(message_id), error)),
        }
    }

    pub fn session_manager(&self) -> &CompanionSessionManager {
        &self.session_manager
    }

    pub fn media_chunk_count(&self) -> usize {
        self.media_chunk_count
    }

    fn dispatch_envelope(&mut self, envelope: QuicEnvelope) -> Result<QuicEnvelope, AppError> {
        validate_quic_envelope(&envelope)?;

        match envelope.kind {
            QuicMessageKind::Hello
            | QuicMessageKind::CapabilityList
            | QuicMessageKind::PermissionState
            | QuicMessageKind::Heartbeat => self.session_manager.handle_envelope(envelope),
            QuicMessageKind::StreamOpen | QuicMessageKind::StreamChunk | QuicMessageKind::StreamClose => {
                self.handle_media_envelope(envelope)
            }
            QuicMessageKind::CommandResponse | QuicMessageKind::Error => self.ack_terminal_response(envelope),
            QuicMessageKind::HelloAck | QuicMessageKind::CommandRequest => Err(AppError::new(
                "COMPANION_INGRESS_MESSAGE_UNSUPPORTED",
                "Core ingress received a message kind that is only valid in the opposite direction.",
                "companion.ingress",
                false,
            )),
        }
    }

    fn handle_media_envelope(&mut self, envelope: QuicEnvelope) -> Result<QuicEnvelope, AppError> {
        if envelope.channel != QuicChannel::Media {
            return Err(AppError::new(
                "COMPANION_MEDIA_CHANNEL_INVALID",
                "Media stream messages must use the media channel.",
                "companion.ingress",
                false,
            ));
        }

        if envelope.kind == QuicMessageKind::StreamChunk {
            self.media_chunk_count += 1;
        }

        Ok(ack_envelope(
            envelope.trace_id,
            envelope.device_id,
            envelope.message_id,
            json!({
                "accepted": true,
                "media": true,
                "receivedKind": envelope.kind,
                "receivedChunkCount": self.media_chunk_count
            }),
        ))
    }

    fn ack_terminal_response(&self, envelope: QuicEnvelope) -> Result<QuicEnvelope, AppError> {
        Ok(ack_envelope(
            envelope.trace_id,
            envelope.device_id,
            envelope.message_id,
            json!({
                "accepted": true,
                "terminalResponse": true,
                "receivedKind": envelope.kind
            }),
        ))
    }
}

fn ack_envelope(
    trace_id: Option<String>,
    device_id: Option<String>,
    source_message_id: String,
    payload: Value,
) -> QuicEnvelope {
    QuicEnvelope {
        protocol: String::from(COMPANION_PROTOCOL),
        version: COMPANION_PROTOCOL_VERSION,
        message_id: format!("ingress-ack-{source_message_id}"),
        trace_id,
        device_id,
        channel: QuicChannel::Control,
        kind: QuicMessageKind::CommandResponse,
        payload,
    }
}

fn error_envelope(
    trace_id: Option<String>,
    device_id: Option<String>,
    source_message_id: Option<String>,
    error: AppError,
) -> QuicEnvelope {
    QuicEnvelope {
        protocol: String::from(COMPANION_PROTOCOL),
        version: COMPANION_PROTOCOL_VERSION,
        message_id: source_message_id
            .map(|message_id| format!("ingress-error-{message_id}"))
            .unwrap_or_else(|| String::from("ingress-error")),
        trace_id,
        device_id,
        channel: QuicChannel::Control,
        kind: QuicMessageKind::Error,
        payload: json!({
            "errorCode": error.error_code,
            "message": error.message,
            "module": error.module,
            "recoverable": error.recoverable,
            "suggestion": error.suggestion
        }),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::companion::protocol::CompanionHello;

    #[test]
    fn valid_hello_json_returns_hello_ack() {
        // 场景：Android Cronet POST hello envelope 到 Core ingress，Core 必须返回 helloAck。
        let mut ingress = CompanionIngress::default();
        let response = ingress.handle_json_bytes(
            &serde_json::to_vec(&hello_envelope("device-1")).expect("hello should serialize"),
        );
        let envelope: QuicEnvelope = serde_json::from_slice(&response).expect("response should parse");

        assert_eq!(envelope.kind, QuicMessageKind::HelloAck);
        assert!(ingress.session_manager().get_session("device-1").is_ok());
    }

    #[test]
    fn invalid_json_returns_ingress_error_envelope() {
        // 场景：网络层收到损坏 JSON 时，Core ingress 必须返回结构化 error envelope，不能 panic。
        let mut ingress = CompanionIngress::default();
        let response = ingress.handle_json_bytes(b"{not-json");
        let envelope: QuicEnvelope = serde_json::from_slice(&response).expect("error should parse");

        assert_eq!(envelope.kind, QuicMessageKind::Error);
        assert_eq!(envelope.payload["errorCode"], "COMPANION_INGRESS_INVALID_JSON");
    }

    #[test]
    fn protocol_mismatch_returns_error_envelope() {
        // 场景：非 ADBControl 协议的 envelope 必须被 ingress 拒绝，并保留 trace/device 方便审计。
        let mut envelope = hello_envelope("device-1");
        envelope.protocol = String::from("other-protocol");
        let mut ingress = CompanionIngress::default();
        let response = ingress.handle_envelope(envelope).expect("ingress converts errors to envelopes");

        assert_eq!(response.kind, QuicMessageKind::Error);
        assert_eq!(response.payload["errorCode"], "COMPANION_PROTOCOL_MISMATCH");
        assert_eq!(response.device_id.as_deref(), Some("device-1"));
    }

    #[test]
    fn media_chunk_is_acked_without_touching_session_state() {
        // 场景：媒体 chunk 应由 media ingress 接收并 ACK，不能误进入 session state manager。
        let mut ingress = CompanionIngress::default();
        let response = ingress
            .handle_envelope(QuicEnvelope {
                protocol: String::from(COMPANION_PROTOCOL),
                version: COMPANION_PROTOCOL_VERSION,
                message_id: String::from("chunk-1"),
                trace_id: Some(String::from("stream-1")),
                device_id: Some(String::from("device-1")),
                channel: QuicChannel::Media,
                kind: QuicMessageKind::StreamChunk,
                payload: json!({"sessionId": "stream-1", "sizeBytes": 4}),
            })
            .expect("media chunk should be acked");

        assert_eq!(response.kind, QuicMessageKind::CommandResponse);
        assert_eq!(response.payload["receivedChunkCount"], 1);
        assert!(ingress.session_manager().get_session("device-1").is_err());
    }

    fn hello_envelope(device_id: &str) -> QuicEnvelope {
        QuicEnvelope {
            protocol: String::from(COMPANION_PROTOCOL),
            version: COMPANION_PROTOCOL_VERSION,
            message_id: format!("hello-{device_id}"),
            trace_id: Some(String::from("trace-1")),
            device_id: Some(String::from(device_id)),
            channel: QuicChannel::Control,
            kind: QuicMessageKind::Hello,
            payload: serde_json::to_value(CompanionHello {
                app_version: String::from("0.1.0"),
                device_id: String::from(device_id),
                device_name: String::from("Pixel Test"),
                android_sdk: 35,
                supported_protocol_versions: vec![COMPANION_PROTOCOL_VERSION],
            })
            .expect("hello should serialize"),
        }
    }
}
