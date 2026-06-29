pub mod protocol;
pub mod registry;
pub mod router;

pub use protocol::{
    quic_protocol_descriptor, validate_quic_envelope, CompanionCommandRequest, CompanionHello,
    QuicEnvelope, QuicMessageKind, COMPANION_PROTOCOL, COMPANION_PROTOCOL_VERSION,
};
pub use registry::{sample_android_companion_device, CompanionDevice, CompanionRegistry, ConnectionState};
pub use router::{
    build_command_request_envelope, CompanionCommandDispatch, CompanionCommandResponse,
    CompanionCommandRouter, CompanionCommandStatus, DisconnectedCompanionCommandRouter,
    InMemoryCompanionCommandRouter, InMemoryCompanionSession,
};
