use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Capability {
    pub id: String,
    pub title: String,
    pub description: String,
    pub provider: CapabilityProvider,
    pub transport: CapabilityTransport,
    pub sensitivity: CapabilitySensitivity,
    pub permission: CapabilityPermissionRequirement,
    pub operations: Vec<String>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum CapabilityProvider {
    Adb,
    AndroidCompanion,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum CapabilityTransport {
    Ipc,
    QuicControl,
    QuicDataStream,
    QuicMediaStream,
    QuicDatagram,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum CapabilitySensitivity {
    Low,
    Medium,
    High,
    Critical,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CapabilityPermissionRequirement {
    pub android_permissions: Vec<String>,
    pub special_permissions: Vec<String>,
    pub requires_user_consent: bool,
    pub audit_required: bool,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CapabilityPermissionState {
    pub capability_id: String,
    pub granted: bool,
    pub android_permissions: Vec<String>,
    pub missing_permissions: Vec<String>,
    pub special_grants: Vec<String>,
    pub missing_special_grants: Vec<String>,
    pub user_consent_required: bool,
}

/// Canonical capability catalog exposed by the Android Companion provider.
///
/// This list is intentionally protocol-level metadata only. Real Android API calls
/// must stay behind the Android app PermissionGuard so Core never becomes coupled
/// to Android framework details or bypasses user consent.
pub fn android_companion_capability_catalog() -> Vec<Capability> {
    vec![
        capability(
            "android.input.ime",
            "Input method and text injection",
            "Provide controlled text input through the Android companion input channel.",
            CapabilityTransport::QuicControl,
            CapabilitySensitivity::High,
            &["input.text", "input.key"],
            &[],
            &["input-method-service"],
            true,
        ),
        capability(
            "android.screen.capture",
            "Screen capture and casting",
            "Capture device screen or selected app window and stream frames to Core.",
            CapabilityTransport::QuicMediaStream,
            CapabilitySensitivity::Critical,
            &["stream.open", "stream.close", "screenshot.capture"],
            &[
                "android.permission.FOREGROUND_SERVICE",
                "android.permission.FOREGROUND_SERVICE_MEDIA_PROJECTION",
            ],
            &["media-projection-consent"],
            true,
        ),
        capability(
            "android.file.read",
            "File read",
            "Read files that the Android companion is allowed to access.",
            CapabilityTransport::QuicDataStream,
            CapabilitySensitivity::High,
            &["file.read"],
            &[
                "android.permission.READ_MEDIA_IMAGES",
                "android.permission.READ_MEDIA_VIDEO",
            ],
            &["scoped-storage-or-document-picker"],
            true,
        ),
        capability(
            "android.file.write",
            "File write",
            "Write files through scoped storage or user-granted document targets.",
            CapabilityTransport::QuicDataStream,
            CapabilitySensitivity::High,
            &["file.write"],
            &[],
            &["scoped-storage-or-document-picker"],
            true,
        ),
        capability(
            "android.camera.stream",
            "Camera stream",
            "Open camera preview stream after permission and foreground visibility checks.",
            CapabilityTransport::QuicMediaStream,
            CapabilitySensitivity::Critical,
            &["camera.open", "camera.close"],
            &["android.permission.CAMERA"],
            &[],
            true,
        ),
        capability(
            "android.audio.record",
            "Audio recording",
            "Record microphone audio and stream it to Core after explicit authorization.",
            CapabilityTransport::QuicMediaStream,
            CapabilitySensitivity::Critical,
            &["audio.record.start", "audio.record.stop"],
            &["android.permission.RECORD_AUDIO"],
            &[],
            true,
        ),
        capability(
            "android.sms.read",
            "SMS read",
            "Read SMS only when platform policy and user authorization allow it.",
            CapabilityTransport::QuicControl,
            CapabilitySensitivity::Critical,
            &["sms.read"],
            &["android.permission.READ_SMS"],
            &[],
            true,
        ),
        capability(
            "android.sms.send",
            "SMS send",
            "Send SMS only after explicit user and policy authorization.",
            CapabilityTransport::QuicControl,
            CapabilitySensitivity::Critical,
            &["sms.send"],
            &["android.permission.SEND_SMS"],
            &[],
            true,
        ),
        capability(
            "android.phone.call",
            "Phone call",
            "Start a phone call through Android after explicit authorization.",
            CapabilityTransport::QuicControl,
            CapabilitySensitivity::Critical,
            &["phone.call"],
            &[
                "android.permission.CALL_PHONE",
                "android.permission.READ_PHONE_STATE",
            ],
            &[],
            true,
        ),
        capability(
            "android.clipboard.read",
            "Clipboard read",
            "Read current clipboard content with foreground and privacy constraints.",
            CapabilityTransport::QuicControl,
            CapabilitySensitivity::High,
            &["clipboard.read"],
            &[],
            &["foreground-required"],
            true,
        ),
        capability(
            "android.clipboard.write",
            "Clipboard write",
            "Write clipboard content and mark sensitive clips when requested.",
            CapabilityTransport::QuicControl,
            CapabilitySensitivity::High,
            &["clipboard.write"],
            &[],
            &["sensitive-clip-flag"],
            true,
        ),
        capability(
            "android.sensor.motion",
            "Motion and orientation sensors",
            "Subscribe to accelerometer, gyroscope and rotation vector updates.",
            CapabilityTransport::QuicDatagram,
            CapabilitySensitivity::Medium,
            &["sensor.subscribe", "sensor.unsubscribe"],
            &[],
            &[],
            false,
        ),
        capability(
            "android.app.list",
            "Application list",
            "List installed applications visible to the companion app package queries.",
            CapabilityTransport::QuicControl,
            CapabilitySensitivity::Medium,
            &["app.list"],
            &[],
            &["package-visibility-query"],
            true,
        ),
        capability(
            "android.volume.media",
            "Media volume control",
            "Read or change Android media stream volume.",
            CapabilityTransport::QuicControl,
            CapabilitySensitivity::Medium,
            &["volume.get", "volume.set"],
            &[],
            &[],
            false,
        ),
        capability(
            "android.ui.background_surface",
            "Background surface prompt",
            "Bring a controlled companion UI surface to foreground when policy allows it.",
            CapabilityTransport::QuicControl,
            CapabilitySensitivity::High,
            &["ui.surface.show"],
            &[],
            &["background-launch-policy"],
            true,
        ),
        capability(
            "android.ui.overlay",
            "Overlay window",
            "Show a companion overlay after special overlay permission is granted.",
            CapabilityTransport::QuicControl,
            CapabilitySensitivity::High,
            &["overlay.show", "overlay.hide"],
            &["android.permission.SYSTEM_ALERT_WINDOW"],
            &["draw-over-apps"],
            true,
        ),
        capability(
            "android.intent.chain_launch",
            "Chained intent launch",
            "Launch a declared chain of Android intents with strict validation.",
            CapabilityTransport::QuicControl,
            CapabilitySensitivity::High,
            &["intent.chainLaunch"],
            &[],
            &["explicit-intent-only"],
            true,
        ),
    ]
}

// The catalog helper mirrors one protocol table row at a time. Keeping all row
// fields adjacent makes permission and sensitivity review less error-prone than
// scattering the same data across several partial builders.
#[allow(clippy::too_many_arguments)]
fn capability(
    id: &str,
    title: &str,
    description: &str,
    transport: CapabilityTransport,
    sensitivity: CapabilitySensitivity,
    operations: &[&str],
    android_permissions: &[&str],
    special_permissions: &[&str],
    requires_user_consent: bool,
) -> Capability {
    Capability {
        id: String::from(id),
        title: String::from(title),
        description: String::from(description),
        provider: CapabilityProvider::AndroidCompanion,
        transport,
        sensitivity,
        permission: CapabilityPermissionRequirement {
            android_permissions: to_strings(android_permissions),
            special_permissions: to_strings(special_permissions),
            requires_user_consent,
            audit_required: matches!(
                sensitivity,
                CapabilitySensitivity::High | CapabilitySensitivity::Critical
            ),
        },
        operations: to_strings(operations),
    }
}

fn to_strings(values: &[&str]) -> Vec<String> {
    values.iter().map(|value| String::from(*value)).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn android_companion_catalog_contains_sensitive_capabilities() {
        // 场景：高敏感 Android 能力必须在能力目录中显式标注权限与敏感等级，不能隐藏成普通命令。
        let catalog = android_companion_capability_catalog();

        let camera = catalog
            .iter()
            .find(|capability| capability.id == "android.camera.stream")
            .expect("camera capability should exist");
        assert_eq!(camera.sensitivity, CapabilitySensitivity::Critical);
        assert!(camera
            .permission
            .android_permissions
            .contains(&String::from("android.permission.CAMERA")));

        let clipboard = catalog
            .iter()
            .find(|capability| capability.id == "android.clipboard.read")
            .expect("clipboard read capability should exist");
        assert_eq!(clipboard.sensitivity, CapabilitySensitivity::High);
        assert!(clipboard.permission.requires_user_consent);
    }
}
