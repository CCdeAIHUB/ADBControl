package com.adbcontrol.remote.ui.devices

import android.content.Context
import android.view.View
import android.view.ViewGroup
import android.widget.LinearLayout
import com.adbcontrol.remote.core.AppGraph
import com.adbcontrol.remote.data.adb.LockStateParser
import com.adbcontrol.remote.model.RemoteDevice
import com.adbcontrol.remote.model.RemoteResult
import com.adbcontrol.remote.ui.common.BasePage
import com.adbcontrol.remote.ui.common.PageHost
import com.adbcontrol.remote.ui.device.CompanionPage
import com.adbcontrol.remote.ui.device.FilesPage
import com.adbcontrol.remote.ui.device.HardwareMonitorPage
import com.adbcontrol.remote.ui.device.HardwarePage
import com.adbcontrol.remote.ui.device.PreviewControlPage
import com.adbcontrol.remote.ui.device.RebootPage
import com.adbcontrol.remote.ui.device.TerminalPage
import com.adbcontrol.remote.ui.device.AppsPage

/**
 * 设备详情中枢：对应桌面端设备详情页。
 * 顶部为状态区（ADB/伴侣双连接徽标 + 电量 + 锁屏状态），中部为功能宫格（对应桌面 8 个工具 Tab），
 * 底部为常用控制区（返回/主页/多任务/音量/电源/锁屏，全部与桌面端快捷控制列一致）。
 */
class DeviceDetailPage(
    context: Context,
    host: PageHost,
    private val graph: AppGraph,
    private val device: RemoteDevice,
) : BasePage(context, host) {

    private val statusText = text("", 12f, pal.muted)
    private val lockText = text("锁屏状态：检测中…", 12f, pal.muted)

    override fun build(): View {
        val body = column(14)
        body.addView(statusCard())
        body.addView(sectionTitle("设备功能", "与桌面端设备详情页工具一致"))
        body.addView(functionGrid())
        body.addView(sectionTitle("常用控制", "经 ADB 发送按键（与桌面端快捷控制一致）"))
        body.addView(quickControls())
        body.addView(lockText)
        refreshStatus()
        return scroll(body)
    }

    private fun statusCard(): View {
        val title = text(device.displayName, 18f, pal.text, true)
        val subtitle = text(device.subtitle(), 12f, pal.muted)
        return card(column(8) {
            addView(row {
                addView(column(3) {
                    addView(title)
                    addView(subtitle)
                }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
                addView(column(3) {
                    addView(badge(if (device.adbOnline) "ADB 在线" else "ADB 离线", if (device.adbOnline) pal.success else pal.muted))
                    addView(badge(if (device.companionOnline) "伴侣已连接" else "伴侣未连接", if (device.companionOnline) pal.brand else pal.muted))
                })
            })
            addView(statusText)
        })
    }

    private fun functionGrid(): View {
        val grid = column(10)
        val tiles = listOf(
            listOf("🖥", "预览控制", "截图 · 触控 · 解锁") to { host.pushPage(PreviewControlPage(context, host, graph, device)) },
            listOf("⌨️", "终端", "Shell 命令") to { host.pushPage(TerminalPage(context, host, graph, device)) },
            listOf("📦", "软件管理", "列表 · 启停 · 卸载") to { host.pushPage(AppsPage(context, host, graph, device)) },
            listOf("📁", "文件管理", "浏览 · 上传 · 下载") to { host.pushPage(FilesPage(context, host, graph, device)) },
            listOf("📈", "硬件信息", "SoC · 内存 · 温度") to { host.pushPage(HardwarePage(context, host, graph, device)) },
            listOf("⏱", "硬件监控", "采样 · 记录 · 导出") to { host.pushPage(HardwareMonitorPage(context, host, graph, device)) },
            listOf("🔄", "重启选项", "系统 · Bootloader 等") to { host.pushPage(RebootPage(context, host, graph, device)) },
            listOf("🧩", "伴侣能力", "状态 · 权限 · 能力") to { host.pushPage(CompanionPage(context, host, graph, device)) },
        )
        tiles.chunked(2).forEach { group ->
            grid.addView(row {
                group.forEachIndexed { index, (item, action) ->
                    val (emoji, title, subtitle) = item
                    addView(
                        // 高度 WRAP_CONTENT：固定 92dp 会压住宫格文字（见 HomePage 同款修复）。
                        featureTile(title, subtitle, emoji, action),
                        LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f).apply {
                            if (index > 0) leftMargin = dp(10)
                        },
                    )
                }
            })
        }
        return grid
    }

    /** 与桌面端“常用控制”一致的单命令快捷键：一行两个，避免窄屏文字被裁切。 */
    private fun quickControls(): View {
        val actions = listOf(
            listOf("返回", "input keyevent KEYCODE_BACK"),
            listOf("主页", "input keyevent KEYCODE_HOME"),
            listOf("多任务", "input keyevent KEYCODE_APP_SWITCH"),
            listOf("音量+", "input keyevent KEYCODE_VOLUME_UP"),
            listOf("音量-", "input keyevent KEYCODE_VOLUME_DOWN"),
            listOf("电源", "input keyevent KEYCODE_POWER"),
            listOf("点亮", "input keyevent KEYCODE_WAKEUP"),
            listOf("锁屏", "input keyevent KEYCODE_SLEEP"),
        )
        val grid = column(8)
        actions.chunked(2).forEach { group ->
            grid.addView(row {
                group.forEachIndexed { index, (label, command) ->
                    addView(
                        secondaryButton(label) { sendKey(command) },
                        LinearLayout.LayoutParams(0, dp(40), 1f).apply { if (index > 0) leftMargin = dp(8) },
                    )
                }
            })
        }
        return grid
    }

    private fun sendKey(shellCommand: String) {
        host.runRemote({ graph.commands.shell(device.id, shellCommand) }) { result ->
            when (result) {
                is RemoteResult.Failure -> host.notify("${result.error.message}（${result.error.errorCode}）")
                is RemoteResult.Success -> Unit // 按键成功无需打断用户
            }
        }
    }

    private fun refreshStatus() {
        // 电量与锁屏状态异步刷新（与桌面端设备详情页的行为一致）。
        host.runRemote({ graph.repository.enrichDevice(device.id) }) { result ->
            if (result is RemoteResult.Success) {
                statusText.text = listOf(
                    result.value.model.takeIf(String::isNotBlank)?.let { "型号 $it" },
                    result.value.androidVersion.takeIf(String::isNotBlank)?.let { "Android $it" },
                    result.value.batteryLevel?.let { "电量 $it%" },
                ).filterNotNull().joinToString(" · ").ifBlank { "信息待采集" }
            }
        }
        host.runRemote({ graph.commands.queryLockState(device.id) }) { result ->
            when (result) {
                is RemoteResult.Failure -> lockText.text = "锁屏状态：查询失败（${result.error.errorCode}）"
                is RemoteResult.Success -> lockText.text = "锁屏状态：" + when (result.value.state) {
                    LockStateParser.LockState.Locked -> "已锁屏"
                    LockStateParser.LockState.Unlocked -> "未锁屏"
                    LockStateParser.LockState.Unknown -> "未知"
                }
            }
        }
    }

    private fun RemoteDevice.subtitle(): String = listOf(brand, model).filter(String::isNotBlank).joinToString(" ").ifBlank { id }
}
