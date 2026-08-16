# Companion 安装工作流

## 目标

桌面端从用户点击“安装伴侣 App”开始，必须立即显示窗口内进度，持续呈现当前阶段，并在成功、部分成功或失败后保留可诊断的终态消息。

## 状态机

状态依次为：

- Idle：尚未开始；
- CheckingDevice：检查目标 ADB 设备是否在线；
- InstallingPackage：传输并安装内置 APK，同时提示用户留意手机端确认；
- ConfiguringConnection：下发桌面 QUIC 连接配置；
- WaitingForConnection：等待伴侣 App 建立连接；
- Succeeded：APK 安装且连接成功；
- CompletedWithWarning：APK 已安装，但配置或连接尚未完成；
- Failed：安装前检查、APK 安装或后续阶段发生不可继续的错误。

所有进行中状态都允许进入 Failed。终态不能继续转换；用户重试会创建新的工作流实例和 traceId。

## UI 行为

- 按钮必须在首次异步等待之前禁用并显示“安装进行中”；
- 详情页使用自绘状态卡显示阶段、说明和活动动画；
- 成功、警告和失败终态必须显示耗时与短 traceId；
- 失败和警告必须显示稳定错误码及可执行建议；
- 最终结果同时通过窗口内全局消息提示，不依赖系统通知。

## 错误分类

- COMPANION_INSTALL_USER_RESTRICTED：设备安全策略或手机端操作拒绝安装；
- COMPANION_INSTALL_SIGNATURE_MISMATCH：已安装版本签名不一致；
- COMPANION_INSTALL_TIMEOUT：ADB 安装超时；
- COMPANION_INSTALL_DEVICE_UNAVAILABLE：设备离线或传输中断；
- COMPANION_INSTALL_APK_MISSING：桌面包缺少 Companion APK；
- COMPANION_INSTALL_ADB_FAILED：其他可识别的 ADB 安装失败；
- COMPANION_CONFIGURE_FAILED：APK 已安装但连接配置失败；
- COMPANION_CONNECTION_TIMEOUT：配置完成但未建立 Companion 连接；
- COMPANION_INSTALL_UNEXPECTED：未预期异常。

## 重试与回滚

失败允许用户重试。本流程不自动重试，避免重复触发手机端安装确认。APK 安装成功后即使连接配置失败也不自动卸载，因为配置可恢复，卸载会破坏已授予权限和用户状态。

## 可观测性

每个阶段以 JSON Lines 写入 %LOCALAPPDATA%\ADBControl\logs\companion-install.log，包含 traceId、匿名设备摘要、阶段、耗时、错误码和技术摘要；禁止记录原始设备 ID、证书、密码或本地 APK 路径。