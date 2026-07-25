use std::{
    collections::HashMap,
    net::{IpAddr, SocketAddr},
    sync::{Arc, Mutex},
    time::Duration,
};

#[cfg(target_os = "android")]
use std::{
    ffi::CString,
    os::raw::{c_char, c_int},
    sync::Once,
};

use crossbeam_channel::{Receiver, RecvTimeoutError};
use jni::{
    objects::{JByteArray, JObject, JString},
    sys::{jboolean, jint, jlong, jstring},
    JNIEnv,
};
use quinn::{crypto::rustls::QuicClientConfig, ClientConfig, Endpoint};
use rustls::{pki_types::CertificateDer, RootCertStore};
use serde::Deserialize;
use sha2::{Digest, Sha256};
use tokio::{
    io::{AsyncReadExt, AsyncWriteExt},
    runtime::Runtime,
    sync::mpsc,
};
use url::Url;

const ALPN: &[u8] = b"adbcontrol-companion/1";
const CONNECT_TIMEOUT: Duration = Duration::from_secs(6);
const WIRE_MAGIC: &[u8; 4] = b"ACQ1";
const STREAM_CONTROL: u8 = 1;
const STREAM_VIDEO: u8 = 2;
const MAX_CONTROL_MESSAGE_BYTES: usize = 4 * 1024 * 1024;
const VIDEO_QUEUE_CAPACITY: usize = 4;
#[cfg(target_os = "android")]
static PANIC_LOGGER: Once = Once::new();

#[cfg(target_os = "android")]
#[link(name = "log")]
unsafe extern "C" {
    fn __android_log_write(priority: c_int, tag: *const c_char, text: *const c_char) -> c_int;
}

fn install_panic_logger() {
    #[cfg(target_os = "android")]
    PANIC_LOGGER.call_once(|| {
        std::panic::set_hook(Box::new(|panic_info| {
            let Ok(tag) = CString::new("ADBControlQuicNative") else {
                return;
            };
            let message = format!("native Rust panic: {panic_info}");
            let Ok(message) = CString::new(message) else {
                return;
            };
            unsafe {
                __android_log_write(6, tag.as_ptr(), message.as_ptr());
            }
        }));
    });
}

#[derive(Debug, thiserror::Error)]
enum NativeError {
    #[error("QUIC endpoint is invalid: {0}")]
    InvalidEndpoint(String),
    #[error("server certificate is invalid: {0}")]
    InvalidCertificate(String),
    #[error("QUIC connection failed: {0}")]
    Connect(String),
    #[error("QUIC control stream failed: {0}")]
    Control(String),
    #[error("video stream metadata is invalid: {0}")]
    VideoMetadata(String),
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct VideoStreamMetadata {
    session_id: String,
    codec: String,
    width: u32,
    height: u32,
    bitrate: u32,
    frame_rate: u32,
}

struct VideoPacket {
    presentation_time_us: i64,
    flags: u32,
    data: Vec<u8>,
}

struct NativeClient {
    runtime: Option<Runtime>,
    endpoint: Endpoint,
    connection: quinn::Connection,
    outgoing_control: mpsc::UnboundedSender<Vec<u8>>,
    incoming_control: Receiver<String>,
    video_senders: Arc<Mutex<HashMap<String, mpsc::Sender<VideoPacket>>>>,
    certificate_fingerprint_sha256: String,
}

impl NativeClient {
    fn connect(
        endpoint_text: &str,
        server_name: &str,
        certificate_der: Vec<u8>,
    ) -> Result<Self, NativeError> {
        let runtime = Runtime::new().map_err(|error| NativeError::Connect(error.to_string()))?;
        let parsed = Url::parse(endpoint_text)
            .map_err(|error| NativeError::InvalidEndpoint(error.to_string()))?;
        if parsed.scheme() != "quic" {
            return Err(NativeError::InvalidEndpoint(String::from(
                "only quic:// endpoints are accepted",
            )));
        }
        let host = parsed
            .host_str()
            .ok_or_else(|| NativeError::InvalidEndpoint(String::from("host is missing")))?;
        let port = parsed
            .port()
            .ok_or_else(|| NativeError::InvalidEndpoint(String::from("port is missing")))?;

        let certificate_fingerprint_sha256 = hex::encode(Sha256::digest(&certificate_der));
        let mut roots = RootCertStore::empty();
        roots
            .add(CertificateDer::from(certificate_der))
            .map_err(|error| NativeError::InvalidCertificate(error.to_string()))?;
        let mut tls_config = rustls::ClientConfig::builder()
            .with_root_certificates(roots)
            .with_no_client_auth();
        tls_config.alpn_protocols = vec![ALPN.to_vec()];
        let crypto = QuicClientConfig::try_from(tls_config)
            .map_err(|error| NativeError::InvalidCertificate(error.to_string()))?;
        let client_config = ClientConfig::new(Arc::new(crypto));

        let remote = runtime.block_on(resolve_remote(host, port))?;
        let bind_address = match remote.ip() {
            IpAddr::V4(_) => SocketAddr::from(([0, 0, 0, 0], 0)),
            IpAddr::V6(_) => SocketAddr::from(([0u16; 8], 0)),
        };
        // Quinn discovers its Tokio runtime while creating the endpoint. JNI calls arrive
        // on Android binder/main threads, so create it inside the runtime context we own.
        let mut endpoint = runtime
            .block_on(async { Endpoint::client(bind_address) })
            .map_err(|error| NativeError::Connect(error.to_string()))?;
        endpoint.set_default_client_config(client_config);
        let connection = runtime.block_on(async {
            let connecting = endpoint
                .connect(remote, server_name)
                .map_err(|error| NativeError::Connect(error.to_string()))?;
            tokio::time::timeout(CONNECT_TIMEOUT, connecting)
                .await
                .map_err(|_| NativeError::Connect(String::from("timed out after 6 seconds")))?
                .map_err(|error| NativeError::Connect(error.to_string()))
        })?;

        let (mut control_send, mut control_recv) = runtime
            .block_on(connection.open_bi())
            .map_err(|error| NativeError::Control(error.to_string()))?;
        runtime.block_on(async {
            control_send
                .write_all(WIRE_MAGIC)
                .await
                .map_err(|error| NativeError::Control(error.to_string()))?;
            control_send
                .write_u8(STREAM_CONTROL)
                .await
                .map_err(|error| NativeError::Control(error.to_string()))?;
            control_send
                .flush()
                .await
                .map_err(|error| NativeError::Control(error.to_string()))
        })?;

        let (outgoing_control, mut outgoing_receiver) = mpsc::unbounded_channel::<Vec<u8>>();
        let (incoming_sender, incoming_control) = crossbeam_channel::bounded::<String>(256);
        runtime.spawn(async move {
            while let Some(message) = outgoing_receiver.recv().await {
                if message.len() > MAX_CONTROL_MESSAGE_BYTES {
                    continue;
                }
                if control_send.write_u32(message.len() as u32).await.is_err()
                    || control_send.write_all(&message).await.is_err()
                    || control_send.flush().await.is_err()
                {
                    break;
                }
            }
            let _ = control_send.finish();
        });
        runtime.spawn(async move {
            while let Ok(length) = control_recv.read_u32().await {
                let length = length as usize;
                if length == 0 || length > MAX_CONTROL_MESSAGE_BYTES {
                    break;
                }
                let mut bytes = vec![0u8; length];
                if control_recv.read_exact(&mut bytes).await.is_err() {
                    break;
                }
                if let Ok(message) = String::from_utf8(bytes) {
                    if incoming_sender.send(message).is_err() {
                        break;
                    }
                }
            }
        });

        Ok(Self {
            runtime: Some(runtime),
            endpoint,
            connection,
            outgoing_control,
            incoming_control,
            video_senders: Arc::new(Mutex::new(HashMap::new())),
            certificate_fingerprint_sha256,
        })
    }

    fn send_control(&self, json: String) -> Result<(), NativeError> {
        self.outgoing_control
            .send(json.into_bytes())
            .map_err(|_| NativeError::Control(String::from("control stream is closed")))
    }

    fn receive_control(&self, timeout: Duration) -> Result<Option<String>, NativeError> {
        receive_control_message(&self.incoming_control, timeout)
    }

    fn start_video_stream(&self, metadata_json: String) -> Result<(), NativeError> {
        let metadata: VideoStreamMetadata = serde_json::from_str(&metadata_json)
            .map_err(|error| NativeError::VideoMetadata(error.to_string()))?;
        if metadata.session_id.is_empty()
            || metadata.codec != "h264"
            || metadata.width == 0
            || metadata.height == 0
            || metadata.bitrate == 0
            || metadata.frame_rate == 0
        {
            return Err(NativeError::VideoMetadata(String::from(
                "sessionId, H.264 codec, dimensions, bitrate and frameRate are required",
            )));
        }

        let (sender, mut receiver) = mpsc::channel::<VideoPacket>(VIDEO_QUEUE_CAPACITY);
        self.video_senders
            .lock()
            .expect("video sender lock poisoned")
            .insert(metadata.session_id.clone(), sender);
        let connection = self.connection.clone();
        self.runtime
            .as_ref()
            .expect("native QUIC runtime is available")
            .spawn(async move {
                let Ok(mut stream) = connection.open_uni().await else {
                    return;
                };
                let header = metadata_json.into_bytes();
                if stream.write_all(WIRE_MAGIC).await.is_err()
                    || stream.write_u8(STREAM_VIDEO).await.is_err()
                    || stream.write_u32(header.len() as u32).await.is_err()
                    || stream.write_all(&header).await.is_err()
                {
                    return;
                }
                while let Some(packet) = receiver.recv().await {
                    if stream.write_i64(packet.presentation_time_us).await.is_err()
                        || stream.write_u32(packet.flags).await.is_err()
                        || stream.write_u32(packet.data.len() as u32).await.is_err()
                        || stream.write_all(&packet.data).await.is_err()
                    {
                        break;
                    }
                }
                let _ = stream.finish();
            });
        Ok(())
    }

    fn send_video_frame(&self, session_id: &str, packet: VideoPacket) -> bool {
        let sender = self
            .video_senders
            .lock()
            .expect("video sender lock poisoned")
            .get(session_id)
            .cloned();
        sender.is_some_and(|sender| sender.try_send(packet).is_ok())
    }

    fn stop_video_stream(&self, session_id: &str) {
        self.video_senders
            .lock()
            .expect("video sender lock poisoned")
            .remove(session_id);
    }
}

fn receive_control_message(
    receiver: &Receiver<String>,
    timeout: Duration,
) -> Result<Option<String>, NativeError> {
    match receiver.recv_timeout(timeout) {
        Ok(message) => Ok(Some(message)),
        Err(RecvTimeoutError::Timeout) => Ok(None),
        Err(RecvTimeoutError::Disconnected) => Err(NativeError::Control(String::from(
            "control stream is disconnected",
        ))),
    }
}

impl Drop for NativeClient {
    fn drop(&mut self) {
        self.video_senders
            .lock()
            .expect("video sender lock poisoned")
            .clear();
        self.connection.close(0u32.into(), b"client closed");
        self.endpoint.close(0u32.into(), b"client closed");
        if let Some(runtime) = self.runtime.take() {
            runtime.shutdown_timeout(Duration::from_secs(1));
        }
    }
}

async fn resolve_remote(host: &str, port: u16) -> Result<SocketAddr, NativeError> {
    tokio::net::lookup_host((host, port))
        .await
        .map_err(|error| NativeError::Connect(error.to_string()))?
        .next()
        .ok_or_else(|| NativeError::Connect(String::from("host resolved to no address")))
}

fn throw_illegal_state(env: &mut JNIEnv<'_>, message: impl ToString) {
    let _ = env.throw_new("java/lang/IllegalStateException", message.to_string());
}

fn string_from_java(env: &mut JNIEnv<'_>, value: JString<'_>) -> Option<String> {
    env.get_string(&value).ok().map(|value| value.into())
}

unsafe fn client_from_handle<'a>(handle: jlong) -> Option<&'a NativeClient> {
    (handle != 0).then(|| &*(handle as *const NativeClient))
}

#[no_mangle]
pub extern "system" fn Java_com_adbcontrol_companion_quic_JniNativeQuicEngine_nativeConnect(
    mut env: JNIEnv,
    _instance: JObject,
    endpoint: JString,
    server_name: JString,
    certificate_der: JByteArray,
) -> jlong {
    install_panic_logger();
    let Some(endpoint) = string_from_java(&mut env, endpoint) else {
        throw_illegal_state(&mut env, "QUIC endpoint is invalid");
        return 0;
    };
    let Some(server_name) = string_from_java(&mut env, server_name) else {
        throw_illegal_state(&mut env, "QUIC server name is invalid");
        return 0;
    };
    let certificate_der = match env.convert_byte_array(certificate_der) {
        Ok(bytes) if !bytes.is_empty() => bytes,
        _ => {
            throw_illegal_state(&mut env, "QUIC server certificate is missing");
            return 0;
        }
    };
    match std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        NativeClient::connect(&endpoint, &server_name, certificate_der)
    })) {
        Ok(Ok(client)) => Box::into_raw(Box::new(client)) as jlong,
        Ok(Err(error)) => {
            throw_illegal_state(&mut env, error);
            0
        }
        Err(payload) => {
            let detail = payload
                .downcast_ref::<String>()
                .cloned()
                .or_else(|| {
                    payload
                        .downcast_ref::<&str>()
                        .map(|value| (*value).to_owned())
                })
                .unwrap_or_else(|| String::from("unknown native panic"));
            throw_illegal_state(&mut env, format!("native QUIC panic: {detail}"));
            0
        }
    }
}

#[no_mangle]
pub extern "system" fn Java_com_adbcontrol_companion_quic_JniNativeQuicEngine_nativeSendEnvelope(
    mut env: JNIEnv,
    _instance: JObject,
    handle: jlong,
    envelope_json: JString,
) {
    let Some(client) = (unsafe { client_from_handle(handle) }) else {
        throw_illegal_state(&mut env, "native QUIC client is closed");
        return;
    };
    let Some(envelope_json) = string_from_java(&mut env, envelope_json) else {
        throw_illegal_state(&mut env, "QUIC envelope JSON is invalid");
        return;
    };
    if let Err(error) = client.send_control(envelope_json) {
        throw_illegal_state(&mut env, error);
    }
}

#[no_mangle]
pub extern "system" fn Java_com_adbcontrol_companion_quic_JniNativeQuicEngine_nativePollEnvelope(
    env: JNIEnv,
    _instance: JObject,
    handle: jlong,
    timeout_ms: jint,
) -> jstring {
    let Some(client) = (unsafe { client_from_handle(handle) }) else {
        return std::ptr::null_mut();
    };
    match client.receive_control(Duration::from_millis(timeout_ms.max(0) as u64)) {
        Ok(Some(message)) => env
            .new_string(message)
            .map_or(std::ptr::null_mut(), |message| message.into_raw()),
        Ok(None) => std::ptr::null_mut(),
        Err(error) => {
            let mut env = env;
            throw_illegal_state(&mut env, error);
            std::ptr::null_mut()
        }
    }
}

#[no_mangle]
pub extern "system" fn Java_com_adbcontrol_companion_quic_JniNativeQuicEngine_nativeStartVideoStream(
    mut env: JNIEnv,
    _instance: JObject,
    handle: jlong,
    metadata_json: JString,
) {
    let Some(client) = (unsafe { client_from_handle(handle) }) else {
        throw_illegal_state(&mut env, "native QUIC client is closed");
        return;
    };
    let Some(metadata_json) = string_from_java(&mut env, metadata_json) else {
        throw_illegal_state(&mut env, "video stream metadata is invalid");
        return;
    };
    if let Err(error) = client.start_video_stream(metadata_json) {
        throw_illegal_state(&mut env, error);
    }
}

#[no_mangle]
pub extern "system" fn Java_com_adbcontrol_companion_quic_JniNativeQuicEngine_nativeSendVideoFrame(
    mut env: JNIEnv,
    _instance: JObject,
    handle: jlong,
    session_id: JString,
    presentation_time_us: jlong,
    flags: jint,
    data: JByteArray,
) -> jboolean {
    let Some(client) = (unsafe { client_from_handle(handle) }) else {
        return 0;
    };
    let Some(session_id) = string_from_java(&mut env, session_id) else {
        return 0;
    };
    let Ok(data) = env.convert_byte_array(data) else {
        return 0;
    };
    client.send_video_frame(
        &session_id,
        VideoPacket {
            presentation_time_us,
            flags: flags as u32,
            data,
        },
    ) as jboolean
}

#[no_mangle]
pub extern "system" fn Java_com_adbcontrol_companion_quic_JniNativeQuicEngine_nativeStopVideoStream(
    mut env: JNIEnv,
    _instance: JObject,
    handle: jlong,
    session_id: JString,
) {
    let Some(client) = (unsafe { client_from_handle(handle) }) else {
        return;
    };
    if let Some(session_id) = string_from_java(&mut env, session_id) {
        client.stop_video_stream(&session_id);
    }
}

#[no_mangle]
pub extern "system" fn Java_com_adbcontrol_companion_quic_JniNativeQuicEngine_nativeCertificateFingerprintSha256(
    env: JNIEnv,
    _instance: JObject,
    handle: jlong,
) -> jstring {
    let Some(client) = (unsafe { client_from_handle(handle) }) else {
        return std::ptr::null_mut();
    };
    env.new_string(&client.certificate_fingerprint_sha256)
        .map_or(std::ptr::null_mut(), |value| value.into_raw())
}

#[no_mangle]
pub extern "system" fn Java_com_adbcontrol_companion_quic_JniNativeQuicEngine_nativeClose(
    _env: JNIEnv,
    _instance: JObject,
    handle: jlong,
) {
    if handle != 0 {
        unsafe { drop(Box::from_raw(handle as *mut NativeClient)) };
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_valid_video_metadata() {
        let metadata: VideoStreamMetadata = serde_json::from_str(
            r#"{"sessionId":"s1","codec":"h264","width":576,"height":1280,"bitrate":8000000,"frameRate":60}"#,
        )
        .unwrap();
        assert_eq!(metadata.session_id, "s1");
        assert_eq!(metadata.width, 576);
    }

    #[test]
    fn wire_constants_are_stable() {
        assert_eq!(WIRE_MAGIC, b"ACQ1");
        assert_ne!(STREAM_CONTROL, STREAM_VIDEO);
    }

    #[test]
    fn distinguishes_control_timeout_from_disconnect() {
        let (sender, receiver) = crossbeam_channel::bounded::<String>(1);
        assert!(matches!(
            receive_control_message(&receiver, Duration::from_millis(1)),
            Ok(None)
        ));
        drop(sender);
        assert!(matches!(
            receive_control_message(&receiver, Duration::from_millis(1)),
            Err(NativeError::Control(_))
        ));
    }
}
