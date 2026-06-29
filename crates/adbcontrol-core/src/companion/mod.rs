pub mod ingress;
pub mod listener;
pub mod media_store;
pub mod protocol;
#[cfg(feature = "quinn-transport")]
pub mod quic_identity;
#[cfg(feature = "quinn-transport")]
pub mod quinn_transport;
pub mod registry;
pub mod router;
pub mod session;
pub mod trust;
pub mod trusted_ingress;

pub use ingress::CompanionIngress;
pub use listener::{CompanionQuicListener, IngressBackedQuicListener};
pub use media_store::{CompanionMediaStore, StoredMediaChunk};
pub use protocol::{
    quic_protocol_descriptor, validate_quic_envelope, CompanionCommandRequest, CompanionHello,
    QuicEnvelope, QuicMessageKind, COMPANION_PROTOCOL, COMPANION_PROTOCOL_VERSION,
};
#[cfg(feature = "quinn-transport")]
pub use quic_identity::CoreQuicIdentity;
#[cfg(feature = "quinn-transport")]
pub use quinn_transport::QuinnCompanionServer;
pub use registry::{sample_android_companion_device, CompanionDevice, CompanionRegistry, ConnectionState};
pub use router::{
    build_command_request_envelope, CompanionCommandDispatch, CompanionCommandResponse,
    CompanionCommandRouter, CompanionCommandStatus, DisconnectedCompanionCommandRouter,
    InMemoryCompanionCommandRouter, InMemoryCompanionSession,
};
pub use session::{CompanionSession, CompanionSessionManager};
pub use trust::{
    CompanionTrustStore, PairingChallenge, PairingRequest, TrustedDevice,
};
pub use trusted_ingress::TrustedCompanionIngress;
