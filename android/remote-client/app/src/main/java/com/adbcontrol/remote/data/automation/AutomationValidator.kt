package com.adbcontrol.remote.data.automation

import com.adbcontrol.remote.model.AutomationActionDefinition
import com.adbcontrol.remote.model.AutomationConditionDefinition
import com.adbcontrol.remote.model.AutomationExecutionException
import com.adbcontrol.remote.model.AutomationTaskDefinition
import com.adbcontrol.remote.model.AutomationTriggerDefinition

/**
 * 任务校验：规则与桌面端 `AutomationTaskValidator.cs` 对齐——至少一个触发器、
 * 触发器参数合法、动作类型受支持、必填参数齐全。校验失败抛结构化错误，不静默通过。
 */
object AutomationValidator {

    val knownActionTypes = setOf(
        "adb.shell", "adb.keyevent", "adb.tap", "adb.swipe", "adb.text",
        "app.start", "url.open", "intent.start", "device.wake", "device.lock", "device.unlock",
        "companion.call", "ai.prompt", "delay", "condition.wait", "flow.if", "flow.repeat", "flow.parallel",
        "log", "fail",
    )

    val knownConditionTypes = setOf(
        "device.connected", "device.authorized", "device.property", "screen.on", "screen.locked",
        "screen.orientation", "app.foreground", "activity.foreground", "app.installed", "process.running",
        "webpage.open", "ui.element", "ui.text", "notification.present", "battery.level", "battery.charging",
        "battery.temperature", "network.connected", "network.type", "wifi.ssid", "internet.reachable",
        "bluetooth.enabled", "airplane.enabled", "location.enabled", "dnd.enabled", "setting.value",
        "call.state", "headset.connected", "file.exists", "clipboard.contains", "companion.installed",
        "companion.accessibility.ready", "companion.output", "adb.output", "time.window",
    )

    val knownOperators = setOf(
        "equals", "notequals", "contains", "notcontains", "startswith", "endswith", "regex",
        "greaterthan", "greaterthanorequal", "lessthan", "lessthanorequal", "in", "exists", "truthy",
    )

    fun validate(task: AutomationTaskDefinition) {
        if (task.name.isBlank()) throw validationError("任务名称不能为空。")
        if (task.actions.isEmpty()) throw validationError("任务至少需要一个动作。")
        if (task.triggers.isEmpty()) throw validationError("任务至少需要一个触发器。")
        task.triggers.forEach(::validateTrigger)
        task.actions.forEach { validateAction(it, depth = 0) }
    }

    private fun validateTrigger(trigger: AutomationTriggerDefinition) {
        when (trigger.type.lowercase()) {
            "manual" -> Unit
            "once" -> if (trigger.runAtEpochMs == null) throw validationError("单次触发缺少执行时间。")
            "daily" -> {
                parseAtOrThrow(trigger.at)
            }
            "weekly" -> {
                parseAtOrThrow(trigger.at)
                if (trigger.days.isEmpty() || trigger.days.any { it !in 1..7 }) {
                    throw validationError("每周触发器必须选择 1-7 的有效星期。")
                }
            }
            "interval" -> if (trigger.intervalSeconds < 1) throw validationError("间隔触发器至少 1 秒。")
            "cron" -> try {
                CronExpression.parse(trigger.cron.orEmpty())
            } catch (error: AutomationExecutionException) {
                throw validationError(error.message)
            }
            "condition" -> {
                if (trigger.pollIntervalSeconds < 1) throw validationError("条件触发器轮询间隔至少 1 秒。")
                if (trigger.conditions.isEmpty()) throw validationError("条件触发器必须配置条件。")
                trigger.conditions.forEach(::validateCondition)
            }
            else -> throw validationError("不支持的触发器类型：${trigger.type}。")
        }
    }

    private fun validateCondition(condition: AutomationConditionDefinition) {
        if (condition.type.lowercase() !in knownConditionTypes) {
            throw validationError("不支持的条件：${condition.type}。")
        }
        if (condition.operator.lowercase() !in knownOperators) {
            throw validationError("不支持的条件操作符：${condition.operator}。")
        }
    }

    private fun validateAction(action: AutomationActionDefinition, depth: Int) {
        if (depth > 8) throw validationError("嵌套动作层级过深（最多 8 层）。")
        val type = action.type.lowercase()
        if (type !in knownActionTypes) throw validationError("不支持的动作：${action.type}。")
        val requiredParameter = when (type) {
            "adb.shell" -> "command"
            "adb.keyevent" -> "keyCode"
            "adb.text" -> "text"
            "app.start" -> "packageName"
            "url.open" -> "url"
            "companion.call" -> "capabilityId"
            "ai.prompt" -> "prompt"
            else -> null
        }
        if (requiredParameter != null && action.parameters[requiredParameter].isNullOrBlank()) {
            throw validationError("动作 ${action.type} 缺少参数 $requiredParameter。")
        }
        if (type == "condition.wait" && action.conditions.isEmpty()) {
            throw validationError("condition.wait 必须配置等待条件。")
        }
        action.conditions.forEach(::validateCondition)
        if (type == "flow.if") {
            if (action.conditions.isEmpty()) throw validationError("flow.if 必须配置条件。")
            action.actions.forEach { validateAction(it, depth + 1) }
            action.elseActions.forEach { validateAction(it, depth + 1) }
        }
        if (type == "flow.repeat" || type == "flow.parallel") {
            if (action.actions.isEmpty()) throw validationError("flow.$type 容器必须包含子动作。")
            action.actions.forEach { validateAction(it, depth + 1) }
        }
    }

    private fun parseAtOrThrow(at: String?) {
        try {
            java.time.LocalTime.parse(at.orEmpty().trim())
        } catch (_: Exception) {
            throw validationError("无效的时间：$at。")
        }
    }

    private fun validationError(message: String) =
        AutomationExecutionException("AUTOMATION_TASK_INVALID", message, "automation.validator", recoverable = true)
}
