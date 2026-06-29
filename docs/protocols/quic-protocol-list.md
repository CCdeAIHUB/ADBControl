# QUIC 协议列表：Android Companion ↔ Core

本文档定义 Android 伴侣 App 与 Core 之间的应用层 QUIC 协议。QUIC 只负责安全传输、连接、多路流和迁移能力；ADBControl 在 QUIC 之上定义自己的 `adbcontrol-companion-quic` envelope、message kind、能力调用和流式数据规则。

## 1. 通用 QUIC Envelope

所有控制消息使用 JSON envelope。大文件、投屏、相机、录音可在 `streamChunk` 中携带二进制帧引用或后续切换为 QUIC datagram / media stream。

### Envelope 字段

| 字段 | 类型 | 必填 | 说明 |
|---|---|---:|---|
| `protocol` | string | 是 | 固定为 `adbcontrol-companion-quic`。 |
| `version` | number | 是 | 当前为 `1`。 |
| `messageId` | string | 是 | 单条 QUIC 应用层消息 ID。 |
| `traceId` | string | 否 | 跨 IPC / QUIC 的追踪 ID。 |
| `deviceId` | string | 否 | Android Companion 设备 ID。 |
| `channel` | string | 是 | `control` / `data` / `media`。 |
| `kind` | string | 是 | 消息类型。 |
| `payload` | object | 是 | 消息内容。 |

### 成功 envelope 示例

```json
{
  "protocol":"adbcontrol-companion-quic",
  "version":1,
  "messageId":"msg-1",
  "traceId":"trace-1",
  "deviceId":"android-companion-sample",
  "channel":"control",
  "kind":"hello",
  "payload":{}
}
```

### 协议错误示例

```json
{
  "protocol":"adbcontrol-companion-quic",
  "version":1,
  "messageId":"msg-error-1",
  "deviceId":"android-companion-sample",
  "channel":"control",
  "kind":"error",
  "payload":{
    "errorCode":"COMPANION_PROTOCOL_MISMATCH",
    "message":"Companion QUIC envelope protocol does not match ADBControl.",
    "module":"companion.protocol",
    "recoverable":false
  }
}
```

---

## 2. `hello`

方向：Android Companion → Core  
通道：control  
状态：已定义。

用途：QUIC 连接建立后，Android Companion 发送设备身份、App 版本、Android SDK、支持的协议版本。

### 请求

```json
{
  "protocol":"adbcontrol-companion-quic",
  "version":1,
  "messageId":"hello-1",
  "deviceId":"pixel-8-pro-001",
  "channel":"control",
  "kind":"hello",
  "payload":{
    "appVersion":"0.1.0",
    "deviceId":"pixel-8-pro-001",
    "deviceName":"Pixel 8 Pro",
    "androidSdk":35,
    "supportedProtocolVersions":[1]
  }
}
```

### 成功响应：`helloAck`

```json
{
  "protocol":"adbcontrol-companion-quic",
  "version":1,
  "messageId":"hello-ack-1",
  "deviceId":"pixel-8-pro-001",
  "channel":"control",
  "kind":"helloAck",
  "payload":{
    "accepted":true,
    "selectedProtocolVersion":1,
    "serverName":"ADBControl Core",
    "nextRequiredMessages":["capabilityList","permissionState"]
  }
}
```

### 失败响应

```json
{
  "protocol":"adbcontrol-companion-quic",
  "version":1,
  "messageId":"hello-error-1",
  "deviceId":"pixel-8-pro-001",
  "channel":"control",
  "kind":"error",
  "payload":{
    "errorCode":"COMPANION_PROTOCOL_VERSION_UNSUPPORTED",
    "message":"Unsupported companion protocol version: 99.",
    "module":"companion.protocol",
    "recoverable":false
  }
}
```

---

## 3. `heartbeat`

方向：双向  
通道：control  
状态：已定义。

用途：维持连接活性、测量延迟、发现断连。

### 请求

```json
{
  "protocol":"adbcontrol-companion-quic",
  "version":1,
  "messageId":"hb-1",
  "deviceId":"pixel-8-pro-001",
  "channel":"control",
  "kind":"heartbeat",
  "payload":{
    "sentAtEpochMs":1782700000000,
    "connectionState":"ready"
  }
}
```

### 成功响应

```json
{
  "protocol":"adbcontrol-companion-quic",
  "version":1,
  "messageId":"hb-ack-1",
  "deviceId":"pixel-8-pro-001",
  "channel":"control",
  "kind":"heartbeat",
  "payload":{
    "receivedMessageId":"hb-1",
    "receivedAtEpochMs":1782700000040,
    "serverState":"ready"
  }
}
```

### 失败响应

```json
{
  "protocol":"adbcontrol-companion-quic",
  "version":1,
  "messageId":"hb-error-1",
  "deviceId":"pixel-8-pro-001",
  "channel":"control",
  "kind":"error",
  "payload":{
    "errorCode":"COMPANION_HEARTBEAT_TIMEOUT",
    "message":"Heartbeat timed out for Android companion device.",
    "module":"companion.connection",
    "recoverable":true
  }
}
```

---

## 4. `capabilityList`

方向：Android Companion → Core  
通道：control  
状态：已定义。

用途：Android Companion 上报当前设备实际支持的能力。此列表必须受 Android 版本、硬件、Manifest、特殊授权状态影响。

### 请求

```json
{
  "protocol":"adbcontrol-companion-quic",
  "version":1,
  "messageId":"cap-list-1",
  "deviceId":"pixel-8-pro-001",
  "channel":"control",
  "kind":"capabilityList",
  "payload":{
    "capabilities":[
      {
        "id":"android.screen.capture",
        "transport":"quic-media-stream",
        "sensitivity":"critical",
        "operations":["stream.open","stream.close","screenshot.capture"]
      },
      {
        "id":"android.volume.media",
        "transport":"quic-control",
        "sensitivity":"medium",
        "operations":["volume.get","volume.set"]
      }
    ]
  }
}
```

### 成功响应

```json
{
  "protocol":"adbcontrol-companion-quic",
  "version":1,
  "messageId":"cap-list-ack-1",
  "deviceId":"pixel-8-pro-001",
  "channel":"control",
  "kind":"commandResponse",
  "payload":{
    "requestId":"cap-list-1",
    "accepted":true,
    "registeredCapabilityCount":2
  }
}
```

### 失败响应

```json
{
  "protocol":"adbcontrol-companion-quic",
  "version":1,
  "messageId":"cap-list-error-1",
  "deviceId":"pixel-8-pro-001",
  "channel":"control",
  "kind":"error",
  "payload":{
    "errorCode":"COMPANION_CAPABILITY_SCHEMA_INVALID",
    "message":"Capability list payload is missing required field: capabilities[0].id",
    "module":"companion.protocol",
    "recoverable":false
  }
}
```

---

## 5. `permissionState`

方向：Android Companion → Core  
通道：control  
状态：已定义。

用途：Android Companion 上报每个能力对应的运行时权限、特殊授权、用户同意状态。Core 将该状态通过 IPC `device.getPermissionState` 提供给前端。

### 请求

```json
{
  "protocol":"adbcontrol-companion-quic",
  "version":1,
  "messageId":"perm-1",
  "deviceId":"pixel-8-pro-001",
  "channel":"control",
  "kind":"permissionState",
  "payload":{
    "states":[
      {
        "capabilityId":"android.camera.stream",
        "granted":false,
        "missingPermissions":["android.permission.CAMERA"],
        "missingSpecialGrants":[],
        "requiresUserConsent":true
      }
    ]
  }
}
```

### 成功响应

```json
{
  "protocol":"adbcontrol-companion-quic",
  "version":1,
  "messageId":"perm-ack-1",
  "deviceId":"pixel-8-pro-001",
  "channel":"control",
  "kind":"commandResponse",
  "payload":{
    "requestId":"perm-1",
    "accepted":true,
    "updatedStateCount":1
  }
}
```

### 失败响应

```json
{
  "protocol":"adbcontrol-companion-quic",
  "version":1,
  "messageId":"perm-error-1",
  "deviceId":"pixel-8-pro-001",
  "channel":"control",
  "kind":"error",
  "payload":{
    "errorCode":"COMPANION_PERMISSION_STATE_INVALID",
    "message":"Permission state references unknown capability: android.unknown",
    "module":"companion.permission",
    "recoverable":true
  }
}
```

---

## 6. `commandRequest`

方向：Core → Android Companion  
通道：control / data / media，依据能力而定  
状态：已定义。

用途：Core 转发前端 `device.invoke` 或 `stream.open` 请求给 Android Companion。

### 请求：设置媒体音量

```json
{
  "protocol":"adbcontrol-companion-quic",
  "version":1,
  "messageId":"cmd-1",
  "traceId":"ipc-invoke-1",
  "deviceId":"pixel-8-pro-001",
  "channel":"control",
  "kind":"commandRequest",
  "payload":{
    "requestId":"ipc-invoke-1",
    "capabilityId":"android.volume.media",
    "operation":"volume.set",
    "args":{"level":5}
  }
}
```

### 成功响应：`commandResponse`

```json
{
  "protocol":"adbcontrol-companion-quic",
  "version":1,
  "messageId":"cmd-resp-1",
  "traceId":"ipc-invoke-1",
  "deviceId":"pixel-8-pro-001",
  "channel":"control",
  "kind":"commandResponse",
  "payload":{
    "requestId":"ipc-invoke-1",
    "ok":true,
    "result":{"previousLevel":3,"currentLevel":5}
  }
}
```

### 失败响应：权限不足

```json
{
  "protocol":"adbcontrol-companion-quic",
  "version":1,
  "messageId":"cmd-error-1",
  "traceId":"ipc-invoke-2",
  "deviceId":"pixel-8-pro-001",
  "channel":"control",
  "kind":"error",
  "payload":{
    "requestId":"ipc-invoke-2",
    "errorCode":"COMPANION_PERMISSION_DENIED",
    "message":"Camera permission is not granted.",
    "module":"companion.permission",
    "recoverable":true,
    "suggestion":"Ask the user to grant android.permission.CAMERA on the Android companion app."
  }
}
```

---

## 7. `streamOpen`

方向：Core → Android Companion  
通道：media / data  
状态：预留。

用途：打开持续性 stream，例如投屏、相机、录音、文件传输、传感器订阅。

### 请求：打开投屏

```json
{
  "protocol":"adbcontrol-companion-quic",
  "version":1,
  "messageId":"stream-open-1",
  "traceId":"ipc-stream-open-1",
  "deviceId":"pixel-8-pro-001",
  "channel":"media",
  "kind":"streamOpen",
  "payload":{
    "requestId":"ipc-stream-open-1",
    "streamId":"screen-001",
    "capabilityId":"android.screen.capture",
    "operation":"stream.open",
    "format":"h264",
    "maxFps":30
  }
}
```

### 成功响应

```json
{
  "protocol":"adbcontrol-companion-quic",
  "version":1,
  "messageId":"stream-open-ack-1",
  "traceId":"ipc-stream-open-1",
  "deviceId":"pixel-8-pro-001",
  "channel":"media",
  "kind":"commandResponse",
  "payload":{
    "requestId":"ipc-stream-open-1",
    "ok":true,
    "streamId":"screen-001",
    "state":"open"
  }
}
```

### 失败响应：用户未同意投屏

```json
{
  "protocol":"adbcontrol-companion-quic",
  "version":1,
  "messageId":"stream-open-error-1",
  "traceId":"ipc-stream-open-1",
  "deviceId":"pixel-8-pro-001",
  "channel":"media",
  "kind":"error",
  "payload":{
    "errorCode":"COMPANION_MEDIA_PROJECTION_CONSENT_REQUIRED",
    "message":"Screen capture requires user consent for each MediaProjection session.",
    "module":"companion.mediaProjection",
    "recoverable":true
  }
}
```

---

## 8. `streamChunk`

方向：Android Companion → Core 或 Core → Android Companion  
通道：data / media  
状态：预留。

用途：传输文件块、视频帧、音频帧、传感器批量数据。媒体帧可携带二进制 frame header，JSON 中只保留元数据。

### 请求：视频帧元数据

```json
{
  "protocol":"adbcontrol-companion-quic",
  "version":1,
  "messageId":"chunk-1",
  "traceId":"ipc-stream-open-1",
  "deviceId":"pixel-8-pro-001",
  "channel":"media",
  "kind":"streamChunk",
  "payload":{
    "streamId":"screen-001",
    "sequence":42,
    "timestampNs":1800000000,
    "contentType":"video/h264",
    "binaryFrameLength":4096,
    "endOfStream":false
  }
}
```

### 成功确认

```json
{
  "protocol":"adbcontrol-companion-quic",
  "version":1,
  "messageId":"chunk-ack-42",
  "deviceId":"pixel-8-pro-001",
  "channel":"media",
  "kind":"commandResponse",
  "payload":{
    "streamId":"screen-001",
    "ackSequence":42
  }
}
```

### 失败响应

```json
{
  "protocol":"adbcontrol-companion-quic",
  "version":1,
  "messageId":"chunk-error-1",
  "deviceId":"pixel-8-pro-001",
  "channel":"media",
  "kind":"error",
  "payload":{
    "errorCode":"COMPANION_STREAM_SEQUENCE_INVALID",
    "message":"Stream chunk sequence is out of order: expected 43, got 45.",
    "module":"companion.stream",
    "recoverable":true
  }
}
```

---

## 9. `streamClose`

方向：双向  
通道：data / media  
状态：预留。

用途：关闭持续性 stream，并释放 Android 端资源，例如 MediaProjection、Camera、AudioRecord、文件句柄、Sensor listener。

### 请求

```json
{
  "protocol":"adbcontrol-companion-quic",
  "version":1,
  "messageId":"stream-close-1",
  "deviceId":"pixel-8-pro-001",
  "channel":"media",
  "kind":"streamClose",
  "payload":{
    "streamId":"screen-001",
    "reason":"frontend-request"
  }
}
```

### 成功响应

```json
{
  "protocol":"adbcontrol-companion-quic",
  "version":1,
  "messageId":"stream-close-ack-1",
  "deviceId":"pixel-8-pro-001",
  "channel":"media",
  "kind":"commandResponse",
  "payload":{
    "streamId":"screen-001",
    "state":"closed",
    "releasedResources":["mediaProjection","virtualDisplay","encoder"]
  }
}
```

### 失败响应

```json
{
  "protocol":"adbcontrol-companion-quic",
  "version":1,
  "messageId":"stream-close-error-1",
  "deviceId":"pixel-8-pro-001",
  "channel":"media",
  "kind":"error",
  "payload":{
    "errorCode":"COMPANION_STREAM_NOT_FOUND",
    "message":"Stream is not active: screen-001",
    "module":"companion.stream",
    "recoverable":true
  }
}
```

---

## 10. `error`

方向：双向  
通道：任意  
状态：已定义。

用途：所有协议、权限、命令、流、连接失败都必须用 error envelope 返回，禁止静默失败。

### 示例：剪贴板读取被拒绝

```json
{
  "protocol":"adbcontrol-companion-quic",
  "version":1,
  "messageId":"error-clipboard-1",
  "traceId":"ipc-clipboard-1",
  "deviceId":"pixel-8-pro-001",
  "channel":"control",
  "kind":"error",
  "payload":{
    "requestId":"ipc-clipboard-1",
    "errorCode":"COMPANION_CLIPBOARD_FOREGROUND_REQUIRED",
    "message":"Clipboard read requires the companion app to be in an allowed foreground state.",
    "module":"companion.clipboard",
    "recoverable":true,
    "suggestion":"Bring the companion app to foreground or use clipboard.write instead."
  }
}
```

---

## 11. 能力到 QUIC 通道映射

| 能力 | 默认通道 | 说明 |
|---|---|---|
| `android.input.ime` | control | 文本、按键、输入会话控制。 |
| `android.screen.capture` | media | 投屏、截图、视频帧。 |
| `android.file.read` | data | 文件元数据和文件块。 |
| `android.file.write` | data | 文件写入块和完成确认。 |
| `android.camera.stream` | media | 相机帧。 |
| `android.audio.record` | media | 音频帧。 |
| `android.sms.read` | control | 短信元数据和受限内容。 |
| `android.sms.send` | control | 发送请求和结果。 |
| `android.phone.call` | control | 拨号请求和结果。 |
| `android.clipboard.read` | control | 剪贴板内容读取。 |
| `android.clipboard.write` | control | 剪贴板内容写入。 |
| `android.sensor.motion` | media / datagram | 高频传感器数据优先 datagram。 |
| `android.app.list` | control | 应用列表分页。 |
| `android.volume.media` | control | 音量读取和设置。 |
| `android.ui.background_surface` | control | 后台弹出界面。 |
| `android.ui.overlay` | control | 悬浮窗显示/隐藏。 |
| `android.intent.chain_launch` | control | 链式 intent 启动。 |

## 12. 安全边界

1. Android Companion 必须先经过 `hello` / `helloAck` 才能发送能力状态。
2. Core 未识别的 `protocol`、`version`、`kind` 必须失败。
3. 高敏感能力必须先经过 Android PermissionGuard。
4. 投屏、相机、录音、短信、电话、剪贴板、文件、悬浮窗、后台弹窗必须写审计日志。
5. `commandRequest` 不允许使用任意脚本或任意 shell 文本。
6. `streamClose` 必须释放 Android 端资源，不能仅通知 Core。
