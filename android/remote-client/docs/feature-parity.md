# Android 远程客户端功能对照（v0.2.0）

对照 Windows 桌面端能力逐项标注远程客户端落点。所有设备操作只经
`adbcontrol-core-remote-quic/1` 的 `auth.*` / `device.*` / `adb.exec`（`-s` 白名单命令）。

| 桌面能力 | Android 落点 | 底层通道 | 状态 |
|---|---|---|---|
| 登录、首次改密、退出 | 全屏认证流 + 会话失效自动回登录 | `auth.*` | 已接通 |
| 服务器身份固定 | 连接页只需地址；指纹可选强校验（来源=Core 启动日志），留空 TOFU 并在登录页回显核对 | 原生指纹校验器（无 ALPN，见 ADR 0002 4b） | 已接通（v0.2.1 修订） |
| 设备列表与型号采集 | 设备 Tab / 首页概览；清单=已分配设备，信息经 ADB 采集 | 会话 devices + `adb.exec get-state/getprop/dumpsys battery` | 已接通 |
| ADB/伴侣双连接徽标 | 设备卡与详情头 | `get-state` + `device.list` | 已接通 |
| 快捷按键（电源/音量/返回/主页/多任务/唤醒/锁屏） | 详情页快捷控制区 + 预览页快捷条 | `adb.exec shell input keyevent` | 已接通 |
| 截图预览（间隔轮询、帧去重） | 预览控制页（500–60000ms） | Web 服务远程鉴权截图路由（ADB screencap） | 已接通（不依赖伴侣） |
| 预览触控（点击/长按/滑动、坐标映射） | 预览控制页手势 | `adb.exec shell input tap/swipe`，INJECT_EVENTS 失败回退 `accessibility.touch.*` | 已接通 |
| 锁屏状态监测（两段式 + OneUI 降级） | 详情页状态 + 解锁/键盘门槛 | `adb.exec shell dumpsys window policy/power/trust` → `dumpsys window` | 已接通 |
| 锁屏覆盖层 + 上滑解锁 | 预览控制页锁屏层 | `KEYCODE_WAKEUP` + `input touchscreen swipe 0.82h→0.2h`，被拒回退伴侣 | 已接通 |
| 安全键盘（逐键发送、互斥、锁屏门槛） | 预览页/锁屏层入口 | `input text '<c>'` / `input keyevent`（错误码对齐） | 已接通 |
| ADB 终端（单次执行、历史、30s 超时、危险确认） | 终端页 | `adb.exec shell` | 已接通 |
| 软件管理（列表/启停/禁用启用/清数据/详情/卸载） | 软件页（搜索、系统应用过滤、dumpsys 名称解析） | `pm list packages` / `dumpsys package` / `monkey` / `am force-stop` / `pm …` / `uninstall` | 已接通 |
| 安装 APK / 提取 APK 到本机 | 明确提示需桌面端本地文件通道 | — | 桌面端专属 |
| 文件管理（浏览/删除/ls 解析） | 文件页 | `ls -la -p` / `rm -rf`（RemotePathPolicy 保护根目录） | 已接通 |
| 文件上传/下载 | 文件页（上传 ≤20MB 分块、下载 ≤3MB、存 Downloads/ADBControl） | shell base64 分块（1MiB/argv/4MiB 协议限制，见代码注释） | 已接通（受限适配） |
| 硬件信息仪表盘（SoC/内存/电池/温度/GPU/刷新率/前台FPS、5s 自动刷新） | 硬件页 | SnapshotCommand / AppFrameCommand（与桌面逐字一致） | 已接通 |
| 硬件监控记录与导出 | 硬件监控页（1s 采样、曲线、记录、CSV 导出） | 同上；导出 CSV 替代桌面 xlsx/html/sqlite | 已接通（格式适配） |
| 重启六动作（系统/Bootloader/Fastbootd/Recovery/EDL/关机） | 重启页（危险确认） | `adb.exec reboot…` / `shell reboot -p` | 已接通 |
| 伴侣安装/QUIC 配置下发 | 状态检测 + 呼出伴侣界面；安装与配置需桌面端 | `pm path` / `dumpsys package` / `ui.surface.show` | 桌面端专属（状态可查） |
| 伴侣能力/权限目录 | 伴侣能力页 | `device.getCapabilities` / `device.getPermissionState` | 已接通（取决于 Core 宿主接线） |
| 实时投屏（scrcpy H.264）与实时画面控制 | 预览控制页 + MediaCodec + 全屏触控 | Web 服务远程鉴权 WebSocket，复用 scrcpy 协议 v2 | 已接通 |
| 实时摄像头预览（scrcpy camera / companion 流） | 暂不可用，界面明示 | Core 媒体下行流待定义 | 服务端阻塞 |
| 无线 ADB 配对/连接/二维码 | 不可用（远程白名单无 pair/connect/devices），设备由管理员分配 | — | 安全策略阻塞 |
| 自动化任务（DSL/调度/条件/动作/运行记录） | 任务 Tab + 编辑器（模板）+ 运行记录；引擎在本机、动作经远程通道 | 本机 SQLite + 远程 `adb.exec` / `device.invoke`；DSL 与桌面同构 | 已接通（调度本机化） |
| AI 助手（多模型/流式/权限三档/agent 工具/选择卡/上下文压缩） | AI 助手页 + 模型管理页 | OpenAI 兼容 SSE + 远程工具；observe_screen 走伴侣截图 | 已接通 |
| 账号管理（admin.*） | 账号页如实透出服务端 REMOTE_AUTH_FORBIDDEN；真实入口在 Core 本机 | `admin.users.*` | 安全策略阻塞（内置 admin 仅本机） |
| 设置（主题/预览间隔等） | 设置页（跟随系统深浅色/手动、间隔、后台调度） | 本机 | 已接通 |
| 统一日志与诊断 | 我的 → 日志与诊断（环形缓冲、会话 JSONL 导出） | 本机；只记录元数据 | 已接通（最小子集） |

“服务端阻塞/桌面端专属/安全策略阻塞”的条目在 UI 中显示明确状态与原因，不会伪造成功。
