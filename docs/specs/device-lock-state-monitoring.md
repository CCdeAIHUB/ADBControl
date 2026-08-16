# 设备锁屏状态持续监控

## 目标

设备详情页在截图预览、ADB scrcpy 和 Companion MediaProjection 期间都必须持续识别锁屏与解锁。实现不能依赖设备型号或 ROM 品牌特判。

## 根因证据

1. 旧实现把锁屏查询放在截图刷新计时器中。
2. 实时投屏启动时会停止该计时器，因此后续锁屏不再采样。
3. 视频首帧呈现后还会无条件隐藏锁屏覆盖层，可能覆盖并发采样结果。
4. M2105K81AC 的 HyperOS 使用 `KeyguardServiceDelegate showing`、`KeyguardStateMonitor mIsShowing` 和 `dumpsys trust deviceLocked`，旧解析器未覆盖。

## 状态来源

优先级固定如下：

1. Companion `KeyguardManager.isKeyguardLocked/isDeviceLocked` 与 `PowerManager.isInteractive`。
2. ADB `dumpsys window policy/window` 的 AOSP、HyperOS、ColorOS、OneUI Keyguard 字段。
3. ADB `dumpsys power` 的熄屏、Sleep 与 Doze 状态。
4. ADB `dumpsys trust` 中标记为 `(current)` 的当前用户 `deviceLocked`。

任何明确锁定或熄屏信号优先于已解锁信号。多用户输出只允许当前用户决定 `deviceLocked`；无法确定时返回 `Unknown`，禁止猜测为已解锁。

## 生命周期

1. 进入设备详情页时启动独立监控，默认每 1 秒串行采样一次。
2. 启动或停止视频投屏不得停止锁屏监控，也不得产生重叠采样。
3. 切换设备、返回列表、删除设备和关闭窗口时必须取消旧监控。
4. 旧采样只能更新同一详情页设备，禁止污染后续打开的设备。
5. 锁屏或未知时覆盖截图/视频并显示解锁入口；解锁后按当前投屏状态恢复视频或截图。
6. 视频首帧只能应用监控中的最新状态，禁止无条件隐藏覆盖层。

## 兼容与错误

- Companion 查询超时为 1 秒；旧版本或通道不可用时回退 ADB。
- ADB 单次锁屏查询最多 3 秒；超时返回 `Unknown`。
- `DEVICE_LOCK_STATE_QUERY_FAILED` 表示监控来源抛出异常，状态必须对调用方显示为未知并继续后续采样。
- 投屏状态迁移写入 `%LOCALAPPDATA%\ADBControl\logs\scrcpy.log` 的 `device.lock_state.changed` 事件，不记录原始设备 ID。
- 不新增权限、第三方依赖、设备型号分支或 ROM 品牌分支。

## TDD 场景

- HyperOS 亮屏锁屏必须由 `showing/mIsShowing/deviceLocked=1` 识别。
- ColorOS/AOSP 明确 false 与 `deviceLocked=0` 必须识别为已解锁。
- 工作资料用户锁定不能覆盖 `(current)` 用户的已解锁状态。
- `Dozing` 必须识别为锁屏。
- 监控必须观察到运行中的 `Unlocked -> Locked` 迁移并在停止后取消。
- 投屏锁屏必须显示覆盖层；投屏解锁不能让旧截图覆盖视频。
