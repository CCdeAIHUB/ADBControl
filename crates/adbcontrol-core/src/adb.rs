use std::{path::Path, process::Command};

use serde::{Deserialize, Serialize};

use crate::{assets::AdbAsset, error::AppError};

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct AdbCommandOutput {
    #[serde(rename = "exitCode")]
    pub exit_code: i32,
    pub stdout: String,
    pub stderr: String,
}

pub trait AdbRunner: Send + Sync {
    fn run(&self, adb_binary: &Path, args: &[String]) -> Result<AdbCommandOutput, AppError>;
}

#[derive(Debug, Default, Clone, Copy)]
pub struct ProcessAdbRunner;

impl AdbRunner for ProcessAdbRunner {
    fn run(&self, adb_binary: &Path, args: &[String]) -> Result<AdbCommandOutput, AppError> {
        validate_adb_args(args)?;

        // 安全边界：核心只能启动 manifest 解析出的 adb 二进制，并且只通过 args 传参。
        let output = Command::new(adb_binary)
            .args(args)
            .output()
            .map_err(|error| {
                AppError::new(
                    "ADB_PROCESS_SPAWN_FAILED",
                    format!("Failed to start adb binary at {}", adb_binary.display()),
                    "adb.runner",
                    true,
                )
                .with_cause(error)
                .with_suggestion(
                    "Verify that the ADB asset exists, is executable, and matches the host target.",
                )
            })?;

        Ok(AdbCommandOutput {
            exit_code: output.status.code().unwrap_or(-1),
            stdout: String::from_utf8_lossy(&output.stdout).to_string(),
            stderr: String::from_utf8_lossy(&output.stderr).to_string(),
        })
    }
}

pub fn validate_adb_args(args: &[String]) -> Result<(), AppError> {
    for arg in args {
        if arg.as_bytes().contains(&0) {
            return Err(AppError::new(
                "ADB_ARGS_CONTAIN_NUL",
                "ADB arguments must not contain NUL bytes.",
                "adb.runner",
                false,
            ));
        }
    }

    Ok(())
}

pub fn adb_asset_display_name(asset: &AdbAsset) -> String {
    format!("{} ({:?})", asset.path, asset.source)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rejects_nul_bytes_in_adb_args() {
        // 场景：前端输入不能通过 NUL 字节污染底层进程参数。
        let args = vec![
            "shell".to_string(),
            String::from("echo") + &char::from(0).to_string(),
        ];

        let error = validate_adb_args(&args).expect_err("NUL byte must be rejected");

        assert_eq!(error.error_code, "ADB_ARGS_CONTAIN_NUL");
        assert_eq!(error.module, "adb.runner");
    }

    #[test]
    fn accepts_normal_adb_args() {
        let args = vec!["devices".to_string(), "-l".to_string()];

        validate_adb_args(&args).expect("normal adb args should be accepted");
    }
}
