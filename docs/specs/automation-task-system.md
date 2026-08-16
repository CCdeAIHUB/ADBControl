# 自动化任务系统行为规范

## 验收清单

1. 自动执行与触发
   - 任务可调用 ADB shell、触控、按键、应用/网页/Intent、设备唤醒/锁定/解锁和 Android Companion 能力。
   - 时间触发支持手动、单次、每天、每周多选、固定间隔和五段 Cron，并支持时区与错过执行策略。
   - 条件触发支持 AND/OR、取反、大小写策略、边沿触发、轮询间隔、冷却时间，以及时间触发附加条件。
2. AI 双向调用
   - AI 可创建、修改、查询、运行、启停和删除任务。
   - 任务可执行 `ai.prompt`，结果进入运行日志和桌面 AI 会话。
3. 运行列表与控制
   - 页面展示所有任务、启停状态、触发摘要、下次执行、当前/最近运行、当前步骤、进度和错误。
   - 用户可手动运行、暂停、继续、停止、编辑和删除任务。
   - 编辑器必须明确展示完整的“任务定义 JSON”，AI 创建的触发器、权限和动作不得折叠、替换为摘要或丢失。
   - JSON 编辑区必须支持选择、复制、横向和纵向滚动，并在保存前使用同一序列化器与校验器验证。

## JSON DSL

任务根对象包含：

- `schemaVersion`：当前为 `1`；
- `id`、`name`、`description`、`deviceId`；
- `enabled`；
- `concurrencyPolicy`：`skip`、`queue`、`restart`、`parallel`；
- `permissions`：`allowAdb`、`allowShell`、`allowCompanion`、`allowAi`、`allowAiDeviceTools`、`allowTaskMutation`、`allowUnlock`；
- `triggers`；
- `actions`。

设备条件与设备动作都必须声明 `allowAdb`；`adb.output` 还必须声明 `allowShell`；剪贴板与 `companion.*` 条件必须声明 `allowCompanion`。校验不通过的定义不会进入任务库。

## 触发器目录

- `manual`：仅手动或 AI 工具运行；
- `once`：`runAt` 指定绝对时间；
- `daily`：`at` 使用 `HH:mm[:ss]`；
- `weekly`：`days` + `at`；
- `interval`：`intervalSeconds`；
- `cron`：`cron` 使用五段 minute/hour/day/month/day-of-week；
- `condition`：按 `pollIntervalSeconds` 评估条件。

所有非手动触发器可包含 `conditions` 和 `conditionMode`。条件触发器还支持 `edgeOnly`、`cooldownSeconds`；时间触发器支持 `catchUp`。每个触发器使用独立 `id` 持久化上次执行、上次条件值和上次评估时间。

## Android 条件目录

- 连接与设备：`device.connected`、`device.authorized`、`device.property`；
- 屏幕与交互：`screen.on`、`screen.locked`、`screen.orientation`、`ui.element`、`ui.text`；
- 应用与页面：`app.foreground`、`activity.foreground`、`app.installed`、`process.running`、`webpage.open`；
- 通知与通信：`notification.present`、`call.state`、`headset.connected`；
- 电源：`battery.level`、`battery.charging`、`battery.temperature`；
- 网络：`network.connected`、`network.type`、`wifi.ssid`、`internet.reachable`；
- 系统开关：`bluetooth.enabled`、`airplane.enabled`、`location.enabled`、`dnd.enabled`、`setting.value`；
- 数据与文件：`file.exists`、`clipboard.contains`；
- Companion：`companion.installed`、`companion.accessibility.ready`、`companion.output`；
- 通用：`adb.output`、`time.window`。

条件比较器支持 `equals`、`notEquals`、`contains`、`notContains`、`startsWith`、`endsWith`、`regex`、`greaterThan`、`greaterThanOrEqual`、`lessThan`、`lessThanOrEqual`、`in`、`exists` 和 `truthy`。

网页 URL、UI 元素和文本从当前 `uiautomator`/Accessibility 可访问树匹配。Android 或目标 App 不公开对应信息时，条件必须记录不可读原因，不得根据历史页面推断为真。

## 动作目录

- ADB：`adb.shell`、`adb.keyEvent`、`adb.tap`、`adb.swipe`、`adb.text`；
- Android：`app.start`、`url.open`、`intent.start`、`device.wake`、`device.lock`、`device.unlock`；
- Companion：`companion.call`；
- AI：`ai.prompt`；
- 流程：`flow.if`、`flow.repeat`、`flow.parallel`、`condition.wait`、`delay`、`log`、`fail`。

`flow.if` 使用 `conditions`、`conditionMode`、`actions` 和 `elseActions`；`flow.repeat` 使用 `count` 与 `actions`；`flow.parallel` 并发执行 `actions`。`condition.wait` 在超时前按指定周期等待条件满足。所有延时和等待都必须响应暂停与停止。

## 状态机与并发

- 新运行先持久化为 `queued`；
- 获得任务并发槽后进入 `running`；
- `running` 或等待中的运行可进入 `paused`，继续后回到 `running`；
- 用户停止进入 `stopped`；
- 全部动作完成进入 `succeeded`；
- 动作、条件、AI、ADB、Companion 或持久化失败进入 `failed`，保留错误码、消息、模块、可恢复性、建议和 traceId；
- 条件不满足、错过且不补跑、或 `skip` 并发策略拒绝的运行记录为 `skipped`；
- 应用异常退出后遗留的 `queued`、`running`、`paused` 运行在下次启动时标记为 `failed/APP_RESTART_INTERRUPTED`。

## 验证方式

- 单元/集成测试：DSL 校验、每天/每周/间隔/Cron、条件比较、SQLite 恢复、运行状态流转、暂停/停止、步骤进度、AI 创建与任务调用 AI；
- 构建：桌面端 Debug 构建、Android Companion Debug APK 构建；
- UI：实际打开任务页，创建任务，运行并观察进度，暂停/继续/停止，编辑、启停和删除；
- 设备：有可用 Android 设备时运行 ADB/Companion 条件与动作集成验证；没有设备时保持为未验证，不以模拟结果冒充通过。
