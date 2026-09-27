package com.adbcontrol.remote.model

/**
 * 自动化任务模型：与 Windows 桌面端 `Models/AutomationModels.cs` 的 JSON DSL 逐字段对齐。
 * 本文件保持纯 Kotlin（不依赖 android SDK 与 org.json），调度、校验、条件求值和动作命令构建
 * 全部基于这些类型，可被纯 JVM 契约测试锁定。
 */

enum class AutomationConcurrencyPolicy(val title: String) {
    SKIP("跳过"), QUEUE("排队"), RESTART("重新运行"), PARALLEL("并行");
}

enum class AutomationConditionMode(val title: String) {
    ALL("全部满足"), ANY("任一满足");
}

enum class AutomationRunStatus { QUEUED, RUNNING, PAUSED, SUCCEEDED, FAILED, STOPPED, SKIPPED }

enum class AutomationStepStatus { RUNNING, SUCCEEDED, FAILED, STOPPED }

data class AutomationPermissionSet(
    val allowAdb: Boolean = false,
    val allowShell: Boolean = false,
    val allowCompanion: Boolean = false,
    val allowAi: Boolean = false,
    val allowAiDeviceTools: Boolean = false,
    val allowTaskMutation: Boolean = false,
    val allowUnlock: Boolean = false,
)

data class AutomationConditionDefinition(
    val type: String,
    val operator: String = "equals",
    val caseSensitive: Boolean = false,
    val negate: Boolean = false,
    val parameters: Map<String, String> = emptyMap(),
)

data class AutomationTriggerDefinition(
    val id: String,
    // manual | once | daily | weekly | interval | cron | condition
    val type: String = "manual",
    val timeZoneId: String = "",
    val at: String? = null,
    val days: List<Int> = emptyList(), // 1=周一 … 7=周日（ISO），与桌面 DayOfWeek 换算见序列化层
    val intervalSeconds: Int = 3600,
    val runAtEpochMs: Long? = null,
    val cron: String? = null,
    val catchUp: Boolean = false,
    val pollIntervalSeconds: Int = 2,
    val edgeOnly: Boolean = true,
    val cooldownSeconds: Int = 60,
    val conditionMode: AutomationConditionMode = AutomationConditionMode.ALL,
    val conditions: List<AutomationConditionDefinition> = emptyList(),
)

data class AutomationActionDefinition(
    val id: String,
    val type: String,
    val parameters: Map<String, String> = emptyMap(),
    val conditionMode: AutomationConditionMode = AutomationConditionMode.ALL,
    val conditions: List<AutomationConditionDefinition> = emptyList(),
    val actions: List<AutomationActionDefinition> = emptyList(),
    val elseActions: List<AutomationActionDefinition> = emptyList(),
)

data class AutomationTaskDefinition(
    val schemaVersion: Int = 1,
    val id: String,
    val name: String,
    val description: String = "",
    val deviceId: String = "",
    val enabled: Boolean = true,
    val concurrencyPolicy: AutomationConcurrencyPolicy = AutomationConcurrencyPolicy.SKIP,
    val permissions: AutomationPermissionSet = AutomationPermissionSet(),
    val triggers: List<AutomationTriggerDefinition> = emptyList(),
    val actions: List<AutomationActionDefinition> = emptyList(),
    val createdAtEpochMs: Long,
    val updatedAtEpochMs: Long,
)

data class AutomationRunRecord(
    val id: String,
    val taskId: String,
    val taskName: String,
    val trigger: String,
    val status: AutomationRunStatus = AutomationRunStatus.QUEUED,
    val createdAtEpochMs: Long,
    val startedAtEpochMs: Long? = null,
    val completedAtEpochMs: Long? = null,
    val totalSteps: Int = 1,
    val completedSteps: Int = 0,
    val progress: Double = 0.0,
    val currentStep: String = "",
    val errorCode: String = "",
    val errorMessage: String = "",
    val errorModule: String = "",
    val errorRecoverable: Boolean = false,
    val errorSuggestion: String = "",
    val traceId: String,
)

data class AutomationRunStep(
    val id: String,
    val runId: String,
    val actionId: String,
    val actionType: String,
    val sequence: Int,
    val status: AutomationStepStatus = AutomationStepStatus.RUNNING,
    val startedAtEpochMs: Long,
    val completedAtEpochMs: Long? = null,
    val output: String = "",
    val errorCode: String = "",
    val errorMessage: String = "",
)

data class AutomationTriggerState(
    val taskId: String,
    val triggerId: String,
    val lastFiredAtEpochMs: Long? = null,
    val lastEvaluatedAtEpochMs: Long? = null,
    val lastConditionValue: Boolean? = null,
)

data class AutomationCommandResult(
    val success: Boolean,
    val exitCode: Int,
    val stdout: String,
    val stderr: String,
) {
    val combinedOutput: String
        get() = listOf(stdout.trim(), stderr.trim()).filter(String::isNotBlank).joinToString("\n")
}

data class AutomationConditionResult(val matched: Boolean, val actual: String, val description: String)

/** 与桌面端 `AutomationExecutionException` 对齐的结构化失败，错误码保持同一命名空间。 */
class AutomationExecutionException(
    val errorCode: String,
    override val message: String,
    val module: String,
    val recoverable: Boolean = false,
    val suggestion: String = "",
) : Exception(message)
