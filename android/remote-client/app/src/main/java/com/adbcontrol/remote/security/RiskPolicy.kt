package com.adbcontrol.remote.security

/**
 * 操作风险分级：危险操作必须显式确认，禁止静默执行。
 * 与桌面端一致：重启/清数据/卸载/删除类为 DANGER；安装/推送类为 CONFIRM；其余 NORMAL。
 */
enum class OperationRisk { NORMAL, CONFIRM, DANGER }

object RiskPolicy {
    private val dangerTokens = setOf(
        "uninstall", "clear", "reboot", "factory-reset", "delete", "rm", "flash", "dd ", "mkfs",
        "settings put", "setprop", "disable", "enable-user",
    )
    private val confirmTokens = setOf("install", "push", "pull", "resetPassword", "assign", "shell", "pm", "am", "mkdir", "mv ", "cp ")

    fun classify(method: String, arguments: List<String> = emptyList()): OperationRisk {
        val text = (listOf(method) + arguments).joinToString(" ").lowercase()
        return when {
            dangerTokens.any(text::contains) -> OperationRisk.DANGER
            confirmTokens.any(text::contains) -> OperationRisk.CONFIRM
            else -> OperationRisk.NORMAL
        }
    }

    enum class Decision { ALLOW, CONFIRM, DANGER }

    /** 终端整行 shell 命令分级：首 token + 危险词命中（词边界匹配，避免误伤 `ls` 中包含的子串）。 */
    fun classify(command: String): Decision {
        val normalized = " " + command.trim().lowercase() + " "
        val firstToken = command.trim().lowercase().substringBefore(' ')
        val dangerHit = dangerTokens.any { token ->
            normalized.contains(" " + token.trim()) || firstToken == token.trim()
        }
        if (dangerHit) return Decision.DANGER
        val confirmHit = confirmTokens.any { normalized.contains(" " + it.trim()) || firstToken == it.trim() }
        return if (confirmHit) Decision.CONFIRM else Decision.ALLOW
    }
}
