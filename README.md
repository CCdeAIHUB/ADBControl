# ADBControl

ADBControl 是一个包含 WinUI 桌面客户端、跨平台 ADB 后端核心和 Android 伴侣 App 的设备管理项目。当前阶段已落地设备管理前端、ADB 与 scrcpy 投屏接入、Android 能力协议、纯 QUIC transport 适配边界、Core Companion ingress、设备配对/信任模型和 ADB 资产 packaging 流程。

## scrcpy 投屏集成

设备详情页使用双投屏后端。ADB 可用时，ADBControl 从 `third_party/scrcpy/` 直接编译 **scrcpy 4.0** Android 服务端源码，并把服务端 class 打包进伴侣 APK；桌面端以已安装 APK 的 `base.apk` 作为 CLASSPATH，通过 `app_process` 启动服务，因此不需要 MediaProjection 弹窗。伴侣 APK 不存在时才回退到临时推送的独立 server 构建产物。

ADB 不可用而伴侣 App 的 QUIC 会话可用时，手机端通过 Android MediaProjection 获取画面并明确显示系统授权弹窗，使用 MediaCodec 编码 H.264，再通过原生 QUIC 视频流发送到桌面端；控制操作通过无障碍服务执行。桌面端两条路径共用 FFmpeg/libavcodec 解码和 Direct3D 交换链渲染，并且只有新后端首帧真正呈现后才隐藏原截图预览。项目不会启动外部 scrcpy 窗口，也不要求用户单独安装 scrcpy。

引入版本为 `v4.0`，上游提交 `2322868e9e256eb5fce0b3d659ab2a409f29bae1`。scrcpy 代码遵循 Apache License 2.0，完整许可证及上游信息见 `third_party/scrcpy/LICENSE` 和 `third_party/scrcpy/UPSTREAM.md`。

桌面解码使用 MIT 许可的 `FFmpeg.AutoGen 7.1.1` 绑定和 `Sdcb.FFmpeg.runtime.windows-x64 7.1.0` 原生运行库。当前运行库 NuGet 包声明为 GPL-3.0-only，分发桌面程序时必须同时遵守该许可证；版本、来源和分发提示见 `third_party/ffmpeg/NOTICE.md`。

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
- `accessibility.status` / `accessibility.global.*` / `accessibility.touch.tap` / `accessibility.touch.swipe`：通过无障碍辅助读取启用状态、执行全局动作和坐标触控手势；
- `intent.chainLaunch`：按显式 package/class 链式启动 Activity。

### Android QUIC / Media transport

当前投屏链路已包含：

- `crates/adbcontrol-quic-android`：基于 Quinn、rustls 和 Tokio 的 Android JNI 原生 QUIC 客户端，构建 `arm64-v8a` 与 `x86_64` 两种 ABI；
- `NativeQuicTransport` / `JniNativeQuicEngine`：连接 `quic://` endpoint，按桌面证书 DER 精确校验 TLS 身份，并承载控制双向流和 H.264 单向流；
- `QuicCompanionService`：前台保活、连接配置持久化、自动重连、hello/helloAck、能力清单和命令请求响应；
- `CompanionProjectionService` / `CompanionScreenEncoder`：MediaProjection 授权、MediaCodec H.264 低延迟编码、关键帧恢复和有界发送队列；
- `CompanionQuicServer`：WinUI 进程内的 System.Net.Quic 服务端，管理设备会话、命令响应、视频流和连接日志；
- `ProjectionSession`：ADB/scrcpy 优先，连接不可用或启动失败时切换到伴侣 App 后端；停止投屏只结束视频会话，不关闭伴侣 App 的 QUIC 控制连接。

控制流和视频流的字节级约定见 `docs/protocols/companion-quic-wire.md`。首次配置会保存桌面端地址和证书；之后用户重新打开伴侣 App 时可使用已保存配置恢复连接，不要求 ADB 持续在线。

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
third_party/scrcpy/            # 引入的 scrcpy 4.0 Android 服务端源码与许可证
third_party/ffmpeg/            # FFmpeg 绑定/运行库版本与许可证告知
client/ADBControl.Desktop/     # WinUI 桌面端及 scrcpy socket/渲染集成
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

构建引入的 scrcpy 服务端源码：

```powershell
.\scripts\build-scrcpy-server.ps1
```

Android 伴侣 App 的常规构建会把 scrcpy server class 直接合并进 APK。上述脚本生成的独立 `server-release-unsigned.apk` 仅作为未安装伴侣 App 时的兼容回退产物；两种方式都不依赖外部 scrcpy 桌面可执行文件。

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
- scrcpy 上游源码、版本和许可证变更必须同步更新 `third_party/scrcpy/UPSTREAM.md`；
- 不在 Android Companion 权限缺失时继续执行敏感能力；
- 所有公共协议变更必须同步更新 spec、测试和文档。
