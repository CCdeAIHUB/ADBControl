use serde::{Deserialize, Serialize};

use crate::error::AppError;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct HostTarget {
    pub os: String,
    pub arch: String,
    #[serde(rename = "targetId")]
    pub target_id: String,
}

impl HostTarget {
    pub fn current() -> Result<Self, AppError> {
        Self::from_parts(std::env::consts::OS, std::env::consts::ARCH)
    }

    pub fn from_parts(os: &str, arch: &str) -> Result<Self, AppError> {
        let os = normalize_os(os)?;
        let arch = normalize_arch(arch)?;
        let target_id = format!("{os}-{arch}");

        match target_id.as_str() {
            "windows-x86_64"
            | "windows-arm64"
            | "macos-x86_64"
            | "macos-arm64"
            | "linux-x86_64"
            | "linux-arm64"
            | "android-x86_64"
            | "android-arm64" => Ok(Self {
                os,
                arch,
                target_id,
            }),
            unsupported => Err(AppError::new(
                "PLATFORM_TARGET_UNSUPPORTED",
                format!("Unsupported host target: {unsupported}"),
                "platform.target",
                false,
            )
            .with_suggestion(
                "Add the target to HostTarget and assets/adb/manifest.json before shipping.",
            )),
        }
    }
}

fn normalize_os(os: &str) -> Result<String, AppError> {
    match os.to_ascii_lowercase().as_str() {
        "windows" | "win32" => Ok("windows".to_string()),
        "macos" | "darwin" | "osx" => Ok("macos".to_string()),
        "linux" => Ok("linux".to_string()),
        "android" => Ok("android".to_string()),
        other => Err(AppError::new(
            "PLATFORM_OS_UNSUPPORTED",
            format!("Unsupported operating system: {other}"),
            "platform.target",
            false,
        )),
    }
}

fn normalize_arch(arch: &str) -> Result<String, AppError> {
    match arch.to_ascii_lowercase().as_str() {
        "x86_64" | "amd64" => Ok("x86_64".to_string()),
        "aarch64" | "arm64" => Ok("arm64".to_string()),
        other => Err(AppError::new(
            "PLATFORM_ARCH_UNSUPPORTED",
            format!("Unsupported CPU architecture: {other}"),
            "platform.target",
            false,
        )),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn maps_linux_aarch64_to_linux_arm64() {
        let target =
            HostTarget::from_parts("linux", "aarch64").expect("linux arm64 is supported");

        assert_eq!(target.os, "linux");
        assert_eq!(target.arch, "arm64");
        assert_eq!(target.target_id, "linux-arm64");
    }

    #[test]
    fn maps_macos_darwin_amd64_to_macos_x86_64() {
        let target =
            HostTarget::from_parts("darwin", "amd64").expect("macos x86_64 is supported");

        assert_eq!(target.os, "macos");
        assert_eq!(target.arch, "x86_64");
        assert_eq!(target.target_id, "macos-x86_64");
    }

    #[test]
    fn rejects_unknown_os() {
        let error = HostTarget::from_parts("freebsd", "x86_64")
            .expect_err("freebsd is not supported yet");

        assert_eq!(error.error_code, "PLATFORM_OS_UNSUPPORTED");
        assert_eq!(error.module, "platform.target");
    }
}
