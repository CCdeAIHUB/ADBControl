package com.adbcontrol.remote.data.automation

import com.adbcontrol.remote.model.AutomationTaskDefinition
import com.adbcontrol.remote.model.AutomationTriggerDefinition
import com.adbcontrol.remote.model.AutomationTriggerState
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

/**
 * 调度器：1 秒 tick（与桌面 `AutomationSchedulerService.cs` 一致），
 * 错过容忍 2 分钟；条件触发器按 edgeOnly + cooldown 判定。
 * 由前台服务持有生命周期；进程被杀后调度停止（页面会明确提示）。
 */
class AutomationScheduler(
    private val store: AutomationStore,
    private val engine: AutomationEngine,
    private val sleeper: (Long) -> Unit = { Thread.sleep(it) },
) {
    private val thread = Executors.newSingleThreadExecutor { runnable ->
        Thread(runnable, "ADBControl-automation-scheduler").apply { isDaemon = false }
    }
    private val running = AtomicBoolean(false)
    private val stopped = AtomicBoolean(false)

    @Volatile
    var aiExecutor: AutomationEngine.AiExecutor? = null

    fun start() {
        if (!running.compareAndSet(false, true)) return
        stopped.set(false)
        thread.execute {
            // 错过容忍：调度起点前移 2 分钟，与桌面端一致。
            var lastTick = System.currentTimeMillis() - MISSED_TOLERANCE_MS
            while (!stopped.get()) {
                val now = System.currentTimeMillis()
                try {
                    tick(lastTick, now)
                } catch (error: Throwable) {
                    // 单轮失败不能终止调度循环；错误进入下一轮重试（调度器不产生用户可见假成功）。
                }
                lastTick = now
                try {
                    sleeper(TICK_INTERVAL_MS)
                } catch (_: InterruptedException) {
                    Thread.currentThread().interrupt()
                    break
                }
            }
        }
    }

    fun stop() {
        stopped.set(true)
        running.set(false)
    }

    /** 单次 tick（包内可见以便测试）：扫描已启用任务，触发到点的调度型触发器与条件触发器。 */
    fun tick(fromExclusive: Long, now: Long) {
        store.listTasks().filter { it.enabled }.forEach { task ->
            task.triggers.forEach { trigger ->
                when (trigger.type.lowercase()) {
                    "manual" -> Unit
                    "condition" -> evaluateConditionTrigger(task, trigger, now)
                    else -> fireScheduledTrigger(task, trigger, fromExclusive, now)
                }
            }
        }
    }

    private fun fireScheduledTrigger(task: AutomationTaskDefinition, trigger: AutomationTriggerDefinition, fromExclusive: Long, now: Long) {
        val next = try {
            ScheduleCalculator.nextOccurrence(task, trigger, fromExclusive)
        } catch (_: Exception) {
            // 计算失败（非法 cron/时区）已在保存时被 Validator 拦截；运行期失败跳过本轮，等待用户修正。
            return
        } ?: return
        if (next > now) return
        val state = store.getTriggerState(task.id, trigger.id)
        // 同一触发点不重复触发：lastFired >= next 说明已执行过。
        if (state?.lastFiredAtEpochMs != null && state.lastFiredAtEpochMs!! >= next) return
        markFired(task.id, trigger.id, now)
        engine.submitRun(task, triggerLabel(trigger), aiExecutor)
    }

    private fun evaluateConditionTrigger(task: AutomationTaskDefinition, trigger: AutomationTriggerDefinition, now: Long) {
        val state = store.getTriggerState(task.id, trigger.id)
        val lastEvaluated = state?.lastEvaluatedAtEpochMs ?: 0L
        if (now - lastEvaluated < trigger.pollIntervalSeconds * 1000L) return
        val matched = try {
            ConditionEvaluator(engine.gateway).evaluateGroup(task.deviceId, trigger.conditions, trigger.conditionMode)
        } catch (_: Exception) {
            false // 条件查询失败视为不满足，等待下一轮；与桌面“查询失败不触发”一致。
        }
        store.saveTriggerState(
            AutomationTriggerState(
                taskId = task.id,
                triggerId = trigger.id,
                lastFiredAtEpochMs = state?.lastFiredAtEpochMs,
                lastEvaluatedAtEpochMs = now,
                lastConditionValue = matched,
            ),
        )
        if (!matched) return
        if (trigger.edgeOnly && state?.lastConditionValue == true) return // 边沿触发：保持 true 期间不重复
        val cooldownMs = trigger.cooldownSeconds * 1000L
        val lastFired = state?.lastFiredAtEpochMs ?: 0L
        if (now - lastFired < cooldownMs) return
        markFired(task.id, trigger.id, now)
        engine.submitRun(task, triggerLabel(trigger), aiExecutor)
    }

    private fun markFired(taskId: String, triggerId: String, at: Long) {
        val state = store.getTriggerState(taskId, triggerId)
        store.saveTriggerState(
            AutomationTriggerState(
                taskId = taskId,
                triggerId = triggerId,
                lastFiredAtEpochMs = at,
                lastEvaluatedAtEpochMs = state?.lastEvaluatedAtEpochMs,
                lastConditionValue = state?.lastConditionValue,
            ),
        )
    }

    /** 立即运行（manual 触发）；返回 false 表示并发策略拒绝。 */
    fun runNow(task: AutomationTaskDefinition): Boolean =
        engine.submitRun(task, "manual", aiExecutor)

    private fun triggerLabel(trigger: AutomationTriggerDefinition): String = when (trigger.type.lowercase()) {
        "manual" -> "手动运行"
        "once" -> "单次"
        "daily" -> "每天 ${trigger.at.orEmpty()}"
        "weekly" -> "每周 ${trigger.days.joinToString()} ${trigger.at.orEmpty()}"
        "interval" -> "每 ${trigger.intervalSeconds} 秒"
        "cron" -> "Cron ${trigger.cron.orEmpty()}"
        "condition" -> "条件触发"
        else -> trigger.type
    }

    companion object {
        const val TICK_INTERVAL_MS = 1000L
        const val MISSED_TOLERANCE_MS = 2 * 60 * 1000L
    }
}
