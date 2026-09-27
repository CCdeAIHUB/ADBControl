# ADR: 远程客户端全能力化重构与中国大陆移动端 UI 重设计

## 背景

v0.1.0 的远程客户端只是只读演示：设备信息靠未接线的 Core 注册表、软件/文件/硬件只有原始文本输出、
AI 与自动化完全缺失，与 Windows 桌面端能力差距过大。用户要求：除远程连接保持不变外，
其余能力完全对照 Windows 桌面端复刻，UI 按中国大陆 App 使用习惯重新设计。

## 决策

1. **能力来源以会话为中心**：Core 的伴侣注册表在当前所有宿主部署中均为空（宿主 main.rs 未接线
   companion router），因此设备清单以 `auth.login` 返回的已分配设备为基线，型号/电量/状态等
   与桌面端一致地通过白名单 `adb.exec` 现场采集；`device.list` 仅作为伴侣连接状态的补充信息合并。
2. **能力层复刻对照桌面端**：锁屏两段式查询（含 OneUI 降级）、硬件快照脚本（SnapshotCommand/
   AppFrameCommand 逐字一致）、软件管理命令集（monkey/am force-stop/pm disable-user/enable/clear）、
   安全键盘逐键发送、解锁手势（0.82h→0.28h、350ms）、自动化 DSL（与桌面 AutomationModels.cs 同构）、
   AI agent 工具集（device_list/observe_screen/adb_shell/adb_ui_dump/adb_tap/adb_swipe/companion_call/
   task_* 等）全部对齐桌面语义与错误码。
3. **远程协议边界的诚实呈现**：二进制 screencap 经 JSON 文本通道会损坏，截图走伴侣
   `accessibility.screenshot`；文件上传以 base64 分块经 shell（受 1MiB 请求与 Linux argv 限制，
   单块 64KB，≤20MB）；下载单文件 ≤3MB（4MiB 响应上限）；实时投屏/实时摄像头/无线配对在
   `adbcontrol-core-remote-quic/1` 下不可表达，界面明确标注“需桌面端”，绝不伪造成功。
4. **UI 信息架构按中国大陆习惯重设计**：底部 4 Tab（首页/设备/任务/我的）+ 二级页导航栈；
   设备详情=功能宫格；预览控制页=大图+手势触控+锁屏覆盖层+底部快捷条；返回键逐级回退，
   根页双击退出；深浅色双主题（跟随系统/手动），全部颜色统一出自 ThemePalette。
4b. **接入流程以“地址+指纹”为中心（v0.2.1 修订）**：原 v0.1.0 要求用户手填证书 Base64，
    但整个生态没有任何界面分发该证书（只有 Core 启动日志打印的一行地址+指纹），且 Core
    服务端未配置 ALPN，原客户端强制 ALPN 的握手必败。修订后：客户端原生层改用
    指纹固定/TOFU 自定义证书校验器（保留握手签名校验），连接页只要求地址，指纹可选强校验，
    不再发送 ALPN 扩展。
5. **自动化引擎本机化**：调度/条件/动作引擎运行在手机本机（SQLite 存储），动作经远程通道执行；
   可选前台服务（specialUse）维持后台调度，并在页面明示系统可能回收。
6. **零新增第三方依赖**：协程/网络/JSON/数据库全部使用 Kotlin 标准库、HttpURLConnection、
   org.json 与 android.database.sqlite，延续项目零依赖惯例。

## 原因

- 远程客户端的真实约束是“每个请求一条 QUIC 双向流的请求/响应协议”，所有能力设计都以此为准；
- 与桌面端语义逐字对齐可以共享排障经验、错误码与文档；
- 中国大陆用户对底部导航、宫格入口、危险操作红色确认等交互有稳定预期。

## 影响范围

仅 `android/remote-client/`：新增 core/automation/ai/data 域约 30 个 Kotlin 文件，
重写全部 UI；未改动 Rust Core、伴侣 App、桌面端与任何协议契约。

## 风险

- Core 宿主未来接线伴侣 router 后，`device.invoke` 能力面会自动增强，客户端无需变更；
- base64 shell 文件通道在未来 Core 提供二进制流后应整体替换（已在代码注释中标注）；
- 后台调度受 Android 低内存杀进程影响，不能承诺绝对保活。

## 未来演进

- Core 增加 `stream.*` 媒体流后实现实时投屏与实时摄像头；
- Core 增加自动化远程 API 后可切换为服务端调度；
- 文件传输切换到二进制流并解除单文件大小限制。
