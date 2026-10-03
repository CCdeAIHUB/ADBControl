package com.adbcontrol.remote.core

import android.content.Context
import com.adbcontrol.remote.data.RemoteRepository
import com.adbcontrol.remote.data.adb.DeviceCommandGateway
import com.adbcontrol.remote.data.ai.AiAgentRuntime
import com.adbcontrol.remote.data.ai.AiClient
import com.adbcontrol.remote.data.ai.AiConversationStore
import com.adbcontrol.remote.data.ai.AiModelStore
import com.adbcontrol.remote.data.automation.AutomationEngine
import com.adbcontrol.remote.data.automation.AutomationScheduler
import com.adbcontrol.remote.data.automation.AutomationStore
import com.adbcontrol.remote.data.automation.ConditionEvaluator
import com.adbcontrol.remote.data.companion.CompanionGateway
import com.adbcontrol.remote.data.log.AppDiagnostics
import com.adbcontrol.remote.data.settings.SettingsStore
import com.adbcontrol.remote.model.AiPermissionMode
import com.adbcontrol.remote.model.AutomationTaskDefinition
import com.adbcontrol.remote.model.CoreProfile
import com.adbcontrol.remote.model.Session
import com.adbcontrol.remote.transport.QuicRemoteTransport
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors

/**
 * 组合根：全部单例在此装配，页面只依赖自己需要的成员。
 * 唯一的全局可变状态是会话（session/sessionProfile），由 MainActivity 生命周期管理。
 */
class AppGraph(context: Context) {

    val appContext: Context = context.applicationContext
    val executor: ExecutorService = Executors.newFixedThreadPool(4)
    val transport = QuicRemoteTransport()
    val repository = RemoteRepository(transport)
    val companion = CompanionGateway(repository)
    val commands = DeviceCommandGateway(repository)
    val hardwareMonitor = com.adbcontrol.remote.transport.RemoteHardwareMonitorClient()
    val remoteCompanion = com.adbcontrol.remote.transport.RemoteCompanionClient()
    val settings = SettingsStore(appContext)
    val aiModelStore = AiModelStore(appContext)
    val aiConversation = AiConversationStore(appContext)
    val aiClient = AiClient()
    val automationStore = AutomationStore(appContext)
    private val automationGateway = commands.automationGateway()
    val engine = AutomationEngine(automationStore, executor, automationGateway, ConditionEvaluator(automationGateway))
    val scheduler = AutomationScheduler(automationStore, engine)
    @Volatile var activeProfile: CoreProfile? = null
    @Volatile var activeSession: Session? = null
    private val diagnosticReporter = com.adbcontrol.remote.data.log.RemoteDiagnosticReporter(repository)

    /** 主界面使用的交互式 AI 运行时（Host 由 MainActivity 注入）。 */
    val interactiveAiRuntime = AiAgentRuntime(
        aiClient, aiConversation, commands, companion, automationStore,
        taskRunner = { taskId -> automationStore.getTask(taskId)?.let { scheduler.runNow(it) } ?: false },
    )

    init {
        ThemeManager.apply(settings.themeMode)
        scheduler.aiExecutor = AutomationEngine.AiExecutor { prompt, modelId, allowDeviceTools, allowTaskMutation ->
            executeAutomationAi(prompt, modelId, allowDeviceTools, allowTaskMutation)
        }
        diagnosticReporter.start()
    }

    /**
     * 自动化 ai.prompt 的执行器：在任务工作线程上同步运行完整 AI 工具环。
     * 无 UI 交互：确认类动作一律拒绝（显式失败），与任务权限语义一致——
     * allowAiDeviceTools 才进入 FULL_ACCESS，否则维持只读审批模式。
     */
    private fun executeAutomationAi(
        prompt: String,
        modelId: String,
        allowDeviceTools: Boolean,
        allowTaskMutation: Boolean,
    ): String {
        val model = modelId.takeIf(String::isNotBlank)?.let { aiModelStore.get(it) }
            ?: aiModelStore.get(settings.preferredAiModelId)
            ?: aiModelStore.list().firstOrNull()
            ?: throw com.adbcontrol.remote.model.AutomationExecutionException(
                "ACTION_AI_NOT_CONFIGURED", "没有可用的 AI 模型", "automation.ai", true,
                "先在 AI 助手中添加模型。",
            )
        val runtime = AiAgentRuntime(
            aiClient, AiConversationStore(appContext), commands, companion, automationStore,
            taskRunner = { taskId -> automationStore.getTask(taskId)?.let { scheduler.runNow(it) } ?: false },
        )
        runtime.host = nonInteractiveHost
        runtime.permissionMode = if (allowDeviceTools) AiPermissionMode.FULL_ACCESS else AiPermissionMode.AUTO_READONLY
        runtime.preferredModel = model
        val result = runtime.sendUserMessage(
            prompt, imageBase64 = "",
            onEvent = { /* 后台执行，无 UI 流式 */ },
            isCancelled = { false },
            onToolRun = { },
        )
        AppDiagnostics.record(
            "info", "automation.ai", "ai.agent", result is com.adbcontrol.remote.model.RemoteResult.Success, 0,
            (result as? com.adbcontrol.remote.model.RemoteResult.Failure)?.error?.errorCode ?: "",
            "allowDeviceTools=$allowDeviceTools allowTaskMutation=$allowTaskMutation",
        )
        return when (result) {
            is com.adbcontrol.remote.model.RemoteResult.Success -> result.value
            is com.adbcontrol.remote.model.RemoteResult.Failure -> throw com.adbcontrol.remote.model.AutomationExecutionException(
                result.error.errorCode, result.error.message, "automation.ai", result.error.recoverable,
                result.error.suggestion.orEmpty(),
            )
        }
    }

    private val nonInteractiveHost = object : AiAgentRuntime.Host {
        override fun currentDeviceId(): String? = null
        override fun availableDevices(): List<Triple<String, String, String>> = emptyList()
        override fun confirm(title: String, detail: String): Boolean = false
        override fun askChoice(question: String, options: List<String>, multiSelect: Boolean): List<String>? = null
    }

    fun preferredAiModel(): com.adbcontrol.remote.model.AiModelConfig? =
        aiModelStore.get(settings.preferredAiModelId) ?: aiModelStore.list().firstOrNull()

    fun shutdown() {
        diagnosticReporter.stop()
        scheduler.stop()
        transport.close()
        executor.shutdownNow()
    }
}

/** 自动化任务触发标签（供任务页展示）。 */
fun triggerSummary(task: AutomationTaskDefinition): String = task.triggers.joinToString("；") { trigger ->
    when (trigger.type.lowercase()) {
        "manual" -> "手动"
        "once" -> "单次"
        "daily" -> "每天 ${trigger.at.orEmpty()}"
        "weekly" -> "每周${trigger.days.joinToString("、")} ${trigger.at.orEmpty()}"
        "interval" -> "每 ${trigger.intervalSeconds} 秒"
        "cron" -> "Cron ${trigger.cron.orEmpty()}"
        "condition" -> "条件触发"
        else -> trigger.type
    }
}
