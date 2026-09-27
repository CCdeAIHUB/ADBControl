package com.adbcontrol.remote.data.adb

import com.adbcontrol.remote.data.RemoteRepository
import com.adbcontrol.remote.data.automation.AutomationGateway
import com.adbcontrol.remote.model.AdbOutput
import com.adbcontrol.remote.model.RemoteResult

/**
 * 设备命令网关：远程 App 中一切“面向目标设备的 ADB 操作”的唯一出口。
 * 约束与桌面端一致：命令只允许来自明确编码的调用点；任意 shell 文本只允许出现在终端页，
 * 且必须先经 RiskPolicy 分级确认。
 */
class DeviceCommandGateway(private val repository: RemoteRepository, private val defaultTimeoutMs: Long = 15_000) {

    fun exec(deviceId: String, args: List<String>, timeoutMs: Long = defaultTimeoutMs): RemoteResult<AdbOutput> =
        repository.adb(deviceId, *args.toTypedArray())

    fun shell(deviceId: String, command: String, timeoutMs: Long = defaultTimeoutMs): RemoteResult<AdbOutput> =
        exec(deviceId, listOf("shell", command), timeoutMs)

    /** 锁屏两段式查询（含 OneUI 降级），返回解析结果与原始输出。 */
    fun queryLockState(deviceId: String): RemoteResult<LockQuery> {
        val primary = StringBuilder()
        LockStateParser.primaryCommands.forEach { command ->
            when (val result = shell(deviceId, command, 3_000)) {
                is RemoteResult.Success -> primary.append(result.value.stdout).append('\n')
                is RemoteResult.Failure -> return RemoteResult.Failure(result.error)
            }
        }
        val parsed = LockStateParser.parse(primary.toString())
        if (parsed != LockStateParser.LockState.Unknown) {
            return RemoteResult.Success(LockQuery(parsed, primary.toString()))
        }
        val fallback = StringBuilder()
        LockStateParser.fallbackCommands.forEach { command ->
            when (val result = shell(deviceId, command, 3_000)) {
                is RemoteResult.Success -> fallback.append(result.value.stdout).append('\n')
                is RemoteResult.Failure -> return RemoteResult.Failure(result.error)
            }
        }
        return RemoteResult.Success(LockQuery(LockStateParser.parseCombined(primary.toString(), fallback.toString()), primary.toString()))
    }

    fun screenSize(deviceId: String): ScreenResult {
        return when (val result = shell(deviceId, "wm size", 5_000)) {
            is RemoteResult.Failure -> ScreenResult(null, result.error)
            is RemoteResult.Success -> {
                val size = WmSizeParser.parse(result.value.stdout)
                if (size == null) {
                    ScreenResult(null, null)
                } else {
                    ScreenResult(size, null)
                }
            }
        }
    }

    data class LockQuery(val state: LockStateParser.LockState, val rawOutput: String)
    data class ScreenResult(val size: WmSizeParser.ScreenSize?, val error: com.adbcontrol.remote.model.AppError?)

    /** 自动化网关适配：把本网关桥接到引擎要求的 AutomationGateway 接口。 */
    fun automationGateway(): AutomationGateway = object : AutomationGateway {
        override fun isConnected(deviceId: String): Boolean =
            exec(deviceId, listOf("get-state"), 5_000) is RemoteResult.Success

        override fun shell(deviceId: String, command: String): com.adbcontrol.remote.model.AutomationCommandResult {
            val result = shell(deviceId, command, 30_000)
            return when (result) {
                is RemoteResult.Success -> com.adbcontrol.remote.model.AutomationCommandResult(
                    success = result.value.success,
                    exitCode = result.value.exitCode,
                    stdout = result.value.stdout,
                    stderr = result.value.stderr,
                )
                is RemoteResult.Failure -> com.adbcontrol.remote.model.AutomationCommandResult(
                    success = false, exitCode = -1, stdout = "", stderr = result.error.message,
                )
            }
        }

        override fun companion(
            deviceId: String,
            capabilityId: String,
            operation: String,
            argsJson: String,
        ): com.adbcontrol.remote.model.AutomationCommandResult {
            val args = try { org.json.JSONObject(argsJson) } catch (_: Exception) { org.json.JSONObject() }
            val result = repository.invoke(deviceId, capabilityId, operation, args)
            return when (result) {
                is RemoteResult.Success -> {
                    val payload = result.value
                    val ok = payload.optBoolean("ok", true)
                    com.adbcontrol.remote.model.AutomationCommandResult(
                        success = ok,
                        exitCode = if (ok) 0 else -1,
                        stdout = if (ok) payload.toString() else "",
                        stderr = payload.optJSONObject("error")?.optString("message").orEmpty(),
                    )
                }
                is RemoteResult.Failure -> com.adbcontrol.remote.model.AutomationCommandResult(
                    success = false, exitCode = -1, stdout = "", stderr = result.error.message,
                )
            }
        }

        override fun screenSize(deviceId: String): AutomationGateway.ScreenSize {
            val screen = this@DeviceCommandGateway.screenSize(deviceId)
            val size = screen.size ?: return AutomationGateway.ScreenSize(1080, 1920)
            // wm size 输出的宽高方向随设备姿态固定；动作坐标校验与桌面一致取原始值。
            return AutomationGateway.ScreenSize(size.width, size.height)
        }
    }
}
