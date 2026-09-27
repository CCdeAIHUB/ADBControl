package com.adbcontrol.remote.ui.device

import android.content.Context
import android.view.View
import android.widget.LinearLayout
import com.adbcontrol.remote.core.AppGraph
import com.adbcontrol.remote.model.RemoteDevice
import com.adbcontrol.remote.model.RemoteResult
import com.adbcontrol.remote.ui.common.BasePage
import com.adbcontrol.remote.ui.common.PageHost
import com.adbcontrol.remote.ui.common.ripple

/**
 * 重启选项（与桌面端“重启”Tab 完全一致的六个动作，全部需要危险确认）：
 * 重启系统 / Bootloader(Fastboot) / Fastbootd / Recovery / EDL / 关机。
 */
class RebootPage(
    context: Context,
    host: PageHost,
    private val graph: AppGraph,
    private val device: RemoteDevice,
) : BasePage(context, host) {

    private data class RebootAction(val title: String, val subtitle: String, val args: List<String>)

    private val actions = listOf(
        RebootAction("重启系统", "立即重启设备到系统", listOf("reboot")),
        RebootAction("Bootloader", "重启到 Fastboot 引导模式", listOf("reboot", "bootloader")),
        RebootAction("Fastbootd", "重启到用户空间 Fastboot（fastbootd）", listOf("reboot", "fastboot")),
        RebootAction("Recovery", "重启到恢复模式", listOf("reboot", "recovery")),
        RebootAction("EDL", "重启到紧急下载模式（9008）", listOf("reboot", "edl")),
        RebootAction("关机", "关闭设备电源", listOf("shell", "reboot", "-p")),
    )

    override fun build(): View {
        val body = column(12)
        body.addView(text("设备会立即离线，请确认设备上的工作已保存。", 12f, pal.danger))
        actions.forEach { action ->
            body.addView(
                card(column(6) {
                    addView(text(action.title, 16f, pal.danger, true))
                    addView(text(action.subtitle, 12f, pal.secondary))
                }).apply {
                    setOnClickListener {
                        host.confirm(
                            action.title,
                            "${action.subtitle}。设备将立即离线，远程连接会在设备回来后恢复。",
                            danger = true,
                        ) { execute(action) }
                    }
                    foreground = ripple()
                },
            )
        }
        body.addView(text("提示：EDL 与 Bootloader 属于底层模式，误操作可能导致设备无法启动，请谨慎使用。", 11f, pal.muted))
        return subPage("重启 · ${device.displayName}", body)
    }

    private fun execute(action: RebootAction) {
        host.runRemote({ graph.commands.exec(device.id, action.args, 30_000) }) { result ->
            when (result) {
                is RemoteResult.Failure -> host.notify("${result.error.message}（${result.error.errorCode}）")
                is RemoteResult.Success -> host.notify("命令已下发，设备正在进入 ${action.title}")
            }
        }
    }
}
