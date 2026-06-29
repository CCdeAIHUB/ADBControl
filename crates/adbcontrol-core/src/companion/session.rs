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
            .with_suggestion("Complete companion hello/helloAck before synchronizing capability state.")
        })
    }

    pub fn devices(&self) -> Vec<CompanionDevice> {
        self.sessions.values().map(CompanionSession::to_device).collect()
    }

    fn handle_hello(&mut self, envelope: QuicEnvelope) -> Result<QuicEnvelope, AppError> {
        let hello: CompanionHello = serde_json::from_value(envelope.payload).map_err(|error| {
            AppError::new(
                "COMPANION_HELLO_INVALID",
                "Companion hello payload is invalid.",
                "companion.session",
                false,
            )
            .with_cause(error)
        })?;

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
        let capabilities: Vec<Capability> = serde_json::from_value(
            envelope
                .payload
                .get("capabilities")
                .cloned()
                .unwrap_or_else(|| json!([])),
        )
        .map_err(|error| {
            AppError::new(
                "COMPANION_CAPABILITY_SCHEMA_INVALID",
                "Companion capabilityList payload is invalid.",
                "companion.session",
                false,
            )
            .with_cause(error)
        })?;

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

        Ok(ack(envelope.trace_id, device_id, envelope.message_id, json!({
            "accepted": true,
            "registeredCapabilityCount": session.capabilities.len()
        })))
    }

    fn handle_permission_state(&mut self, envelope: QuicEnvelope) -> Result<QuicEnvelope, AppError> {
        let device_id = require_device_id(&envelope)?;
        let states: Vec<CapabilityPermissionState> = serde_json::from_value(
            envelope
                .payload
                .get("states")
                .cloned()
                .unwrap_or_else(|| json!([])),
        )
        .map_err(|error| {
            AppError::new(
                "COMPANION_PERMISSION_STATE_INVALID",
                "Companion permissionState payload is invalid.",
                "companion.session",
                false,
            )
            .with_cause(error)
        })?;

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

        Ok(ack(envelope.trace_id, device_id, envelope.message_id, json!({
            "accepted": true,
            "updatedStateCount": session.permission_states.len()
        })))
    }

    fn handle_heartbeat(&self, envelope: QuicEnvelope) -> Result<QuicEnvelope, AppError> {
        let device_id = require_device_id(&envelope)?;
        self.get_session(&device_id)?;

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
                "serverState": "ready"
            }),
        })
    }
}

fn ack(trace_id: Option<String>, device_id: String, source_message_id: String, payload: Value) -> QuicEnvelope {
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
