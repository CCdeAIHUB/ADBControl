package com.adbcontrol.remote.data.automation

import com.adbcontrol.remote.model.AutomationActionDefinition
import com.adbcontrol.remote.model.AutomationConditionMode
import com.adbcontrol.remote.model.AutomationExecutionException
import com.adbcontrol.remote.model.AutomationRunRecord
import com.adbcontrol.remote.model.AutomationRunStatus
import com.adbcontrol.remote.model.AutomationStepStatus
import com.adbcontrol.remote.model.AutomationTaskDefinition
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.ExecutorService
import java.util.concurrent.Future
import java.util.concurrent.atomic.AtomicReference

/**
 * 运行引擎：语义对齐桌面端 `AutomationExecutionEngine.cs`——顺序执行动作树，
 * 支持 flow.if / flow.repeat / flow.parallel，运行与步骤记录写入 AutomationStore，
 * 失败抛结构化错误并落入运行记录（不静默吞掉）。
 */
class AutomationEngine(
    private val store: AutomationStore,
    private val executor: ExecutorService,
    val gateway: AutomationGateway,
    private val conditions: ConditionEvaluator,
    private val sleeper: (Long) -> Unit = { Thread.sleep(it) },
) {
    private val running = ConcurrentHashMap<String, RunHandle>()

    /** 按并发策略提交一次运行；返回 false 表示被策略拒绝（SKIP 且已有活动运行）。 */
    fun submitRun(task: AutomationTaskDefinition, triggerLabel: String, aiExecutor: AiExecutor?): Boolean {
        val active = store.activeRun(task.id)
        if (active != null) {
            when (task.concurrencyPolicy) {
                com.adbcontrol.remote.model.AutomationConcurrencyPolicy.SKIP -> return false
                com.adbcontrol.remote.model.AutomationConcurrencyPolicy.QUEUE -> { /* 直接排队新运行 */ }
                com.adbcontrol.remote.model.AutomationConcurrencyPolicy.RESTART -> stopRun(task.id)
                com.adbcontrol.remote.model.AutomationConcurrencyPolicy.PARALLEL -> Unit
            }
        }
        val totalSteps = countLeafActions(task.actions).coerceAtLeast(1)
        val run = AutomationRunRecord(
            id = UUID.randomUUID().toString().replace("-", ""),
            taskId = task.id,
            taskName = task.name,
            trigger = triggerLabel,
            status = AutomationRunStatus.QUEUED,
            createdAtEpochMs = System.currentTimeMillis(),
            totalSteps = totalSteps,
            traceId = UUID.randomUUID().toString().replace("-", ""),
        )
        store.insertRun(run)
        val handle = RunHandle(task.id)
        running[run.id] = handle
        executor.execute {
            runTask(task, run, aiExecutor, handle)
        }
        return true
    }

    fun stopRun(taskId: String) {
        running.values.filter { it.taskId == taskId }.forEach { it.stopRequested.set(true) }
    }

    fun isRunning(taskId: String): Boolean = running.values.any { it.taskId == taskId }

    private fun runTask(task: AutomationTaskDefinition, run: AutomationRunRecord, aiExecutor: AiExecutor?, handle: RunHandle) {
        store.updateRunStatus(run.id, AutomationRunStatus.RUNNING, startedAt = System.currentTimeMillis())
        try {
            val actionExecutor = ActionExecutor(gateway, conditions, sleeper)
            // fun interface → 函数类型：包装为 ActionExecutor 期望的 lambda。
            val aiLambda: ((String, String, Boolean, Boolean) -> String)? = aiExecutor?.let { executor ->
                { prompt, modelId, tools, mutation -> executor(prompt, modelId, tools, mutation) }
            }
            executeList(task, task.actions, run, actionExecutor, aiLambda, handle, depth = 0)
            finishRun(run.id, AutomationRunStatus.SUCCEEDED)
        } catch (interrupted: RunStoppedException) {
            finishRun(run.id, AutomationRunStatus.STOPPED, currentStep = "已手动停止")
        } catch (error: AutomationExecutionException) {
            finishRun(
                run.id, AutomationRunStatus.FAILED,
                errorCode = error.errorCode, errorMessage = error.message, errorModule = error.module,
                errorRecoverable = error.recoverable, errorSuggestion = error.suggestion,
            )
        } catch (error: Throwable) {
            finishRun(
                run.id, AutomationRunStatus.FAILED,
                errorCode = "AUTOMATION_RUN_CRASHED",
                errorMessage = error.message ?: "运行发生未分类异常",
                errorModule = "automation.engine",
            )
        } finally {
            running.remove(run.id)
        }
    }

    private fun executeList(
        task: AutomationTaskDefinition,
        actions: List<AutomationActionDefinition>,
        run: AutomationRunRecord,
        actionExecutor: ActionExecutor,
        aiExecutor: ((String, String, Boolean, Boolean) -> String)?,
        handle: RunHandle,
        depth: Int,
        stepOffset: Int = 0,
    ): Int {
        var completed = stepOffset
        actions.forEachIndexed { index, action ->
            assertActive(handle)
            completed += executeOne(task, action, run, actionExecutor, aiExecutor, completed, depth, handle)
        }
        return completed
    }

    private fun executeOne(
        task: AutomationTaskDefinition,
        action: AutomationActionDefinition,
        run: AutomationRunRecord,
        actionExecutor: ActionExecutor,
        aiExecutor: ((String, String, Boolean, Boolean) -> String)?,
        completedSoFar: Int,
        depth: Int,
        handle: RunHandle,
    ): Int {
        assertActive(handle)
        val type = action.type.lowercase()
        val label = ActionExecutor.describe(action)
        store.updateRunStatus(run.id, AutomationRunStatus.RUNNING, currentStep = label, progress = completedSoFar.toDouble() / run.totalSteps)
        when (type) {
            "flow.if" -> {
                val branch = conditions.evaluateGroup(task.deviceId, action.conditions, action.conditionMode)
                val branchActions = if (branch) action.actions else action.elseActions
                return executeList(task, branchActions, run, actionExecutor, aiExecutor, handle, depth + 1, completedSoFar)
            }
            "flow.repeat" -> {
                val times = (action.parameters["times"]?.toIntOrNull() ?: 1).coerceIn(1, 10_000)
                var completed = completedSoFar
                repeat(times) {
                    assertActive(handle)
                    completed = executeList(task, action.actions, run, actionExecutor, aiExecutor, handle, depth + 1, completed)
                }
                return completed - completedSoFar
            }
            "flow.parallel" -> {
                // 并行容器：子动作并发执行，任一失败整体失败（与桌面一致）。
                val failures = java.util.Collections.synchronizedList(mutableListOf<AutomationExecutionException>())
                val threads = action.actions.map { child ->
                    Thread {
                        try {
                            executeOne(task, child, run, actionExecutor, aiExecutor, 0, depth + 1, handle)
                        } catch (error: AutomationExecutionException) {
                            failures.add(error)
                        } catch (error: Throwable) {
                            failures.add(
                                AutomationExecutionException(
                                    "AUTOMATION_RUN_CRASHED", error.message ?: "并行动作异常", "automation.engine",
                                ),
                            )
                        }
                    }
                }
                threads.forEach(Thread::start)
                threads.forEach(Thread::join)
                failures.firstOrNull()?.let { throw it }
                return action.actions.size
            }
            else -> {
                val output = try {
                    actionExecutor.executeLeaf(task, action, aiExecutor)
                } catch (error: AutomationExecutionException) {
                    store.updateRunStatus(
                        run.id, AutomationRunStatus.RUNNING,
                        currentStep = "$label 失败：${error.message}",
                        progress = completedSoFar.toDouble() / run.totalSteps,
                    )
                    throw error
                }
                val completed = completedSoFar + 1
                store.updateRunStatus(
                    run.id, AutomationRunStatus.RUNNING,
                    completedSteps = completed,
                    progress = completed.toDouble() / run.totalSteps,
                    currentStep = label,
                )
                return 1
            }
        }
    }

    private fun assertActive(handle: RunHandle) {
        if (handle.stopRequested.get()) throw RunStoppedException()
    }

    private fun finishRun(
        runId: String,
        status: AutomationRunStatus,
        currentStep: String? = null,
        errorCode: String? = null,
        errorMessage: String? = null,
        errorModule: String? = null,
        errorRecoverable: Boolean? = null,
        errorSuggestion: String? = null,
    ) {
        store.updateRunStatus(
            runId, status,
            completedAt = System.currentTimeMillis(),
            currentStep = currentStep,
            errorCode = errorCode, errorMessage = errorMessage, errorModule = errorModule,
            errorRecoverable = errorRecoverable, errorSuggestion = errorSuggestion,
        )
    }

    class RunHandle(val taskId: String) {
        val stopRequested = AtomicReference(false)
    }

    private class RunStoppedException : Exception()

    fun interface AiExecutor {
        operator fun invoke(prompt: String, modelId: String, allowDeviceTools: Boolean, allowTaskMutation: Boolean): String
    }

    companion object {
        fun countLeafActions(actions: List<AutomationActionDefinition>): Int = actions.sumOf { action ->
            when (action.type.lowercase()) {
                "flow.if" -> countLeafActions(action.actions).coerceAtLeast(countLeafActions(action.elseActions))
                "flow.repeat" -> countLeafActions(action.actions) * (action.parameters["times"]?.toIntOrNull() ?: 1)
                "flow.parallel" -> countLeafActions(action.actions)
                else -> 1
            }
        }
    }
}
