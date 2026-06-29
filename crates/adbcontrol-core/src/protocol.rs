use std::path::PathBuf;

use serde::{de::DeserializeOwned, Deserialize, Serialize};
use serde_json::{json, Value};

use crate::{
    adb::{validate_adb_args, AdbRunner},
    assets::{find_adb_asset, resolve_asset_path, AdbManifest},
    capability::android_companion_capability_catalog,
    companion::{quic_protocol_descriptor, CompanionRegistry},
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

#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
struct DeviceScopedParams {
    #[serde(default)]
    device_id: String,
}

#[derive(Debug, Clone, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
struct DeviceInvokeParams {
    #[serde(default)]
    device_id: String,
    #[serde(default)]
    capability_id: String,
    #[serde(default)]
    operation: String,
    #[serde(default = "default_params_object")]
    args: Value,
}

pub struct CoreService<R: AdbRunner> {
    runner: R,
    manifest: AdbManifest,
    companion_registry: CompanionRegistry,
}

impl<R: AdbRunner> CoreService<R> {
    pub fn new(runner: R, manifest: AdbManifest) -> Self {
        Self {
            runner,
            manifest,
            companion_registry: CompanionRegistry::default(),
        }
    }

    pub fn new_with_companion_registry(
        runner: R,
        manifest: AdbManifest,
        companion_registry: CompanionRegistry,
    ) -> Self {
        Self {
            runner,
            manifest,
            companion_registry,
        }
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
            "companion.protocol.info" => self.handle_companion_protocol_info(request.id),
            "capability.list" => self.handle_capability_list(request.id),
            "device.list" => self.handle_device_list(request.id),
            "device.getCapabilities" => self.handle_device_get_capabilities(request),
            "device.getPermissionState" => self.handle_device_get_permission_state(request),
            "device.invoke" => self.handle_device_invoke(request),
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
        let params: AdbExecParams = match parse_ipc_params(
            request.params,
            "adb.exec params must match { args: string[] }.",
        ) {
            Ok(params) => params,
            Err(error) => return IpcResponse::failure(Some(request.id), error),
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

    fn handle_companion_protocol_info(&self, id: String) -> IpcResponse {
        IpcResponse::success(id, quic_protocol_descriptor())
    }

    fn handle_capability_list(&self, id: String) -> IpcResponse {
        response_from_serializable(
            id,
            &android_companion_capability_catalog(),
            "capability.catalog",
        )
    }

    fn handle_device_list(&self, id: String) -> IpcResponse {
        response_from_serializable(
            id,
            self.companion_registry.list_devices(),
            "companion.registry",
        )
    }

    fn handle_device_get_capabilities(&self, request: IpcRequest) -> IpcResponse {
        let params: DeviceScopedParams = match parse_ipc_params(
            request.params,
            "device.getCapabilities params must match { deviceId: string }.",
        ) {
            Ok(params) => params,
            Err(error) => return IpcResponse::failure(Some(request.id), error),
        };

        if let Err(error) = require_non_empty(&params.device_id, "deviceId") {
            return IpcResponse::failure(Some(request.id), error);
        }

        match self.companion_registry.get_device(&params.device_id) {
            Ok(device) => response_from_serializable(
                request.id,
                &device.capabilities,
                "companion.registry",
            ),
            Err(error) => IpcResponse::failure(Some(request.id), error),
        }
    }

    fn handle_device_get_permission_state(&self, request: IpcRequest) -> IpcResponse {
        let params: DeviceScopedParams = match parse_ipc_params(
            request.params,
            "device.getPermissionState params must match { deviceId: string }.",
        ) {
            Ok(params) => params,
            Err(error) => return IpcResponse::failure(Some(request.id), error),
        };

        if let Err(error) = require_non_empty(&params.device_id, "deviceId") {
            return IpcResponse::failure(Some(request.id), error);
        }

        match self.companion_registry.get_device(&params.device_id) {
            Ok(device) => response_from_serializable(
                request.id,
                &device.permission_states,
                "companion.registry",
            ),
            Err(error) => IpcResponse::failure(Some(request.id), error),
        }
    }

    fn handle_device_invoke(&self, request: IpcRequest) -> IpcResponse {
        let params: DeviceInvokeParams = match parse_ipc_params(
            request.params,
            "device.invoke params must match { deviceId, capabilityId, operation, args }.",
        ) {
            Ok(params) => params,
            Err(error) => return IpcResponse::failure(Some(request.id), error),
        };

        for (value, name) in [
            (&params.device_id, "deviceId"),
            (&params.capability_id, "capabilityId"),
            (&params.operation, "operation"),
        ] {
            if let Err(error) = require_non_empty(value, name) {
                return IpcResponse::failure(Some(request.id), error);
            }
        }

        if !params.args.is_object() {
            return IpcResponse::failure(
                Some(request.id),
                AppError::new(
                    "IPC_PARAMS_INVALID",
                    "device.invoke args must be a JSON object.",
                    "ipc.protocol",
                    false,
                ),
            );
        }

        let device = match self.companion_registry.get_device(&params.device_id) {
            Ok(device) => device,
            Err(error) => return IpcResponse::failure(Some(request.id), error),
        };

        let capability = match device
            .capabilities
            .iter()
            .find(|capability| capability.id == params.capability_id)
        {
            Some(capability) => capability,
            None => {
                return IpcResponse::failure(
                    Some(request.id),
                    AppError::new(
                        "COMPANION_CAPABILITY_NOT_FOUND",
                        format!(
                            "Capability {} is not exposed by device {}.",
                            params.capability_id, params.device_id
                        ),
                        "companion.registry",
                        true,
                    ),
                )
            }
        };

        if !capability.operations.contains(&params.operation) {
            return IpcResponse::failure(
                Some(request.id),
                AppError::new(
                    "COMPANION_OPERATION_NOT_SUPPORTED",
                    format!(
                        "Operation {} is not supported by capability {}.",
                        params.operation, params.capability_id
                    ),
                    "companion.registry",
                    true,
                ),
            );
        }

        // The command has passed Core-side routing validation. Execution still
        // belongs to the future QUIC transport/provider implementation, so the
        // protocol returns an explicit recoverable failure instead of silently
        // pretending the Android command was delivered.
        IpcResponse::failure(
            Some(request.id),
            AppError::new(
                "COMPANION_COMMAND_ROUTER_NOT_READY",
                "Android companion command routing is not connected to a QUIC transport yet.",
                "companion.router",
                true,
            )
            .with_suggestion("Start a QUIC companion session before invoking device capabilities."),
        )
    }

    fn resolve_current_adb_path(&self) -> Result<PathBuf, AppError> {
        let target = HostTarget::current()?;
        let asset = find_adb_asset(&self.manifest, &target)?;
        resolve_asset_path(&asset)
    }
}

fn default_params_object() -> Value {
    json!({})
}

fn parse_ipc_params<T: DeserializeOwned>(
    params: Value,
    expected_message: &str,
) -> Result<T, AppError> {
    serde_json::from_value(params).map_err(|error| {
        AppError::new(
            "IPC_PARAMS_INVALID",
            expected_message,
            "ipc.protocol",
            false,
        )
        .with_cause(error)
    })
}

fn require_non_empty(value: &str, field_name: &str) -> Result<(), AppError> {
    if value.trim().is_empty() {
        return Err(AppError::new(
            "IPC_PARAMS_INVALID",
            format!("Required IPC param is missing or empty: {field_name}"),
            "ipc.protocol",
            false,
        ));
    }

    Ok(())
}

fn response_from_serializable<T: Serialize + ?Sized>(
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
    use crate::{
        adb::AdbCommandOutput,
        assets::load_embedded_manifest,
        companion::{sample_android_companion_device, CompanionRegistry},
    };

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

    fn service_with_sample_companion() -> CoreService<RecordingRunner> {
        CoreService::new_with_companion_registry(
            RecordingRunner {
                calls: Arc::new(Mutex::new(Vec::new())),
            },
            load_embedded_manifest().expect("embedded manifest must be valid"),
            CompanionRegistry::new(vec![sample_android_companion_device()]),
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

    #[test]
    fn companion_protocol_info_returns_quic_descriptor() {
        // 场景：前端需要发现 Core 与 Android 伴侣 App 之间的 QUIC 协议版本与消息类型。
        let service = service_with_recording_runner(Arc::new(Mutex::new(Vec::new())));

        let encoded = service.handle_json_line(
            r#"{"id":"4","method":"companion.protocol.info","params":{}}"#,
        );
        let response: IpcResponse = serde_json::from_str(encoded.as_str())
            .expect("response should be valid JSON");

        assert!(response.ok);
        assert_eq!(
            response.result.expect("result is required")["protocol"],
            "adbcontrol-companion-quic"
        );
    }

    #[test]
    fn capability_list_exposes_android_companion_catalog() {
        // 场景：即使尚未连接设备，前端也可以读取 Core 支持的 Android Companion 能力目录。
        let service = service_with_recording_runner(Arc::new(Mutex::new(Vec::new())));

        let encoded = service.handle_json_line(
            r#"{"id":"5","method":"capability.list","params":{}}"#,
        );
        let response: IpcResponse = serde_json::from_str(encoded.as_str())
            .expect("response should be valid JSON");
        let result = response.result.expect("result is required");
        let capabilities = result.as_array().expect("capabilities should be array");

        assert!(response.ok);
        assert!(capabilities
            .iter()
            .any(|capability| capability["id"] == "android.ui.overlay"));
    }

    #[test]
    fn device_list_returns_registered_companions() {
        // 场景：Core 作为中间件必须能向前端列出已注册的 Android 伴侣设备。
        let service = service_with_sample_companion();

        let encoded = service.handle_json_line(
            r#"{"id":"6","method":"device.list","params":{}}"#,
        );
        let response: IpcResponse = serde_json::from_str(encoded.as_str())
            .expect("response should be valid JSON");
        let result = response.result.expect("result is required");
        let devices = result.as_array().expect("devices should be array");

        assert!(response.ok);
        assert_eq!(devices[0]["deviceId"], "android-companion-sample");
    }

    #[test]
    fn device_get_capabilities_requires_connected_device() {
        // 场景：前端查询不存在的 Android 伴侣设备时，Core 必须返回可恢复连接错误。
        let service = service_with_recording_runner(Arc::new(Mutex::new(Vec::new())));

        let encoded = service.handle_json_line(
            r#"{"id":"7","method":"device.getCapabilities","params":{"deviceId":"missing"}}"#,
        );
        let response: IpcResponse = serde_json::from_str(encoded.as_str())
            .expect("response should be valid JSON");

        assert!(!response.ok);
        assert_eq!(
            response.error.expect("error is required").error_code,
            "COMPANION_DEVICE_NOT_CONNECTED"
        );
    }

    #[test]
    fn device_get_permission_state_returns_permission_matrix() {
        // 场景：前端展示授权界面前，必须能按能力读取 Android 权限缺失原因。
        let service = service_with_sample_companion();

        let encoded = service.handle_json_line(
            r#"{"id":"8","method":"device.getPermissionState","params":{"deviceId":"android-companion-sample"}}"#,
        );
        let response: IpcResponse = serde_json::from_str(encoded.as_str())
            .expect("response should be valid JSON");
        let result = response.result.expect("result is required");
        let states = result.as_array().expect("permission states should be array");

        assert!(response.ok);
        assert!(states
            .iter()
            .any(|state| state["capabilityId"] == "android.screen.capture"));
    }

    #[test]
    fn device_invoke_validates_capability_before_transport_dispatch() {
        // 场景：设备存在且能力/操作合法时，本阶段仍必须显式说明 QUIC router 未连接，不能假装执行成功。
        let service = service_with_sample_companion();

        let encoded = service.handle_json_line(
            r#"{"id":"9","method":"device.invoke","params":{"deviceId":"android-companion-sample","capabilityId":"android.volume.media","operation":"volume.set","args":{"level":5}}}"#,
        );
        let response: IpcResponse = serde_json::from_str(encoded.as_str())
            .expect("response should be valid JSON");

        assert!(!response.ok);
        assert_eq!(
            response.error.expect("error is required").error_code,
            "COMPANION_COMMAND_ROUTER_NOT_READY"
        );
    }
}
