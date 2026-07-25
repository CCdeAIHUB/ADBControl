# IPC 协议列表：Frontend ↔ Core

本文档是跨平台前端与 Core 之间的 IPC 协议清单。当前传输层为 JSON Lines over stdio，后续可替换为 Windows Named Pipe / Unix Domain Socket，但消息契约保持一致。

## 1. 通用 Envelope

### 请求

```json
{"id":"req-1","method":"method.name","params":{}}
```

字段：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---:|---|
| `id` | string | 是 | 前端生成的请求 ID，Core 原样返回。 |
| `method` | string | 是 | IPC 方法名。 |
| `params` | object | 否 | 方法参数。缺省时按 `{}` 处理。 |

### 成功响应

```json
{"id":"req-1","ok":true,"result":{}}
```

### 失败响应

```json
{"id":"req-1","ok":false,"error":{"errorCode":"IPC_METHOD_UNKNOWN","message":"Unknown IPC method: bad.method","module":"ipc.protocol","recoverable":false}}
```

错误对象统一为：

```json
{
  "errorCode": "STRING_CODE",
  "message": "Human readable message",
  "module": "module.owner",
  "recoverable": true,
  "cause": "optional low level cause",
  "suggestion": "optional recovery suggestion",
  "traceId": "optional-trace-id"
}
```

---

## 2. `core.getHostTarget`

状态：已实现。

用途：返回 Core 当前识别到的主机 OS / CPU 架构 target，用于前端展示和 ADB 资产选择排查。

### 请求

```json
{"id":"core-1","method":"core.getHostTarget","params":{}}
```

### 成功

```json
{
  "id":"core-1",
  "ok":true,
  "result":{
    "os":"linux",
    "arch":"aarch64",
    "targetId":"linux-arm64"
  }
}
```

### 失败

```json
{
  "id":"core-1",
  "ok":false,
  "error":{
    "errorCode":"PLATFORM_OS_UNSUPPORTED",
    "message":"Unsupported host operating system: unknown-os",
    "module":"platform.target",
    "recoverable":false
  }
}
```

---

## 3. `adb.asset.current`

状态：已实现。

用途：返回当前平台将使用的 ADB asset manifest 声明。

### 请求

```json
{"id":"adb-asset-1","method":"adb.asset.current","params":{}}
```

### 成功

```json
{
  "id":"adb-asset-1",
  "ok":true,
  "result":{
    "targetId":"linux-arm64",
    "source":"custom-github-build",
    "path":"assets/adb/linux-arm64/adb",
    "sha256":null
  }
}
```

### 失败

```json
{
  "id":"adb-asset-1",
  "ok":false,
  "error":{
    "errorCode":"ADB_ASSET_NOT_FOUND",
    "message":"No ADB asset is declared for target linux-riscv64",
    "module":"adb.assets",
    "recoverable":false
  }
}
```

---

## 4. `adb.exec`

状态：已实现。

用途：通过 Core 调用内置 ADB。Core 只执行 manifest 指向的 ADB 二进制，参数必须是数组，不能拼接 shell 字符串。

### 请求

```json
{"id":"adb-1","method":"adb.exec","params":{"args":["devices","-l"]}}
```

### 成功

```json
{
  "id":"adb-1",
  "ok":true,
  "result":{
    "exitCode":0,
    "stdout":"List of devices attached\nemulator-5554 device product:sdk_gphone64\n",
    "stderr":""
  }
}
```

### 失败：参数类型错误

```json
{
  "id":"adb-1",
  "ok":false,
  "error":{
    "errorCode":"IPC_PARAMS_INVALID",
    "message":"adb.exec params must match { args: string[] }.",
    "module":"ipc.protocol",
    "recoverable":false
  }
}
```

### 失败：参数包含 NUL 字节

```json
{
  "id":"adb-1",
  "ok":false,
  "error":{
    "errorCode":"ADB_ARGS_CONTAIN_NUL",
    "message":"ADB arguments must not contain NUL bytes.",
    "module":"adb.runner",
    "recoverable":false
  }
}
```

---

## 5. `companion.protocol.info`

状态：已实现。

用途：返回 Core 支持的 Android Companion QUIC 应用层协议描述。

### 请求

```json
{"id":"companion-protocol-1","method":"companion.protocol.info","params":{}}
```

### 成功

```json
{
  "id":"companion-protocol-1",
  "ok":true,
  "result":{
    "protocol":"adbcontrol-companion-quic",
    "version":1,
    "channels":["control","data","media"],
    "messageKinds":["hello","helloAck","heartbeat","capabilityList","permissionState","commandRequest","commandResponse","streamOpen","streamChunk","streamClose","error"],
    "controlStream":"JSON envelope over reliable QUIC stream",
    "dataStream":"length-prefixed binary or JSON envelope over reliable QUIC stream",
    "mediaStream":"binary frame stream or unreliable datagram depending on capability"
  }
}
```

### 失败

正常情况下本方法不依赖外部状态。若响应序列化失败，返回：

```json
{
  "id":"companion-protocol-1",
  "ok":false,
  "error":{
    "errorCode":"IPC_RESPONSE_SERIALIZATION_FAILED",
    "message":"Core failed to serialize IPC response.",
    "module":"companion.protocol",
    "recoverable":false
  }
}
```

---

## 6. `capability.list`

状态：已实现。

用途：返回 Core 已知的 Android Companion 能力目录。该方法不要求设备已连接，用于前端提前渲染能力说明和权限说明。

### 请求

```json
{"id":"cap-1","method":"capability.list","params":{}}
```

### 成功

```json
{
  "id":"cap-1",
  "ok":true,
  "result":[
    {
      "id":"android.camera.stream",
      "title":"Camera stream",
      "description":"Open camera preview stream after permission and foreground visibility checks.",
      "provider":"android-companion",
      "transport":"quic-media-stream",
      "sensitivity":"critical",
      "permission":{
        "androidPermissions":["android.permission.CAMERA"],
        "specialPermissions":[],
        "requiresUserConsent":true,
        "auditRequired":true
      },
      "operations":["camera.open","camera.close"]
    }
  ]
}
```

### 失败

```json
{
  "id":"cap-1",
  "ok":false,
  "error":{
    "errorCode":"IPC_RESPONSE_SERIALIZATION_FAILED",
    "message":"Core failed to serialize IPC response.",
    "module":"capability.catalog",
    "recoverable":false
  }
}
```

---

## 7. `device.list`

状态：已实现，当前返回 Core 内已注册的 Companion 设备；真实 QUIC 注册将在后续 transport 阶段接入。

用途：列出已连接或已注册的 Android Companion 设备。

### 请求

```json
{"id":"device-list-1","method":"device.list","params":{}}
```

### 成功：有设备

```json
{
  "id":"device-list-1",
  "ok":true,
  "result":[
    {
      "deviceId":"android-companion-sample",
      "displayName":"Android Companion Sample",
      "appVersion":"0.1.0",
      "androidSdk":35,
      "connectionState":"ready",
      "capabilities":[],
      "permissionStates":[]
    }
  ]
}
```

### 成功：无设备

```json
{"id":"device-list-1","ok":true,"result":[]}
```

### 失败

```json
{
  "id":"device-list-1",
  "ok":false,
  "error":{
    "errorCode":"IPC_RESPONSE_SERIALIZATION_FAILED",
    "message":"Core failed to serialize IPC response.",
    "module":"companion.registry",
    "recoverable":false
  }
}
```

---

## 8. `device.getCapabilities`

状态：已实现。

用途：读取指定 Android Companion 设备实际暴露的能力。不同设备可能因为系统版本、硬件、授权状态、厂商限制而能力不同。

### 请求

```json
{"id":"device-cap-1","method":"device.getCapabilities","params":{"deviceId":"android-companion-sample"}}
```

### 成功

```json
{
  "id":"device-cap-1",
  "ok":true,
  "result":[
    {
      "id":"android.accessibility.control",
      "title":"Accessibility control",
      "provider":"android-companion",
      "transport":"quic-control",
      "sensitivity":"critical",
      "permission":{
        "androidPermissions":[],
        "specialPermissions":["accessibility-service"],
        "requiresUserConsent":true,
        "auditRequired":true
      },
      "operations":["accessibility.status","accessibility.global.back","accessibility.global.home","accessibility.global.recents","accessibility.global.notifications","accessibility.global.quickSettings","accessibility.global.powerDialog","accessibility.touch.tap","accessibility.touch.swipe"]
    }
  ]
}
```

### 失败：缺少 deviceId

```json
{
  "id":"device-cap-1",
  "ok":false,
  "error":{
    "errorCode":"IPC_PARAMS_INVALID",
    "message":"Required IPC param is missing or empty: deviceId",
    "module":"ipc.protocol",
    "recoverable":false
  }
}
```

### 失败：设备未连接

```json
{
  "id":"device-cap-1",
  "ok":false,
  "error":{
    "errorCode":"COMPANION_DEVICE_NOT_CONNECTED",
    "message":"Android companion device is not connected: pixel-8",
    "module":"companion.registry",
    "recoverable":true,
    "suggestion":"Pair the Android companion app with Core before invoking capabilities."
  }
}
```

---

## 9. `device.getPermissionState`

状态：已实现。

用途：读取指定设备每个能力对应的 Android 权限、特殊授权、用户同意状态。前端应优先调用该接口渲染授权引导，而不是直接调用 `device.invoke`。

### 请求

```json
{"id":"perm-1","method":"device.getPermissionState","params":{"deviceId":"android-companion-sample"}}
```

### 成功

```json
{
  "id":"perm-1",
  "ok":true,
  "result":[
    {
      "capabilityId":"android.camera.stream",
      "granted":false,
      "androidPermissions":["android.permission.CAMERA"],
      "missingPermissions":["android.permission.CAMERA"],
      "specialGrants":[],
      "missingSpecialGrants":[],
      "userConsentRequired":true
    }
  ]
}
```

### 失败

```json
{
  "id":"perm-1",
  "ok":false,
  "error":{
    "errorCode":"COMPANION_DEVICE_NOT_CONNECTED",
    "message":"Android companion device is not connected: android-companion-sample",
    "module":"companion.registry",
    "recoverable":true
  }
}
```

---

## 10. `device.invoke`

状态：协议校验与 commandRequest envelope 构建已实现；真实网络 QUIC session 未连接时返回可恢复 session 错误。

用途：前端通过 Core 调用某个 Android Companion 能力。Core 先检查 device、capability、operation 是否存在，再由后续 QUIC router 转发给 Android App。

### 请求

```json
{
  "id":"invoke-1",
  "method":"device.invoke",
  "params":{
    "deviceId":"android-companion-sample",
    "capabilityId":"android.volume.media",
    "operation":"volume.set",
    "args":{"level":5}
  }
}
```

### 成功：session 已连接并已分发

```json
{
  "id":"invoke-1",
  "ok":true,
  "result":{
    "requestId":"invoke-1",
    "deviceId":"android-companion-sample",
    "capabilityId":"android.volume.media",
    "operation":"volume.set",
    "status":"dispatched",
    "envelope":{
      "protocol":"adbcontrol-companion-quic",
      "version":1,
      "messageId":"command-invoke-1",
      "traceId":"invoke-1",
      "deviceId":"android-companion-sample",
      "channel":"control",
      "kind":"commandRequest"
    },
    "result":{
      "dispatched":true,
      "transport":"quic-control",
      "messageId":"command-invoke-1"
    }
  }
}
```

### 失败：设备已注册但 QUIC session 未连接

```json
{
  "id":"invoke-1",
  "ok":false,
  "error":{
    "errorCode":"COMPANION_SESSION_NOT_CONNECTED",
    "message":"Android companion QUIC session is not connected for device android-companion-sample.",
    "module":"companion.router",
    "recoverable":true,
    "suggestion":"Complete companion pairing and QUIC hello/helloAck before invoking capabilities."
  }
}
```

### 失败：能力不存在

```json
{
  "id":"invoke-1",
  "ok":false,
  "error":{
    "errorCode":"COMPANION_CAPABILITY_NOT_FOUND",
    "message":"Capability android.unknown is not exposed by device android-companion-sample.",
    "module":"companion.registry",
    "recoverable":true
  }
}
```

### 失败：操作不支持

```json
{
  "id":"invoke-1",
  "ok":false,
  "error":{
    "errorCode":"COMPANION_OPERATION_NOT_SUPPORTED",
    "message":"Operation volume.max is not supported by capability android.volume.media.",
    "module":"companion.registry",
    "recoverable":true
  }
}
```

---

## 11. `stream.open`

状态：预留。

用途：前端请求 Core 打开一个持续性数据流或媒体流，例如相机、录音、传感器订阅。

### 请求

```json
{
  "id":"stream-open-1",
  "method":"stream.open",
  "params":{
    "deviceId":"android-companion-sample",
    "capabilityId":"android.camera.stream",
    "operation":"camera.open",
    "options":{"format":"h264","maxFps":30}
  }
}
```

### 成功

```json
{
  "id":"stream-open-1",
  "ok":true,
  "result":{
    "streamId":"camera-001",
    "deviceId":"android-companion-sample",
    "capabilityId":"android.camera.stream",
    "transport":"quic-media-stream",
    "state":"opening"
  }
}
```

### 失败

```json
{
  "id":"stream-open-1",
  "ok":false,
  "error":{
    "errorCode":"STREAM_PERMISSION_DENIED",
    "message":"Camera streaming requires camera permission before opening stream.",
    "module":"companion.stream",
    "recoverable":true
  }
}
```

---

## 12. `stream.close`

状态：预留。

用途：关闭已打开的 Companion stream。

### 请求

```json
{"id":"stream-close-1","method":"stream.close","params":{"streamId":"camera-001"}}
```

### 成功

```json
{"id":"stream-close-1","ok":true,"result":{"streamId":"camera-001","state":"closed"}}
```

### 失败

```json
{
  "id":"stream-close-1",
  "ok":false,
  "error":{
    "errorCode":"STREAM_NOT_FOUND",
    "message":"Stream is not active: camera-001",
    "module":"companion.stream",
    "recoverable":true
  }
}
```
