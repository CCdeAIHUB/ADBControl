use serde::{Deserialize, Serialize};
use serde_json::{json, Value};

use crate::error::AppError;

pub const COMPANION_PROTOCOL: &str = "adbcontrol-companion-quic";
pub const COMPANION_PROTOCOL_VERSION: u16 = 1;

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct QuicEnvelope {
    pub protocol: String,
    pub version: u16,
    pub message_id: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub trace_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub device_id: Option<String>,
    pub channel: QuicChannel,
    pub kind: QuicMessageKind,
    #[serde(default)]
    pub payload: Value,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum QuicChannel {
    Control,
    Data,
    Media,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum QuicMessageKind {
    Hello,
    HelloAck,
    Heartbeat,
    CapabilityList,
    PermissionState,
    CommandRequest,
    CommandResponse,
    StreamOpen,
    StreamChunk,
    StreamClose,
    Error,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CompanionHello {
    pub app_version: String,
    pub device_id: String,
    pub device_name: String,
    pub android_sdk: u32,
    pub supported_protocol_versions: Vec<u16>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CompanionCommandRequest {
    pub request_id: String,
    pub capability_id: String,
    pub operation: String,
    #[serde(default)]
    pub args: Value,
}

pub fn quic_protocol_descriptor() -> Value {
    json!({
        "protocol": COMPANION_PROTOCOL,
        "version": COMPANION_PROTOCOL_VERSION,
        "channels": ["control", "data", "media"],
        "messageKinds": [
            "hello",
            "helloAck",
            "heartbeat",
            "capabilityList",
            "permissionState",
            "commandRequest",
            "commandResponse",
            "streamOpen",
            "streamChunk",
            "streamClose",
            "error"
        ],
        "controlStream": "JSON envelope over reliable QUIC stream",
        "dataStream": "length-prefixed binary or JSON envelope over reliable QUIC stream",
        "mediaStream": "binary frame stream or unreliable datagram depending on capability"
    })
}

/// Validate the application-level QUIC envelope before dispatching payloads.
///
/// QUIC itself secures transport packets, but the Core still needs this protocol
/// guard to reject mismatched application protocols and incompatible companion
/// versions before any Android capability command is considered routable.
pub fn validate_quic_envelope(envelope: &QuicEnvelope) -> Result<(), AppError> {
    if envelope.protocol != COMPANION_PROTOCOL {
        return Err(AppError::new(
            "COMPANION_PROTOCOL_MISMATCH",
            "Companion QUIC envelope protocol does not match ADBControl.",
            "companion.protocol",
            false,
        ));
    }

    if envelope.version != COMPANION_PROTOCOL_VERSION {
        return Err(AppError::new(
            "COMPANION_PROTOCOL_VERSION_UNSUPPORTED",
            format!(
                "Unsupported companion protocol version: {}.",
                envelope.version
            ),
            "companion.protocol",
            false,
        ));
    }

    if envelope.message_id.trim().is_empty() {
        return Err(AppError::new(
            "COMPANION_MESSAGE_ID_MISSING",
            "Companion QUIC envelope messageId must not be empty.",
            "companion.protocol",
            false,
        ));
    }

    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn valid_hello_envelope_passes_protocol_guard() {
        // 场景：Android 伴侣 App 建立 QUIC 后发送 hello，Core 必须先校验协议名、版本和 messageId。
        let envelope = QuicEnvelope {
            protocol: String::from(COMPANION_PROTOCOL),
            version: COMPANION_PROTOCOL_VERSION,
            message_id: String::from("msg-1"),
            trace_id: Some(String::from("trace-1")),
            device_id: Some(String::from("device-1")),
            channel: QuicChannel::Control,
            kind: QuicMessageKind::Hello,
            payload: serde_json::to_value(CompanionHello {
                app_version: String::from("0.1.0"),
                device_id: String::from("device-1"),
                device_name: String::from("Pixel Test"),
                android_sdk: 35,
                supported_protocol_versions: vec![COMPANION_PROTOCOL_VERSION],
            })
            .expect("hello should serialize"),
        };

        assert!(validate_quic_envelope(&envelope).is_ok());
    }

    #[test]
    fn protocol_mismatch_is_rejected() {
        // 场景：非 ADBControl 协议的 QUIC 消息必须被拒绝，不能进入能力路由。
        let envelope = QuicEnvelope {
            protocol: String::from("other-protocol"),
            version: COMPANION_PROTOCOL_VERSION,
            message_id: String::from("msg-1"),
            trace_id: None,
            device_id: None,
            channel: QuicChannel::Control,
            kind: QuicMessageKind::Hello,
            payload: json!({}),
        };

        let error = validate_quic_envelope(&envelope).expect_err("mismatch should fail");
        assert_eq!(error.error_code, "COMPANION_PROTOCOL_MISMATCH");
    }
}
