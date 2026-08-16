# 设备硬件与应用元数据采集

## 目标

设备详情页必须在不同 Android 厂商实现上稳定读取基础硬件信息，并优先使用设备本机数据展示 App 名称和真实图标。

## 硬件采集契约

1. 桌面端发送到 Android shell 的多行命令必须统一使用 LF，禁止把 Windows CRLF 传给 `/system/bin/sh`。
2. 基础硬件快照不得因 Windows 换行、单个可选 sysfs 节点不可读或厂商输出差异而整体失败。
3. sysfs 批量读取优先使用 shell 内建 `read`，避免为每个节点启动 `cat` 子进程。
4. 目标设备单次完整采样应小于 5 秒；真机集成测试通过 `ADB_CONTROL_HARDWARE_INTEGRATION=1` 执行。
5. ADB transport 离线可进行一次受控重试；脚本语法错误不得重试掩盖。
6. 温度传感器不得拼成单个长字符串；UI 按处理器、图形处理器、电池、机身和其他传感器稳定排序，每个传感器显示独立名称与温度卡片。
7. 分组只影响呈现，不能丢弃厂商特有但数值有效的传感器。

## 应用元数据来源

优先级固定如下：

1. Android Companion `PackageManager`：本机语言 App 名称、enabled/system 状态和渲染后的真实 PNG 图标。
2. ADB `dumpsys package`：仅在设备确实暴露 `application-label` 时补充名称。
3. Google Play 页面：只对仍未识别的包名进行可选联网补充，最多 60 个并限制为 12 秒。

不建设包名到名称的自有数据库。该数据库无法可靠覆盖侧载应用、地区版本、本机语言、企业应用和版本变更，不能作为设备真实元数据来源。

## OEM 兼容与分页

1. ADB `pm list packages` 是包名集合来源。
2. 桌面端按最多 64 个包名调用 `app.list` 的显式查询模式，不能依赖 `getInstalledApplications()` 的 OEM 全量枚举结果。
3. 图标由 Android 端将 Drawable 渲染为 48x48 PNG；桌面端验证 Base64、PNG 签名和 256 KiB 大小上限。
4. 单个图标损坏时只隔离该图标并显示占位图标，名称和同页其他应用必须保留。
5. OEM 广播可能早于异步结果快照落盘返回；桌面端只在结果文件不存在或为空时按 100 毫秒间隔轮询，最多 8 次。
6. 单页元数据请求只允许在 ADB transport 不可用时重试一次；权限、协议和解析错误不得通过重试掩盖。
7. 旧伴侣响应不含图标时仍可解析名称；UI 必须明确显示真实图标数量和可恢复警告。

## 错误契约

- `PACKAGE_LIST_DEVICE_UNAVAILABLE`：ADB transport 不可用。
- `PACKAGE_LIST_ADB_FAILED`：包列表命令失败。
- `PACKAGE_LABEL_ADB_UNAVAILABLE`：ADB 标签来源不可用，可继续。
- `PACKAGE_METADATA_COMPANION_UNAVAILABLE`：伴侣元数据不可用，可继续显示包名。
- `PACKAGE_METADATA_INVALID_RESPONSE`：伴侣响应无法解析。
- `PACKAGE_ICON_INVALID`：图标载荷无效并已隔离。
- `PACKAGE_NAME_LOOKUP_TIMEOUT`：可选联网补充超时。
- `PACKAGE_ICON_DECODE_FAILED`：WinUI 图像解码失败并使用占位图标。

## 验收

1. 默认桌面测试覆盖 CRLF、OEM 广播引号、分页、显式包名、PNG 校验和旧响应兼容。
2. `ADB_CONTROL_PACKAGE_INTEGRATION=1` 在连接真机上验证所有包的本机名称与图标。
3. Android `assembleDebug` 和桌面 Debug 构建必须成功。
