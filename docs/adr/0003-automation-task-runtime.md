# ADR: 桌面自动化任务运行时与 JSON DSL

## 背景

ADBControl 需要在当前桌面应用内提供完整的自动化任务能力：按时间或 Android 设备状态触发，执行 ADB 与 Android Companion 能力，允许 AI 创建任务且允许任务调用 AI，并在 UI 中展示运行状态、步骤、进度和控制操作。

桌面端当前使用 .NET 8 / WinUI 3，ADB 与 Companion 已由 C# 服务接入，SQLite 依赖也已存在。额外引入 Node.js 作为常驻运行时会产生第二套生命周期、打包、权限与进程隔离边界，并且无法复用现有 C# 设备服务。

## 决策

1. 自动化任务的持久化脚本格式使用版本化 JSON DSL，运行时使用 C#；
2. JSON DSL 只允许调用注册过的触发器、条件和动作，不允许动态加载任意本机代码；
3. 原始 ADB shell 作为显式授权的 `adb.shell` 动作提供，高风险动作需要任务权限声明；
4. Android Companion 通过 `companion.call` 动作和 Companion 条件探针接入；
5. 任务定义、运行、步骤、日志和触发器状态存入 SQLite；
6. 调度器由桌面应用生命周期管理，任务并发策略支持跳过、排队、重启和并行；
7. 运行状态使用显式状态机：`queued -> running <-> paused -> succeeded|failed|stopped`，条件不满足或并发跳过使用 `skipped`；
8. AI 通过受控工具创建、修改、查询、运行、启停和删除任务；任务通过 `ai.prompt` 调用当前配置的 AI；
9. UI 只通过 `AutomationTaskService` 操作任务，不直接访问 SQLite、ADB、Companion 或调度器。

## 原因

- JSON DSL 便于 AI 生成、Schema 校验、权限审计、持久化和可视化编辑；
- C# 运行时与现有 WinUI、ADB、Companion 和 SQLite 服务共享类型、取消令牌及生命周期；
- 白名单动作和显式权限比任意脚本进程更容易暂停、停止、计算进度和记录步骤；
- 触发器状态持久化可以避免应用重启后重复执行固定时间任务。

## 替代方案

### TypeScript + Node.js 子进程

不采用。它会要求应用额外分发 Node.js 与 TypeScript 编译器，并建立第二套设备 API、取消、日志、权限和进程恢复协议。高级逻辑由 JSON DSL 的条件、分支、循环、并行、等待和原始 ADB 动作覆盖。

### 让 UI 定时器直接执行命令

不采用。UI 生命周期和页面切换不能成为调度与运行状态的事实来源。

### 把任务调度写入 Rust Core

不采用。当前任务需要调用桌面 AI 配置和 WinUI 运行管理；Rust Core 继续保持 ADB/Companion IPC 边界，不承载桌面产品状态。

## 影响范围

- 新增桌面自动化模型、仓储、调度、条件、动作、状态机、AI 工具和任务页面；
- 现有 AI 系统提示和工具清单增加任务工具；
- `MainWindow` 增加任务服务启动、停止和 AI 输出接收；
- 不修改现有 Rust IPC 和 Companion 协议。

## 风险

- Android 厂商 ROM 的 `dumpsys` 输出存在差异，条件探针必须保留原始输出与明确失败；
- 网页 URL、通知和剪贴板受 Android 版本、应用可访问性与权限限制，无法读取时必须返回未匹配或可见错误，不能猜测；
- 桌面应用未运行时调度器不会执行，应用重新启动后按每个触发器的 `catchUp` 规则处理错过的时间点；
- 原始 ADB shell 和自动解锁包含高风险能力，只能在任务权限已声明时执行。

## 完整交付边界

本 ADR 描述的触发、执行、AI 闭环、运行控制、持久化、UI 与验证均属于同一次交付，不使用第一版、后续版本或占位实现划分范围。
