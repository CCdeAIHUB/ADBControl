# ADBControl Remote for Android

原生 Kotlin 安卓远程客户端（v0.3.1）。它只连接远程 `adbcontrol-core`，不携带 ADB
可执行文件、不访问 USB 调试接口，也不在手机本地执行目标设备能力——一切设备操作
经加密 QUIC 通道由 Core 定向下发。

## 能力总览（对照 Windows 桌面端）

- **连接与认证**：证书固定 + SHA-256 指纹核对、登录、强制改密、会话失效自动回登录；
- **设备**：设备卡（在线状态/品牌/型号/安卓版本/电量）、ADB/伴侣双徽标、快捷按键；
- **预览控制**：伴侣截图轮询（500–60000ms、帧去重）、手势触控（点击/长按/滑动、
  截图坐标→屏幕坐标映射、INJECT_EVENTS 回退伴侣无障碍）、锁屏覆盖层与上滑解锁、
  安全键盘（逐键发送、互斥、锁屏门槛）；
- **工具**：ADB 终端（历史/危险确认/30s 超时）、软件管理（启停/禁用启用/清数据/详情/卸载）、
  文件管理（浏览/删除/上传/下载/预览）、硬件信息仪表盘（与桌面同一采集脚本）、
  硬件监控（1s 采样/曲线/记录/CSV 导出）、重启六动作、伴侣能力与权限目录；
- **AI 助手**：OpenAI 兼容流式聊天、多模态探针、权限三档（请求批准/替我审批/完全访问）、
  agent 工具（device_list/observe_screen/adb_shell/adb_ui_dump/adb_tap/adb_swipe/
  device_unlock/companion_call/task_*）、选择卡、上下文压缩；
- **自动化任务**：与桌面端同构的 JSON DSL（触发器/条件/动作/权限集）、本机调度引擎
  （1s tick、错过容忍 2 分钟、条件边沿触发）、运行记录（SQLite）、可选前台服务保活；
- **我的**：主题（跟随系统/浅色/深色）、预览间隔、日志与诊断导出、账号管理入口。

详细逐项对照见 `docs/feature-parity.md`；架构决策见 `docs/adr/`。

## 界面设计

底部 4 Tab（首页 / 设备 / 任务 / 我的）+ 二级页导航栈，符合中国大陆移动 App 的一级
导航习惯；设备详情为功能宫格；危险操作统一红色确认；全部颜色出自统一 ThemePalette，
深浅色两套主题完整覆盖；返回键逐级回退，根页双击退出。

## 用户流程

1. 管理员按 `docs/protocols/remote-control-quic.md`（仓库根）在 Core 所在主机启用远程监听
   （如 `ADBCONTROL_REMOTE_LISTEN=0.0.0.0:45921`）；
2. 使用者在 App 里只需输入**服务器地址**（`主机:端口`，自动补 `quic://`）；
3. 使用 Core 管理员创建的**用户名/密码**登录（若密码被重置则先强制改密）；
4. 证书信任全程无需用户操作：首次连接自动记录服务器证书指纹（SHA-256，TOFU），
   之后每次连接校验，防止连接被劫持；内置 `admin` 账号不允许远程登录；
5. 设备控制全部通过每请求一个双向 QUIC stream 调用 Core。

会话令牌仅保存在内存中。应用退出、账号退出、密码修改或 Core 撤销会话后均需重新登录。

### 传输安全说明（v0.2.1 修订）

- 客户端不再强制要求 ALPN：实测 Core 的 `CoreQuicIdentity::server_config()` 未配置 ALPN，
  任何非空 ALPN 要求都会导致握手失败；身份校验完全依赖证书指纹/证书固定 + TLS 握手签名校验；
- 指纹固定通过 rustls 自定义 `ServerCertVerifier` 实现：预置指纹不匹配时连接显式失败
  （`REMOTE_CERTIFICATE_FINGERPRINT_MISMATCH`）；TOFU 模式记录首次指纹并在后续连接校验。

## 工程边界（高模块化）

- `core`：主题（ThemePalette/ThemeManager）与组合根（AppGraph）；
- `model`：会话、设备、错误、自动化 DSL、AI 模型（纯 Kotlin，可契约测试）；
- `transport`：证书固定的 QUIC/JNI 适配，不了解业务方法；
- `data`：远程协议封装、ADB 解析器（锁屏/ls/包/硬件）、伴侣能力网关、自动化
  （存储/序列化/校验/调度/条件/动作）、AI（模型存储/SSE 客户端/agent 运行时/策略）、
  日志与设置；
- `navigation` / `security`：角色访问门控、危险操作分级、路径保护；
- `ui`：主题化页面（BasePage 统一组件），不直接调用 JNI 或协议层；
- `service`：自动化前台调度（specialUse）；
- `native`：Quinn/rustls 实现的 `adbcontrol-core-remote-quic/1` 客户端。

## 构建

需要 Android SDK 36、NDK 27.2、JDK 17、Rust 和 `cargo-ndk`：

```powershell
cd android/remote-client
gradle testContracts     # 纯 JVM 契约测试（导航门控/解析器/调度/条件/键盘/AI 策略）
gradle assembleDebug     # 产出 app/build/outputs/apk/debug/app-debug.apk
```

## 当前 Core 协议边界（重要）

- 当前所有 Core 宿主部署中伴侣注册表为空（宿主未接线 companion router），因此设备清单
  以 `auth.login` 返回的已分配设备为基线，经 `adb.exec` 现场采集信息；伴侣相关能力
  （截图/无障碍触控/IME 输入/剪贴板等）依赖 Core 宿主接线伴侣会话，未接线时页面如实
  展示 `COMPANION_*` 错误；
- `adb.exec` 仅接受 `["-s", <serial>, <cmd>, ...]` 且 cmd 在 23 个白名单命令内；
- 截图走伴侣 `accessibility.screenshot`（PNG base64 ≤2MiB，响应上限 4MiB）；
- 文件上传/下载以 shell base64 分块适配（1MiB 请求与 Linux argv 限制），未来 Core
  提供二进制流后应整体替换；
- 实时投屏/实时摄像头需要 Core 媒体下行流；无线配对/连接为管理员桌面专属；
  `admin.*` 受 Core 安全策略限制（内置 admin 仅本机登录）——以上均不伪造成功。
