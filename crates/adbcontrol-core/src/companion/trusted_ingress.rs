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
    media_store: CompanionMediaStore,
    now_unix_ms: u64,
}

impl TrustedCompanionIngress {
    pub fn new(
        trust_store: CompanionTrustStore,
        media_root: impl Into<PathBuf>,
        now_unix_ms: u64,
    ) -> Self {
        Self {
            ingress: CompanionIngress::default(),
            trust_store,
            media_store: CompanionMediaStore::new(media_root),
            now_unix_ms,
        }
    }

    pub fn handle_envelope(&mut self, envelope: QuicEnvelope) -> Result<QuicEnvelope, AppError> {
        self.enforce_trust(&envelope)?;
        self.media_store.store_envelope(&envelope)?;
        self.ingress.handle_envelope(envelope)
    }

    pub fn handle_json_bytes(&mut self, bytes: &[u8]) -> Vec<u8> {
        match serde_json::from_slice::<QuicEnvelope>(bytes) {
            Ok(envelope) => match self.handle_envelope(envelope) {
                Ok(response) => serde_json::to_vec(&response).expect("trusted ingress response should serialize"),
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

        if self.trust_store.is_trusted(device_id, fingerprint, self.now_unix_ms) {
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
        })).expect("trusted ingress fallback error should serialize")
    }
}
