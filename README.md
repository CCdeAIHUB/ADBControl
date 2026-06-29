# ADBControl

ADBControl 是一个跨平台 ADB 后端核心项目。当前阶段只落地后端核心与 Android 伴侣 App 能力协议，不实现前端 UI。

## 第一阶段目标

后端核心负责作为前端 IPC 管道与 ADB 之间的中间层：

- 前端通过结构化 IPC 请求调用核心；
- 核心根据当前系统和 CPU 架构选择内置 ADB；
- 核心只执行 ADB 二进制，不执行任意系统 shell；
- 核心将 ADB 的 stdout、stderr、exit code 以统一响应返回给前端；
- 官方未覆盖的 ADB 平台产物通过 GitHub Actions / 独立 ADB 源码镜像仓库产出后，再按 manifest 接入。

## 第二阶段目标：Android Companion App

Android 伴侣 App 是独立运行在 Android 设备上的能力提供端，不是跨平台前端。它通过 QUIC 与 Core 建立连接，并将 Android 权限能力转换成 Capability 提供给 Core。Core 再通过 IPC 将这些能力暴露给前端。

```text
Frontend
  ↓ IPC
Core
  ├─ ADB Provider
  └─ Android Companion Provider
       ↓ QUIC
Android Companion App
       ↓ Android permission / service / sensor / media APIs
Android device
```

第二阶段已预留能力范围：

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

## 技术路线

- 后端核心：Rust workspace；
- Android 伴侣 App：Kotlin / Android；
- IPC 协议：JSON Lines request/response；
- 第一阶段 IPC transport：stdio pipe，方便跨平台前端先以子进程方式集成；
- 后续 IPC transport：Windows Named Pipe、Unix Domain Socket；
- Companion 通讯：QUIC 应用层协议 `adbcontrol-companion-quic`；
- 错误结构：统一 `AppError`，所有关键失败路径必须包含 `errorCode`、`module`、`recoverable`。

## 目录结构

```text
assets/adb/                  # ADB 资产 manifest 与后续二进制放置位置
crates/adbcontrol-core/       # 后端核心 crate
android/companion-app/        # Android 伴侣 App 工程骨架
docs/adr/                     # 架构决策记录
docs/specs/                   # 行为 spec / TDD 场景说明
docs/protocols/               # IPC / QUIC 协议清单与示例
.github/workflows/            # CI 与 ADB 资产构建工作流
```

## 开发命令

```bash
cargo fmt --all -- --check
cargo clippy --workspace --all-targets -- -D warnings
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

以上文档逐项列出 method / message、请求示例、成功示例、失败示例和当前实现状态。

## 重要边界

- 不把前端逻辑写进核心；
- 不把 Android 系统 API 直接写进 Core；
- 不把平台差异写进业务层；
- 不提交真实 ADB 二进制到普通代码提交；
- 不复制完整 AOSP 源码到主仓库；
- 不在 Android Companion 权限缺失时继续执行敏感能力；
- 所有公共协议变更必须同步更新 spec、测试和文档。
