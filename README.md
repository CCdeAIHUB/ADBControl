# ADBControl

ADBControl 是一个跨平台 ADB 后端核心项目。当前阶段落地后端核心、Android 伴侣 App 能力协议、Android 功能执行层、媒体能力执行层、Android 纯 QUIC transport 适配边界、Core Quinn 纯 QUIC server wrapper、Core Companion ingress、Core 设备配对/信任模型和 ADB 资产 packaging 流程，不实现前端 UI。

## 第一阶段目标

后端核心负责作为前端 IPC 管道与 ADB 之间的中间层：

- 前端通过结构化 IPC 请求调用核心；
- 核心根据当前系统和 CPU 架构选择内置 ADB；
- 核心只执行 ADB 二进制，不执行任意系统 shell；
- 核心将 ADB 的 stdout、stderr、exit code 以统一响应返回给前端；
- 官方未覆盖的 ADB 平台产物通过 GitHub Actions / 独立 ADB 源码镜像仓库产出后，再按 manifest 接入。

## 第二阶段目标：Android Companion App

Android 伴侣 App 是独立运行在 Android 设备上的能力提供端，不是跨平台前端。它通过纯 QUIC 与 Core 建立连接，并将 Android 权限能力转换成 Capability 提供给 Core。Core 再通过 IPC 将这些能力暴露给前端。

```text
Frontend
  ↓ IPC
Core
  ├─ ADB Provider
  └─ Android Companion Provider
       ↓ custom QUIC protocol
Android Companion App
       ↓ Android permission / service / sensor / media APIs
Android device
```

第二阶段能力范围：

- 输入法 / 输入相关能力；
- 投屏 / 屏幕采集；
- 文件读取、写入、传输；
- 相机；
- 录音 / 麦克风；
- 短信；
- 电话；
- 剪贴板读取 / 写入；
- 设备动作与方向传感器；
- 应用列表；
- 媒体音量控制；
- 后台弹出界面；
- 悬浮窗；
- 链式启动。

### Android Companion 当前已落地的功能性 handler

Android Companion App 已新增 `features` 执行层和 `AndroidFeatureDispatcher`。当前已经有具体执行代码的能力：

- `input.text` / `input.key`：通过 ADBControl Companion IME 向当前输入连接提交文本或按键；
- `stream.open` / `stream.close`：无授权时拉起 Android MediaProjection 授权；有授权后启动屏幕 H.264/MP4 编码会话并写入 sandbox；
- `screenshot.capture`：使用 MediaProjection + ImageReader + VirtualDisplay 抓取一帧并保存为 Companion App sandbox 内 PNG 文件；
- `camera.open` / `camera.close`：通过 Camera2 + MediaRecorder surface 录制 H.264/MP4 到 sandbox；
- `audio.record.start` / `audio.record.stop`：通过 AudioRecord 边录边写 PCM，并将 PCM chunk 推送到 `MediaStreamSink`；
- `clipboard.read` / `clipboard.write`：读取和写入文本剪贴板；
- `volume.get` / `volume.set`：读取和设置媒体音量；
- `app.list`：读取当前用户可见应用列表；
- `file.read` / `file.write`：读取和写入 Companion App sandbox 内的文本文件；
- `phone.call`：在权限允许后发起电话调用；
- `sms.read`：读取短信列表；
- `sms.send`：支持短信 compose 模式，`direct=true` 时走直接发送；
- `sensor.subscribe` / `sensor.unsubscribe`：注册/取消运动传感器监听，并保存最近样本；
- `ui.surface.show`：拉起 Companion 主界面；
- `overlay.show` / `overlay.hide`：显示/隐藏悬浮窗；
- `intent.chainLaunch`：按显式 package/class 链式启动 Activity。

### Android QUIC / Media transport

已新增：

- `NativeQuicTransport`：Android 侧纯 QUIC transport 适配边界，只接受 `quic://` endpoint；
- `NativeQuicEngine`：后续 JNI / native QUIC engine 必须实现的接口；
- `NativeQuicEngineProvider`：允许后续 JNI / NDK 实现向 Companion Service 注入真实 engine；
- `QuicMediaStreamSink`：把媒体 stream open/chunk/close 包装为 `STREAM_OPEN`、`STREAM_CHUNK`、`STREAM_CLOSE` envelope；
- `QuicCompanionService.connect(endpoint, deviceId)`：创建 native QUIC transport，发送 hello，并把音频 media sink 切换到 QUIC envelope sink；
- `SandboxFileMediaStreamSink`：本地文件 sink，用于无网络或测试环境下保留媒体数据。

仍明确未完成的部分：

- Android native QUIC engine 的 JNI / native 实现；
- 屏幕/相机实时 chunk 级编码输出 drain 到 QUIC media sink；
- 证书持久化文件和 UI 配对确认页面；
- 设备会话恢复。

这些部分已进入权限、session、router、handler、media sink、transport、trust 和 ingress 边界，后续应在不改变 IPC/QUIC 契约的前提下接入纯 QUIC native engine、媒体低延迟编码输出和持久化信任存储。

### Core Companion pairing / trust

已新增 `CompanionTrustStore`：

- `begin_pairing`：接收 `deviceId`、`deviceName`、证书 SHA-256 指纹，生成 6 位短码和 pairing id；
- `confirm_pairing`：只有短码匹配且未过期时，才把设备证书指纹加入信任记录；
- 错误短码会消耗尝试次数，次数耗尽后移除 pending pairing；
- 过期 pairing 会被移除并拒绝；
- `is_trusted`：同时校验 device id、证书指纹、过期时间、撤销状态；
- `revoke_device`：撤销信任，撤销后相同证书指纹也不能继续通过。

配对短码由 `rand` 生成，避免使用时间戳或计数器伪造随机数。

### Core Companion ingress / QUIC listener

已新增 `CompanionIngress`：

- 接收 Android Companion 发来的 JSON envelope bytes；
- 校验协议名、版本、messageId；
- 将 `hello`、`capabilityList`、`permissionState`、`heartbeat` 分发给 `CompanionSessionManager`；
- ACK `streamOpen`、`streamChunk`、`streamClose`，并避免 media chunk 污染 session state；
- 将非法 JSON、协议错误、状态错误转换为 QUIC `error` envelope。

已新增 Core 纯 QUIC 接入层：

- `CompanionQuicListener`：抽象 control/media bytes 接收入口；
- `IngressBackedQuicListener`：把 QUIC stream/datagram bytes 转入 `CompanionIngress`；
- `QuinnCompanionServer`：可选 `quinn-transport` feature 下的纯 QUIC server wrapper，绑定 `quinn::Endpoint`，接收 bidirectional stream 和 datagram，并转发到 `CompanionQuicListener`；
- Core CI 会额外执行 `cargo check -p adbcontrol-core --features quinn-transport`，确保 Quinn transport 代码可编译。

### Core Companion command router

Core 已新增 `CompanionCommandRouter` 抽象，并让 `device.invoke` 接入 router：

- 未连接 QUIC session 时返回 `COMPANION_SESSION_NOT_CONNECTED`；
- 已连接 session 时生成 `adbcontrol-companion-quic` 的 `commandRequest` envelope；
- 测试用 in-memory session 可以验证 IPC `device.invoke` 到 QUIC `commandRequest` 的转换；
- `CompanionSessionManager` 能处理 `hello`、`capabilityList`、`permissionState`、`heartbeat`；
- 真实网络 QUIC transport 后续只需要实现同一个 router trait。

详细说明见 `docs/protocols/companion-command-router.md`。

## ADB 资产 packaging

已新增：

- `scripts/adb/download-official-platform-tools.sh`：下载并提取官方 Platform-Tools ADB；
- `scripts/adb/verify-adb-manifest.py`：校验 manifest 声明的 ADB asset 是否存在及 SHA256；
- `.github/workflows/package-adb-assets.yml`：手动 packaging workflow，产出官方平台 ADB artifact。

官方 Platform-Tools 不覆盖所有目标平台。`windows-arm64`、`linux-arm64`、`android-x86_64`、`android-arm64` 仍需要由独立 ADB 源码构建仓库产出后接入 manifest。

## 技术路线

- 后端核心：Rust workspace；
- Android 伴侣 App：Kotlin / Android；
- IPC 协议：JSON Lines request/response；
- 第一阶段 IPC transport：stdio pipe，方便跨平台前端先以子进程方式集成；
- 后续 IPC transport：Windows Named Pipe、Unix Domain Socket；
- Companion 通讯：自定义 QUIC 应用层协议 `adbcontrol-companion-quic`，不使用 HTTP/3 作为主线；
- Core ingress：网络无关的 Companion envelope 接收入口；
- Core QUIC server：可选 `quinn-transport` feature，使用 Quinn 绑定纯 QUIC endpoint，将 stream/datagram 转给 ingress；
- Core trust：短码配对 + 证书指纹绑定 + 可撤销信任记录；
- 错误结构：统一 `AppError`，所有关键失败路径必须包含 `errorCode`、`module`、`recoverable`。

## 目录结构

```text
assets/adb/                  # ADB 资产 manifest 与后续二进制放置位置
crates/adbcontrol-core/       # 后端核心 crate
android/companion-app/        # Android 伴侣 App 工程与能力执行层
docs/adr/                     # 架构决策记录
docs/specs/                   # 行为 spec / TDD 场景说明
docs/protocols/               # IPC / QUIC 协议清单与示例
scripts/adb/                  # ADB 资产下载与校验脚本
.github/workflows/            # CI 与 ADB 资产构建工作流
```

## 开发命令

```bash
cargo fmt --all -- --check
cargo clippy --workspace --all-targets -- -D warnings
cargo check -p adbcontrol-core --features quinn-transport
cargo test --workspace
```

Android Companion App 目录：

```bash
cd android/companion-app
./gradlew :app:assembleDebug
```

当前仓库尚未提交 Gradle Wrapper，Android 构建需要本机已有 Gradle 或后续补充 wrapper。

## 运行核心

```bash
cargo run -p adbcontrol-core
```

示例请求：

```json
{"id":"1","method":"core.getHostTarget","params":{}}
```

示例 ADB 请求：

```json
{"id":"2","method":"adb.exec","params":{"args":["devices","-l"]}}
```

示例 Companion 协议查询：

```json
{"id":"3","method":"companion.protocol.info","params":{}}
```

示例能力目录查询：

```json
{"id":"4","method":"capability.list","params":{}}
```

## 协议文档

- IPC 协议列表：`docs/protocols/ipc-protocol-list.md`
- QUIC 协议列表：`docs/protocols/quic-protocol-list.md`
- Core Companion command router：`docs/protocols/companion-command-router.md`
- Core Companion ingress：`docs/protocols/companion-ingress.md`

以上文档逐项列出 method / message、请求示例、成功示例、失败示例和当前实现状态。

## 重要边界

- 不把前端逻辑写进核心；
- 不把 Android 系统 API 直接写进 Core；
- 不把平台差异写进业务层；
- 不提交真实 ADB 二进制到普通代码提交；
- 不复制完整 AOSP 源码到主仓库；
- 不在 Android Companion 权限缺失时继续执行敏感能力；
- 所有公共协议变更必须同步更新 spec、测试和文档。
