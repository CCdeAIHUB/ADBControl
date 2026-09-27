package com.adbcontrol.remote.ui.device

import android.content.Context
import android.os.Handler
import android.os.Looper
import android.view.View
import android.view.ViewGroup
import android.widget.LinearLayout
import com.adbcontrol.remote.core.AppGraph
import com.adbcontrol.remote.data.adb.AppFrameParser
import com.adbcontrol.remote.data.adb.HardwareSnapshot
import com.adbcontrol.remote.data.adb.HardwareSnapshotData
import com.adbcontrol.remote.data.io.DownloadsWriter
import com.adbcontrol.remote.model.RemoteDevice
import com.adbcontrol.remote.model.RemoteResult
import com.adbcontrol.remote.ui.common.BasePage
import com.adbcontrol.remote.ui.common.PageHost
import com.adbcontrol.remote.ui.common.SparklineView
import java.util.Locale

/**
 * 硬件监控（对应桌面端“硬件监控”窗口的最小完整子集）：
 * - 1 秒采样（同一 SnapshotCommand，与桌面端一致），默认曲线：CPU 估算占用 / 内存占用 / 最高温度 / 刷新率；
 * - 记录：开始/结束记录，样本保存到内存（100ms 统计节流的桌面行为此处简化为采样即记录）；
 * - 导出：CSV 写入 Downloads/ADBControl（桌面端导出 xlsx/html/sqlite；远程端受本地文件能力限制，
 *   CSV 为移动端等价交换格式，协议说明见 README）。
 */
class HardwareMonitorPage(
    context: Context,
    host: PageHost,
    private val graph: AppGraph,
    private val device: RemoteDevice,
) : BasePage(context, host) {

    private val mainHandler = Handler(Looper.getMainLooper())
    private var timer: Runnable? = null

    // 曲线窗口（240 点）。
    private val cpuSamples = mutableListOf<Double?>()
    private val memorySamples = mutableListOf<Double?>()
    private val temperatureSamples = mutableListOf<Double?>()
    private val refreshSamples = mutableListOf<Double?>()
    private val fpsSamples = mutableListOf<Double?>()

    private lateinit var cpuChart: SparklineView
    private lateinit var memoryChart: SparklineView
    private lateinit var temperatureChart: SparklineView
    private lateinit var refreshChart: SparklineView
    private lateinit var fpsChart: SparklineView
    private lateinit var statusLabel: android.widget.TextView

    private val lastAppFrameTimestamp = LongArray(1)
    private val recorded = mutableListOf<Sample>()
    private var recording = false

    data class Sample(
        val atEpochMs: Long,
        val cpuPercent: Double?,
        val memoryPercent: Double?,
        val temperatureCelsius: Double?,
        val refreshRate: Double?,
        val appFps: Double?,
    )

    override fun build(): View {
        val root = column {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(pal.background)
        }
        root.addView(topBar("硬件监控 · ${device.displayName}", onBack = { host.popPage() }), LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, dp(56),
        ))
        val content = column(12)
        statusLabel = text("采样中… 已记录 0 条样本", 12f, pal.muted)
        content.addView(statusLabel)
        content.addView(row {
            addView(secondaryButton(if (recording) "结束记录" else "开始记录") { toggleRecording() },
                LinearLayout.LayoutParams(0, dp(44), 1f))
            addView(secondaryButton("导出 CSV") { exportCsv() },
                LinearLayout.LayoutParams(0, dp(44), 1f).apply { leftMargin = dp(8) })
        })
        cpuChart = chart(content, "CPU 估算占用（%）")
        memoryChart = chart(content, "内存占用（%）")
        temperatureChart = chart(content, "最高温度（°C，归一化到 0-100）")
        refreshChart = chart(content, "屏幕刷新率（Hz，归一化 0-165）")
        fpsChart = chart(content, "前台应用帧率（fps）")
        root.addView(scroll(content))
        startSampling()
        return root
    }

    private fun chart(parent: LinearLayout, label: String): SparklineView {
        parent.addView(text(label, 12f, pal.secondary))
        val chart = SparklineView(context)
        parent.addView(chart, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(84)))
        return chart
    }

    private fun startSampling() {
        stopTimer()
        val runnable = object : Runnable {
            override fun run() {
                sample()
                timer = this
                mainHandler.postDelayed(this, 1_000)
            }
        }
        timer = runnable
        mainHandler.postDelayed(runnable, 50)
    }

    private fun stopTimer() {
        timer?.let { mainHandler.removeCallbacks(it) }
        timer = null
    }

    override fun onDetach() {
        stopTimer()
    }

    private fun sample() {
        host.runRemote({ graph.commands.shell(device.id, HardwareSnapshot.snapshotCommand, 10_000) }) { snapshot ->
            if (snapshot !is RemoteResult.Success) return@runRemote
            host.runRemote({ graph.commands.shell(device.id, HardwareSnapshot.appFrameCommand, 10_000) }) { frame ->
                val data = HardwareSnapshotData.parse(snapshot.value.stdout)
                val fps = (frame as? RemoteResult.Success)?.value?.stdout?.let { output ->
                    AppFrameParser.fps(AppFrameParser.parseFrameTimes(output), lastAppFrameTimestamp)
                }
                val cpu = data.cpuUsageEstimatePercent
                val memory = data.memUsedPercent
                val temperature = data.temperaturesCelsius.maxOfOrNull { it.second }
                    ?: data.batteryTempTenths?.let { it / 10.0 }
                val refresh = data.refreshRate
                cpuSamples.add(cpu); memorySamples.add(memory)
                temperatureSamples.add(temperature); refreshSamples.add(refresh)
                fpsSamples.add(fps)
                if (recording) {
                    recorded.add(Sample(System.currentTimeMillis(), cpu, memory, temperature, refresh, fps))
                }
                mainHandler.post { render() }
            }
        }
    }

    private fun render() {
        cpuChart.setSamples(cpuSamples)
        memoryChart.setSamples(memorySamples)
        temperatureChart.setSamples(temperatureSamples)
        refreshChart.setSamples(refreshSamples)
        fpsChart.setSamples(fpsSamples)
        statusLabel.text = if (recording) {
            "记录中… 已记录 ${recorded.size} 条样本"
        } else {
            "采样中… 已记录 ${recorded.size} 条样本"
        }
    }

    private fun toggleRecording() {
        if (recording) {
            recording = false
            host.notify("记录结束，共 ${recorded.size} 条样本，可导出 CSV")
        } else {
            recorded.clear()
            recording = true
            host.notify("开始记录（1 秒/样本）")
        }
        render()
    }

    private fun exportCsv() {
        if (recorded.isEmpty()) {
            host.notify("没有可导出的记录，请先“开始记录”")
            return
        }
        val csv = buildString {
            appendLine("time,cpu_percent,memory_percent,temperature_c,refresh_hz,app_fps")
            recorded.forEach { sample ->
                appendLine(
                    listOf(
                        sample.atEpochMs,
                        sample.cpuPercent?.let { fmt("%.2f", it) } ?: "",
                        sample.memoryPercent?.let { fmt("%.2f", it) } ?: "",
                        sample.temperatureCelsius?.let { fmt("%.2f", it) } ?: "",
                        sample.refreshRate?.let { fmt("%.2f", it) } ?: "",
                        sample.appFps?.let { fmt("%.2f", it) } ?: "",
                    ).joinToString(","),
                )
            }
        }
        val saved = DownloadsWriter.save(context, "hardware-monitor-${System.currentTimeMillis()}.csv", csv.toByteArray())
        if (saved == null) {
            host.notify("导出失败，请检查存储空间")
        } else {
            host.notify("已导出到 $saved")
        }
    }

    private fun fmt(pattern: String, vararg args: Any?): String = String.format(Locale.US, pattern, *args)
}
