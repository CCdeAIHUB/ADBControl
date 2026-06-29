pub mod adb;
pub mod assets;
pub mod capability;
pub mod companion;
pub mod error;
pub mod ipc;
pub mod platform;
pub mod protocol;

pub use adb::{AdbCommandOutput, AdbRunner, ProcessAdbRunner};
pub use assets::{find_adb_asset, load_embedded_manifest, AdbAsset, AdbManifest};
pub use capability::{
    android_companion_capability_catalog, Capability, CapabilityPermissionRequirement,
    CapabilityPermissionState, CapabilityProvider, CapabilitySensitivity, CapabilityTransport,
};
pub use companion::{
    quic_protocol_descriptor, sample_android_companion_device, validate_quic_envelope,
    CompanionDevice, CompanionRegistry, ConnectionState, QuicEnvelope, QuicMessageKind,
    COMPANION_PROTOCOL, COMPANION_PROTOCOL_VERSION,
};
pub use error::AppError;
pub use platform::HostTarget;
pub use protocol::{CoreService, IpcRequest, IpcResponse};
