# AI 对话交互、错误与输入路由规格

## 背景

2026-08-15 的本机历史对话中连续出现三次 HTTP 400。服务返回的稳定特征为：

```text
messages[n]: unknown variant image_url, expected text
```

当时配置的端点为 `https://api.deepseek.com`、模型为 `deepseek-v4-flash`。桌面端在打开设备详情时会自动附加当前截图，`AiService` 又无条件把图片序列化为 OpenAI 风格的 `image_url` 内容块，因此文本模型拒绝请求。旧版本没有 AI 请求日志，完整错误只能从本地持久化的可见消息中恢复。

## 行为要求

### 全局设备上下文

- AI 请求必须携带全部已知设备的 `deviceId`、显示名称、ADB 状态和伴侣状态。
- 查询“有哪些设备”、设备数量或连接状态不依赖当前详情页。
- `device_list` 是不需要当前详情设备的全局只读工具。
- 具体设备工具接受可选 `deviceId`。省略时使用当前详情设备；两者都没有时返回 `DEVICE_SELECTION_REQUIRED`。
- 模型不得猜测目标设备。存在多个候选时应先查询清单，再让用户选择。

### 内联选择

- `ask_user_choice` 只在主 AI 对话中可用，自动化后台执行不提供该工具。
- `selectionMode` 只允许 `single` 或 `multiple`。
- 选项必须是 2–8 个不重复、非空且不超过 80 字符的字符串。
- 选择卡是自绘控件；提交后选择结果作为同一 AI 回复中的 tool result 返回。
- 需要自由文字时，模型应直接提问并结束当前回复，由用户开启新一轮对话。
- 选择前说明、选择卡和选择后续写必须按时间顺序显示。

### 模型输入兼容

- 已确认只接受文本内容的 DeepSeek API 不发送 `image_url`。
- 被省略的图像以文本兼容提示替代，并向 UI 返回 `AI_IMAGE_INPUT_OMITTED` 警告。
- 未知 OpenAI 兼容端点可先按多模态格式发送；仅当 HTTP 400 同时明确包含 `image_url` 与反序列化/期望文本特征时，移除图像并重试一次。
- 兼容重试最多一次，不得对普通 400、鉴权错误或其他失败盲目重试。

### 错误分类与展示

| HTTP/异常 | 稳定错误码 | 类别 | 可恢复 |
| --- | --- | --- | --- |
| 图像内容不兼容 400 | `AI_REQUEST_IMAGE_UNSUPPORTED` | Compatibility | 是 |
| 其他 400 | `AI_REQUEST_INVALID` | Validation | 否 |
| 401 | `AI_AUTHENTICATION_FAILED` | Authentication | 否 |
| 403 | `AI_ACCESS_DENIED` | Authorization | 否 |
| 404 | `AI_ENDPOINT_OR_MODEL_NOT_FOUND` | Configuration | 否 |
| 408/客户端超时 | `AI_REQUEST_TIMEOUT` | Timeout | 是 |
| 413 | `AI_REQUEST_TOO_LARGE` | Payload | 是 |
| 429 | `AI_RATE_LIMITED` | RateLimit | 是 |
| 5xx | `AI_PROVIDER_UNAVAILABLE` | Provider | 是 |
| 网络异常 | `AI_NETWORK_FAILED` | Network | 是 |
| 无法解析响应 | `AI_RESPONSE_INVALID` | InvalidResponse | 否 |

- 错误在消息流内使用专用错误卡，不作为普通 assistant 消息回放给模型。
- 卡片默认只显示标题、摘要和恢复建议；详情按钮可展开，悬停显示完整错误。
- 每次失败同时显示应用内 `InfoBar` 和 Windows 系统通知。
- 每次请求生成 `traceId`，错误卡、系统通知和日志使用同一追踪号。

### 日志与隐私

- 日志路径：`%LOCALAPPDATA%\ADBControl\logs\ai-request.log`。
- 日志只记录时间、`traceId`、供应商主机、模型标识、阶段、HTTP 状态、稳定错误码、供应商错误类型/代码/消息和耗时。
- 日志不得记录 API Key、对话正文、工具参数、设备截图或附件数据。

### 流式响应与界面响应性

- SSE 网络读取和 JSON 分片解析不得捕获 WinUI 同步上下文；只有合并后的可见文本更新进入 Dispatcher。
- UI 刷新间隔不得低于 80 毫秒，不能按每个 token 强制布局。
- 生成期间完整思考内容保存在 `StringBuilder`，界面只展示有长度上限的最新预览；完成、取消或工具交互边界再物化完整文本。
- 自动滚动不得调用同步 `UpdateLayout`；使用 Dispatcher 队列中的 `ChangeView`，最多补一次布局后的定位。
- 完成后的思考过程和正文仍须完整展示、选择、复制和持久化，性能优化不得截断最终内容。

### 消息选择与滚轮

- 用户消息、AI 正文、思考过程、错误详情和提示文本必须允许鼠标选择复制。
- 鼠标滚轮优先滚动指针下方最内层可滚动区域；内层到边界后交给父级。
- 桌面根元素和每个滚动容器必须用 `handledEventsToo: true` 注册 `UIElement.PointerWheelChanged`，覆盖控件已标记处理的路由事件。
- WinUI 指针事件、`WM_MOUSEWHEEL` 和 `WM_POINTERWHEEL` 必须进入同一滚动策略。
- XAML 路由与 Win32 原生消息监听并行工作；原生路径必须始终观察滚轮消息，并通过默认处理前后的偏移比较避免同一消息重复滚动。
- 原生窗口钩子必须先调用原窗口过程，不得返回 `0` 提前吞掉滚轮消息。
- 刷新原生钩子时必须读取当前 WndProc；WinUI 替换窗口过程后必须重新安装，释放时只还原仍由本程序持有的代理。
- 仅当默认处理前后目标偏移未变化时，才允许在 Dispatcher 队列中异步执行一次兜底滚动。
- 当物理滚轮既未进入 XAML 也未进入目标 WndProc 时，桌面端使用 `WH_MOUSE_LL` 作为最后观察路径；只接受光标下窗口属于当前进程的垂直滚轮，不吞掉系统消息。
- 低级观察事件按短时间窗口合并；默认路径已改变偏移时整批跳过，否则按累计 delta 路由，避免正常机器重复滚动和高频滚轮丢步。
- 同一原生消息经子 HWND 向父窗口传播时只能由最外层钩子调度一次兜底。
- 原生命中测试只沿最上层元素的父链选择滚动容器，不得滚动被弹窗或浮层遮挡的后台页面。
- 设备页、任务页、AI 面板、列表和对话框不得依赖 `_activePageScroller` 才能滚动。
- 诊断日志写入 `%LOCALAPPDATA%\ADBControl\logs\mouse-wheel.log`，必须区分原生钩子安装、XAML 接收、原生消息接收、路由和兼容回退阶段；每进程最多 200 条且不包含正文或设备标识。

### 离线设备预览

- ADB 不可用且没有活动伴侣投屏时，连接状态优先于锁屏状态。
- 覆盖层标题为“设备未连接”，并提示开启无线调试后重连。
- 离线时隐藏截图、PIN 键盘和解锁动作，不启动无意义的锁屏轮询或截图定时器。
- ADB 离线但伴侣投屏活动时，仍按真实锁屏状态展示解锁动作。

## 验收

- 无详情页询问设备清单时，AI 能直接列出保存设备及连接状态。
- DeepSeek 文本模型请求中不存在 `image_url`，历史 400 根因不再出现。
- 单选、多选、非法文字模式和设备目标解析均有无 UI 回归测试。
- HTTP/网络错误分类、日志结构、滚轮方向、边界、默认处理优先级与嵌套钩子去重均有无 UI 回归测试。
- 长思考预览上限、受控刷新间隔、后台 SSE 解析和禁止同步布局均有回归测试或源码契约测试。
- 桌面项目以 `x64 Debug` 构建通过，且无警告。
