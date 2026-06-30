use serde::{Deserialize, Serialize};

use crate::{
    capability::{android_companion_capability_catalog, Capability, CapabilityPermissionState},
    error::AppError,
};

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum ConnectionState {
    Disconnected,
    Connecting,
    Handshaking,
    Ready,
    Degraded,
    Failed,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CompanionDevice {
    pub device_id: String,
    pub display_name: String,
    pub app_version: String,
    pub android_sdk: u32,
    pub connection_state: ConnectionState,
    pub capabilities: Vec<Capability>,
    pub permission_states: Vec<CapabilityPermissionState>,
}

#[derive(Debug, Clone, Default)]
pub struct CompanionRegistry {
    devices: Vec<CompanionDevice>,
}

impl CompanionRegistry {
    pub fn new(devices: Vec<CompanionDevice>) -> Self {
        Self { devices }
    }

    pub fn list_devices(&self) -> &[CompanionDevice] {
        &self.devices
    }

    pub fn get_device(&self, device_id: &str) -> Result<&CompanionDevice, AppError> {
        self.devices
            .iter()
            .find(|device| device.device_id == device_id)
            .ok_or_else(|| {
                AppError::new(
                    "COMPANION_DEVICE_NOT_CONNECTED",
                    format!("Android companion device is not connected: {device_id}"),
                    "companion.registry",
                    true,
                )
                .with_suggestion(
                    "Pair the Android companion app with Core before invoking capabilities.",
                )
            })
    }
}

pub fn sample_android_companion_device() -> CompanionDevice {
    let capabilities = android_companion_capability_catalog();
    let permission_states = capabilities
        .iter()
        .map(|capability| CapabilityPermissionState {
            capability_id: capability.id.clone(),
            granted: false,
            android_permissions: capability.permission.android_permissions.clone(),
            missing_permissions: capability.permission.android_permissions.clone(),
            special_grants: capability.permission.special_permissions.clone(),
            missing_special_grants: capability.permission.special_permissions.clone(),
            user_consent_required: capability.permission.requires_user_consent,
        })
        .collect();

    CompanionDevice {
        device_id: String::from("android-companion-sample"),
        display_name: String::from("Android Companion Sample"),
        app_version: String::from("0.1.0"),
        android_sdk: 35,
        connection_state: ConnectionState::Ready,
        capabilities,
        permission_states,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn missing_device_returns_recoverable_connection_error() {
        // 场景：前端请求未连接的 Android 伴侣设备时，Core 必须返回可恢复错误，不能返回空能力误导前端。
        let registry = CompanionRegistry::default();

        let error = registry
            .get_device("missing-device")
            .expect_err("missing device should fail");

        assert_eq!(error.error_code, "COMPANION_DEVICE_NOT_CONNECTED");
        assert!(error.recoverable);
    }

    #[test]
    fn sample_device_exposes_permission_state_for_every_capability() {
        // 场景：设备注册后，每个能力必须有权限状态，前端才能展示需要授权的具体原因。
        let device = sample_android_companion_device();

        assert_eq!(device.capabilities.len(), device.permission_states.len());
        assert!(device
            .permission_states
            .iter()
            .any(|state| state.capability_id == "android.screen.capture"));
    }
}
