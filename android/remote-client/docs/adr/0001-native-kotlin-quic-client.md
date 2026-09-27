# ADR: 原生 Kotlin 安卓远程端与 Rust QUIC 适配层

## 背景

安卓远程端需要控制远端 ADBControl Core，但不能携带 ADB 核心。Android SDK 没有与
Core 当前自定义 QUIC 协议兼容的稳定标准客户端；仓库已有 Quinn/rustls 的生产使用经验。

## 决策

1. UI、状态机、权限导航、业务仓库均使用 Kotlin 与 Android 原生 View；
2. 仅 QUIC/TLS 传输使用小型 Rust `cdylib`，通过 JNI 暴露连接、请求、指纹与关闭；
3. 每个业务请求独占一个 QUIC 双向 stream，严格匹配 `adbcontrol-core-remote-quic/1`；
4. 客户端只信任用户导入的 Core DER 证书，并可再次核对 SHA-256 指纹；
5. 会话令牌仅驻留内存，Core 地址和公钥证书可持久化且禁止系统备份；
6. UI 不直接调用 JNI，业务层不依赖具体 QUIC 实现。

## 原因

- 保持安卓交互与生命周期由原生 Kotlin 管理；
- 复用 Core 同栈的 Quinn/rustls，避免把自定义 QUIC 错当 HTTP/3；
- 传输、协议、业务和页面各自可测试、可替换；
- 不引入 ADB 可执行文件或 Android USB 调试能力。

## 替代方案

- Cronet：主要面向 HTTP/3，不能直接匹配当前应用层 framing；
- 把 ADB 编译进 APK：违反远程客户端的产品边界；
- 使用明文 TCP/WebSocket：破坏 Core 已确定的 TLS 1.3 与证书固定安全边界。

## 影响范围

新增 `android/remote-client` 与一个 workspace Rust crate；不改桌面端、Companion 或 Core
公共协议。

## 风险

- APK 目前仅包含 arm64-v8a 与 x86_64；
- 实时视频、文件二进制流等必须等待 Core 定义新的 QUIC stream 类型；
- 当前 Core 的内置管理员仅允许本机登录，远程账号管理不可达。

## 未来演进

Core 扩充远程媒体、文件、自动化、AI、日志与设置契约后，在 data 层新增 repository
方法并替换对应不可用页；现有导航、会话、安全与传输层保持不变。
