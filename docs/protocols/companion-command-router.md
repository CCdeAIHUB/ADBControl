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

## 无障碍触控参数

`android.accessibility.control` 的触控操作支持以下 `args`：

```json
{"operation":"accessibility.touch.tap","args":{"x":400,"y":640,"coordinateWidth":800,"coordinateHeight":1280}}
```

```json
{"operation":"accessibility.touch.swipe","args":{"startX":400,"startY":1000,"endX":400,"endY":300,"durationMs":250,"coordinateWidth":800,"coordinateHeight":1280}}
```

`coordinateWidth` 与 `coordinateHeight` 必须同时提供且大于零。提供时，坐标表示投屏编码帧空间，Android handler 会映射到当前物理屏幕；两个字段都省略时保留 v1 旧行为，坐标直接表示当前物理屏幕像素。字段不完整、尺寸无效或坐标越界时返回 `COMPANION_INVALID_ARGUMENT`。

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


## 应用元数据分页

`android.app.list/app.list` 支持以下可选参数：

```json
{
  "includeSystem": true,
  "includeIcons": true,
  "iconSizePx": 48,
  "offset": 0,
  "limit": 64,
  "packageNames": ["com.android.settings", "com.example.app"]
}
```

- `packageNames` 存在时使用显式查询模式，最多 64 项；省略时保留旧版枚举模式。
- `includeIcons=true` 时单页上限为 64，图标在 Android 端渲染为 PNG。
- `iconSizePx` 允许 32 至 96，桌面端默认 48。
- 响应保留 `apps/count/total/offset/limit`，并新增 `queryMode` 与 `unresolvedPackages`。
- 每个 app 可新增 `iconPngBase64`；渲染失败时返回 `iconErrorCode=APP_ICON_RENDER_FAILED`。
- 所有新增字段均为可选字段，旧桌面与旧伴侣端保持兼容。
## 设备锁屏状态

`android.device.power/device.state` 无需参数，返回 Android Framework 的当前状态：

```json
{
  "state": "locked",
  "isInteractive": true,
  "isKeyguardLocked": true,
  "isDeviceLocked": true
}
```

- `state` 仅允许 `locked` 或 `unlocked`。
- 屏幕不处于 interactive、Keyguard 已锁定或当前用户设备已锁定时，`state=locked`。
- 该操作不申请新权限，也不会唤醒或解锁设备。
- Companion `0.13.0` 起支持；旧版本不支持时桌面端必须有界回退 ADB，不得把失败当作已解锁。
