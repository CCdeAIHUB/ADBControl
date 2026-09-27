package com.adbcontrol.remote.ui.automation

import android.app.AlertDialog
import android.content.Context
import android.content.Intent
import android.view.View
import android.widget.LinearLayout
import com.adbcontrol.remote.core.AppGraph
import com.adbcontrol.remote.core.triggerSummary
import com.adbcontrol.remote.model.AutomationRunStatus
import com.adbcontrol.remote.model.Session
import com.adbcontrol.remote.model.UserRole
import com.adbcontrol.remote.service.AutomationForegroundService
import com.adbcontrol.remote.ui.common.BasePage
import com.adbcontrol.remote.ui.common.PageHost

/**
 * 自动化任务（对应桌面端“任务”页）：
 * - 指标卡（全部/已启用/运行中）、任务卡（状态、触发摘要、设备、进度）；
 * - 操作：立即运行、启用/停用、编辑(JSON DSL)、运行记录、删除；
 * - 调度引擎与桌面端一致（1s tick、错过容忍 2 分钟），但执行在本机——
 *   进程被杀后调度停止，可用“后台调度”前台服务维持（页面明示）。
 * - JSON DSL 与桌面端同构，任务可在两端互迁。
 */
class TasksPage(
    context: Context,
    host: PageHost,
    private val graph: AppGraph,
    private val session: Session,
) : BasePage(context, host) {

    private val listContainer = column(12)
    private val metricsRow = LinearLayout(context)

    override fun build(): View {
        val body = column(12)
        if (!com.adbcontrol.remote.navigation.AccessPolicy.canSeeTasks(session.role)) {
            // 导航门控与桌面端一致：任务仅对管理员角色开放。
            body.addView(emptyView("任务仅管理员可见", "当前账号为成员账号，请联系管理员分配权限。", "🔒"))
            return scroll(body)
        }
        body.addView(row {
            addView(column(3) {
                addView(text("自动化任务", 22f, pal.text, true))
                addView(text("任务在本机调度执行，命令经远程 Core 发往设备", 11f, pal.muted))
            }, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
            addView(text("新建", 15f, pal.brand, true).apply { setOnClickListener { host.pushPage(TaskEditPage(context, host, graph, existingTaskId = null)) } })
        })
        metricsRow.orientation = LinearLayout.HORIZONTAL
        body.addView(metricsRow)
        body.addView(row {
            addView(secondaryButton(backgroundText()) {
                if (AutomationForegroundService.running) {
                    context.stopService(Intent(context, AutomationForegroundService::class.java))
                } else {
                    context.startForegroundService(Intent(context, AutomationForegroundService::class.java))
                }
                host.notify(if (AutomationForegroundService.running) "已请求停止后台调度" else "已请求启动后台调度")
            }, LinearLayout.LayoutParams(0, dp(44), 1f))
            addView(secondaryButton("运行记录") { host.pushPage(TaskRunsPage(context, host, graph)) },
                LinearLayout.LayoutParams(0, dp(44), 1f).apply { leftMargin = dp(8) })
        })
        body.addView(listContainer)
        load()
        return scroll(body)
    }

    private fun backgroundText(): String =
        if (AutomationForegroundService.running) "停止后台调度" else "后台调度保活"

    private fun load() {
        metricsRow.removeAllViews()
        val tasks = graph.automationStore.listTasks()
        val running = tasks.count { graph.engine.isRunning(it.id) }
        metricsRow.addView(metricTile(tasks.size.toString(), "全部", pal.brand), LinearLayout.LayoutParams(0, dp(84), 1f))
        metricsRow.addView(metricTile(tasks.count { it.enabled }.toString(), "已启用", pal.success), LinearLayout.LayoutParams(0, dp(84), 1f).apply { leftMargin = dp(10) })
        metricsRow.addView(metricTile(running.toString(), "运行中", pal.warning), LinearLayout.LayoutParams(0, dp(84), 1f).apply { leftMargin = dp(10) })
        listContainer.removeAllViews()
        if (tasks.isEmpty()) {
            listContainer.addView(emptyView("暂无任务", "点击右上角“新建”创建任务，或让 AI 助手帮你写任务", "🗂"))
            return
        }
        tasks.forEach { task ->
            val lastRun = graph.automationStore.listRuns(task.id, limit = 1).firstOrNull()
            listContainer.addView(card(column(8) {
                addView(row {
                    addView(column(4) {
                        addView(text(task.name, 15f, pal.text, true))
                        addView(text("${triggerSummary(task)} · 设备 ${task.deviceId.ifBlank { "未指定" }}", 11f, pal.muted))
                    }, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
                    addView(badge(
                        if (graph.engine.isRunning(task.id)) "运行中" else if (task.enabled) "已启用" else "已停用",
                        when {
                            graph.engine.isRunning(task.id) -> pal.warning
                            task.enabled -> pal.success
                            else -> pal.muted
                        },
                    ))
                })
                lastRun?.let { run ->
                    addView(text(
                        "上次运行：${com.adbcontrol.remote.ui.common.formatTime(run.createdAtEpochMs)} · ${statusText(run.status)}" +
                            if (run.errorMessage.isNotBlank()) " · ${run.errorMessage.take(60)}" else "",
                        11f,
                        if (run.status == AutomationRunStatus.FAILED) pal.danger else pal.muted,
                    ))
                }
                addView(row {
                    addView(secondaryButton("运行") {
                        if (graph.scheduler.runNow(task)) host.notify("已提交运行") else host.notify("任务正在运行（并发策略拒绝）")
                    }, LinearLayout.LayoutParams(0, dp(40), 1f))
                    addView(secondaryButton(if (task.enabled) "停用" else "启用") {
                        graph.automationStore.setTaskEnabled(task.id, !task.enabled)
                        load()
                    }, LinearLayout.LayoutParams(0, dp(40), 1f).apply { leftMargin = dp(6) })
                    addView(secondaryButton("编辑") { host.pushPage(TaskEditPage(context, host, graph, task.id)) },
                        LinearLayout.LayoutParams(0, dp(40), 1f).apply { leftMargin = dp(6) })
                    addView(secondaryButton("删除") {
                        host.confirm("删除任务", "${task.name} 及其运行记录将被删除。", danger = true) {
                            graph.engine.stopRun(task.id)
                            graph.automationStore.deleteTask(task.id)
                            load()
                        }
                    }, LinearLayout.LayoutParams(0, dp(40), 1f).apply { leftMargin = dp(6) })
                })
            }, 14))
        }
    }

    private fun statusText(status: AutomationRunStatus): String = when (status) {
        AutomationRunStatus.QUEUED -> "排队中"
        AutomationRunStatus.RUNNING -> "运行中"
        AutomationRunStatus.PAUSED -> "已暂停"
        AutomationRunStatus.SUCCEEDED -> "成功"
        AutomationRunStatus.FAILED -> "失败"
        AutomationRunStatus.STOPPED -> "已停止"
        AutomationRunStatus.SKIPPED -> "已跳过"
    }
}
