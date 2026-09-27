package com.adbcontrol.remote.data.automation

import com.adbcontrol.remote.model.AutomationCommandResult
import com.adbcontrol.remote.model.AutomationConditionDefinition
import com.adbcontrol.remote.model.AutomationConditionMode
import com.adbcontrol.remote.model.AutomationConditionResult
import com.adbcontrol.remote.model.AutomationExecutionException
import java.time.LocalTime
import java.time.ZoneId

/**
 * 条件求值：解析规则与操作符语义逐条对齐桌面端 `AutomationConditionEvaluator.cs`。
 * 通过 [AutomationGateway] 抽象设备访问，保证解析逻辑可被纯 JVM 契约测试覆盖。
 * 说明：全部为阻塞调用，由调度器/引擎的工作线程执行；不引入协程依赖。
 */
interface AutomationGateway {
    /** 设备是否可达（桌面端 `device.connected` 条件使用心跳等价探测）。 */
    fun isConnected(deviceId: String): Boolean

    /** 执行 `adb -s <serial> shell <command>` 等价命令，返回 stdout/stderr/exitCode。 */
    fun shell(deviceId: String, command: String): AutomationCommandResult

    /** 调用伴侣能力，成功时返回结果 JSON 文本；失败时 success=false 且 stderr 带错误信息。 */
    fun companion(deviceId: String, capabilityId: String, operation: String, argsJson: String): AutomationCommandResult

    /** 屏幕分辨率（桌面端用 `wm size` 获取）。 */
    fun screenSize(deviceId: String): ScreenSize

    data class ScreenSize(val width: Int, val height: Int)
}

class ConditionEvaluator(
    private val gateway: AutomationGateway,
    /** 可注入的等待实现，便于测试与统一的中断处理。 */
    private val sleeper: (Long) -> Unit = { Thread.sleep(it) },
) {

    fun evaluate(deviceId: String, condition: AutomationConditionDefinition): AutomationConditionResult {
        val expected = condition.parameters["value"] ?: "true"
        val actual = readActualValue(deviceId, condition)
        var matched = compare(actual, condition.operator, expected, condition.caseSensitive)
        if (condition.negate) matched = !matched
        return AutomationConditionResult(matched, actual, "${condition.type} ${condition.operator} $expected")
    }

    fun evaluateGroup(
        deviceId: String,
        conditions: List<AutomationConditionDefinition>,
        mode: AutomationConditionMode,
    ): Boolean {
        if (conditions.isEmpty()) return true
        if (mode == AutomationConditionMode.ALL) {
            conditions.forEach { if (!evaluate(deviceId, it).matched) return false }
            return true
        }
        conditions.forEach { if (evaluate(deviceId, it).matched) return true }
        return false
    }

    private fun readActualValue(deviceId: String, condition: AutomationConditionDefinition): String {
        val parameters = condition.parameters
        return when (condition.type.lowercase()) {
            "device.connected", "device.authorized" -> gateway.isConnected(deviceId).toString()
            "device.property" -> query(deviceId, "getprop ${shellToken(required(parameters, "name"))}")
            "screen.on" -> parseScreenOn(query(deviceId, "dumpsys power")).toString()
            "screen.locked" -> parseLocked(query(deviceId, "dumpsys window policy"))
            "screen.orientation" -> parseOrientation(query(deviceId, "dumpsys input"))
            "app.foreground" -> parseForeground(query(deviceId, "dumpsys activity activities")).first
            "activity.foreground" -> parseForeground(query(deviceId, "dumpsys activity activities")).second
            "app.installed" ->
                query(deviceId, "pm path ${shellToken(required(parameters, "packageName"))}").isNotBlank().toString()
            "process.running" ->
                query(deviceId, "pidof ${shellToken(required(parameters, "process"))}").isNotBlank().toString()
            "webpage.open", "ui.element", "ui.text" -> queryUi(deviceId)
            "notification.present" -> query(deviceId, "dumpsys notification --noredact")
            "battery.level" -> parseNamedNumber(query(deviceId, "dumpsys battery"), "level")
            "battery.charging" -> parseCharging(query(deviceId, "dumpsys battery")).toString()
            "battery.temperature" -> formatTemperature(parseNamedNumber(query(deviceId, "dumpsys battery"), "temperature"))
            "network.connected" -> parseNetworkConnected(query(deviceId, "dumpsys connectivity")).toString()
            "network.type" -> parseNetworkType(query(deviceId, "dumpsys connectivity"))
            "wifi.ssid" -> parseSsid(query(deviceId, "dumpsys wifi"))
            "internet.reachable" -> tryQuery(deviceId, "ping -c 1 -W 2 1.1.1.1").success.toString()
            "bluetooth.enabled" -> normalizeSetting(query(deviceId, "settings get global bluetooth_on"))
            "airplane.enabled" -> normalizeSetting(query(deviceId, "settings get global airplane_mode_on"))
            "location.enabled" -> (parseDouble(query(deviceId, "settings get secure location_mode")) > 0).toString()
            "dnd.enabled" -> (parseDouble(query(deviceId, "settings get global zen_mode")) > 0).toString()
            "setting.value" -> query(
                deviceId,
                "settings get ${settingNamespace(parameters)} ${shellToken(required(parameters, "name"))}",
            )
            "call.state" -> parseCallState(query(deviceId, "dumpsys telephony.registry"))
            "headset.connected" -> parseHeadset(query(deviceId, "dumpsys audio")).toString()
            "file.exists" -> tryQuery(deviceId, "test -e ${shellToken(required(parameters, "path"))}").success.toString()
            // 桌面端这里写的是 android.clipboard；实际伴侣目录里剪贴板读取能力 id 是 android.clipboard.read。
            "clipboard.contains" -> companionCall(deviceId, "android.clipboard.read", "clipboard.read", "{}")
            "companion.installed" -> query(deviceId, "pm path com.adbcontrol.companion").isNotBlank().toString()
            "companion.accessibility.ready" ->
                parseCompanionBoolean(companionCall(deviceId, "android.accessibility.control", "accessibility.status", "{}"), "enabled")
            "companion.output" -> companionCall(
                deviceId,
                required(parameters, "capabilityId"),
                required(parameters, "operation"),
                parameters["args"] ?: "{}",
            )
            "adb.output" -> query(deviceId, required(parameters, "command"))
            "time.window" -> isInTimeWindow(parameters).toString()
            else -> throw AutomationExecutionException("CONDITION_UNSUPPORTED", "不支持的条件：${condition.type}。", "automation.condition")
        }
    }

    private fun query(deviceId: String, command: String): String {
        val result = tryQuery(deviceId, command)
        if (!result.success) {
            throw AutomationExecutionException(
                "CONDITION_ADB_FAILED",
                result.stderr.trim().ifBlank { "ADB 条件命令失败，退出码 ${result.exitCode}。" },
                "automation.condition",
                recoverable = true,
                suggestion = "确认设备在线、已授权且当前 Android 版本允许读取该状态。",
            )
        }
        return result.stdout.trim()
    }

    private fun tryQuery(deviceId: String, command: String): AutomationCommandResult = gateway.shell(deviceId, command)

    private fun companionCall(deviceId: String, capabilityId: String, operation: String, argsJson: String): String {
        val result = gateway.companion(deviceId, capabilityId, operation, argsJson)
        if (!result.success) {
            throw AutomationExecutionException(
                "CONDITION_COMPANION_FAILED",
                result.stderr.trim().ifBlank { "Companion 条件调用失败。" },
                "automation.condition",
                recoverable = true,
                suggestion = "确认伴侣 App 已安装且对应权限已开启。",
            )
        }
        return result.combinedOutput
    }

    private fun queryUi(deviceId: String): String = query(
        deviceId,
        "sh -c 'uiautomator dump --compressed /sdcard/adbcontrol-automation.xml >/dev/null && cat /sdcard/adbcontrol-automation.xml && rm /sdcard/adbcontrol-automation.xml'",
    )

    companion object {
        // 操作符语义与桌面端 `Compare` 一致；契约测试必须覆盖全部操作符分支。
        fun compare(actual: String, comparisonOperator: String, expected: String, caseSensitive: Boolean): Boolean {
            val op = comparisonOperator.lowercase()
            val a = actual
            val e = expected
            val exact = caseSensitive
            fun eq(x: String, y: String) = if (exact) x == y else x.equals(y, ignoreCase = true)
            fun contains(x: String, y: String) = if (exact) x.contains(y) else x.contains(y, ignoreCase = true)
            fun starts(x: String, y: String) = if (exact) x.startsWith(y) else x.startsWith(y, ignoreCase = true)
            fun ends(x: String, y: String) = if (exact) x.endsWith(y) else x.endsWith(y, ignoreCase = true)
            return when (op) {
                "equals" -> eq(a, e)
                "notequals" -> !eq(a, e)
                "contains" -> contains(a, e)
                "notcontains" -> !contains(a, e)
                "startswith" -> starts(a, e)
                "endswith" -> ends(a, e)
                "regex" -> regexMatches(a, e, caseSensitive)
                "greaterthan" -> numeric(a, e) { l, r -> l > r }
                "greaterthanorequal" -> numeric(a, e) { l, r -> l >= r }
                "lessthan" -> numeric(a, e) { l, r -> l < r }
                "lessthanorequal" -> numeric(a, e) { l, r -> l <= r }
                "in" -> e.split(',').map(String::trim).filter(String::isNotEmpty).any { eq(a, it) }
                "exists" -> a.isNotBlank()
                "truthy" -> isTruthy(a)
                else -> false
            }
        }

        fun parseScreenOn(output: String): Boolean =
            output.contains("mWakefulness=Awake", ignoreCase = true) ||
                output.contains("Display Power: state=ON", ignoreCase = true) ||
                Regex("mScreenOn\\s*=\\s*true", RegexOption.IGNORE_CASE).containsMatchIn(output)

        fun parseLocked(output: String): String {
            val trueMatch = Regex("(?:mKeyguardShowing|isStatusBarKeyguard|keyguardShowing)\\s*=\\s*true", RegexOption.IGNORE_CASE)
                .containsMatchIn(output)
            val falseMatch = Regex("(?:mKeyguardShowing|isStatusBarKeyguard|keyguardShowing)\\s*=\\s*false", RegexOption.IGNORE_CASE)
                .containsMatchIn(output)
            return when {
                trueMatch -> "true"
                falseMatch -> "false"
                else -> "unknown"
            }
        }

        fun parseForeground(output: String): Pair<String, String> {
            val match = ForegroundActivityRegex.find(output) ?: return "" to ""
            return match.groupValues[1] to match.groupValues[2]
        }

        fun parseOrientation(output: String): String {
            val match = Regex("SurfaceOrientation:\\s*(\\d)", RegexOption.IGNORE_CASE).find(output) ?: return ""
            return when (match.groupValues[1]) {
                "0" -> "portrait"
                "1" -> "landscape"
                "2" -> "reversePortrait"
                "3" -> "reverseLandscape"
                else -> match.groupValues[1]
            }
        }

        fun parseNamedNumber(output: String, name: String): String {
            val match = Regex("(?m)^\\s*${Regex.escape(name)}:\\s*(-?\\d+(?:\\.\\d+)?)\\s*$", RegexOption.IGNORE_CASE)
                .find(output) ?: return ""
            return match.groupValues[1]
        }

        fun parseCharging(output: String): Boolean {
            val status = parseNamedNumber(output, "status")
            return status == "2" || status == "5" ||
                Regex("(?m)^\\s*(AC|USB|Wireless) powered:\\s*true", RegexOption.IGNORE_CASE).containsMatchIn(output)
        }

        fun parseNetworkConnected(output: String): Boolean =
            Regex("CONNECTED|VALIDATED|isAvailable\\(\\)=true", RegexOption.IGNORE_CASE).containsMatchIn(output) &&
                !output.contains("DISCONNECTED", ignoreCase = true)

        fun parseNetworkType(output: String): String = when {
            Regex("TRANSPORT_WIFI|type:\\s*WIFI|type=WIFI", RegexOption.IGNORE_CASE).containsMatchIn(output) -> "wifi"
            Regex("TRANSPORT_CELLULAR|type:\\s*MOBILE|type=MOBILE", RegexOption.IGNORE_CASE).containsMatchIn(output) -> "cellular"
            Regex("TRANSPORT_ETHERNET|type:\\s*ETHERNET", RegexOption.IGNORE_CASE).containsMatchIn(output) -> "ethernet"
            Regex("TRANSPORT_VPN|type:\\s*VPN", RegexOption.IGNORE_CASE).containsMatchIn(output) -> "vpn"
            parseNetworkConnected(output) -> "other"
            else -> "offline"
        }

        fun parseSsid(output: String): String {
            val match = Regex("(?:SSID|mWifiInfo).*?SSID[:=]\\s*\"?(?<ssid>[^,\\r\\n\"]+)", RegexOption.IGNORE_CASE).find(output)
                ?: Regex("SSID:\\s*(?<ssid>[^,\\r\\n]+)").find(output)
            val value = match?.groups?.get("ssid")?.value?.trim()?.trim('"') ?: ""
            return if (value.contains("unknown ssid", ignoreCase = true)) "" else value
        }

        fun parseCallState(output: String): String {
            val match = Regex("mCallState\\s*=\\s*(\\d+)", RegexOption.IGNORE_CASE).find(output) ?: return ""
            return when (val raw = match.groupValues[1]) {
                "0" -> "idle"
                "1" -> "ringing"
                "2" -> "offhook"
                else -> raw
            }
        }

        fun parseHeadset(output: String): Boolean =
            Regex("(?:headset|headphones|usb_headset|a2dp)[^\\r\\n]*(?:connected|state\\s*=\\s*1|available)", RegexOption.IGNORE_CASE)
                .containsMatchIn(output)

        fun normalizeSetting(output: String): String = (parseDouble(output) > 0).toString()

        fun isTruthy(value: String): Boolean =
            value.equals("true", ignoreCase = true) ||
                value.equals("yes", ignoreCase = true) ||
                value.equals("on", ignoreCase = true) ||
                (value.trim().toDoubleOrNull()?.let { it != 0.0 } ?: false)

        fun parseDouble(value: String): Double = value.trim().toDoubleOrNull() ?: 0.0

        fun formatTemperature(raw: String): String {
            val number = parseDouble(raw) / 10.0
            return java.lang.String.format(java.util.Locale.US, "%.1f", number)
        }

        fun parseCompanionBoolean(output: String, propertyName: String): String {
            // 桌面端从广播 data= 行提取 JSON；QUIC 通道返回的就是结果对象本身，两种形态都兼容。
            val json = extractJsonCandidate(output) ?: return "unknown"
            val enabled = Regex("\"$propertyName\"\\s*:\\s*(true|false)").find(json) ?: return "unknown"
            return enabled.groupValues[1]
        }

        private fun extractJsonCandidate(output: String): String? = output.lineSequence()
            .map { line -> line.indexOf("data=", ignoreCase = true).let { if (it >= 0) line.substring(it + 5).trim() else line.trim() } }
            .firstOrNull { it.startsWith("{") }

        private fun settingNamespace(parameters: Map<String, String>): String {
            val value = (parameters["namespace"] ?: "global").lowercase()
            return if (value in setOf("global", "secure", "system")) value else "global"
        }

        fun isInTimeWindow(parameters: Map<String, String>, zone: ZoneId = ZoneId.systemDefault()): Boolean {
            val start = parseLocalTime(parameters["start"]) ?: return false
            val end = parseLocalTime(parameters["end"]) ?: return false
            val now = LocalTime.now(zone)
            return if (start <= end) now >= start && now <= end else now >= start || now <= end
        }

        private fun parseLocalTime(value: String?): LocalTime? = try {
            LocalTime.parse(value.orEmpty().trim())
        } catch (_: Exception) {
            null
        }

        fun required(parameters: Map<String, String>, name: String): String {
            val value = parameters[name]
            if (value.isNullOrBlank()) {
                throw AutomationExecutionException("CONDITION_PARAMETER_MISSING", "条件缺少参数 $name。", "automation.condition")
            }
            return value
        }

        fun shellToken(value: String): String = "'" + value.replace("'", "'\\''") + "'"

        private fun regexMatches(actual: String, pattern: String, caseSensitive: Boolean): Boolean = try {
            val options = if (caseSensitive) emptySet() else setOf(RegexOption.IGNORE_CASE)
            Regex(pattern, options).containsMatchIn(actual)
        } catch (_: Exception) {
            false
        }

        private fun numeric(actual: String, expected: String, comparison: (Double, Double) -> Boolean): Boolean {
            val left = actual.trim().toDoubleOrNull() ?: return false
            val right = expected.trim().toDoubleOrNull() ?: return false
            return comparison(left, right)
        }

        private val ForegroundActivityRegex = Regex(
            "(?:mResumedActivity|topResumedActivity|ResumedActivity)[^\\r\\n]*?\\s([A-Za-z0-9._]+?)/([A-Za-z0-9._$]+)",
            RegexOption.IGNORE_CASE,
        )
    }
}
