use std::{
    fs,
    path::{Path, PathBuf},
};

use base64::{engine::general_purpose, Engine as _};
use serde_json::Value;

use crate::error::AppError;

use super::protocol::{QuicEnvelope, QuicMessageKind};

#[derive(Debug, Clone)]
pub struct CompanionMediaStore {
    root_dir: PathBuf,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StoredMediaChunk {
    pub session_id: String,
    pub chunk_index: u64,
    pub path: PathBuf,
    pub size_bytes: usize,
}

impl CompanionMediaStore {
    pub fn new(root_dir: impl Into<PathBuf>) -> Self {
        Self {
            root_dir: root_dir.into(),
        }
    }

    pub fn store_envelope(
        &self,
        envelope: &QuicEnvelope,
    ) -> Result<Option<StoredMediaChunk>, AppError> {
        if envelope.kind != QuicMessageKind::StreamChunk {
            return Ok(None);
        }

        let session_id = required_string(&envelope.payload, "sessionId")?;
        let encoding = required_string(&envelope.payload, "encoding")?;
        if encoding != "base64" {
            return Err(AppError::new(
                "COMPANION_MEDIA_ENCODING_UNSUPPORTED",
                "Core media store only accepts base64 media chunks in JSON envelopes.",
                "companion.media_store",
                false,
            ));
        }

        let data = required_string(&envelope.payload, "data")?;
        let bytes = general_purpose::STANDARD.decode(data).map_err(|error| {
            AppError::new(
                "COMPANION_MEDIA_CHUNK_BASE64_INVALID",
                "Core failed to decode base64 media chunk.",
                "companion.media_store",
                false,
            )
            .with_cause(error)
        })?;
        let chunk_index = next_chunk_index(&self.root_dir, session_id)?;
        let session_dir = self.root_dir.join(safe_path_segment(session_id));
        fs::create_dir_all(&session_dir).map_err(|error| {
            AppError::new(
                "COMPANION_MEDIA_STORE_DIR_CREATE_FAILED",
                "Core failed to create media session directory.",
                "companion.media_store",
                true,
            )
            .with_cause(error)
        })?;
        let path = session_dir.join(format!("chunk-{chunk_index:020}.bin"));
        fs::write(&path, &bytes).map_err(|error| {
            AppError::new(
                "COMPANION_MEDIA_CHUNK_WRITE_FAILED",
                "Core failed to write media chunk.",
                "companion.media_store",
                true,
            )
            .with_cause(error)
        })?;

        Ok(Some(StoredMediaChunk {
            session_id: session_id.to_string(),
            chunk_index,
            path,
            size_bytes: bytes.len(),
        }))
    }
}

fn required_string<'a>(payload: &'a Value, key: &str) -> Result<&'a str, AppError> {
    payload.get(key).and_then(Value::as_str).ok_or_else(|| {
        AppError::new(
            "COMPANION_MEDIA_PAYLOAD_INVALID",
            format!("Media streamChunk payload is missing string field {key}."),
            "companion.media_store",
            false,
        )
    })
}

fn next_chunk_index(root_dir: &Path, session_id: &str) -> Result<u64, AppError> {
    let session_dir = root_dir.join(safe_path_segment(session_id));
    if !session_dir.exists() {
        return Ok(0);
    }
    let mut count = 0_u64;
    for entry in fs::read_dir(&session_dir).map_err(|error| {
        AppError::new(
            "COMPANION_MEDIA_STORE_READ_FAILED",
            "Core failed to read media session directory.",
            "companion.media_store",
            true,
        )
        .with_cause(error)
    })? {
        let entry = entry.map_err(|error| {
            AppError::new(
                "COMPANION_MEDIA_STORE_READ_FAILED",
                "Core failed to read media chunk entry.",
                "companion.media_store",
                true,
            )
            .with_cause(error)
        })?;
        if entry.file_name().to_string_lossy().starts_with("chunk-") {
            count += 1;
        }
    }
    Ok(count)
}

fn safe_path_segment(value: &str) -> String {
    value
        .chars()
        .map(|character| {
            if character.is_ascii_alphanumeric() || character == '-' || character == '_' {
                character
            } else {
                '_'
            }
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::companion::protocol::{QuicChannel, COMPANION_PROTOCOL, COMPANION_PROTOCOL_VERSION};

    #[test]
    fn stores_base64_stream_chunk_by_session() {
        // 场景：Core 收到 Android 伴侣 App 的 media streamChunk 后，必须按 sessionId 落盘，供后续 relay/读取。
        let root =
            std::env::temp_dir().join(format!("adbcontrol-media-store-{}", rand::random::<u64>()));
        let store = CompanionMediaStore::new(&root);
        let stored = store
            .store_envelope(&chunk_envelope("stream-1", "AQIDBA=="))
            .unwrap()
            .unwrap();

        assert_eq!(stored.session_id, "stream-1");
        assert_eq!(stored.chunk_index, 0);
        assert_eq!(fs::read(&stored.path).unwrap(), vec![1, 2, 3, 4]);
        fs::remove_dir_all(root).ok();
    }

    #[test]
    fn unsupported_media_encoding_is_rejected() {
        // 场景：媒体 chunk 编码不明确时，Core 不能写入无法解析的内容。
        let root =
            std::env::temp_dir().join(format!("adbcontrol-media-store-{}", rand::random::<u64>()));
        let store = CompanionMediaStore::new(&root);
        let mut envelope = chunk_envelope("stream-1", "AQIDBA==");
        envelope.payload["encoding"] = Value::String(String::from("raw"));

        let error = store.store_envelope(&envelope).unwrap_err();
        assert_eq!(error.error_code, "COMPANION_MEDIA_ENCODING_UNSUPPORTED");
        fs::remove_dir_all(root).ok();
    }

    fn chunk_envelope(session_id: &str, data: &str) -> QuicEnvelope {
        QuicEnvelope {
            protocol: String::from(COMPANION_PROTOCOL),
            version: COMPANION_PROTOCOL_VERSION,
            message_id: String::from("chunk-1"),
            trace_id: Some(String::from(session_id)),
            device_id: Some(String::from("device-1")),
            channel: QuicChannel::Media,
            kind: QuicMessageKind::StreamChunk,
            payload: serde_json::json!({
                "sessionId": session_id,
                "chunkType": "media",
                "encoding": "base64",
                "data": data,
                "sizeBytes": 4
            }),
        }
    }
}
