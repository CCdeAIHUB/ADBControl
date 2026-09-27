package com.adbcontrol.remote.ui.profile

import android.content.Context
import android.view.View
import android.widget.LinearLayout
import com.adbcontrol.remote.core.AppGraph
import com.adbcontrol.remote.data.io.DownloadsWriter
import com.adbcontrol.remote.data.log.AppDiagnostics
import com.adbcontrol.remote.ui.common.BasePage
import com.adbcontrol.remote.ui.common.PageHost

/**
 * 日志与诊断（对应桌面端“日志与诊断”的最小子集）：
 * - 会话 ID、内存事件环形缓冲（512 条）实时展示；
 * - 导出：会话 JSONL 写入 Downloads/ADBControl；
 * - 隐私边界与桌面端一致：只有事件元数据，不含命令参数、密码、token 与截图。
 */
class LogsPage(
    context: Context,
    host: PageHost,
    private val graph: AppGraph,
) : BasePage(context, host) {

    private val listContainer = column(8)

    override fun build(): View {
        val body = column(12)
        body.addView(card(column(6) {
            addView(text("会话 ${AppDiagnostics.currentSessionId()}", 14f, pal.text, true))
            addView(text("事件 ${AppDiagnostics.snapshot().size} 条 · 只记录元数据，不含命令内容与凭据", 11f, pal.muted))
        }))
        body.addView(row {
            addView(secondaryButton("刷新") { load() }, LinearLayout.LayoutParams(0, dp(44), 1f))
            addView(secondaryButton("导出到下载") { export() }, LinearLayout.LayoutParams(0, dp(44), 1f).apply { leftMargin = dp(8) })
        })
        body.addView(listContainer)
        load()
        return subPage("日志与诊断", body)
    }

    private fun load() {
        listContainer.removeAllViews()
        val events = AppDiagnostics.snapshot().reversed()
        if (events.isEmpty()) {
            listContainer.addView(emptyView("暂无日志", "关键事件会展示在这里", "🧾"))
            return
        }
        events.take(200).forEach { event ->
            val line = text(event.display(), 11f, if (event.ok) pal.secondary else pal.danger)
            listContainer.addView(line)
        }
        if (events.size > 200) listContainer.addView(text("仅显示最近 200 条", 11f, pal.muted))
    }

    private fun export() {
        val lines = AppDiagnostics.snapshot().joinToString("\n") { event -> exportEvent(event) }
        val saved = DownloadsWriter.save(
            context,
            "remote-diagnostics-${System.currentTimeMillis()}.jsonl",
            lines.toByteArray(),
        )
        host.notify(if (saved == null) "导出失败，请检查存储空间" else "已导出到 $saved")
    }

    private fun exportEvent(event: AppDiagnostics.Event): String = org.json.JSONObject()
        .put("at", event.atEpochMs)
        .put("level", event.level)
        .put("phase", event.phase)
        .put("module", event.module)
        .put("ok", event.ok)
        .put("elapsedMs", event.elapsedMs)
        .put("errorCode", event.errorCode)
        .put("detail", event.detail)
        .toString()
}
