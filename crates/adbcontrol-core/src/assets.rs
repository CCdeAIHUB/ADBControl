use std::path::PathBuf;

use serde::{Deserialize, Serialize};

use crate::{error::AppError, platform::HostTarget};

const EMBEDDED_ADB_MANIFEST: &str = include_str!("../../../assets/adb/manifest.json");

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct AdbManifest {
    #[serde(rename = "schemaVersion")]
    pub schema_version: u32,
    #[serde(rename = "adbVersion")]
    pub adb_version: String,
    pub assets: Vec<AdbAsset>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct AdbAsset {
    #[serde(rename = "targetId")]
    pub target_id: String,
    pub path: String,
    pub source: AdbAssetSource,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub sha256: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum AdbAssetSource {
    OfficialPlatformTools,
    CustomGithubBuild,
}

pub fn load_embedded_manifest() -> Result<AdbManifest, AppError> {
    parse_manifest_str(EMBEDDED_ADB_MANIFEST)
}

pub fn parse_manifest_str(content: &str) -> Result<AdbManifest, AppError> {
    let manifest: AdbManifest = serde_json::from_str(content).map_err(|error| {
        AppError::new(
            "ADB_MANIFEST_INVALID_JSON",
            "ADB asset manifest is not valid JSON.",
            "adb.assets",
            false,
        )
        .with_cause(error)
    })?;

    if manifest.schema_version != 1 {
        return Err(AppError::new(
            "ADB_MANIFEST_SCHEMA_UNSUPPORTED",
            format!(
                "Unsupported ADB manifest schema version: {}",
                manifest.schema_version
            ),
            "adb.assets",
            false,
        ));
    }

    Ok(manifest)
}

pub fn find_adb_asset(
    manifest: &AdbManifest,
    target: &HostTarget,
) -> Result<AdbAsset, AppError> {
    manifest
        .assets
        .iter()
        .find(|asset| asset.target_id == target.target_id)
        .cloned()
        .ok_or_else(|| {
            AppError::new(
                "ADB_ASSET_NOT_FOUND",
                format!("No ADB asset is declared for target {}", target.target_id),
                "adb.assets",
                false,
            )
            .with_suggestion(
                "Build or download the target ADB binary and add it to assets/adb/manifest.json.",
            )
        })
}

pub fn resolve_asset_path(asset: &AdbAsset) -> Result<PathBuf, AppError> {
    let executable_dir = std::env::current_exe()
        .map_err(|error| {
            AppError::new(
                "CORE_EXECUTABLE_PATH_UNAVAILABLE",
                "Cannot resolve current executable path.",
                "adb.assets",
                false,
            )
            .with_cause(error)
        })?
        .parent()
        .map(PathBuf::from)
        .ok_or_else(|| {
            AppError::new(
                "CORE_EXECUTABLE_DIR_UNAVAILABLE",
                "Cannot resolve current executable directory.",
                "adb.assets",
                false,
            )
        })?;

    Ok(executable_dir.join(&asset.path))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_manifest_and_finds_linux_arm64_custom_asset() {
        // 场景：Linux arm64 没有官方 Platform-Tools 产物时，manifest 必须声明为自建 ADB。
        let manifest = parse_manifest_str(
            r#"{
                "schemaVersion": 1,
                "adbVersion": "37.0.0",
                "assets": [
                    {
                        "targetId": "linux-arm64",
                        "path": "assets/adb/linux-arm64/adb",
                        "source": "custom-github-build",
                        "sha256": null
                    }
                ]
            }"#,
        )
        .expect("manifest should parse");
        let target = HostTarget::from_parts("linux", "aarch64").expect("target should parse");

        let asset = find_adb_asset(&manifest, &target).expect("asset should exist");

        assert_eq!(asset.target_id, "linux-arm64");
        assert_eq!(asset.source, AdbAssetSource::CustomGithubBuild);
    }

    #[test]
    fn returns_explicit_error_when_asset_is_missing() {
        // 场景：当前平台未声明 ADB 资产时，核心不能静默降级为其他平台二进制。
        let manifest = parse_manifest_str(
            r#"{
                "schemaVersion": 1,
                "adbVersion": "37.0.0",
                "assets": []
            }"#,
        )
        .expect("manifest should parse");
        let target = HostTarget::from_parts("windows", "x86_64").expect("target should parse");

        let error = find_adb_asset(&manifest, &target).expect_err("asset should be missing");

        assert_eq!(error.error_code, "ADB_ASSET_NOT_FOUND");
        assert_eq!(error.module, "adb.assets");
    }
}
