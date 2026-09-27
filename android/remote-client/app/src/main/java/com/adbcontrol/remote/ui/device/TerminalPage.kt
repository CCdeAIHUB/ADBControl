package com.adbcontrol.remote.ui.device

import android.content.Context
import android.graphics.Typeface
import android.view.View
import android.view.ViewGroup
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import com.adbcontrol.remote.core.AppGraph
import com.adbcontrol.remote.model.RemoteDevice
import com.adbcontrol.remote.model.RemoteResult
import com.adbcontrol.remote.security.RiskPolicy
import com.adbcontrol.remote.ui.common.BasePage
import com.adbcontrol.remote.ui.common.PageHost

/**
 * ADB 终端（对应桌面端“终端”Tab）：
 * - 单次执行式：整行命令经 `adb -s <serial> shell <cmd>` 执行，默认 30 秒超时（与桌面一致）；
 * - ↑/↓ 浏览历史（桌面端同款交互）；
 * - 危险命令需确认（RiskPolicy 分级，与 AI 工具的安全策略一致）。
 */
class TerminalPage(
    context: Context,
    host: PageHost,
    private val graph: AppGraph,
    private val device: RemoteDevice,
) : BasePage(context, host) {

    private val history = ArrayDeque<String>()
    private val outputBuilder = StringBuilder("ADBShell (${device.id})。输入 shell 命令，Core 自动加 -s 前缀。\n")
    private lateinit var outputView: TextView
    private lateinit var outputScroll: ScrollView
    private lateinit var inputView: EditText
    private var historyIndex = -1
    private var draftCommand: String = ""

    override fun build(): View {
        val root = column {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(pal.background)
        }
        root.addView(topBar("终端 · ${device.displayName}", onBack = { host.popPage() }), LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, dp(56),
        ))

        outputView = text(outputBuilder.toString(), 12f, pal.terminalText).apply {
            typeface = Typeface.MONOSPACE
            setTextIsSelectable(true)
            setPadding(dp(12), dp(12), dp(12), dp(12))
        }
        outputScroll = ScrollView(context).apply {
            setBackgroundColor(pal.terminalBackground)
            addView(outputView, ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT))
        }
        root.addView(outputScroll, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f))

        inputView = input("例如 getprop ro.build.version.release").apply {
            typeface = Typeface.MONOSPACE
            // 回车即执行（与桌面终端交互一致）。
            setOnEditorActionListener { _, _, _ ->
                execute()
                true
            }
        }
        root.addView(inputView, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(52)).apply {
            setMargins(dp(12), dp(8), dp(12), 0)
        })
        root.addView(row {
            addView(secondaryButton("↑ 历史") { browseHistory(-1) }, LinearLayout.LayoutParams(0, dp(44), 1f).apply {
                setMargins(dp(12), dp(8), 0, dp(8))
                leftMargin = dp(12)
            })
            addView(secondaryButton("↓ 历史") { browseHistory(1) }, LinearLayout.LayoutParams(0, dp(44), 1f).apply {
                setMargins(dp(8), dp(8), 0, dp(8))
                leftMargin = dp(8)
            })
            addView(primaryButton("执行") { execute() }, LinearLayout.LayoutParams(0, dp(44), 1f).apply {
                setMargins(dp(8), dp(8), dp(12), dp(8))
                leftMargin = dp(8)
            })
        })
        return root
    }

    private fun browseHistory(direction: Int) {
        if (history.isEmpty()) return
        if (historyIndex == -1 && direction < 0) draftCommand = inputView.text.toString()
        historyIndex = (historyIndex + direction).coerceIn(-1, history.size - 1)
        inputView.setText(if (historyIndex == -1) draftCommand else history.toList()[historyIndex])
        inputView.setSelection(inputView.text.length)
    }

    private fun execute() {
        val command = inputView.text.toString().trim()
        if (command.isBlank()) return
        inputView.setText("")
        historyIndex = -1
        if (history.isEmpty() || history.first() != command) history.addFirst(command)
        when (RiskPolicy.classify(command)) {
            RiskPolicy.Decision.CONFIRM -> host.confirm("执行远程命令", command) { run(command) }
            RiskPolicy.Decision.DANGER -> host.confirm("危险命令", "$command\n\n该命令可能造成不可逆影响。", danger = true) { run(command) }
            RiskPolicy.Decision.ALLOW -> run(command)
        }
    }

    private fun run(command: String) {
        append("\n> $command\n执行中…\n")
        host.runRemote({ graph.commands.shell(device.id, command, timeoutMs = 30_000) }) { result ->
            when (result) {
                is RemoteResult.Failure -> append("失败：${result.error.message}（${result.error.errorCode}）\n")
                is RemoteResult.Success -> {
                    if (result.value.stdout.isNotBlank()) append(result.value.stdout.trimEnd() + "\n")
                    if (result.value.stderr.isNotBlank()) append("[stderr] " + result.value.stderr.trimEnd() + "\n")
                    if (result.value.stdout.isBlank() && result.value.stderr.isBlank()) {
                        append("(无输出, 退出码 ${result.value.exitCode})\n")
                    }
                }
            }
        }
    }

    private fun append(textValue: String) {
        outputBuilder.append(textValue)
        outputView.text = outputBuilder.toString()
        outputScroll.post { outputScroll.fullScroll(View.FOCUS_DOWN) }
    }
}
