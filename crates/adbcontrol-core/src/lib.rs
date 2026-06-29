pub mod adb;
pub mod assets;
pub mod error;
pub mod ipc;
pub mod platform;
pub mod protocol;

pub use adb::{AdbCommandOutput, AdbRunner, ProcessAdbRunner};
pub use assets::{find_adb_asset, load_embedded_manifest, AdbAsset, AdbManifest};
pub use error::AppError;
pub use platform::HostTarget;
pub use protocol::{CoreService, IpcRequest, IpcResponse};
