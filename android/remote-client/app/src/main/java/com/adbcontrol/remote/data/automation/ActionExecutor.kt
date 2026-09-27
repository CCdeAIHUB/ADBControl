package com.adbcontrol.remote.data.automation

import com.adbcontrol.remote.model.AutomationActionDefinition
import com.adbcontrol.remote.model.AutomationCommandResult
import com.adbcontrol.remote.model.AutomationExecutionException
import com.adbcontrol.remote.model.AutomationTaskDefinition

/**
 * 动作执行：命令构建规则逐条对齐桌面端 `AutomationActionExecutor.cs`。
 * 纯命令构建函数（buildIntentCommand/shellToken/describe）便于契约测试；执行通过 [AutomationGateway]。
 * 说明：全部为阻塞调用，由引擎工作线程执行；不引入协程依赖。
 */
class ActionExecutor(
    private val gateway: AutomationGateway,
    private val conditions: ConditionEvaluator,
    private val sleeper: (Long) -> Unit = { Thread.sleep(it) },
) {

    fun executeLeaf(
        task: AutomationTaskDefinition,
        action: AutomationActionDefinition,
        aiExecutor: ((prompt: String, modelId: String, allowDeviceTools: Boolean, allowTaskMutation: Boolean) -> String)?,
    ): String {
        val parameters = action.parameters
        requirePermissions(task.permissions, action.type)
        return when (action.type.lowercase()) {
            "log" -> parameters["message"] ?: "记录"
            "delay" -> {
                val ms = parameters["milliseconds"]?.toLongOrNull() ?: 0L
                if (ms > 0) sleepInterruptible(ms)
                "等待完成"
            }
            "condition.wait" -> waitForConditions(task, action)
            "adb.shell" -> shell(task, required(parameters, "command"))
            "adb.keyevent" -> shell(task, "input keyevent ${shellToken(required(parameters, "keyCode"))}")
            "adb.tap" -> tap(task, parameters)
            "adb.swipe" -> swipe(task, parameters)
            "adb.text" -> shell(task, "input text ${shellToken(required(parameters, "text").replace(" ", "%s"))}")
            "app.start" -> startApp(task, parameters)
            "url.open" -> shell(task, "am start -a android.intent.action.VIEW -d ${shellToken(required(parameters, "url"))}")
            "intent.start" -> shell(task, buildIntentCommand(parameters, parseIntentExtras(parameters["extrasJson"].orEmpty())))
            "device.wake" -> shell(task, "input keyevent KEYCODE_WAKEUP")
            "device.lock" -> shell(task, "input keyevent KEYCODE_SLEEP")
            "device.unlock" -> unlock(task, parameters)
            "companion.call" -> companionCall(task, parameters)
            "ai.prompt" -> executeAi(
                aiExecutor,
                parameters,
                allowDeviceTools = task.permissions.allowAiDeviceTools,
                allowTaskMutation = task.permissions.allowTaskMutation,
            )
            "fail" -> throw AutomationExecutionException(
                parameters["errorCode"] ?: "TASK_SCRIPT_FAILURE",
                parameters["message"] ?: "任务脚本主动失败。",
                "automation.script",
            )
            else -> throw AutomationExecutionException("ACTION_UNSUPPORTED", "不支持的动作：${action.type}。", "automation.action")
        }
    }

    private fun waitForConditions(task: AutomationTaskDefinition, action: AutomationActionDefinition): String {
        val timeoutSeconds = (action.parameters["timeoutSeconds"]?.toIntOrNull() ?: 60).coerceIn(1, 86_400)
        val pollSeconds = (action.parameters["pollIntervalSeconds"]?.toIntOrNull() ?: 2).coerceIn(1, 3600)
        val deadline = System.currentTimeMillis() + timeoutSeconds * 1000L
        while (System.currentTimeMillis() < deadline) {
            if (conditions.evaluateGroup(task.deviceId, action.conditions, action.conditionMode)) return "条件已满足"
            sleepInterruptible(pollSeconds * 1000L)
        }
        throw AutomationExecutionException(
            "CONDITION_WAIT_TIMEOUT", "等待设备条件超过 $timeoutSeconds 秒。", "automation.action",
            recoverable = true, suggestion = "检查设备状态或增加 timeoutSeconds。",
        )
    }

    private fun tap(task: AutomationTaskDefinition, parameters: Map<String, String>): String {
        val x = parameters["x"]?.toIntOrNull() ?: -1
        val y = parameters["y"]?.toIntOrNull() ?: -1
        val screen = gateway.screenSize(task.deviceId)
        if (x < 0 || y < 0 || x >= screen.width || y >= screen.height) {
            throw AutomationExecutionException(
                "ACTION_COORDINATE_OUT_OF_RANGE", "点击坐标 ($x,$y) 超出屏幕 ${screen.width}x${screen.height}。", "automation.action",
            )
        }
        return shell(task, "input tap $x $y")
    }

    private fun swipe(task: AutomationTaskDefinition, parameters: Map<String, String>): String {
        val startX = parameters["startX"]?.toIntOrNull() ?: -1
        val startY = parameters["startY"]?.toIntOrNull() ?: -1
        val endX = parameters["endX"]?.toIntOrNull() ?: -1
        val endY = parameters["endY"]?.toIntOrNull() ?: -1
        val duration = (parameters["durationMs"]?.toIntOrNull() ?: 250).coerceIn(1, 3000)
        val screen = gateway.screenSize(task.deviceId)
        fun inside(x: Int, y: Int) = x >= 0 && y >= 0 && x < screen.width && y < screen.height
        if (!inside(startX, startY) || !inside(endX, endY)) {
            throw AutomationExecutionException(
                "ACTION_COORDINATE_OUT_OF_RANGE", "滑动坐标超出屏幕 ${screen.width}x${screen.height}。", "automation.action",
            )
        }
        return shell(task, "input swipe $startX $startY $endX $endY $duration")
    }

    private fun startApp(task: AutomationTaskDefinition, parameters: Map<String, String>): String {
        val packageName = required(parameters, "packageName")
        val activity = parameters["activity"].orEmpty()
        val command = if (activity.isBlank()) {
            "monkey -p ${shellToken(packageName)} -c android.intent.category.LAUNCHER 1"
        } else {
            "am start -n ${shellToken("$packageName/$activity")}"
        }
        return shell(task, command)
    }

    private fun unlock(task: AutomationTaskDefinition, parameters: Map<String, String>): String {
        // 解锁手势参数与桌面端一致：上滑 0.82h → 0.28h、350ms；解锁是敏感能力，必须显式声明 allowUnlock。
        if (!task.permissions.allowUnlock) {
            throw AutomationExecutionException(
                "ACTION_PERMISSION_DENIED", "任务未开启 allowUnlock 权限，不能执行解锁动作。", "automation.permission", recoverable = true,
            )
        }
        val screen = gateway.screenSize(task.deviceId)
        val pin = parameters["pin"].orEmpty()
        val commands = mutableListOf(
            "input keyevent KEYCODE_WAKEUP",
            "input touchscreen swipe ${screen.width / 2} ${(screen.height * 0.82).toInt()} ${screen.width / 2} ${(screen.height * 0.28).toInt()} 350",
        )
        if (pin.isNotBlank()) {
            commands.add("input text ${shellToken(pin)}")
            commands.add("input keyevent KEYCODE_ENTER")
        }
        return commands.map { shell(task, it) }.filter(String::isNotBlank).joinToString("\n")
    }

    private fun companionCall(task: AutomationTaskDefinition, parameters: Map<String, String>): String {
        if (!task.permissions.allowCompanion) {
            throw AutomationExecutionException(
                "ACTION_PERMISSION_DENIED", "任务未开启 allowCompanion 权限。", "automation.permission", recoverable = true,
            )
        }
        val result = gateway.companion(
            task.deviceId,
            required(parameters, "capabilityId"),
            required(parameters, "operation"),
            parameters["args"] ?: "{}",
        )
        return ensureSuccess(result, "ACTION_COMPANION_FAILED", "automation.action")
    }

    private fun executeAi(
        aiExecutor: ((prompt: String, modelId: String, allowDeviceTools: Boolean, allowTaskMutation: Boolean) -> String)?,
        parameters: Map<String, String>,
        allowDeviceTools: Boolean,
        allowTaskMutation: Boolean,
    ): String {
        if (aiExecutor == null) {
            throw AutomationExecutionException(
                "ACTION_AI_NOT_CONFIGURED", "任务运行时没有可用的 AI 执行器。", "automation.ai",
                recoverable = true, suggestion = "先在 AI 助手中添加并配置模型。",
            )
        }
        val prompt = required(parameters, "prompt")
        val response = aiExecutor(prompt, parameters["modelId"].orEmpty(), allowDeviceTools, allowTaskMutation)
        if (response.isBlank()) {
            throw AutomationExecutionException("ACTION_AI_EMPTY_RESPONSE", "AI 没有返回内容。", "automation.ai", recoverable = true)
        }
        return response
    }

    private fun shell(task: AutomationTaskDefinition, command: String): String {
        val result = gateway.shell(task.deviceId, command)
        return ensureSuccess(result, "ACTION_ADB_FAILED", "automation.action")
    }

    private fun ensureSuccess(result: AutomationCommandResult, errorCode: String, module: String): String {
        if (!result.success) {
            throw AutomationExecutionException(
                errorCode,
                result.stderr.trim().ifBlank { "设备命令失败，退出码 ${result.exitCode}。" },
                module,
                recoverable = true,
                suggestion = "确认设备在线、已授权且任务权限满足动作要求。",
            )
        }
        return result.combinedOutput.let { if (it.length <= 200_000) it else it.substring(0, 200_000) }
    }

    /** 权限门槛：缺权限是显式失败（ACTION_PERMISSION_DENIED），不允许静默降级执行。 */
    private fun requirePermissions(permissions: com.adbcontrol.remote.model.AutomationPermissionSet, actionType: String) {
        val type = actionType.lowercase()
        when {
            type == "companion.call" && !permissions.allowCompanion -> throw permissionError("allowCompanion")
            type == "ai.prompt" && !permissions.allowAi -> throw permissionError("allowAi")
            type in shellActions && !permissions.allowShell -> throw permissionError("allowShell")
            type in adbDeviceActions && !permissions.allowAdb -> throw permissionError("allowAdb")
        }
    }

    private fun permissionError(name: String) = AutomationExecutionException(
        "ACTION_PERMISSION_DENIED", "任务未开启 $name 权限，动作被拒绝。", "automation.permission", recoverable = true,
    )

    private fun sleepInterruptible(ms: Long) {
        try {
            sleeper(ms)
        } catch (_: InterruptedException) {
            Thread.currentThread().interrupt()
            throw AutomationExecutionException("ACTION_INTERRUPTED", "动作等待被中断。", "automation.action", recoverable = true)
        }
    }

    /** intent extras 的类型化表示（序列化层解析产生，构建命令保持纯 JVM）。 */
    data class IntentExtra(val type: String, val key: String, val value: String)

    companion object {
        /** extrasJson 由序列化层保留原始 JSON；这里解析成带类型的 extras（org.json 仅在数据层使用）。 */
        fun parseIntentExtras(extrasJson: String): List<IntentExtra> {
            if (extrasJson.isBlank()) return emptyList()
            return try {
                val extras = org.json.JSONObject(extrasJson)
                extras.keys().asSequence().map { key ->
                    when (val value = extras.opt(key)) {
                        is Boolean -> IntentExtra("ez", key, value.toString())
                        is Int, is Long -> IntentExtra("el", key, value.toString())
                        else -> IntentExtra("es", key, value?.toString().orEmpty())
                    }
                }.toList()
            } catch (_: Exception) {
                // extrasJson 只能由序列化层产生；格式异常属于契约破坏，显式失败而不是忽略。
                throw AutomationExecutionException(
                    "ACTION_PARAMETER_INVALID", "intent.start 的 extrasJson 不是合法 JSON 对象。", "automation.action",
                )
            }
        }

        private val shellActions = setOf(
            "adb.shell", "adb.keyevent", "adb.tap", "adb.swipe", "adb.text",
            "app.start", "url.open", "intent.start", "condition.wait",
        )
        private val adbDeviceActions = setOf("device.wake", "device.lock", "device.unlock")

        fun required(parameters: Map<String, String>, name: String): String {
            val value = parameters[name]
            if (value.isNullOrBlank()) {
                throw AutomationExecutionException("ACTION_PARAMETER_MISSING", "动作缺少参数 $name。", "automation.action")
            }
            return value
        }

        /**
         * intent.start 命令构建：flag 与值类型规则与桌面端 `BuildIntentCommand` 一致。
         * extras 由序列化层解析为带类型的 [IntentExtra]（保持纯 JVM 可测）。
         */
        fun buildIntentCommand(parameters: Map<String, String>, extras: List<IntentExtra>): String {
            val builder = StringBuilder("am start")
            fun add(flag: String, value: String?) {
                if (!value.isNullOrBlank()) builder.append(' ').append(flag).append(' ').append(shellToken(value))
            }
            add("-a", parameters["action"])
            add("-d", parameters["data"])
            add("-n", parameters["component"])
            extras.forEach { extra ->
                when (extra.type) {
                    "ez" -> builder.append(" --ez ").append(shellToken(extra.key)).append(' ').append(extra.value.lowercase())
                    "el" -> builder.append(" --el ").append(shellToken(extra.key)).append(' ').append(extra.value)
                    else -> builder.append(" --es ").append(shellToken(extra.key)).append(' ').append(shellToken(extra.value))
                }
            }
            return builder.toString()
        }

        fun shellToken(value: String): String = "'" + value.replace("'", "'\\''") + "'"

        fun describe(action: AutomationActionDefinition): String = when (action.type.lowercase()) {
            "adb.shell" -> "ADB: ${action.parameters["command"].orEmpty()}"
            "ai.prompt" -> "调用 AI"
            "companion.call" -> "Companion: ${action.parameters["operation"].orEmpty()}"
            "condition.wait" -> "等待设备条件"
            "delay" -> "等待 ${action.parameters["milliseconds"] ?: "0"} ms"
            "log" -> action.parameters["message"] ?: "记录日志"
            else -> action.type
        }
    }
}
