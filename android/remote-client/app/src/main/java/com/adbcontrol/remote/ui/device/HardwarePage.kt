package com.adbcontrol.remote.ui.device

import android.app.AlertDialog
import com.adbcontrol.remote.ui.common.themedDialogBuilder
import android.content.Context
import android.view.View
import android.view.ViewGroup
import android.widget.LinearLayout
import android.widget.TextView
import com.adbcontrol.remote.core.AppGraph
import com.adbcontrol.remote.data.adb.HardwareSnapshot
import com.adbcontrol.remote.data.adb.HardwareSnapshotData
import com.adbcontrol.remote.model.RemoteDevice
import com.adbcontrol.remote.model.RemoteResult
import com.adbcontrol.remote.ui.common.BasePage
import com.adbcontrol.remote.ui.common.PageHost
import com.adbcontrol.remote.ui.common.formatBytes
import com.adbcontrol.remote.ui.common.formatFrequency

/**
 * 硬件信息（对应桌面端“硬件”Tab 仪表盘）：
 * - 采集命令与桌面端 DeviceHardwareService.SnapshotCommand 逐字一致（一条 shell 脚本，key=value 输出）；
 * - 仪表盘：设备/系统、CPU（频率法估算占用）、内存/Swap/ZRAM、电池与温度、GPU、刷新率；
 * - 默认 5 秒静默自动刷新（与桌面端一致），离开页面停止；
 * - “详情”弹窗展示全部字段与每核频率（对应桌面端详情对话框）。
 */
class HardwarePage(
    context: Context,
    host: PageHost,
    private val graph: AppGraph,
    private val device: RemoteDevice,
) : BasePage(context, host) {

    private val mainHandler = android.os.Handler(android.os.Looper.getMainLooper())
    private val dashboard = column(12)
    private var timer: Runnable? = null
    private var latest: HardwareSnapshotData? = null

    override fun build(): View {
        val root = column {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(pal.background)
        }
        root.addView(topBar("硬件信息 · ${device.displayName}", onBack = { host.popPage() }), LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, dp(56),
        ))
        root.addView(scroll(dashboard))
        refresh()
        scheduleNext()
        return root
    }

    override fun onDetach() {
        timer?.let { mainHandler.removeCallbacks(it) }
        timer = null
    }

    private fun scheduleNext() {
        val runnable = Runnable { refresh(); scheduleNext() }
        timer = runnable
        mainHandler.postDelayed(runnable, 5_000)
    }

    private fun refresh() {
        host.runRemote({ graph.commands.shell(device.id, HardwareSnapshot.snapshotCommand, 15_000) }) { output ->
            if (output is RemoteResult.Failure) {
                dashboard.removeAllViews()
                dashboard.addView(errorCard("采集失败", output.error.message, output.error.errorCode, output.error.suggestion) { refresh() })
                return@runRemote
            }
            host.runRemote({ graph.commands.shell(device.id, HardwareSnapshot.appFrameCommand, 15_000) }) { frame ->
                val snapshot = HardwareSnapshotData.parse((output as RemoteResult.Success).value.stdout)
                latest = snapshot
                val frameOutput = (frame as? RemoteResult.Success)?.value?.stdout.orEmpty()
                render(snapshot, frameOutput)
            }
        }
    }

    private fun render(snapshot: HardwareSnapshotData, frameOutput: String) {
        dashboard.removeAllViews()
        dashboard.addView(metricRow(
            "CPU ${snapshot.cpuUsageEstimatePercent?.let { fmt("%.0f%%", it) } ?: "—"}",
            "内存 ${snapshot.memUsedPercent?.let { fmt("%.0f%%", it) } ?: "—"}",
            "电量 ${snapshot.batteryLevel?.let { "$it%" } ?: "—"}",
        ))
        dashboard.addView(sectionCard("设备与系统", listOf(
            "品牌" to snapshot.brand, "型号" to snapshot.model, "设备代号" to snapshot.device,
            "安卓版本" to snapshot.android, "SDK" to snapshot.sdk, "ABI" to snapshot.abi,
            "前台应用" to com.adbcontrol.remote.data.adb.AppFrameParser.parseForegroundPackage(frameOutput),
            "前台帧率" to com.adbcontrol.remote.data.adb.AppFrameParser
                .parseFrameTimes(frameOutput).let { times ->
                    com.adbcontrol.remote.data.adb.AppFrameParser.fps(times, LongArray(1))?.let { fmt("%.1f fps", it) } ?: "—"
                },
        )))
        dashboard.addView(sectionCard("CPU", listOf(
            "型号" to snapshot.cpuModel, "核心数" to snapshot.cpuCores.toString(),
            "负载" to snapshot.load,
        ) + snapshot.cpuFreqs.entries.sortedBy { it.key }.take(8).map { "${it.key} 当前" to formatFrequency(it.value * 1000) }))
        dashboard.addView(sectionCard("内存与存储", listOf(
            "总内存" to formatBytes(snapshot.memTotalKb * 1024),
            "可用内存" to formatBytes(snapshot.memAvailableKb * 1024),
            "Swap" to formatBytes(snapshot.swapTotalKb * 1024) + "（可用 ${formatBytes(snapshot.swapFreeKb * 1024)}）",
            "ZRAM" to formatBytes(snapshot.zramDiskBytes),
        )))
        dashboard.addView(sectionCard("电池与温度", buildList {
            add("电量" to (snapshot.batteryLevel?.toString() ?: "—"))
            add("充电状态" to if (snapshot.batteryCharging) "充电中" else "未充电")
            snapshot.batteryTempTenths?.let { add("电池温度" to fmt("%.1f°C", it / 10.0)) }
            snapshot.temperaturesCelsius.take(6).forEach { (name, value) ->
                add(name to fmt("%.1f°C", value))
            }
        }))
        dashboard.addView(sectionCard("GPU 与显示", listOf(
            "占用" to (snapshot.gpuUsagePercent?.let { fmt("%.1f%%", it) } ?: "—"),
            "当前频率" to (snapshot.gpuCurFreqHz?.let { formatFrequency(it) } ?: "—"),
            "最高频率" to (snapshot.gpuMaxFreqHz?.let { formatFrequency(it) } ?: "—"),
            "显存" to (snapshot.gpuMemoryBytes?.let { formatBytes(it) } ?: "—"),
            "访问状态" to (snapshot.gpuAccess.ifBlank { "—" }),
            "刷新率" to (snapshot.refreshRate?.let { fmt("%.0f Hz", it) } ?: "—"),
        )))
        dashboard.addView(primaryButton("查看全部字段详情") { showDetail(snapshot) })
    }

    private fun metricRow(left: String, middle: String, right: String): View = row {
        addView(metricTile(left, "CPU", pal.brand), LinearLayout.LayoutParams(0, dp(84), 1f))
        addView(metricTile(middle, "内存", pal.success), LinearLayout.LayoutParams(0, dp(84), 1f).apply { leftMargin = dp(10) })
        addView(metricTile(right, "电量", pal.warning), LinearLayout.LayoutParams(0, dp(84), 1f).apply { leftMargin = dp(10) })
    }

    private fun sectionCard(title: String, entries: List<Pair<String, String>>): View = card(column(8) {
        addView(text(title, 15f, pal.text, true))
        addView(divider())
        entries.filter { it.second.isNotBlank() }.take(12).forEach { (key, value) ->
            addView(kvRow(key, value))
        }
    })

    private fun showDetail(snapshot: HardwareSnapshotData) {
        val detail = buildString {
            appendLine("品牌: ${snapshot.brand}")
            appendLine("型号: ${snapshot.model}")
            appendLine("设备代号: ${snapshot.device}")
            appendLine("安卓: ${snapshot.android} (SDK ${snapshot.sdk}) ${snapshot.abi}")
            appendLine("CPU: ${snapshot.cpuModel} × ${snapshot.cpuCores}")
            snapshot.cpuMaxFreqs.forEach { (core, max) ->
                val cur = snapshot.cpuFreqs[core]
                appendLine("$core: ${cur?.let { formatFrequency(it * 1000) } ?: "—"} / ${formatFrequency(max * 1000)}")
            }
            appendLine("负载: ${snapshot.load}")
            appendLine("内存: ${formatBytes(snapshot.memTotalKb * 1024)}（可用 ${formatBytes(snapshot.memAvailableKb * 1024)}）")
            appendLine("Swap: ${formatBytes(snapshot.swapTotalKb * 1024)} / ZRAM ${formatBytes(snapshot.zramDiskBytes)}")
            appendLine("电池: ${snapshot.batteryLevel ?: "—"}% ${if (snapshot.batteryCharging) "充电中" else ""}")
            snapshot.temperaturesCelsius.forEach { (name, value) -> appendLine("温度 $name: %.1f°C".format(value)) }
            appendLine("GPU: ${snapshot.gpuAccess} ${snapshot.gpuCurFreqHz?.let { formatFrequency(it) } ?: ""}")
            appendLine("刷新率: ${snapshot.refreshRate ?: "—"} Hz")
        }
        context.themedDialogBuilder()
            .setTitle("硬件详情")
            .setMessage(detail)
            .setPositiveButton("关闭", null)
            .show()
    }
}

/** Locale 安全的格式化（数字恒为西文数字，避免跟随系统语言输出异常字符）。 */
private fun fmt(pattern: String, vararg args: Any?): String = java.lang.String.format(java.util.Locale.US, pattern, *args)
