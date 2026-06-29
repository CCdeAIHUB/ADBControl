use std::path::PathBuf;

use serde::{Deserialize, Serialize};
use serde_json::{json, Value};

use crate::{
    adb::{validate_adb_args, AdbRunner},
    assets::{find_adb_asset, resolve_asset_path, AdbManifest},
    error::AppError,
    platform::HostTarget,
};

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct IpcRequest {
    pub id: String,
    pub method: String,
    #[serde(default)]
    pub params: Value,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct IpcResponse {
    pub id: Option<String>,
    pub ok: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub result: Option<Value>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error: Option<AppError>,
}

impl IpcResponse {
    pub fn success(id: impl Into<String>, result: Value) -> Self {
        Self {
            id: Some(id.into()),
            ok: true,
            result: Some(result),
            error: None,
        }
    }

    pub fn failure(id: Option<String>, error: AppError) -> Self {
        Self {
            id,
            ok: false,
            result: None,
            error: Some(error),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
struct AdbExecParams {
    #[serde(default)]
    args: Vec<String>,
}

pub struct CoreService<R: AdbRunner> {
    runner: R,
    manifest: AdbManifest,
}

impl<R: AdbRunner> CoreService<R> {
    pub fn new(runner: R, manifest: AdbManifest) -> Self {
        Self { runner, manifest }
    }

    pub fn handle_json_line(&self, line: &str) -> String {
        encode_response(&self.handle_json_line_result(line))
    }

    pub fn handle_json_line_result(&self, line: &str) -> IpcResponse {
        let request: IpcRequest = match serde_json::from_str(line) {
            Ok(request) => request,
            Err(error) => {
                return IpcResponse::failure(
                    None,
                    AppError::new(
                        "IPC_INVALID_JSON",
                        "IPC request must be a valid JSON object.",
                        "ipc.protocol",
                        false,
                    )
                    .with_cause(error),
                )
            }
        };

        self.handle_request(request)
    }

    pub fn handle_request(&self, request: IpcRequest) -> IpcResponse {
        match request.method.as_str() {
            "core.getHostTarget" => self.handle_get_host_target(request.id),
            "adb.asset.current" => self.handle_get_current_adb_asset(request.id),
            "adb.exec" => self.handle_adb_exec(request),
            unknown => IpcResponse::failure(
                Some(request.id),
                AppError::new(
                    "IPC_METHOD_UNKNOWN",
                    format!("Unknown IPC method: {unknown}"),
                    "ipc.protocol",
                    false,
                ),
            ),
        }
    }

    fn handle_get_host_target(&self, id: String) -> IpcResponse {
        match HostTarget::current() {
            Ok(target) => response_from_serializable(id, &target, "platform.target"),
            Err(error) => IpcResponse::failure(Some(id), error),
        }
    }

    fn handle_get_current_adb_asset(&self, id: String) -> IpcResponse {
        let target = match HostTarget::current() {
            Ok(target) => target,
            Err(error) => return IpcResponse::failure(Some(id), error),
        };

        match find_adb_asset(&self.manifest, &target) {
            Ok(asset) => response_from_serializable(id, &asset, "adb.assets"),
            Err(error) => IpcResponse::failure(Some(id), error),
        }
    }

    fn handle_adb_exec(&self, request: IpcRequest) -> IpcResponse {
        let params: AdbExecParams = match serde_json::from_value(request.params) {
            Ok(params) => params,
            Err(error) => {
                return IpcResponse::failure(
                    Some(request.id),
                    AppError::new(
                        "IPC_PARAMS_INVALID",
                        "adb.exec params must match { args: string[] }.",
                        "ipc.protocol",
                        false,
                    )
                    .with_cause(error),
                )
            }
        };

        if let Err(error) = validate_adb_args(&params.args) {
            return IpcResponse::failure(Some(request.id), error);
        }

        let adb_path = match self.resolve_current_adb_path() {
            Ok(path) => path,
            Err(error) => return IpcResponse::failure(Some(request.id), error),
        };

        match self.runner.run(&adb_path, &params.args) {
            Ok(output) => response_from_serializable(request.id, &output, "adb.runner"),
            Err(error) => IpcResponse::failure(Some(request.id), error),
        }
    }

    fn resolve_current_adb_path(&self) -> Result<PathBuf, AppError> {
        let target = HostTarget::current()?;
        let asset = find_adb_asset(&self.manifest, &target)?;
        resolve_asset_path(&asset)
    }
}

fn response_from_serializable<T: Serialize>(
    id: String,
    value: &T,
    module: &'static str,
) -> IpcResponse {
    match serde_json::to_value(value) {
        Ok(value) => IpcResponse::success(id, value),
        Err(error) => IpcResponse::failure(
            Some(id),
            AppError::new(
                "IPC_RESPONSE_SERIALIZATION_FAILED",
                "Core failed to serialize IPC response.",
                module,
                false,
            )
            .with_cause(error),
        ),
    }
}

fn encode_response(response: &IpcResponse) -> String {
    match serde_json::to_string(response) {
        Ok(encoded) => encoded,
        Err(error) => json!({
            "id": null,
            "ok": false,
            "error": {
                "errorCode": "IPC_RESPONSE_ENCODING_FAILED",
                "message": "Core failed to encode IPC response.",
                "module": "ipc.protocol",
                "recoverable": false,
                "cause": error.to_string()
            }
        })
        .to_string(),
    }
}

#[cfg(test)]
mod tests {
    use std::{
        path::Path,
        sync::{Arc, Mutex},
    };

    use super::*;
    use crate::{adb::AdbCommandOutput, assets::load_embedded_manifest};

    #[derive(Clone)]
    struct RecordingRunner {
        calls: Arc<Mutex<Vec<Vec<String>>>>,
    }

    impl AdbRunner for RecordingRunner {
        fn run(
            &self,
            _adb_binary: &Path,
            args: &[String],
        ) -> Result<AdbCommandOutput, AppError> {
            self.calls
                .lock()
                .expect("lock should not be poisoned")
                .push(args.to_vec());

            Ok(AdbCommandOutput {
                exit_code: 0,
                stdout: "mock stdout".to_string(),
                stderr: String::new(),
            })
        }
    }

    fn service_with_recording_runner(
        calls: Arc<Mutex<Vec<Vec<String>>>>,
    ) -> CoreService<RecordingRunner> {
        CoreService::new(
            RecordingRunner { calls },
            load_embedded_manifest().expect("embedded manifest must be valid"),
        )
    }

    #[test]
    fn invalid_json_returns_structured_error() {
        // 场景：前端发送非法 JSON 时，核心必须返回统一错误结构，不能 panic 或静默忽略。
        let service = service_with_recording_runner(Arc::new(Mutex::new(Vec::new())));

        let encoded = service.handle_json_line("{");
        let response: IpcResponse = serde_json::from_str(encoded.as_str())
            .expect("response should be valid JSON");

        assert!(!response.ok);
        assert_eq!(response.id, None);
        assert_eq!(
            response.error.expect("error is required").error_code,
            "IPC_INVALID_JSON"
        );
    }

    #[test]
    fn unknown_method_returns_structured_error() {
        // 场景：前端调用未知 method 时，核心必须显式失败，不能假装成功。
        let service = service_with_recording_runner(Arc::new(Mutex::new(Vec::new())));

        let encoded = service.handle_json_line(
            r#"{"id":"1","method":"unknown.method","params":{}}"#,
        );
        let response: IpcResponse = serde_json::from_str(encoded.as_str())
            .expect("response should be valid JSON");

        assert!(!response.ok);
        assert_eq!(response.id, Some("1".to_string()));
        assert_eq!(
            response.error.expect("error is required").error_code,
            "IPC_METHOD_UNKNOWN"
        );
    }

    #[test]
    fn adb_exec_preserves_args_and_returns_command_output() {
        // 场景：前端请求 adb.exec 时，核心只把参数数组交给 ADB runner，不拼接命令文本。
        let calls = Arc::new(Mutex::new(Vec::new()));
        let service = service_with_recording_runner(calls.clone());

        let encoded = service.handle_json_line(
            r#"{"id":"2","method":"adb.exec","params":{"args":["devices","-l"]}}"#,
        );
        let response: IpcResponse = serde_json::from_str(encoded.as_str())
            .expect("response should be valid JSON");

        assert!(response.ok);
        assert_eq!(
            calls.lock().expect("lock should not be poisoned")[0],
            vec!["devices".to_string(), "-l".to_string()]
        );
        assert_eq!(
            response.result.expect("result is required"),
            json!({"exitCode": 0, "stdout": "mock stdout", "stderr": ""})
        );
    }

    #[test]
    fn adb_exec_rejects_invalid_params() {
        // 场景：adb.exec 的 args 必须是 string[]，契约错误必须被协议层拦截。
        let service = service_with_recording_runner(Arc::new(Mutex::new(Vec::new())));

        let encoded = service.handle_json_line(
            r#"{"id":"3","method":"adb.exec","params":{"args":"devices"}}"#,
        );
        let response: IpcResponse = serde_json::from_str(encoded.as_str())
            .expect("response should be valid JSON");

        assert!(!response.ok);
        assert_eq!(
            response.error.expect("error is required").error_code,
            "IPC_PARAMS_INVALID"
        );
    }
}
