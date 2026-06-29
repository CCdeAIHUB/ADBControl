# ADBControl

ADBControl 是一个跨平台 ADB 后端核心项目。当前阶段只落地后端核心，不实现前端 UI。

## 第一阶段目标

后端核心负责作为前端 IPC 管道与 ADB 之间的中间层：

- 前端通过结构化 IPC 请求调用核心；
- 核心根据当前系统和 CPU 架构选择内置 ADB；
- 核心只执行 ADB 二进制，不执行任意系统 shell；
- 核心将 ADB 的 stdout、stderr、exit code 以统一响应返回给前端；
- 官方未覆盖的 ADB 平台产物通过 GitHub Actions / 独立 ADB 源码镜像仓库产出后，再按 manifest 接入。

## 技术路线

- 后端核心：Rust workspace；
- IPC 协议：JSON Lines request/response；
- 第一阶段 transport：stdio pipe，方便跨平台前端先以子进程方式集成；
- 后续 transport：Windows Named Pipe、Unix Domain Socket；
- 错误结构：统一 `AppError`，所有关键失败路径必须包含 `errorCode`、`module`、`recoverable`。

## 目录结构

```text
assets/adb/                  # ADB 资产 manifest 与后续二进制放置位置
crates/adbcontrol-core/       # 后端核心 crate
docs/adr/                     # 架构决策记录
docs/specs/                   # 行为 spec / TDD 场景说明
.github/workflows/            # CI 与 ADB 资产构建工作流
```

## 开发命令

```bash
cargo fmt --all -- --check
cargo clippy --workspace --all-targets -- -D warnings
cargo test --workspace
```

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

## 重要边界

- 不把前端逻辑写进核心；
- 不把平台差异写进业务层；
- 不提交真实 ADB 二进制到普通代码提交；
- 不复制完整 AOSP 源码到主仓库；
- 所有公共协议变更必须同步更新 spec、测试和文档。
