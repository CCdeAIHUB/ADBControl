use crate::error::AppError;

use super::ingress::CompanionIngress;

pub trait CompanionQuicListener {
    fn accept_control_bytes(&mut self, bytes: &[u8]) -> Result<Vec<u8>, AppError>;
    fn accept_media_bytes(&mut self, bytes: &[u8]) -> Result<Vec<u8>, AppError>;
}

#[derive(Debug, Default, Clone)]
pub struct IngressBackedQuicListener {
    ingress: CompanionIngress,
}

impl IngressBackedQuicListener {
    pub fn new(ingress: CompanionIngress) -> Self {
        Self { ingress }
    }

    pub fn ingress(&self) -> &CompanionIngress {
        &self.ingress
    }
}

impl CompanionQuicListener for IngressBackedQuicListener {
    fn accept_control_bytes(&mut self, bytes: &[u8]) -> Result<Vec<u8>, AppError> {
        Ok(self.ingress.handle_json_bytes(bytes))
    }

    fn accept_media_bytes(&mut self, bytes: &[u8]) -> Result<Vec<u8>, AppError> {
        Ok(self.ingress.handle_json_bytes(bytes))
    }
}

#[cfg(test)]
mod tests {
    use serde_json::json;

    use super::*;
    use crate::companion::protocol::QuicChannel;
    use crate::companion::{
        protocol::CompanionHello, QuicEnvelope, QuicMessageKind, COMPANION_PROTOCOL,
        COMPANION_PROTOCOL_VERSION,
    };

    #[test]
    fn control_listener_routes_hello_to_ingress() {
        // 场景：真实 QUIC control stream 收到 hello bytes 后，只需调用 listener adapter 即可进入 ingress/session。
        let mut listener = IngressBackedQuicListener::default();
        let response = listener
            .accept_control_bytes(&serde_json::to_vec(&hello_envelope("device-1")).unwrap())
            .expect("listener should respond");
        let envelope: QuicEnvelope =
            serde_json::from_slice(&response).expect("response should parse");

        assert_eq!(envelope.kind, QuicMessageKind::HelloAck);
        assert!(listener
            .ingress()
            .session_manager()
            .get_session("device-1")
            .is_ok());
    }

    #[test]
    fn media_listener_routes_chunk_to_ingress() {
        // 场景：真实 QUIC media stream/datagram 收到 streamChunk 后，listener adapter 必须 ACK media chunk。
        let mut listener = IngressBackedQuicListener::default();
        let response = listener
            .accept_media_bytes(
                &serde_json::to_vec(&QuicEnvelope {
                    protocol: String::from(COMPANION_PROTOCOL),
                    version: COMPANION_PROTOCOL_VERSION,
                    message_id: String::from("chunk-1"),
                    trace_id: Some(String::from("stream-1")),
                    device_id: Some(String::from("device-1")),
                    channel: QuicChannel::Media,
                    kind: QuicMessageKind::StreamChunk,
                    payload: json!({"sessionId": "stream-1", "sizeBytes": 4}),
                })
                .unwrap(),
            )
            .expect("listener should respond");
        let envelope: QuicEnvelope =
            serde_json::from_slice(&response).expect("response should parse");

        assert_eq!(envelope.kind, QuicMessageKind::CommandResponse);
        assert_eq!(envelope.payload["receivedChunkCount"], 1);
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
            .unwrap(),
        }
    }
}
