# Core Companion Ingress

## 目标

`CompanionIngress` 是 Core 侧接收 Android Companion 自定义 QUIC 消息的网络无关入口。真实 QUIC listener 只负责接收 stream/datagram bytes，然后调用 ingress；ingress 负责：

1. 解析 JSON envelope；
2. 校验 `adbcontrol-companion-quic` 协议名与版本；
3. 按 message kind 分发；
4. 将成功响应或错误统一转换为 `QuicEnvelope`；
5. 避免 media chunk 污染 session state。

## 入口

Rust API：

```rust
let mut ingress = CompanionIngress::default();
let response_bytes = ingress.handle_json_bytes(request_bytes);
```

## 支持的入站消息

### `hello`

用于创建 Core session。

请求：

```json
{
  "protocol": "adbcontrol-companion-quic",
  "version": 1,
  "messageId": "hello-device-1",
  "traceId": "trace-1",
  "deviceId": "device-1",
  "channel": "control",
  "kind": "hello",
  "payload": {
    "appVersion": "0.1.0",
    "deviceId": "device-1",
    "deviceName": "Pixel Test",
    "androidSdk": 35,
    "supportedProtocolVersions": [1]
  }
}
```

成功：

```json
{
  "protocol": "adbcontrol-companion-quic",
  "version": 1,
  "messageId": "hello-ack-device-1",
  "traceId": "trace-1",
  "deviceId": "device-1",
  "channel": "control",
  "kind": "helloAck",
  "payload": {
    "accepted": true,
    "selectedProtocolVersion": 1,
    "serverName": "ADBControl Core",
    "nextRequiredMessages": ["capabilityList", "permissionState"]
  }
}
```

失败：

```json
{
  "protocol": "adbcontrol-companion-quic",
  "version": 1,
  "messageId": "ingress-error-hello-device-1",
  "traceId": "trace-1",
  "deviceId": "device-1",
  "channel": "control",
  "kind": "error",
  "payload": {
    "errorCode": "COMPANION_HELLO_INVALID",
    "message": "Companion hello payload is invalid.",
    "module": "companion.session",
    "recoverable": false
  }
}
```

### `capabilityList`

同步 Android Companion 实际能力列表。必须在 `hello` 之后发送。

成功：

```json
{
  "protocol": "adbcontrol-companion-quic",
  "version": 1,
  "messageId": "ack-cap-list-device-1",
  "traceId": "trace-2",
  "deviceId": "device-1",
  "channel": "control",
  "kind": "commandResponse",
  "payload": {
    "accepted": true,
    "registeredCapabilityCount": 17
  }
}
```

失败：

```json
{
  "protocol": "adbcontrol-companion-quic",
  "version": 1,
  "messageId": "ingress-error-cap-list-device-1",
  "deviceId": "device-1",
  "channel": "control",
  "kind": "error",
  "payload": {
    "errorCode": "COMPANION_SESSION_NOT_CONNECTED",
    "message": "Capability list arrived before hello for device device-1.",
    "module": "companion.session",
    "recoverable": true
  }
}
```

### `permissionState`

同步 Android Companion 权限矩阵。必须在 `hello` 之后发送。

成功：

```json
{
  "protocol": "adbcontrol-companion-quic",
  "version": 1,
  "messageId": "ack-permission-device-1",
  "deviceId": "device-1",
  "channel": "control",
  "kind": "commandResponse",
  "payload": {
    "accepted": true,
    "updatedStateCount": 17
  }
}
```

失败：

```json
{
  "protocol": "adbcontrol-companion-quic",
  "version": 1,
  "messageId": "ingress-error-permission-device-1",
  "deviceId": "device-1",
  "channel": "control",
  "kind": "error",
  "payload": {
    "errorCode": "COMPANION_PERMISSION_STATE_INVALID",
    "message": "Companion permissionState payload is invalid.",
    "module": "companion.session",
    "recoverable": false
  }
}
```

### `heartbeat`

用于保持 session 活跃。

成功：

```json
{
  "protocol": "adbcontrol-companion-quic",
  "version": 1,
  "messageId": "heartbeat-ack-heartbeat-1",
  "deviceId": "device-1",
  "channel": "control",
  "kind": "heartbeat",
  "payload": {
    "receivedMessageId": "heartbeat-1",
    "serverState": "ready"
  }
}
```

失败：

```json
{
  "protocol": "adbcontrol-companion-quic",
  "version": 1,
  "messageId": "ingress-error-heartbeat-1",
  "deviceId": "device-1",
  "channel": "control",
  "kind": "error",
  "payload": {
    "errorCode": "COMPANION_SESSION_NOT_CONNECTED",
    "message": "Android companion QUIC session is not connected for device device-1.",
    "module": "companion.session",
    "recoverable": true
  }
}
```

### `streamOpen` / `streamChunk` / `streamClose`

媒体消息必须使用 `media` channel。当前 ingress 会 ACK 媒体消息并记录 chunk 数量，但不把媒体 bytes 写入最终存储；后续 Core media store / decoder / relay 可在这个分支接入。

请求：

```json
{
  "protocol": "adbcontrol-companion-quic",
  "version": 1,
  "messageId": "chunk-1",
  "traceId": "stream-1",
  "deviceId": "device-1",
  "channel": "media",
  "kind": "streamChunk",
  "payload": {
    "sessionId": "stream-1",
    "chunkType": "media",
    "encoding": "base64",
    "data": "AAAA",
    "sizeBytes": 4
  }
}
```

成功：

```json
{
  "protocol": "adbcontrol-companion-quic",
  "version": 1,
  "messageId": "ingress-ack-chunk-1",
  "traceId": "stream-1",
  "deviceId": "device-1",
  "channel": "control",
  "kind": "commandResponse",
  "payload": {
    "accepted": true,
    "media": true,
    "receivedKind": "streamChunk",
    "receivedChunkCount": 1
  }
}
```

失败：

```json
{
  "protocol": "adbcontrol-companion-quic",
  "version": 1,
  "messageId": "ingress-error-chunk-1",
  "deviceId": "device-1",
  "channel": "control",
  "kind": "error",
  "payload": {
    "errorCode": "COMPANION_MEDIA_CHANNEL_INVALID",
    "message": "Media stream messages must use the media channel.",
    "module": "companion.ingress",
    "recoverable": false
  }
}
```

## 通用错误

### 非法 JSON

```json
{
  "protocol": "adbcontrol-companion-quic",
  "version": 1,
  "messageId": "ingress-error",
  "channel": "control",
  "kind": "error",
  "payload": {
    "errorCode": "COMPANION_INGRESS_INVALID_JSON",
    "message": "Companion ingress received invalid JSON envelope.",
    "module": "companion.ingress",
    "recoverable": false
  }
}
```

### 协议名不匹配

```json
{
  "protocol": "adbcontrol-companion-quic",
  "version": 1,
  "messageId": "ingress-error-msg-1",
  "channel": "control",
  "kind": "error",
  "payload": {
    "errorCode": "COMPANION_PROTOCOL_MISMATCH",
    "message": "Companion QUIC envelope protocol does not match ADBControl.",
    "module": "companion.protocol",
    "recoverable": false
  }
}
```

## 当前边界

- 已完成：网络无关 ingress、session 分发、media ACK、error envelope 化、单元测试。
- 未完成：真实纯 QUIC listener、TLS 证书、设备配对、媒体落盘 / relay。
