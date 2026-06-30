# ADR: Android Companion App 与 QUIC Capability 协议

## 背景

ADBControl 除了 ADB Provider 之外，还需要一个独立 Android 伴侣 App。该 App 不是跨平台前端，而是运行在 Android 设备上的能力提供端。它负责申请和管理 Android 本机权限，并通过 QUIC 将输入法、投屏、文件、相机、录音、短信、电话、剪贴板、传感器、应用列表、音量、悬浮窗、后台弹窗、链式启动等能力提供给 Core。Core 再通过 IPC 将这些能力提供给前端。

## 决策

1. Android Companion App 独立放置在 `android/companion-app/`；
2. Core 新增 `capability` 模块描述能力、权限、敏感等级和默认传输类型；
3. Core 新增 `companion` 模块描述 Android Companion 设备注册表和 QUIC 应用层 envelope；
4. Core IPC 新增 `companion.protocol.info`、`capability.list`、`device.list`、`device.getCapabilities`、`device.getPermissionState`、`device.invoke`；
5. Android Companion 与 Core 的应用层协议固定为 `adbcontrol-companion-quic`，当前版本为 `1`；
6. QUIC 通道分为 `control`、`data`、`media`；
7. 本阶段只落地协议模型、能力目录、权限网关和 command router 边界，不引入真实 QUIC 实现依赖；
8. 敏感能力必须先经过 Android 端 PermissionGuard 和 Core 端权限状态同步，不能直接执行。

## 原因

- Android 权限能力非常敏感，必须用 Capability + Permission + Audit 的方式建模；
- Core 需要作为中间件隔离前端和 Android 权限细节；
- QUIC 真实实现依赖较重，且 Rust / Android 两端选型需要独立验证，因此先锁定应用层协议和模块边界；
- IPC / QUIC 协议文档先行，可以让后续前端、Core transport、Android 能力实现并行开发；
- 保持 `adb.exec` 兼容，避免第一阶段能力被破坏。

## 替代方案

### 让前端直接连接 Android Companion

拒绝。这样会把权限状态、能力路由、连接恢复、审计日志、错误码处理分散到每个前端。

### 把 Android 能力塞进 `adb.exec`

拒绝。ADB 与 Android Companion 是两个 provider，能力来源、权限状态和传输路径完全不同，必须通过 Capability Router 统一。

### 本阶段直接引入完整 QUIC 实现

暂缓。真实 QUIC transport 需要同时验证 Rust Core 和 Android App 的库选择、证书/配对、连接恢复、包体积、安全维护。本阶段先锁定协议和边界，避免重依赖误选。

## 影响范围

- Core 新增 `capability` 和 `companion` 模块；
- IPC 协议新增 Companion / Device / Capability 方法；
- Android Companion App 新增工程骨架；
- 文档新增 `docs/protocols/ipc-protocol-list.md` 与 `docs/protocols/quic-protocol-list.md`；
- 后续前端应通过 IPC 统一访问 ADB 与 Android Companion 能力。

## 风险

- 当前 `device.invoke` 已能构建 `commandRequest` envelope；真实 QUIC session 未连接时会返回 `COMPANION_SESSION_NOT_CONNECTED`；
- Android Companion 工程当前没有纳入 GitHub Actions 编译；
- Manifest 声明的高敏感权限需要后续按功能逐一申请和解释，不能一次性强迫用户授权；
- 部分权限如短信、电话、悬浮窗、后台启动、输入法服务受 Android 系统版本、厂商策略和应用商店政策限制。

## 未来演进

1. 选择并接入 Rust Core QUIC transport；
2. 选择并接入 Android QUIC transport；
3. 增加配对、证书、设备信任和会话恢复；
4. 增加 Capability Router，将 ADB Provider 与 Android Companion Provider 统一路由；
5. 为每个高敏感能力补真实 Android API 实现和测试；
6. 增加 Android Companion CI；
7. 增加审计日志、traceId 和权限撤销同步。
