# Core Companion Command Router

本文档说明 `device.invoke` 从 IPC 请求转换为 Android Companion QUIC `commandRequest` 的 Core 侧闭环。

## 当前状态

- 已实现 Core 侧 `CompanionCommandRouter` trait；
- 已实现未连接 router：无 QUIC session 时返回结构化错误；
- 已实现 in-memory session：用于测试 IPC → QUIC envelope 转换；
- 已实现 `CompanionCommandTransport` 和 `TransportCompanionCommandRouter`：用于把 commandRequest envelope 发送到真实网络/session transport 边界；
- `device.invoke` 已不再固定返回 `COMPANION_COMMAND_ROUTER_NOT_READY`；
- 真实网络 QUIC socket 写入和响应等待尚未接入。

## 调用链

```text
Frontend
  -> IPC device.invoke
  -> Core protocol validation
  -> CompanionRegistry device lookup
  -> capability / operation validation
  -> CompanionCommandRouter.dispatch
  -> QUIC commandRequest envelope
  -> Android Companion QuicCompanionService.handleCommandRequest
  -> AndroidFeatureDispatcher
  -> Android feature handler
```

## IPC 请求

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

## 成功响应：session 已连接并已分发

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
      "kind":"commandRequest",
      "payload":{
        "requestId":"invoke-1",
        "capabilityId":"android.volume.media",
        "operation":"volume.set",
        "args":{"level":5}
      }
    },
    "result":{
      "dispatched":true,
      "transport":"quic-control",
      "messageId":"command-invoke-1"
    }
  }
}
```

## 失败响应：设备已注册但 QUIC session 未连接

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

## 失败响应：设备未注册

```json
{
  "id":"invoke-1",
  "ok":false,
  "error":{
    "errorCode":"COMPANION_DEVICE_NOT_CONNECTED",
    "message":"Android companion device is not connected: android-companion-sample",
    "module":"companion.registry",
    "recoverable":true
  }
}
```

## 失败响应：能力不存在

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

## 失败响应：操作不支持

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

## 设计边界

1. Core 只负责把 IPC invoke 转换成 QUIC command request，不直接调用 Android API。
2. Android API 执行仍由 Android Companion `features` handler 完成。
3. 无 session 时必须返回 `COMPANION_SESSION_NOT_CONNECTED`，不能假装分发成功。
4. In-memory session 只用于测试协议闭环，不代表真实网络 QUIC transport。
5. 后续真实 QUIC transport 应优先实现 `CompanionCommandTransport`，再交给 `TransportCompanionCommandRouter` 复用 IPC → commandRequest 转换逻辑。

## 后续接入点

真实 QUIC transport 接入时应实现：

```rust
impl CompanionCommandTransport for QuicCompanionCommandTransport {
    fn send_command_envelope(
        &self,
        device_id: &str,
        envelope: &QuicEnvelope,
    ) -> Result<Value, AppError> {
        // 1. 查询 deviceId 对应 QUIC session；
        // 2. 写入 reliable control stream；
        // 3. 等待 commandResponse/error 或返回 dispatched；
        // 4. 将 transport 结果作为 structured JSON 返回。
    }
}
```
