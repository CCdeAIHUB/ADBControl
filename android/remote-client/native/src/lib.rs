use std::{
    net::{IpAddr, SocketAddr},
    sync::{Arc, Mutex, Once},
    time::Duration,
};

use jni::{
    objects::{JByteArray, JObject, JString},
    sys::{jlong, jstring},
    JNIEnv,
};
use quinn::{ClientConfig, Endpoint};
use rustls::{
    client::danger::{HandshakeSignatureValid, ServerCertVerified, ServerCertVerifier},
    crypto::{verify_tls12_signature, verify_tls13_signature, WebPkiSupportedAlgorithms},
    pki_types::{CertificateDer, ServerName, UnixTime},
    DigitallySignedStruct, Error, SignatureScheme,
};
use sha2::{Digest, Sha256};
use tokio::runtime::Runtime;
use url::Url;

/// 协议名对应的 ALPN 标识。
/// 实测说明：Core 的 `CoreQuicIdentity::server_config()`（quic_identity.rs）没有配置任何
/// ALPN；如果客户端强制要求 ALPN，TLS 1.3 握手会因无协商结果直接失败。
/// 因此客户端不发送 ALPN 扩展，身份校验完全依赖证书指纹/证书固定（TOFU 或预置指纹）。
pub const PROTOCOL_ALPN: &[u8] = b"adbcontrol-core-remote-quic/1";
const MAX_REQUEST_BYTES: usize = 1024 * 1024;
// Companion screenshots are base64-encoded today and may exceed the request limit.
// Keep a separate bounded response ceiling until Core exposes a binary media stream.
const MAX_RESPONSE_BYTES: usize = 4 * 1024 * 1024;
static CRYPTO: Once = Once::new();

#[derive(Debug, thiserror::Error)]
enum NativeError {
    #[error("远程地址无效：{0}")]
    Endpoint(String),
    #[error("Core 证书无效：{0}")]
    Certificate(String),
    #[error("证书指纹无效：{0}")]
    Fingerprint(String),
    #[error("QUIC 连接失败：{0}")]
    Connect(String),
    #[error("远程请求失败：{0}")]
    Request(String),
}

struct RemoteClient {
    runtime: Runtime,
    endpoint: Endpoint,
    connection: quinn::Connection,
    /// 服务器证书的 SHA-256（hex 小写）。Pinned 模式下已验证一致；TOFU 模式下为首次记录值。
    fingerprint: String,
}

/// 证书信任策略。
#[derive(Debug, Clone)]
enum TrustMode {
    /// 预置指纹（来自 Core 启动日志或用户提供的证书）。
    Pinned([u8; 32]),
    /// 首次使用即信任：接受任意证书并记录其指纹，由 Kotlin 层保存并在后续连接校验。
    TrustOnFirstUse,
}

impl TrustMode {
    fn from_inputs(certificate: &[u8], fingerprint_hex: &str) -> Result<Self, NativeError> {
        if !fingerprint_hex.is_empty() {
            let normalized = normalize_fingerprint(fingerprint_hex).ok_or_else(|| {
                NativeError::Fingerprint("指纹必须是 64 位十六进制（SHA-256）".into())
            })?;
            let mut pinned = [0u8; 32];
            hex::decode_to_slice(&normalized, &mut pinned)
                .map_err(|error| NativeError::Fingerprint(error.to_string()))?;
            return Ok(Self::Pinned(pinned));
        }
        if !certificate.is_empty() {
            // 用户直接粘贴证书：等价于把该证书的 SHA-256 作为固定指纹。
            return Ok(Self::Pinned(Sha256::digest(certificate).into()));
        }
        Ok(Self::TrustOnFirstUse)
    }
}

/// 允许 `:` 分隔与大小写混合的指纹输入；返回 64 位小写 hex，非法输入返回 None。
fn normalize_fingerprint(value: &str) -> Option<String> {
    let cleaned: String = value
        .chars()
        .filter(|c| *c != ':' && !c.is_whitespace())
        .collect();
    let lowered = cleaned.to_lowercase();
    if lowered.len() == 64 && lowered.bytes().all(|b| b.is_ascii_hexdigit()) {
        Some(lowered)
    } else {
        None
    }
}

#[cfg(test)]
fn fingerprint_matches(certificate: &[u8], expected_hex: &str) -> bool {
    match normalize_fingerprint(expected_hex) {
        Some(expected) => {
            let digest = Sha256::digest(certificate);
            hex::encode(digest) == expected
        }
        None => false,
    }
}

/// 指纹固定校验器：不验证证书链与主机名（身份=SHA-256 指纹），
/// 但保留握手签名校验（防止没有服务器私钥的中间人伪造握手）。
#[derive(Debug)]
struct FingerprintVerifier {
    mode: TrustMode,
    observed: Arc<Mutex<Option<String>>>,
    algs: WebPkiSupportedAlgorithms,
}

impl FingerprintVerifier {
    fn new(
        mode: TrustMode,
        observed: Arc<Mutex<Option<String>>>,
        algs: WebPkiSupportedAlgorithms,
    ) -> Self {
        Self {
            mode,
            observed,
            algs,
        }
    }
}

impl ServerCertVerifier for FingerprintVerifier {
    fn verify_server_cert(
        &self,
        end_entity: &CertificateDer<'_>,
        _intermediates: &[CertificateDer<'_>],
        _server_name: &ServerName<'_>,
        _ocsp_response: &[u8],
        _now: UnixTime,
    ) -> Result<ServerCertVerified, Error> {
        let digest = Sha256::digest(end_entity.as_ref());
        let hex_digest = hex::encode(digest);
        match &self.mode {
            TrustMode::Pinned(expected) => {
                if expected.as_slice() != digest.as_slice() {
                    return Err(Error::General(
                        "REMOTE_CERTIFICATE_FINGERPRINT_MISMATCH".into(),
                    ));
                }
            }
            TrustMode::TrustOnFirstUse => {}
        }
        if let Ok(mut slot) = self.observed.lock() {
            *slot = Some(hex_digest);
        }
        Ok(ServerCertVerified::assertion())
    }

    fn verify_tls12_signature(
        &self,
        message: &[u8],
        cert: &CertificateDer<'_>,
        dss: &DigitallySignedStruct,
    ) -> Result<HandshakeSignatureValid, Error> {
        verify_tls12_signature(message, cert, dss, &self.algs)
    }

    fn verify_tls13_signature(
        &self,
        message: &[u8],
        cert: &CertificateDer<'_>,
        dss: &DigitallySignedStruct,
    ) -> Result<HandshakeSignatureValid, Error> {
        verify_tls13_signature(message, cert, dss, &self.algs)
    }

    fn supported_verify_schemes(&self) -> Vec<SignatureScheme> {
        self.algs.supported_schemes()
    }
}

impl RemoteClient {
    fn connect(
        endpoint_text: &str,
        server_name: &str,
        certificate: Vec<u8>,
        fingerprint_hex: &str,
    ) -> Result<Self, NativeError> {
        CRYPTO.call_once(|| {
            let _ = rustls::crypto::ring::default_provider().install_default();
        });
        let parsed = Url::parse(endpoint_text).map_err(|e| NativeError::Endpoint(e.to_string()))?;
        if parsed.scheme() != "quic" {
            return Err(NativeError::Endpoint("仅接受 quic:// 地址".into()));
        }
        let host = parsed
            .host_str()
            .ok_or_else(|| NativeError::Endpoint("缺少主机名".into()))?;
        let port = parsed
            .port()
            .ok_or_else(|| NativeError::Endpoint("缺少端口".into()))?;
        let trust_mode = TrustMode::from_inputs(&certificate, fingerprint_hex)?;
        let runtime = Runtime::new().map_err(|e| NativeError::Connect(e.to_string()))?;
        let remote = runtime.block_on(async {
            tokio::net::lookup_host((host, port))
                .await
                .map_err(|e| NativeError::Connect(e.to_string()))?
                .next()
                .ok_or_else(|| NativeError::Connect("域名没有解析结果".into()))
        })?;

        // 不发送 ALPN 扩展：Core 服务端未配置 ALPN，任何非空要求都会导致握手失败。
        let provider = rustls::crypto::ring::default_provider();
        let observed: Arc<Mutex<Option<String>>> = Arc::new(Mutex::new(None));
        let verifier = FingerprintVerifier::new(
            trust_mode,
            Arc::clone(&observed),
            provider.signature_verification_algorithms,
        );
        let tls = rustls::ClientConfig::builder()
            .dangerous()
            .with_custom_certificate_verifier(Arc::new(verifier))
            .with_no_client_auth();
        let crypto = quinn::crypto::rustls::QuicClientConfig::try_from(tls)
            .map_err(|e| NativeError::Certificate(e.to_string()))?;
        let bind = match remote.ip() {
            IpAddr::V4(_) => SocketAddr::from(([0, 0, 0, 0], 0)),
            IpAddr::V6(_) => SocketAddr::from(([0u16; 8], 0)),
        };
        let mut endpoint = runtime
            .block_on(async { Endpoint::client(bind) })
            .map_err(|e| NativeError::Connect(e.to_string()))?;
        endpoint.set_default_client_config(ClientConfig::new(std::sync::Arc::new(crypto)));
        let connection = runtime.block_on(async {
            let pending = endpoint
                .connect(remote, server_name)
                .map_err(|e| NativeError::Connect(e.to_string()))?;
            tokio::time::timeout(Duration::from_secs(8), pending)
                .await
                .map_err(|_| NativeError::Connect("连接超时".into()))?
                .map_err(|e| NativeError::Connect(e.to_string()))
        })?;
        let fingerprint = observed
            .lock()
            .ok()
            .and_then(|slot| slot.clone())
            .unwrap_or_else(|| {
                // 理论不可达：verify_server_cert 必然记录 observed；兜底用旧证书摘要。
                if certificate.is_empty() {
                    String::new()
                } else {
                    hex::encode(Sha256::digest(&certificate))
                }
            });
        Ok(Self {
            runtime,
            endpoint,
            connection,
            fingerprint,
        })
    }

    fn request(&self, payload: Vec<u8>, timeout: Duration) -> Result<String, NativeError> {
        if payload.is_empty() || payload.len() > MAX_REQUEST_BYTES {
            return Err(NativeError::Request(
                "请求大小必须在 1 B 到 1 MiB 之间".into(),
            ));
        }
        self.runtime.block_on(async {
            tokio::time::timeout(timeout, async {
                let (mut send, mut recv) = self
                    .connection
                    .open_bi()
                    .await
                    .map_err(|e| NativeError::Request(e.to_string()))?;
                send.write_all(&payload)
                    .await
                    .map_err(|e| NativeError::Request(e.to_string()))?;
                send.finish()
                    .map_err(|e| NativeError::Request(e.to_string()))?;
                let bytes = recv
                    .read_to_end(MAX_RESPONSE_BYTES)
                    .await
                    .map_err(|e| NativeError::Request(e.to_string()))?;
                String::from_utf8(bytes).map_err(|e| NativeError::Request(e.to_string()))
            })
            .await
            .map_err(|_| NativeError::Request("请求超时".into()))?
        })
    }
}

impl Drop for RemoteClient {
    fn drop(&mut self) {
        self.connection.close(0u32.into(), b"android remote closed");
        self.endpoint.close(0u32.into(), b"android remote closed");
    }
}

fn java_string(env: &mut JNIEnv<'_>, value: JString<'_>) -> Option<String> {
    env.get_string(&value).ok().map(Into::into)
}
fn throw(env: &mut JNIEnv<'_>, error: impl ToString) {
    let _ = env.throw_new("java/lang/IllegalStateException", error.to_string());
}
unsafe fn client<'a>(handle: jlong) -> Option<&'a RemoteClient> {
    (handle != 0).then(|| &*(handle as *const RemoteClient))
}

#[no_mangle]
pub extern "system" fn Java_com_adbcontrol_remote_transport_NativeQuicBridge_nativeConnect(
    mut env: JNIEnv,
    _: JObject,
    endpoint: JString,
    server_name: JString,
    certificate: JByteArray,
    fingerprint_hex: JString,
) -> jlong {
    let Some(endpoint) = java_string(&mut env, endpoint) else {
        return 0;
    };
    let Some(server_name) = java_string(&mut env, server_name) else {
        return 0;
    };
    let certificate = match env.convert_byte_array(certificate) {
        Ok(certificate) => certificate,
        Err(error) => {
            throw(
                &mut env,
                format!("Core certificate JNI input is invalid: {error}"),
            );
            return 0;
        }
    };
    let fingerprint_hex = java_string(&mut env, fingerprint_hex).unwrap_or_default();
    match std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        RemoteClient::connect(&endpoint, &server_name, certificate, &fingerprint_hex)
    })) {
        Ok(Ok(value)) => Box::into_raw(Box::new(value)) as jlong,
        Ok(Err(error)) => {
            throw(&mut env, error);
            0
        }
        Err(_) => {
            throw(&mut env, "QUIC 原生传输发生异常");
            0
        }
    }
}

#[no_mangle]
pub extern "system" fn Java_com_adbcontrol_remote_transport_NativeQuicBridge_nativeRequest(
    mut env: JNIEnv,
    _: JObject,
    handle: jlong,
    request: JString,
    timeout_ms: jlong,
) -> jstring {
    let Some(client) = (unsafe { client(handle) }) else {
        throw(&mut env, "QUIC 连接已关闭");
        return std::ptr::null_mut();
    };
    let Some(request) = java_string(&mut env, request) else {
        return std::ptr::null_mut();
    };
    match client.request(
        request.into_bytes(),
        Duration::from_millis(timeout_ms.max(1) as u64),
    ) {
        Ok(response) => env
            .new_string(response)
            .map_or(std::ptr::null_mut(), |v| v.into_raw()),
        Err(error) => {
            throw(&mut env, error);
            std::ptr::null_mut()
        }
    }
}

#[no_mangle]
pub extern "system" fn Java_com_adbcontrol_remote_transport_NativeQuicBridge_nativeFingerprint(
    env: JNIEnv,
    _: JObject,
    handle: jlong,
) -> jstring {
    let Some(client) = (unsafe { client(handle) }) else {
        return std::ptr::null_mut();
    };
    env.new_string(&client.fingerprint)
        .map_or(std::ptr::null_mut(), |v| v.into_raw())
}

#[no_mangle]
pub extern "system" fn Java_com_adbcontrol_remote_transport_NativeQuicBridge_nativeClose(
    _: JNIEnv,
    _: JObject,
    handle: jlong,
) {
    if handle != 0 {
        unsafe {
            drop(Box::from_raw(handle as *mut RemoteClient));
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn protocol_constant_documents_core_contract() {
        // 场景：协议 ALPN 常量保留为文档性契约（服务端当前不协商 ALPN，见类型注释）。
        assert_eq!(PROTOCOL_ALPN, b"adbcontrol-core-remote-quic/1");
    }
    #[test]
    fn response_limit_matches_core_contract() {
        assert_eq!(MAX_REQUEST_BYTES, 1024 * 1024);
        assert_eq!(MAX_RESPONSE_BYTES, 4 * 1024 * 1024);
    }
    #[test]
    fn fingerprint_normalization_and_matching() {
        // 场景：允许冒号分隔与大小写混合；非法输入拒绝；摘要比较正确。
        let mixed = normalize_fingerprint(
            "AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89",
        )
        .expect("mixed-case colon input should normalize");
        assert_eq!(mixed.len(), 64);
        assert!(mixed
            .chars()
            .all(|c| c.is_ascii_hexdigit() && !c.is_ascii_uppercase()));
        assert!(normalize_fingerprint("not-a-fingerprint").is_none());
        assert!(normalize_fingerprint("abcd").is_none());
        let digest = hex::encode(Sha256::digest(b"abc"));
        assert!(fingerprint_matches(b"abc", &digest));
        assert!(fingerprint_matches(b"abc", &digest.to_uppercase()));
        assert!(!fingerprint_matches(
            b"abc",
            &hex::encode(Sha256::digest(b"abd"))
        ));
        assert!(!fingerprint_matches(b"abc", "zz"));
    }
    #[test]
    fn trust_mode_prefers_fingerprint_then_certificate() {
        // 场景：指纹优先；无指纹时以证书摘要为固定值；两者皆空进入 TOFU。
        let cert = b"server-cert".to_vec();
        let digest_hex = hex::encode(Sha256::digest(&cert));
        match TrustMode::from_inputs(&cert, "").expect("cert mode") {
            TrustMode::Pinned(pinned) => assert_eq!(hex::encode(pinned), digest_hex),
            other => panic!("cert input must pin, got {:?}", other),
        }
        match TrustMode::from_inputs(&cert, &digest_hex).expect("fingerprint mode") {
            TrustMode::Pinned(pinned) => assert_eq!(hex::encode(pinned), digest_hex),
            other => panic!("fingerprint input must pin, got {:?}", other),
        }
        assert!(matches!(
            TrustMode::from_inputs(b"", "").expect("tofu mode"),
            TrustMode::TrustOnFirstUse
        ));
        assert!(TrustMode::from_inputs(b"", "nothex").is_err());
    }

    #[test]
    #[ignore = "requires ADBCONTROL_REMOTE_TEST_ENDPOINT"]
    fn live_remote_endpoint_completes_tls_and_quic_handshake() {
        // 场景：使用与 Android App 完全相同的客户端连接真实 Core；只验证
        // UDP/TLS/QUIC 握手，不发送账号、密码或远程协议请求。
        let endpoint = std::env::var("ADBCONTROL_REMOTE_TEST_ENDPOINT")
            .expect("ADBCONTROL_REMOTE_TEST_ENDPOINT must be set");
        let client = RemoteClient::connect(&endpoint, "adbcontrol.local", Vec::new(), "")
            .expect("deployed Core should accept the production client handshake");

        assert_eq!(client.fingerprint.len(), 64);
    }
}
