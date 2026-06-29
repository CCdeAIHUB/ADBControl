# ADR: Rust 后端核心与 ADB IPC 中间层

## 背景

ADBControl 需要一个后端核心作为前端与 ADB 之间的中间层。前端跨平台，后端需要在 Windows、macOS、Linux、Android 以及 x86_64 / arm64 目标上工作。官方 Android SDK Platform-Tools 并不覆盖所有目标组合，因此项目必须支持官方产物和自建 GitHub Actions 产物并存。

## 决策

1. 后端核心使用 Rust workspace 实现；
2. 第一阶段 IPC 使用 JSON Lines over stdio pipe；
3. 核心协议使用 `IpcRequest` / `IpcResponse`；
4. 错误统一为 `AppError`；
5. ADB 资产通过 `assets/adb/manifest.json` 声明；
6. `adb.exec` 只允许启动 manifest 指向的 ADB 二进制；
7. `adb.exec` 只接收 `args: string[]`，不接受单段命令文本；
8. Windows Named Pipe 与 Unix Domain Socket 在后续阶段作为 transport 扩展，不进入业务层。

## 原因

- Rust 适合长期维护的跨平台核心，便于构建单二进制后端；
- stdio pipe 是最小跨平台 IPC 方案，前端可以先以子进程方式稳定集成；
- JSON Lines 便于前端调试，也能稳定分帧；
- ADB 命令使用参数数组可以减少平台差异和解析歧义；
- manifest 让官方产物和自建产物的来源清晰可审计。

## 替代方案

### 直接让前端调用 ADB

拒绝。这样会把平台差异、二进制选择、错误处理和安全边界扩散到每个前端。

### 第一阶段直接做 Windows Named Pipe / Unix Domain Socket

暂缓。命名管道与 Unix Domain Socket 的平台差异较大，先保持 transport 抽象，避免把平台 IPC 细节和 ADB 业务逻辑耦合。

### 直接内置完整 AOSP 源码

拒绝。AOSP 源码体积和构建复杂度较高，应该在独立 ADB 源码镜像仓库维护，并通过 GitHub Actions 发布二进制产物，再同步到主仓库 release 或构建资产中。

## 影响范围

- 新增 Rust workspace；
- 新增核心 IPC 协议；
- 新增 ADB manifest；
- 新增 GitHub Actions CI；
- 后续前端需要按 JSON Lines 协议对接核心。

## 风险

- 第一阶段 stdio pipe 不是最终命名管道方案；
- 当前没有提交真实 ADB 二进制，运行 `adb.exec` 前需要打包阶段放置对应资产；
- 自建 ADB 编译流程需要后续独立仓库和 CI 继续细化。

## 未来演进

1. 增加 named pipe / Unix socket transport；
2. 增加 ADB asset checksum 强校验；
3. 增加 ADB 版本查询与健康检查；
4. 增加高级领域 API，而不是永远只暴露 `adb.exec`；
5. 将自建 ADB 产物从独立仓库通过 release artifact 同步到主仓库打包流程。
