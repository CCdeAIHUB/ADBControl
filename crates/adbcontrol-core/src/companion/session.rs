use std::collections::HashMap;

use serde::{Deserialize, Serialize};
use serde_json::{json, Value};

use crate::{
    capability::{Capability, CapabilityPermissionState},
    error::AppError,
};

use super::{
    protocol::{
        validate_quic_envelope, CompanionHello, QuicChannel, QuicEnvelope, QuicMessageKind,
        COMPANION_PROTOCOL, COMPANION_PROTOCOL_VERSION,
    },
    registry::{CompanionDevice, ConnectionState},
};

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CompanionSession {
    pub device_id: String,
    pub display_name: String,
    pub app_version: String,
    pub android_sdk: u32,
    pub connection_state: ConnectionState,
    pub capabilities: Vec<Capability>,
    pub permission_states: Vec<CapabilityPermissionState>,
}

impl CompanionSession {
    pub fn to_device(&self) -> CompanionDevice {
        CompanionDevice {
            device_id: self.device_id.clone(),
            display_name: self.display_name.clone(),
            app_version: self.app_version.clone(),
            android_sdk: self.android_sdk,
            connection_state: self.connection_state.clone(),
            capabilities: self.capabilities.clone(),
            permission_states: self.permission_states.clone(),
        }
    }
}

#[derive(Debug, Default, Clone)]
pub struct CompanionSessionManager {
    sessions: HashMap<String, CompanionSession>,
}

impl CompanionSessionManager {
    pub fn handle_envelope(&mut self, envelope: QuicEnvelope) -> Result<QuicEnvelope, AppError> {
        validate_quic_envelope(&envelope)?;

        match envelope.kind {
            QuicMessageKind::Hello => self.handle_hello(envelope),
            QuicMessageKind::CapabilityList => self.handle_capability_list(envelope),
            QuicMessageKind::PermissionState => self.handle_permission_state(envelope),
            QuicMessageKind::Heartbeat => self.handle_heartbeat(envelope),
            unsupported => Err(AppError::new(
                "COMPANION_SESSION_MESSAGE_UNSUPPORTED",
                format!("Companion session manager does not accept message kind: {unsupported:?}"),
                "companion.session",
                false,
            )),
        }
    }

    pub fn get_session(&self, device_id: &str) -> Result<&CompanionSession, AppError> {
        self.sessions.get(device_id).ok_or_else(|| {
            AppError::new(
                "COMPANION_SESSION_NOT_CONNECTED",
                format!("Android companion QUIC session is not connected for device {device_id}."),
                "companion.session",
                true,
            )
            .with_suggestion(
                "Complete companion hello/helloAck before synchronizing capability state.",
            )
        })
    }

    pub fn devices(&self) -> Vec<CompanionDevice> {
        self.sessions
            .values()
            .map(CompanionSession::to_device)
            .collect()
    }

    fn handle_hello(&mut self, envelope: QuicEnvelope) -> Result<QuicEnvelope, AppError> {
        let envelope_device_id = envelope.device_id.clone();
        let hello: CompanionHello = serde_json::from_value(envelope.payload).map_err(|error| {
            AppError::new(
                "COMPANION_HELLO_INVALID",
                "Companion hello payload is invalid.",
                "companion.session",
                false,
            )
            .with_cause(error)
        })?;

        if let Some(envelope_device_id) =
            envelope_device_id.filter(|device_id| !device_id.trim().is_empty())
        {
            if envelope_device_id != hello.device_id {
                return Err(AppError::new(
                    "COMPANION_HELLO_DEVICE_ID_MISMATCH",
                    "Companion hello envelope deviceId must match payload deviceId.",
                    "companion.session",
                    false,
                ));
            }
        }

        if !hello
            .supported_protocol_versions
            .contains(&COMPANION_PROTOCOL_VERSION)
        {
            return Err(AppError::new(
                "COMPANION_PROTOCOL_VERSION_UNSUPPORTED",
                format!(
                    "Companion device {} does not support protocol version {}.",
                    hello.device_id, COMPANION_PROTOCOL_VERSION
                ),
                "companion.session",
                false,
            ));
        }

        self.sessions.insert(
            hello.device_id.clone(),
            CompanionSession {
                device_id: hello.device_id.clone(),
                display_name: hello.device_name.clone(),
                app_version: hello.app_version.clone(),
                android_sdk: hello.android_sdk,
                connection_state: ConnectionState::Handshaking,
                capabilities: Vec::new(),
                permission_states: Vec::new(),
            },
        );

        Ok(QuicEnvelope {
            protocol: String::from(COMPANION_PROTOCOL),
            version: COMPANION_PROTOCOL_VERSION,
            message_id: format!("hello-ack-{}", hello.device_id),
            trace_id: envelope.trace_id,
            device_id: Some(hello.device_id),
            channel: QuicChannel::Control,
            kind: QuicMessageKind::HelloAck,
            payload: json!({
                "accepted": true,
                "selectedProtocolVersion": COMPANION_PROTOCOL_VERSION,
                "serverName": "ADBControl Core",
                "nextRequiredMessages": ["capabilityList", "permissionState"]
            }),
        })
    }

    fn handle_capability_list(&mut self, envelope: QuicEnvelope) -> Result<QuicEnvelope, AppError> {
        let device_id = require_device_id(&envelope)?;
        let capabilities: Vec<Capability> = parse_required_payload_field(
            &envelope.payload,
            "capabilities",
            "COMPANION_CAPABILITY_SCHEMA_INVALID",
            "Companion capabilityList payload is invalid.",
        )?;

        let session = self.sessions.get_mut(&device_id).ok_or_else(|| {
            AppError::new(
                "COMPANION_SESSION_NOT_CONNECTED",
                format!("Capability list arrived before hello for device {device_id}."),
                "companion.session",
                true,
            )
        })?;
        session.capabilities = capabilities;
        if !session.permission_states.is_empty() {
            session.connection_state = ConnectionState::Ready;
        }

        Ok(ack(
            envelope.trace_id,
            device_id,
            envelope.message_id,
            json!({
                "accepted": true,
                "registeredCapabilityCount": session.capabilities.len()
            }),
        ))
    }

    fn handle_permission_state(
        &mut self,
        envelope: QuicEnvelope,
    ) -> Result<QuicEnvelope, AppError> {
        let device_id = require_device_id(&envelope)?;
        let states: Vec<CapabilityPermissionState> = parse_required_payload_field(
            &envelope.payload,
            "states",
            "COMPANION_PERMISSION_STATE_INVALID",
            "Companion permissionState payload is invalid.",
        )?;

        let session = self.sessions.get_mut(&device_id).ok_or_else(|| {
            AppError::new(
                "COMPANION_SESSION_NOT_CONNECTED",
                format!("Permission state arrived before hello for device {device_id}."),
                "companion.session",
                true,
            )
        })?;
        session.permission_states = states;
        if !session.capabilities.is_empty() {
            session.connection_state = ConnectionState::Ready;
        }

        Ok(ack(
            envelope.trace_id,
            device_id,
            envelope.message_id,
            json!({
                "accepted": true,
                "updatedStateCount": session.permission_states.len()
            }),
        ))
    }

    fn handle_heartbeat(&self, envelope: QuicEnvelope) -> Result<QuicEnvelope, AppError> {
        let device_id = require_device_id(&envelope)?;
        let session = self.get_session(&device_id)?;

        Ok(QuicEnvelope {
            protocol: String::from(COMPANION_PROTOCOL),
            version: COMPANION_PROTOCOL_VERSION,
            message_id: format!("heartbeat-ack-{}", envelope.message_id),
            trace_id: envelope.trace_id,
            device_id: Some(device_id),
            channel: QuicChannel::Control,
            kind: QuicMessageKind::Heartbeat,
            payload: json!({
                "receivedMessageId": envelope.message_id,
                "serverState": session.connection_state
            }),
        })
    }
}

fn parse_required_payload_field<T: for<'de> Deserialize<'de>>(
    payload: &Value,
    field_name: &str,
    error_code: &'static str,
    message: &'static str,
) -> Result<T, AppError> {
    let value = payload.get(field_name).cloned().ok_or_else(|| {
        AppError::new(error_code, message, "companion.session", false)
            .with_suggestion(format!("Missing required payload field: {field_name}."))
    })?;

    serde_json::from_value(value).map_err(|error| {
        AppError::new(error_code, message, "companion.session", false).with_cause(error)
    })
}

fn ack(
    trace_id: Option<String>,
    device_id: String,
    source_message_id: String,
    payload: Value,
) -> QuicEnvelope {
    QuicEnvelope {
        protocol: String::from(COMPANION_PROTOCOL),
        version: COMPANION_PROTOCOL_VERSION,
        message_id: format!("ack-{source_message_id}"),
        trace_id,
        device_id: Some(device_id),
        channel: QuicChannel::Control,
        kind: QuicMessageKind::CommandResponse,
        payload,
    }
}

fn require_device_id(envelope: &QuicEnvelope) -> Result<String, AppError> {
    envelope
        .device_id
        .as_ref()
        .filter(|device_id| !device_id.trim().is_empty())
        .cloned()
        .ok_or_else(|| {
            AppError::new(
                "COMPANION_DEVICE_ID_MISSING",
                "Companion envelope deviceId is required for session state messages.",
                "companion.session",
                false,
            )
        })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::capability::{android_companion_capability_catalog, CapabilityPermissionState};

    #[test]
    fn hello_creates_handshaking_session_and_returns_ack() {
        // 场景：Companion 建立连接后先发送 hello，Core 必须创建 session 并返回 helloAck。
        let mut manager = CompanionSessionManager::default();

        let ack = manager
            .handle_envelope(hello_envelope("device-1"))
            .expect("hello should create session");

        assert_eq!(ack.kind, QuicMessageKind::HelloAck);
        assert_eq!(
            ack.payload["selectedProtocolVersion"],
            COMPANION_PROTOCOL_VERSION
        );
        assert_eq!(
            manager
                .get_session("device-1")
                .expect("session exists")
                .connection_state,
            ConnectionState::Handshaking
        );
    }

    #[test]
    fn state_message_before_hello_is_rejected() {
        // 场景：能力状态不能在 hello 之前进入 Core，防止未配对设备污染 registry。
        let mut manager = CompanionSessionManager::default();

        let error = manager
            .handle_envelope(capability_list_envelope("device-1", Vec::new()))
            .expect_err("state before hello should fail");

        assert_eq!(error.error_code, "COMPANION_SESSION_NOT_CONNECTED");
    }

    #[test]
    fn hello_rejects_envelope_payload_device_id_mismatch() {
        // 场景：hello 外层 deviceId 与 payload deviceId 不一致时必须失败，防止连接身份被混淆。
        let mut manager = CompanionSessionManager::default();
        let mut envelope = hello_envelope("device-1");
        envelope.device_id = Some(String::from("different-device"));

        let error = manager
            .handle_envelope(envelope)
            .expect_err("mismatched hello device id should fail");

        assert_eq!(error.error_code, "COMPANION_HELLO_DEVICE_ID_MISMATCH");
        assert!(manager.get_session("device-1").is_err());
    }

    #[test]
    fn capability_list_requires_capabilities_field() {
        // 场景：capabilityList 缺少 capabilities 字段时必须协议失败，不能静默注册空能力。
        let mut manager = CompanionSessionManager::default();
        manager
            .handle_envelope(hello_envelope("device-1"))
            .expect("hello should create session");
        let mut envelope = capability_list_envelope("device-1", Vec::new());
        envelope.payload = json!({});

        let error = manager
            .handle_envelope(envelope)
            .expect_err("missing capabilities should fail");

        assert_eq!(error.error_code, "COMPANION_CAPABILITY_SCHEMA_INVALID");
        assert!(manager
            .get_session("device-1")
            .unwrap()
            .capabilities
            .is_empty());
    }

    #[test]
    fn permission_state_requires_states_field() {
        // 场景：permissionState 缺少 states 字段时必须协议失败，不能静默注册空权限矩阵。
        let mut manager = CompanionSessionManager::default();
        manager
            .handle_envelope(hello_envelope("device-1"))
            .expect("hello should create session");
        let mut envelope = permission_state_envelope("device-1", Vec::new());
        envelope.payload = json!({});

        let error = manager
            .handle_envelope(envelope)
            .expect_err("missing states should fail");

        assert_eq!(error.error_code, "COMPANION_PERMISSION_STATE_INVALID");
        assert!(manager
            .get_session("device-1")
            .unwrap()
            .permission_states
            .is_empty());
    }

    #[test]
    fn heartbeat_reports_actual_session_state() {
        // 场景：hello 后尚未同步能力/权限时 heartbeat 不能谎报 ready，应返回当前 handshaking 状态。
        let mut manager = CompanionSessionManager::default();
        manager
            .handle_envelope(hello_envelope("device-1"))
            .expect("hello should create session");

        let response = manager
            .handle_envelope(heartbeat_envelope("device-1"))
            .expect("heartbeat should be accepted for existing session");

        assert_eq!(response.payload["serverState"], "handshaking");
    }

    #[test]
    fn capability_and_permission_state_make_session_ready() {
        // 场景：hello 后同步能力与权限状态，Core 才能把设备视为可路由 ready session。
        let mut manager = CompanionSessionManager::default();
        manager
            .handle_envelope(hello_envelope("device-1"))
            .expect("hello should create session");

        let capabilities = android_companion_capability_catalog();
        manager
            .handle_envelope(capability_list_envelope("device-1", capabilities.clone()))
            .expect("capability list should sync");
        manager
            .handle_envelope(permission_state_envelope(
                "device-1",
                capabilities
                    .iter()
                    .map(|capability| CapabilityPermissionState {
                        capability_id: capability.id.clone(),
                        granted: true,
                        android_permissions: capability.permission.android_permissions.clone(),
                        missing_permissions: Vec::new(),
                        special_grants: capability.permission.special_permissions.clone(),
                        missing_special_grants: Vec::new(),
                        user_consent_required: capability.permission.requires_user_consent,
                    })
                    .collect(),
            ))
            .expect("permission state should sync");

        let session = manager.get_session("device-1").expect("session exists");
        assert_eq!(session.connection_state, ConnectionState::Ready);
        assert_eq!(session.capabilities.len(), capabilities.len());
        assert_eq!(manager.devices()[0].device_id, "device-1");
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

    fn capability_list_envelope(device_id: &str, capabilities: Vec<Capability>) -> QuicEnvelope {
        QuicEnvelope {
            protocol: String::from(COMPANION_PROTOCOL),
            version: COMPANION_PROTOCOL_VERSION,
            message_id: format!("cap-list-{device_id}"),
            trace_id: Some(String::from("trace-2")),
            device_id: Some(String::from(device_id)),
            channel: QuicChannel::Control,
            kind: QuicMessageKind::CapabilityList,
            payload: json!({"capabilities": capabilities}),
        }
    }

    fn permission_state_envelope(
        device_id: &str,
        states: Vec<CapabilityPermissionState>,
    ) -> QuicEnvelope {
        QuicEnvelope {
            protocol: String::from(COMPANION_PROTOCOL),
            version: COMPANION_PROTOCOL_VERSION,
            message_id: format!("perm-{device_id}"),
            trace_id: Some(String::from("trace-3")),
            device_id: Some(String::from(device_id)),
            channel: QuicChannel::Control,
            kind: QuicMessageKind::PermissionState,
            payload: json!({"states": states}),
        }
    }

    fn heartbeat_envelope(device_id: &str) -> QuicEnvelope {
        QuicEnvelope {
            protocol: String::from(COMPANION_PROTOCOL),
            version: COMPANION_PROTOCOL_VERSION,
            message_id: format!("heartbeat-{device_id}"),
            trace_id: Some(String::from("trace-heartbeat")),
            device_id: Some(String::from(device_id)),
            channel: QuicChannel::Control,
            kind: QuicMessageKind::Heartbeat,
            payload: json!({"sentAtEpochMs": 1_700_000_000_000_u64}),
        }
    }
}
