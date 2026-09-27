package com.adbcontrol.remote.ui.automation

import android.content.Context
import android.view.View
import android.widget.LinearLayout
import com.adbcontrol.remote.core.AppGraph
import com.adbcontrol.remote.model.AutomationRunStatus
import com.adbcontrol.remote.ui.common.BasePage
import com.adbcontrol.remote.ui.common.PageHost
import com.adbcontrol.remote.ui.common.formatTime

/**
 * 运行记录页（对应桌面端任务运行记录）：状态、触发方式、进度、错误码与建议。
 * 记录保存在本机 SQLite（与桌面端 automation.sqlite 同语义）。
 */
class TaskRunsPage(
    context: Context,
    host: PageHost,
    private val graph: AppGraph,
) : BasePage(context, host) {

    private val listContainer = column(12)

    override fun build(): View {
        val body = column(12)
        body.addView(row {
            addView(text("运行记录", 16f, pal.text, true), LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
            addView(text("清空全部", 13f, pal.danger, true).apply {
                setOnClickListener {
                    host.confirm("清空运行记录", "所有任务的运行记录将被删除。", danger = true) {
                        graph.automationStore.listRuns(limit = 500).forEach { graph.automationStore.deleteRun(it.id) }
                        load()
                    }
                }
            })
        })
        body.addView(listContainer)
        load()
        return subPage("运行记录", body)
    }

    private fun load() {
        listContainer.removeAllViews()
        val runs = graph.automationStore.listRuns(limit = 100)
        if (runs.isEmpty()) {
            listContainer.addView(emptyView("暂无运行记录", "运行任务后会在这里记录执行过程", "🧾"))
            return
        }
        runs.forEach { run ->
            listContainer.addView(card(column(6) {
                addView(row {
                    addView(column(4) {
                        addView(text(run.taskName, 14f, pal.text, true))
                        addView(text("${formatTime(run.createdAtEpochMs)} · ${run.trigger}", 11f, pal.muted))
                    }, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
                    addView(badge(statusText(run.status), statusColor(run.status)))
                })
                if (run.currentStep.isNotBlank()) {
                    addView(text("当前/最后步骤：${run.currentStep}", 11f, pal.secondary))
                }
                addView(progressBar(run.progress))
                if (run.errorMessage.isNotBlank()) {
                    addView(text("${run.errorMessage}${if (run.errorCode.isNotBlank()) "（${run.errorCode}）" else ""}", 11f, pal.danger))
                    if (run.errorSuggestion.isNotBlank()) addView(text("建议：${run.errorSuggestion}", 11f, pal.muted))
                }
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

    private fun statusColor(status: AutomationRunStatus): Int = when (status) {
        AutomationRunStatus.SUCCEEDED -> pal.success
        AutomationRunStatus.FAILED -> pal.danger
        AutomationRunStatus.RUNNING, AutomationRunStatus.QUEUED -> pal.warning
        else -> pal.muted
    }
}
