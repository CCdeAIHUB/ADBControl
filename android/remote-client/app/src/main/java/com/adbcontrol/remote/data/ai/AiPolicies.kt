package com.adbcontrol.remote.data.ai

import com.adbcontrol.remote.model.AiPermissionMode

/**
 * AI 安全策略：规则逐条对齐桌面端 `AiAgentToolService.cs` 的只读前缀白名单、拒绝词表
 * 与低风险伴侣操作列表。纯 Kotlin，可被契约测试锁定。
 */
object AiPolicies {

    /** “替我审批”模式下允许自动执行的 shell 只读前缀（与桌面一致）。 */
    private val readOnlyPrefixes = listOf(
        "getprop", "dumpsys", "pm list", "cmd package list", "ls", "cat ", "/proc/", "df", "du ",
        "wm size", "wm density", "ip addr", "date", "id", "whoami",
    )

    /** 明确拒绝自动执行的破坏性 token（即使完全访问模式下也应提醒；审批模式下直接拒绝）。 */
    private val denyTokens = listOf(
        "rm ", "reboot", "svc power", "input ", "am force-stop", "pm clear", "pm uninstall",
        "pm disable", "pm enable", "settings put", "setprop", "cmd package", "monkey",
    )

    /** 低风险伴侣操作（桌面端 lowRiskCompanionOperations 同款），自动放行。 */
    private val lowRiskCompanionOperations = setOf(
        "accessibility.status", "input.text", "input.key",
        "accessibility.global.back", "accessibility.global.home", "accessibility.global.recents",
        "accessibility.global.notifications", "accessibility.global.quickSettings", "accessibility.global.powerDialog",
        "accessibility.touch.tap", "accessibility.touch.swipe", "accessibility.screenshot",
        "volume.get", "app.list", "sensor.subscribe", "sensor.unsubscribe",
    )

    /** shell 命令分级：ALLOW=自动放行，CONFIRM=需要用户确认，DENY=直接拒绝。 */
    fun classifyShell(command: String, mode: AiPermissionMode): ShellDecision {
        val trimmed = command.trim()
        if (mode == AiPermissionMode.FULL_ACCESS) {
            // 完全访问模式继承桌面语义：全部自动执行（用户已显式承担风险）。
            return ShellDecision.ALLOW
        }
        if (mode == AiPermissionMode.APPROVE_EVERY) {
            // 请求批准模式继承桌面语义：一切命令都需要用户确认。
            return ShellDecision.CONFIRM
        }
        val denyHit = denyTokens.any { token ->
            trimmed.startsWith(token) || trimmed.contains(" ${token.trim()}") || trimmed.contains("|${token.trim()}")
        }
        if (denyHit) return ShellDecision.DENY
        val readOnly = readOnlyPrefixes.any { trimmed.startsWith(it) }
        return if (readOnly) ShellDecision.ALLOW else ShellDecision.CONFIRM
    }

    fun isLowRiskCompanion(operation: String): Boolean = operation.lowercase() in lowRiskCompanionOperations

    /** 携带图片的消息要求已验证多模态的模型（桌面端 AI_MODEL_MULTIMODAL_REQUIRED 同款约束）。 */
    fun multimodalRequired(hasImage: Boolean, multimodalVerified: Boolean): Boolean = hasImage && !multimodalVerified

    enum class ShellDecision { ALLOW, CONFIRM, DENY }
}
