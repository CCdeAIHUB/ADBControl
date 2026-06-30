use std::path::PathBuf;

use crate::error::AppError;

use super::{
    ingress::CompanionIngress,
    media_store::CompanionMediaStore,
    protocol::{QuicEnvelope, QuicMessageKind},
    trust::CompanionTrustStore,
};

#[derive(Debug, Clone)]
pub struct TrustedCompanionIngress {
    ingress: CompanionIngress,
    trust_store: CompanionTrustStore,
    now_unix_ms: u64,
}

impl TrustedCompanionIngress {
    pub fn new(
        trust_store: CompanionTrustStore,
        media_root: impl Into<PathBuf>,
        now_unix_ms: u64,
    ) -> Self {
        let media_store = CompanionMediaStore::new(media_root);
        Self {
            ingress: CompanionIngress::new_with_media_store(Default::default(), media_store),
            trust_store,
            now_unix_ms,
        }
    }

    pub fn handle_envelope(&mut self, envelope: QuicEnvelope) -> Result<QuicEnvelope, AppError> {
        self.enforce_trust(&envelope)?;
        self.ingress.handle_envelope(envelope)
    }

    pub fn handle_json_bytes(&mut self, bytes: &[u8]) -> Vec<u8> {
        match serde_json::from_slice::<QuicEnvelope>(bytes) {
            Ok(envelope) => match self.handle_envelope(envelope) {
                Ok(response) => serde_json::to_vec(&response)
                    .expect("trusted ingress response should serialize"),
                Err(error) => self.ingress_error_bytes(error),
            },
            Err(_) => self.ingress.handle_json_bytes(bytes),
        }
    }

    fn enforce_trust(&self, envelope: &QuicEnvelope) -> Result<(), AppError> {
        if envelope.kind == QuicMessageKind::Hello {
            return Ok(());
        }
        let device_id = envelope.device_id.as_deref().ok_or_else(|| {
            AppError::new(
                "COMPANION_TRUST_DEVICE_ID_MISSING",
                "Trusted companion messages require deviceId after hello.",
                "companion.trusted_ingress",
                false,
            )
        })?;
        let fingerprint = envelope
            .payload
            .get("certificateFingerprintSha256")
            .and_then(serde_json::Value::as_str)
            .ok_or_else(|| {
                AppError::new(
                    "COMPANION_TRUST_FINGERPRINT_MISSING",
                    "Trusted companion messages require certificateFingerprintSha256 in payload.",
                    "companion.trusted_ingress",
                    false,
                )
            })?;

        if self
            .trust_store
            .is_trusted(device_id, fingerprint, self.now_unix_ms)
        {
            Ok(())
        } else {
            Err(AppError::new(
                "COMPANION_DEVICE_NOT_TRUSTED",
                "Companion device is not trusted for this certificate fingerprint.",
                "companion.trusted_ingress",
                true,
            ))
        }
    }

    fn ingress_error_bytes(&self, error: AppError) -> Vec<u8> {
        serde_json::to_vec(&serde_json::json!({
            "protocol": super::protocol::COMPANION_PROTOCOL,
            "version": super::protocol::COMPANION_PROTOCOL_VERSION,
            "messageId": "trusted-ingress-error",
            "channel": "control",
            "kind": "error",
            "payload": {
                "errorCode": error.error_code,
                "message": error.message,
                "module": error.module,
                "recoverable": error.recoverable,
                "suggestion": error.suggestion
            }
        }))
        .expect("trusted ingress fallback error should serialize")
    }
}

#[cfg(test)]
mod tests {
    use std::fs;

    use serde_json::json;

    use super::*;
    use crate::companion::protocol::{QuicChannel, COMPANION_PROTOCOL, COMPANION_PROTOCOL_VERSION};
    use crate::companion::trust::PairingRequest;

    const NOW: u64 = 1_700_000_000_000;
    const FINGERPRINT: &str = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    #[test]
    fn trusted_media_chunk_is_stored_through_inner_ingress() {
        // 场景：设备完成配对后，trusted ingress 必须复用 media store 写入 chunk，并返回落盘 ACK 元数据。
        let root = temp_media_root("trusted-ingress-store");
        let mut ingress = TrustedCompanionIngress::new(trusted_store(), &root, NOW + 1_000);

        let response = ingress
            .handle_envelope(chunk_envelope("stream-1", FINGERPRINT))
            .expect("trusted media chunk should be accepted");

        assert_eq!(response.kind, QuicMessageKind::CommandResponse);
        assert_eq!(response.payload["stored"], true);
        assert_eq!(response.payload["storedChunkIndex"], 0);
        assert_eq!(
            fs::read(root.join("stream-1").join("chunk-00000000000000000000.bin")).unwrap(),
            vec![1, 2, 3, 4]
        );
        fs::remove_dir_all(root).ok();
    }

    #[test]
    fn untrusted_media_chunk_is_rejected_before_storage() {
        // 场景：证书指纹未被信任时，trusted ingress 不能把 media chunk 写入 Core 存储。
        let root = temp_media_root("trusted-ingress-reject");
        let mut ingress = TrustedCompanionIngress::new(CompanionTrustStore::default(), &root, NOW);

        let error = ingress
            .handle_envelope(chunk_envelope("stream-1", FINGERPRINT))
            .expect_err("untrusted media chunk should fail before storage");

        assert_eq!(error.error_code, "COMPANION_DEVICE_NOT_TRUSTED");
        assert!(!root.join("stream-1").exists());
        fs::remove_dir_all(root).ok();
    }

    fn trusted_store() -> CompanionTrustStore {
        let mut store = CompanionTrustStore::default();
        let challenge = store
            .begin_pairing(
                PairingRequest {
                    device_id: String::from("device-1"),
                    device_name: String::from("Pixel Test"),
                    certificate_fingerprint_sha256: String::from(FINGERPRINT),
                },
                NOW,
            )
            .expect("pairing should start");
        store
            .confirm_pairing(&challenge.pairing_id, &challenge.short_code, NOW + 1)
            .expect("pairing should trust device");
        store
    }

    fn chunk_envelope(session_id: &str, fingerprint: &str) -> QuicEnvelope {
        QuicEnvelope {
            protocol: String::from(COMPANION_PROTOCOL),
            version: COMPANION_PROTOCOL_VERSION,
            message_id: String::from("chunk-1"),
            trace_id: Some(String::from(session_id)),
            device_id: Some(String::from("device-1")),
            channel: QuicChannel::Media,
            kind: QuicMessageKind::StreamChunk,
            payload: json!({
                "sessionId": session_id,
                "chunkType": "media",
                "encoding": "base64",
                "data": "AQIDBA==",
                "sizeBytes": 4,
                "certificateFingerprintSha256": fingerprint
            }),
        }
    }

    fn temp_media_root(name: &str) -> std::path::PathBuf {
        std::env::temp_dir().join(format!("adbcontrol-{name}-{}", rand::random::<u64>()))
    }
}
