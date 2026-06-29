# Spec: ADB 后端核心 IPC 第一阶段与 Companion 能力扩展

## 目标

本 spec 锁定 ADBControl 后端核心行为。核心作为前端与 ADB / Android Companion 之间的中间层，通过 IPC 接收前端请求，解析当前平台 ADB 资产，执行 ADB 命令，并把 Android Companion 能力以统一 Capability 方式提供给前端。

## 非目标

- 不实现前端 UI；
- 不提交真实 ADB 二进制；
- 不把完整 AOSP 源码复制进主仓库；
- 不允许执行任意系统 shell 命令；
- 不在 Core 中直接调用 Android 系统 API；
- 不在 Core 中实现 Android 媒体编码器；
- 不在 QUIC session 未连接时假装 Android Companion 命令已执行成功。

## IPC 请求格式

```json
{
  "id": "1",
  "method": "adb.exec",
  "params": {
    "args": ["devices", "-l"]
  }
}
```

## IPC 响应格式

成功：

```json
{
  "id": "1",
  "ok": true,
  "result": {
    "exitCode": 0,
    "stdout": "...",
    "stderr": "..."
  }
}
```

失败：

```json
{
  "id": "1",
  "ok": false,
  "error": {
    "errorCode": "IPC_METHOD_UNKNOWN",
    "message": "Unknown IPC method: ...",
    "module": "ipc.protocol",
    "recoverable": false
  }
}
```

## 已实现 method

### `core.getHostTarget`

返回当前核心识别到的平台 target。

### `adb.asset.current`

返回当前平台将使用的 ADB 资产声明。

### `adb.exec`

参数：

```json
{
  "args": ["devices", "-l"]
}
```

核心行为：

1. 解析当前 OS / CPU 架构；
2. 从 `assets/adb/manifest.json` 查找对应 ADB 资产；
3. 校验 args 不能包含 NUL 字节；
4. 只启动 manifest 指向的 ADB 二进制；
5. 使用参数数组传参，不拼接 shell 字符串；
6. 返回 ADB 的 `exitCode`、`stdout`、`stderr`。

### `companion.protocol.info`

返回 Core 支持的 Android Companion QUIC 应用层协议描述，包括协议名、版本、通道和 message kind。

### `capability.list`

返回 Core 已知的 Android Companion 能力目录。该接口不要求设备已连接，用于前端提前展示能力说明和权限说明。

### `device.list`

返回 Core 当前注册的 Android Companion 设备。真实 QUIC transport 接入后，设备列表应由 `CompanionSessionManager` 与 registry 同步。

### `device.getCapabilities`

参数：

```json
{
  "deviceId": "android-companion-sample"
}
```

返回指定 Android Companion 设备实际暴露的能力列表。若设备不存在，必须返回 `COMPANION_DEVICE_NOT_CONNECTED`，不能返回空能力误导前端。

### `device.getPermissionState`

参数：

```json
{
  "deviceId": "android-companion-sample"
}
```

返回指定设备每个能力对应的权限状态、缺失运行时权限、缺失特殊授权、是否需要用户同意。

### `device.invoke`

参数：

```json
{
  "deviceId": "android-companion-sample",
  "capabilityId": "android.volume.media",
  "operation": "volume.set",
  "args": {
    "level": 5
  }
}
```

核心行为：

1. 校验 `deviceId`、`capabilityId`、`operation` 必填；
2. 校验设备已连接或已注册；
3. 校验设备暴露该 capability；
4. 校验 capability 支持该 operation；
5. 校验 `args` 必须是 JSON object；
6. 调用 `CompanionCommandRouter`；
7. 若 session 未连接，返回 `COMPANION_SESSION_NOT_CONNECTED`；
8. 若 session 已连接，生成 `adbcontrol-companion-quic` 的 `commandRequest` envelope。

## Core Companion Session

Core 已实现 `CompanionSessionManager`：

- `hello`：创建 `Handshaking` session，并返回 `helloAck`；
- `capabilityList`：同步设备能力；
- `permissionState`：同步权限状态；
- `heartbeat`：检查 session 并返回 heartbeat ack；
- 当能力与权限都已同步后，session 进入 `Ready`。

## Android Companion 功能执行层

Android Companion 已实现 `AndroidFeatureDispatcher` 与以下 handler：

- 输入法：`input.text`、`input.key`；
- 屏幕采集授权：`stream.open`、`stream.close`、`screenshot.capture` 状态检查；
- 相机：`camera.open`、`camera.close`；
- 录音：`audio.record.start`、`audio.record.stop`；
- 剪贴板：`clipboard.read`、`clipboard.write`；
- 音量：`volume.get`、`volume.set`；
- 应用列表：`app.list`；
- App sandbox 文件：`file.read`、`file.write`；
- 电话 / 短信：`phone.call`、`sms.read`、`sms.send`；
- 传感器：`sensor.subscribe`、`sensor.unsubscribe`；
- UI：`ui.surface.show`、`overlay.show`、`overlay.hide`、`intent.chainLaunch`。

## Android Companion QUIC 协议

- 应用层协议名：`adbcontrol-companion-quic`；
- 当前版本：`1`；
- 通道：`control`、`data`、`media`；
- message kind：`hello`、`helloAck`、`heartbeat`、`capabilityList`、`permissionState`、`commandRequest`、`commandResponse`、`streamOpen`、`streamChunk`、`streamClose`、`error`。

详细协议列表见：

- `docs/protocols/ipc-protocol-list.md`
- `docs/protocols/quic-protocol-list.md`
- `docs/protocols/companion-command-router.md`

## TDD 场景

以下场景已沉淀为测试：

- 场景：Linux `aarch64` 必须映射为 `linux-arm64`；
- 场景：Linux arm64 没有官方 Platform-Tools 产物时，manifest 必须声明为自建 ADB；
- 场景：manifest 缺失当前 target 时，必须返回 `ADB_ASSET_NOT_FOUND`，不能静默降级；
- 场景：非法 JSON 必须返回 `IPC_INVALID_JSON`；
- 场景：未知 method 必须返回 `IPC_METHOD_UNKNOWN`；
- 场景：`adb.exec` 必须保持参数数组，不拼接 shell 字符串；
- 场景：`adb.exec` 的 `args` 类型错误必须返回 `IPC_PARAMS_INVALID`；
- 场景：args 包含 NUL 字节必须返回 `ADB_ARGS_CONTAIN_NUL`；
- 场景：Android Companion 能力目录必须标注高敏感能力、权限和用户同意要求；
- 场景：QUIC hello envelope 必须校验协议名、版本和 messageId；
- 场景：QUIC 协议名不匹配必须返回 `COMPANION_PROTOCOL_MISMATCH`；
- 场景：未连接设备查询能力必须返回 `COMPANION_DEVICE_NOT_CONNECTED`；
- 场景：查询设备权限状态必须返回每个能力的权限矩阵；
- 场景：设备已注册但 session 未连接时 `device.invoke` 必须返回 `COMPANION_SESSION_NOT_CONNECTED`；
- 场景：session 已连接时 `device.invoke` 必须生成 QUIC `commandRequest` envelope；
- 场景：Companion hello 创建 handshaking session；
- 场景：capabilityList / permissionState 不能在 hello 之前污染 session；
- 场景：capabilityList + permissionState 同步后 session 进入 ready。

## 后续阶段

- 增加 Windows Named Pipe transport；
- 增加 Unix Domain Socket transport；
- 将真实网络 QUIC socket 接到 `CompanionCommandRouter` 与 `CompanionSessionManager`；
- 增加独立 AOSP ADB 源码镜像仓库与 GitHub Actions 编译产物同步；
- 增加设备配对、证书、信任、会话恢复；
- 增加屏幕帧 ImageReader/encoder surface；
- 增加相机预览/编码 surface；
- 增加音频实时 QUIC media stream；
- 增加 Android 单元测试和 instrumentation 测试。
