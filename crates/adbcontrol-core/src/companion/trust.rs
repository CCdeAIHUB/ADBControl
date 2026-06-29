use std::{collections::HashMap, fs, io::ErrorKind, path::Path};

use rand::prelude::*;
use serde::{Deserialize, Serialize};

use crate::error::AppError;

const TRUST_STORE_SCHEMA_VERSION: u32 = 1;
const PAIRING_TTL_MS: u64 = 5 * 60 * 1000;
const TRUST_TTL_MS: u64 = 30 * 24 * 60 * 60 * 1000;
const MAX_PAIRING_ATTEMPTS: u8 = 5;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct PairingRequest {
    #[serde(rename = "deviceId")]
    pub device_id: String,
    #[serde(rename = "deviceName")]
    pub device_name: String,
    #[serde(rename = "certificateFingerprintSha256")]
    pub certificate_fingerprint_sha256: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct PairingChallenge {
    #[serde(rename = "pairingId")]
    pub pairing_id: String,
    #[serde(rename = "deviceId")]
    pub device_id: String,
    #[serde(rename = "deviceName")]
    pub device_name: String,
    #[serde(rename = "certificateFingerprintSha256")]
    pub certificate_fingerprint_sha256: String,
    #[serde(rename = "shortCode")]
    pub short_code: String,
    #[serde(rename = "createdAtUnixMs")]
    pub created_at_unix_ms: u64,
    #[serde(rename = "expiresAtUnixMs")]
    pub expires_at_unix_ms: u64,
    #[serde(rename = "attemptsRemaining")]
    pub attempts_remaining: u8,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct TrustedDevice {
    #[serde(rename = "deviceId")]
    pub device_id: String,
    #[serde(rename = "deviceName")]
    pub device_name: String,
    #[serde(rename = "certificateFingerprintSha256")]
    pub certificate_fingerprint_sha256: String,
    #[serde(rename = "trustedAtUnixMs")]
    pub trusted_at_unix_ms: u64,
    #[serde(rename = "expiresAtUnixMs")]
    pub expires_at_unix_ms: u64,
    #[serde(rename = "revokedAtUnixMs", skip_serializing_if = "Option::is_none")]
    pub revoked_at_unix_ms: Option<u64>,
}

#[derive(Debug, Default, Clone)]
pub struct CompanionTrustStore {
    pending_pairings: HashMap<String, PairingChallenge>,
    trusted_devices: HashMap<String, TrustedDevice>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct CompanionTrustStoreFile {
    #[serde(rename = "schemaVersion")]
    schema_version: u32,
    #[serde(rename = "trustedDevices")]
    trusted_devices: Vec<TrustedDevice>,
}

impl CompanionTrustStore {
    pub fn load_trusted_devices_from_file(path: impl AsRef<Path>) -> Result<Self, AppError> {
        let path = path.as_ref();
        let content = match fs::read_to_string(path) {
            Ok(content) => content,
            Err(error) if error.kind() == ErrorKind::NotFound => return Ok(Self::default()),
            Err(error) => {
                return Err(AppError::new(
                    "COMPANION_TRUST_STORE_READ_FAILED",
                    "Core failed to read companion trust store file.",
                    "companion.trust",
                    true,
                )
                .with_cause(error));
            }
        };

        let file: CompanionTrustStoreFile = serde_json::from_str(&content).map_err(|error| {
            AppError::new(
                "COMPANION_TRUST_STORE_PARSE_FAILED",
                "Core failed to parse companion trust store JSON.",
                "companion.trust",
                false,
            )
            .with_cause(error)
        })?;

        if file.schema_version != TRUST_STORE_SCHEMA_VERSION {
            return Err(AppError::new(
                "COMPANION_TRUST_STORE_SCHEMA_UNSUPPORTED",
                "Companion trust store schema version is not supported.",
                "companion.trust",
                false,
            )
            .with_suggestion(format!(
                "Expected schemaVersion {TRUST_STORE_SCHEMA_VERSION}, got {}.",
                file.schema_version
            )));
        }

        let mut store = Self::default();
        for device in file.trusted_devices {
            validate_device_id(&device.device_id)?;
            validate_fingerprint(&device.certificate_fingerprint_sha256)?;
            store.trusted_devices.insert(device.device_id.clone(), device);
        }
        Ok(store)
    }

    pub fn save_trusted_devices_to_file(&self, path: impl AsRef<Path>) -> Result<(), AppError> {
        let path = path.as_ref();
        if let Some(parent) = path.parent() {
            if !parent.as_os_str().is_empty() {
                fs::create_dir_all(parent).map_err(|error| {
                    AppError::new(
                        "COMPANION_TRUST_STORE_DIR_CREATE_FAILED",
                        "Core failed to create companion trust store directory.",
                        "companion.trust",
                        true,
                    )
                    .with_cause(error)
                })?;
            }
        }

        let file = CompanionTrustStoreFile {
            schema_version: TRUST_STORE_SCHEMA_VERSION,
            trusted_devices: self.trusted_devices.values().cloned().collect(),
        };
        let content = serde_json::to_string_pretty(&file).map_err(|error| {
            AppError::new(
                "COMPANION_TRUST_STORE_SERIALIZE_FAILED",
                "Core failed to serialize companion trust store JSON.",
                "companion.trust",
                false,
            )
            .with_cause(error)
        })?;

        fs::write(path, content).map_err(|error| {
            AppError::new(
                "COMPANION_TRUST_STORE_WRITE_FAILED",
                "Core failed to write companion trust store file.",
                "companion.trust",
                true,
            )
            .with_cause(error)
        })
    }

    pub fn begin_pairing(
        &mut self,
        request: PairingRequest,
        now_unix_ms: u64,
    ) -> Result<PairingChallenge, AppError> {
        validate_device_id(&request.device_id)?;
        validate_fingerprint(&request.certificate_fingerprint_sha256)?;

        let challenge = PairingChallenge {
            pairing_id: format!("pair-{:016x}", rand::rng().random::<u64>()),
            device_id: request.device_id,
            device_name: request.device_name,
            certificate_fingerprint_sha256: normalize_fingerprint(&request.certificate_fingerprint_sha256),
            short_code: format!("{:06}", rand::random_range(0..1_000_000_u32)),
            created_at_unix_ms: now_unix_ms,
            expires_at_unix_ms: now_unix_ms + PAIRING_TTL_MS,
            attempts_remaining: MAX_PAIRING_ATTEMPTS,
        };

        self.pending_pairings
            .insert(challenge.pairing_id.clone(), challenge.clone());
        Ok(challenge)
    }

    pub fn confirm_pairing(
        &mut self,
        pairing_id: &str,
        short_code: &str,
        now_unix_ms: u64,
    ) -> Result<TrustedDevice, AppError> {
        let Some(challenge) = self.pending_pairings.get_mut(pairing_id) else {
            return Err(AppError::new(
                "COMPANION_PAIRING_NOT_FOUND",
                "Companion pairing challenge does not exist or has already been consumed.",
                "companion.trust",
                true,
            ));
        };

        if now_unix_ms > challenge.expires_at_unix_ms {
            self.pending_pairings.remove(pairing_id);
            return Err(AppError::new(
                "COMPANION_PAIRING_EXPIRED",
                "Companion pairing challenge has expired.",
                "companion.trust",
                true,
            ));
        }

        if challenge.short_code != short_code {
            challenge.attempts_remaining = challenge.attempts_remaining.saturating_sub(1);
            let attempts_remaining = challenge.attempts_remaining;
            if attempts_remaining == 0 {
                self.pending_pairings.remove(pairing_id);
            }
            return Err(AppError::new(
                "COMPANION_PAIRING_CODE_MISMATCH",
                "Companion pairing short code does not match.",
                "companion.trust",
                true,
            )
            .with_suggestion(format!(
                "Pairing attempts remaining: {attempts_remaining}"
            )));
        }

        let challenge = self
            .pending_pairings
            .remove(pairing_id)
            .expect("challenge should exist after successful code check");
        let trusted = TrustedDevice {
            device_id: challenge.device_id,
            device_name: challenge.device_name,
            certificate_fingerprint_sha256: challenge.certificate_fingerprint_sha256,
            trusted_at_unix_ms: now_unix_ms,
            expires_at_unix_ms: now_unix_ms + TRUST_TTL_MS,
            revoked_at_unix_ms: None,
        };
        self.trusted_devices
            .insert(trusted.device_id.clone(), trusted.clone());
        Ok(trusted)
    }

    pub fn is_trusted(
        &self,
        device_id: &str,
        certificate_fingerprint_sha256: &str,
        now_unix_ms: u64,
    ) -> bool {
        let Some(device) = self.trusted_devices.get(device_id) else {
            return false;
        };

        device.revoked_at_unix_ms.is_none()
            && now_unix_ms <= device.expires_at_unix_ms
            && device.certificate_fingerprint_sha256
                == normalize_fingerprint(certificate_fingerprint_sha256)
    }

    pub fn revoke_device(
        &mut self,
        device_id: &str,
        now_unix_ms: u64,
    ) -> Result<TrustedDevice, AppError> {
        let Some(device) = self.trusted_devices.get_mut(device_id) else {
            return Err(AppError::new(
                "COMPANION_TRUST_DEVICE_NOT_FOUND",
                "Trusted companion device does not exist.",
                "companion.trust",
                true,
            ));
        };
        device.revoked_at_unix_ms = Some(now_unix_ms);
        Ok(device.clone())
    }

    pub fn trusted_device(&self, device_id: &str) -> Option<&TrustedDevice> {
        self.trusted_devices.get(device_id)
    }

    pub fn pending_pairing(&self, pairing_id: &str) -> Option<&PairingChallenge> {
        self.pending_pairings.get(pairing_id)
    }
}

fn validate_device_id(device_id: &str) -> Result<(), AppError> {
    if device_id.trim().is_empty() {
        return Err(AppError::new(
            "COMPANION_PAIRING_DEVICE_ID_MISSING",
            "Companion pairing requires a non-empty deviceId.",
            "companion.trust",
            false,
        ));
    }
    Ok(())
}

fn validate_fingerprint(fingerprint: &str) -> Result<(), AppError> {
    let normalized = normalize_fingerprint(fingerprint);
    if normalized.len() != 64 || !normalized.chars().all(|character| character.is_ascii_hexdigit()) {
        return Err(AppError::new(
            "COMPANION_CERT_FINGERPRINT_INVALID",
            "Companion certificate fingerprint must be a SHA-256 hex string.",
            "companion.trust",
            false,
        ));
    }
    Ok(())
}

fn normalize_fingerprint(fingerprint: &str) -> String {
    fingerprint
        .chars()
        .filter(|character| *character != ':' && !character.is_ascii_whitespace())
        .flat_map(|character| character.to_lowercase())
        .collect()
}

#[cfg(test)]
mod tests {
    use std::path::PathBuf;

    use super::*;

    const NOW: u64 = 1_700_000_000_000;
    const FINGERPRINT: &str =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    #[test]
    fn pairing_confirmation_trusts_certificate_fingerprint() {
        // 场景：用户确认 Android 伴侣 App 显示的 6 位短码后，Core 才能绑定该设备证书指纹。
        let mut store = CompanionTrustStore::default();
        let challenge = store
            .begin_pairing(pairing_request(), NOW)
            .expect("pairing should start");
        let trusted = store
            .confirm_pairing(&challenge.pairing_id, &challenge.short_code, NOW + 1_000)
            .expect("correct short code should trust device");

        assert_eq!(trusted.device_id, "device-1");
        assert!(store.is_trusted("device-1", FINGERPRINT, NOW + 2_000));
        assert!(store.pending_pairing(&challenge.pairing_id).is_none());
    }

    #[test]
    fn trusted_devices_roundtrip_through_json_file() {
        // 场景：Core 重启后必须能从 trust store 文件恢复已配对设备和证书指纹。
        let path = temp_trust_store_path("roundtrip");
        let mut store = CompanionTrustStore::default();
        let challenge = store.begin_pairing(pairing_request(), NOW).unwrap();
        store
            .confirm_pairing(&challenge.pairing_id, &challenge.short_code, NOW + 1_000)
            .unwrap();
        store.save_trusted_devices_to_file(&path).unwrap();

        let loaded = CompanionTrustStore::load_trusted_devices_from_file(&path).unwrap();
        fs::remove_file(&path).ok();

        assert!(loaded.is_trusted("device-1", FINGERPRINT, NOW + 2_000));
        assert!(loaded.pending_pairings.is_empty());
    }

    #[test]
    fn revoked_state_is_persisted_to_json_file() {
        // 场景：已撤销设备写入 trust store 后，Core 重启也不能恢复其信任。
        let path = temp_trust_store_path("revoked");
        let mut store = CompanionTrustStore::default();
        let challenge = store.begin_pairing(pairing_request(), NOW).unwrap();
        store
            .confirm_pairing(&challenge.pairing_id, &challenge.short_code, NOW + 1_000)
            .unwrap();
        store.revoke_device("device-1", NOW + 2_000).unwrap();
        store.save_trusted_devices_to_file(&path).unwrap();

        let loaded = CompanionTrustStore::load_trusted_devices_from_file(&path).unwrap();
        fs::remove_file(&path).ok();

        assert!(!loaded.is_trusted("device-1", FINGERPRINT, NOW + 3_000));
        assert!(loaded.trusted_device("device-1").unwrap().revoked_at_unix_ms.is_some());
    }

    #[test]
    fn missing_trust_store_file_loads_empty_store() {
        // 场景：首次启动 Core 没有 trust store 文件时，应得到空 store，而不是启动失败。
        let path = temp_trust_store_path("missing");
        fs::remove_file(&path).ok();

        let loaded = CompanionTrustStore::load_trusted_devices_from_file(&path).unwrap();

        assert!(loaded.trusted_devices.is_empty());
        assert!(loaded.pending_pairings.is_empty());
    }

    #[test]
    fn wrong_short_code_consumes_attempts_and_does_not_trust_device() {
        // 场景：短码错误不能建立信任，并且必须消耗尝试次数防止暴力猜测。
        let mut store = CompanionTrustStore::default();
        let challenge = store.begin_pairing(pairing_request(), NOW).unwrap();
        let error = store
            .confirm_pairing(&challenge.pairing_id, "000000", NOW + 1_000)
            .expect_err("wrong code should fail");

        assert_eq!(error.error_code, "COMPANION_PAIRING_CODE_MISMATCH");
        assert!(!store.is_trusted("device-1", FINGERPRINT, NOW + 2_000));
        assert_eq!(
            store
                .pending_pairing(&challenge.pairing_id)
                .expect("challenge remains")
                .attempts_remaining,
            MAX_PAIRING_ATTEMPTS - 1
        );
    }

    #[test]
    fn expired_pairing_is_removed_and_rejected() {
        // 场景：过期配对挑战必须被拒绝和移除，不能继续用于建立信任。
        let mut store = CompanionTrustStore::default();
        let challenge = store.begin_pairing(pairing_request(), NOW).unwrap();
        let error = store
            .confirm_pairing(
                &challenge.pairing_id,
                &challenge.short_code,
                challenge.expires_at_unix_ms + 1,
            )
            .expect_err("expired challenge should fail");

        assert_eq!(error.error_code, "COMPANION_PAIRING_EXPIRED");
        assert!(store.pending_pairing(&challenge.pairing_id).is_none());
    }

    #[test]
    fn revoked_device_is_no_longer_trusted() {
        // 场景：用户撤销 Android 伴侣 App 信任后，相同证书指纹也不能再通过信任检查。
        let mut store = CompanionTrustStore::default();
        let challenge = store.begin_pairing(pairing_request(), NOW).unwrap();
        store
            .confirm_pairing(&challenge.pairing_id, &challenge.short_code, NOW + 1_000)
            .unwrap();
        store.revoke_device("device-1", NOW + 2_000).unwrap();

        assert!(!store.is_trusted("device-1", FINGERPRINT, NOW + 3_000));
        assert!(store.trusted_device("device-1").unwrap().revoked_at_unix_ms.is_some());
    }

    #[test]
    fn fingerprint_mismatch_is_not_trusted() {
        // 场景：设备 ID 相同但证书指纹变化时，Core 必须视为不可信，防止中间人或重装后复用旧信任。
        let mut store = CompanionTrustStore::default();
        let challenge = store.begin_pairing(pairing_request(), NOW).unwrap();
        store
            .confirm_pairing(&challenge.pairing_id, &challenge.short_code, NOW + 1_000)
            .unwrap();

        assert!(!store.is_trusted(
            "device-1",
            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
            NOW + 2_000
        ));
    }

    fn pairing_request() -> PairingRequest {
        PairingRequest {
            device_id: String::from("device-1"),
            device_name: String::from("Pixel Test"),
            certificate_fingerprint_sha256: String::from(FINGERPRINT),
        }
    }

    fn temp_trust_store_path(name: &str) -> PathBuf {
        std::env::temp_dir().join(format!(
            "adbcontrol-trust-{name}-{}.json",
            rand::rng().random::<u64>()
        ))
    }
}
