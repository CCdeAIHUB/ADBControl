use std::{fs, path::Path};

use rcgen::{generate_simple_self_signed, CertifiedKey};
use rustls::pki_types::{CertificateDer, PrivateKeyDer, PrivatePkcs8KeyDer};
use sha2::{Digest, Sha256};

use crate::error::AppError;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CoreQuicIdentity {
    pub cert_der: Vec<u8>,
    pub private_key_der: Vec<u8>,
    pub certificate_fingerprint_sha256: String,
}

impl CoreQuicIdentity {
    pub fn generate_self_signed(subject_alt_names: Vec<String>) -> Result<Self, AppError> {
        let CertifiedKey { cert, key_pair } = generate_simple_self_signed(subject_alt_names)
            .map_err(|error| {
                AppError::new(
                    "COMPANION_QUIC_CERT_GENERATE_FAILED",
                    "Core failed to generate self-signed QUIC certificate.",
                    "companion.quic_identity",
                    false,
                )
                .with_cause(error)
            })?;
        let cert_der = cert.der().to_vec();
        let private_key_der = key_pair.serialize_der();
        Ok(Self::from_der(cert_der, private_key_der))
    }

    pub fn from_der(cert_der: Vec<u8>, private_key_der: Vec<u8>) -> Self {
        let certificate_fingerprint_sha256 = hex::encode(Sha256::digest(&cert_der));
        Self {
            cert_der,
            private_key_der,
            certificate_fingerprint_sha256,
        }
    }

    pub fn load_or_generate(
        cert_path: impl AsRef<Path>,
        key_path: impl AsRef<Path>,
        subject_alt_names: Vec<String>,
    ) -> Result<Self, AppError> {
        let cert_path = cert_path.as_ref();
        let key_path = key_path.as_ref();
        if cert_path.exists() && key_path.exists() {
            return Self::load_from_files(cert_path, key_path);
        }
        let identity = Self::generate_self_signed(subject_alt_names)?;
        identity.save_to_files(cert_path, key_path)?;
        Ok(identity)
    }

    pub fn load_from_files(
        cert_path: impl AsRef<Path>,
        key_path: impl AsRef<Path>,
    ) -> Result<Self, AppError> {
        let cert_der = fs::read(cert_path).map_err(|error| {
            AppError::new(
                "COMPANION_QUIC_CERT_READ_FAILED",
                "Core failed to read QUIC certificate file.",
                "companion.quic_identity",
                true,
            )
            .with_cause(error)
        })?;
        let private_key_der = fs::read(key_path).map_err(|error| {
            AppError::new(
                "COMPANION_QUIC_KEY_READ_FAILED",
                "Core failed to read QUIC private key file.",
                "companion.quic_identity",
                true,
            )
            .with_cause(error)
        })?;
        Ok(Self::from_der(cert_der, private_key_der))
    }

    pub fn save_to_files(
        &self,
        cert_path: impl AsRef<Path>,
        key_path: impl AsRef<Path>,
    ) -> Result<(), AppError> {
        write_file(
            cert_path,
            &self.cert_der,
            "COMPANION_QUIC_CERT_WRITE_FAILED",
        )?;
        write_file(
            key_path,
            &self.private_key_der,
            "COMPANION_QUIC_KEY_WRITE_FAILED",
        )
    }

    pub fn server_config(&self) -> Result<quinn::ServerConfig, AppError> {
        quinn::ServerConfig::with_single_cert(
            vec![CertificateDer::from(self.cert_der.clone())],
            PrivateKeyDer::Pkcs8(PrivatePkcs8KeyDer::from(self.private_key_der.clone())),
        )
        .map_err(|error| {
            AppError::new(
                "COMPANION_QUIC_SERVER_CONFIG_FAILED",
                "Core failed to build Quinn ServerConfig from identity.",
                "companion.quic_identity",
                false,
            )
            .with_cause(error)
        })
    }
}

fn write_file(path: impl AsRef<Path>, bytes: &[u8], error_code: &str) -> Result<(), AppError> {
    let path = path.as_ref();
    if let Some(parent) = path.parent() {
        if !parent.as_os_str().is_empty() {
            fs::create_dir_all(parent).map_err(|error| {
                AppError::new(
                    "COMPANION_QUIC_IDENTITY_DIR_CREATE_FAILED",
                    "Core failed to create QUIC identity directory.",
                    "companion.quic_identity",
                    true,
                )
                .with_cause(error)
            })?;
        }
    }
    fs::write(path, bytes).map_err(|error| {
        AppError::new(
            error_code,
            "Core failed to write QUIC identity file.",
            "companion.quic_identity",
            true,
        )
        .with_cause(error)
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn generated_identity_builds_server_config() {
        // 场景：Core 首次启动时生成自签证书，并能立即构建 Quinn ServerConfig。
        let identity =
            CoreQuicIdentity::generate_self_signed(vec![String::from("localhost")]).unwrap();
        assert_eq!(identity.certificate_fingerprint_sha256.len(), 64);
        identity.server_config().unwrap();
    }

    #[test]
    fn identity_roundtrips_through_files() {
        // 场景：Core 重启后必须复用同一证书指纹，避免已配对 Android 设备失效。
        let root = std::env::temp_dir().join(format!(
            "adbcontrol-quic-identity-{}",
            rand::random::<u64>()
        ));
        let cert_path = root.join("core.cert.der");
        let key_path = root.join("core.key.der");
        let created = CoreQuicIdentity::load_or_generate(
            &cert_path,
            &key_path,
            vec![String::from("localhost")],
        )
        .unwrap();
        let loaded = CoreQuicIdentity::load_or_generate(
            &cert_path,
            &key_path,
            vec![String::from("localhost")],
        )
        .unwrap();

        assert_eq!(
            created.certificate_fingerprint_sha256,
            loaded.certificate_fingerprint_sha256
        );
        fs::remove_dir_all(root).ok();
    }
}
