package com.adbcontrol.remote.ui.profile

import android.app.AlertDialog
import android.content.Context
import android.view.View
import android.widget.LinearLayout
import com.adbcontrol.remote.core.AppGraph
import com.adbcontrol.remote.core.ThemeMode
import com.adbcontrol.remote.service.AutomationForegroundService
import com.adbcontrol.remote.ui.common.BasePage
import com.adbcontrol.remote.ui.common.PageHost

/** 设置页：主题模式、预览轮询间隔、后台调度开关。 */
class SettingsPage(
    context: Context,
    host: PageHost,
    private val graph: AppGraph,
) : BasePage(context, host) {

    override fun build(): View {
        val body = column(14)
        body.addView(listRow("主题模式", themeLabel(), emoji = "🎨", onClick = { pickTheme() }))
        body.addView(listRow("预览轮询间隔", "${graph.settings.previewIntervalMs} ms（500-60000）", emoji = "⏱", onClick = { pickInterval() }))
        body.addView(listRow(
            "后台调度保活",
            if (AutomationForegroundService.running) "运行中：进程被杀前可持续调度任务" else "已停止：任务仅在应用前台时调度",
            emoji = "🔔",
            onClick = {
                if (AutomationForegroundService.running) {
                    context.stopService(android.content.Intent(context, AutomationForegroundService::class.java))
                    host.notify("已请求停止后台调度")
                } else {
                    context.startForegroundService(android.content.Intent(context, AutomationForegroundService::class.java))
                    host.notify("已请求启动后台调度")
                }
            },
        ))
        body.addView(text(
            "说明：自动化任务调度运行于本机，Core 无任务 API；后台调度使用前台服务，" +
                "系统在极端内存压力下仍可能回收进程。",
            11f, pal.muted,
        ))
        return subPage("设置", body)
    }

    private fun themeLabel(): String = when (graph.settings.themeMode) {
        ThemeMode.FOLLOW_SYSTEM -> "跟随系统"
        ThemeMode.LIGHT -> "浅色"
        ThemeMode.DARK -> "深色"
    }

    private fun pickTheme() {
        val modes = ThemeMode.entries.map { it.title }
        AlertDialog.Builder(context)
            .setTitle("主题模式")
            .setItems(modes.toTypedArray()) { _, which ->
                val mode = ThemeMode.entries[which]
                graph.settings.themeMode = mode
                com.adbcontrol.remote.core.ThemeManager.apply(mode)
                host.notify("主题已切换为${mode.title}")
                host.recreateForTheme()
            }
            .show()
    }

    private fun pickInterval() {
        val input = android.widget.EditText(context).apply {
            hint = "毫秒（500-60000）"
            inputType = android.text.InputType.TYPE_CLASS_NUMBER
            setText(graph.settings.previewIntervalMs.toString())
            setPadding(dp(40), dp(20), dp(40), 0)
        }
        AlertDialog.Builder(context)
            .setTitle("预览轮询间隔")
            .setView(input)
            .setNegativeButton("取消", null)
            .setPositiveButton("保存") { _, _ ->
                input.text.toString().toIntOrNull()?.let { graph.settings.previewIntervalMs = it }
                host.notify("已保存：${graph.settings.previewIntervalMs} ms")
            }
            .show()
    }
}
