pub mod ingress;
pub mod protocol;
pub mod registry;
pub mod router;
pub mod session;

pub use ingress::CompanionIngress;
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
pub use session::{CompanionSession, CompanionSessionManager};
