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
import com.adbcontrol.remote.data.adb.HardwareMonitorConfig
import com.adbcontrol.remote.data.adb.HardwareMonitorMetric
import com.adbcontrol.remote.data.io.DownloadsWriter
import com.adbcontrol.remote.model.RemoteDevice
import com.adbcontrol.remote.model.RemoteResult
import com.adbcontrol.remote.transport.RemoteHardwareMonitorStatus
import com.adbcontrol.remote.transport.ScreenEndpointPolicy
import com.adbcontrol.remote.ui.common.BasePage
import com.adbcontrol.remote.ui.common.PageHost
import com.adbcontrol.remote.ui.common.SparklineView
import java.util.Locale

/**
 * 硬件监控（对应桌面端“硬件监控”窗口的最小完整子集）：
 * - 采样任务归 Core 所在的 Go 服务所有，离开页面后仍继续，只有显式停止才结束；
 * - 页面只轮询任务状态与最近样本，不再自行叠加 ADB 请求；
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
    private val cpuFrequencySamples = mutableListOf<Double?>()
    private val extendedMemorySamples = mutableListOf<Double?>()
    private val gpuUsageSamples = mutableListOf<Double?>()
    private val gpuFrequencySamples = mutableListOf<Double?>()
    private val gpuMemorySamples = mutableListOf<Double?>()

    private lateinit var cpuChart: SparklineView
    private lateinit var memoryChart: SparklineView
    private lateinit var temperatureChart: SparklineView
    private lateinit var refreshChart: SparklineView
    private lateinit var fpsChart: SparklineView
    private lateinit var cpuFrequencyChart: SparklineView
    private lateinit var extendedMemoryChart: SparklineView
    private lateinit var gpuUsageChart: SparklineView
    private lateinit var gpuFrequencyChart: SparklineView
    private lateinit var gpuMemoryChart: SparklineView
    private lateinit var statusLabel: android.widget.TextView
    private lateinit var recordButton: android.widget.Button
    private lateinit var metricButton: android.widget.Button
    private val metricViews = mutableMapOf<HardwareMonitorMetric, View>()
    private val monitorConfig = HardwareMonitorConfig()

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
        val cpuFrequencyGhz: Double?,
        val extendedMemoryPercent: Double?,
        val gpuUsagePercent: Double?,
        val gpuFrequencyMhz: Double?,
        val gpuMemoryMb: Double?,
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
        statusLabel = text("正在读取后台任务状态…", 12f, pal.muted)
        content.addView(statusLabel)
        content.addView(row {
            recordButton = primaryButton("开始记录") { toggleRecording() }
            addView(recordButton,
                LinearLayout.LayoutParams(0, dp(44), 1f))
            addView(secondaryButton("导出 CSV") { exportCsv() },
                LinearLayout.LayoutParams(0, dp(44), 1f).apply { leftMargin = dp(8) })
        })
        metricButton = secondaryButton("调整监控项目") { chooseMetric() }
        content.addView(metricButton, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(44)))
        cpuChart = chart(content, HardwareMonitorMetric.CPU)
        cpuFrequencyChart = chart(content, HardwareMonitorMetric.CPU_FREQUENCY)
        memoryChart = chart(content, HardwareMonitorMetric.MEMORY)
        extendedMemoryChart = chart(content, HardwareMonitorMetric.EXTENDED_MEMORY)
        gpuUsageChart = chart(content, HardwareMonitorMetric.GPU_USAGE)
        gpuFrequencyChart = chart(content, HardwareMonitorMetric.GPU_FREQUENCY)
        gpuMemoryChart = chart(content, HardwareMonitorMetric.GPU_MEMORY)
        temperatureChart = chart(content, HardwareMonitorMetric.TEMPERATURE)
        refreshChart = chart(content, HardwareMonitorMetric.REFRESH_RATE)
        fpsChart = chart(content, HardwareMonitorMetric.APP_FPS)
        applyMetricVisibility()
        root.addView(scroll(content), LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f))
        startSampling()
        return root
    }

    private fun chart(parent: LinearLayout, metric: HardwareMonitorMetric): SparklineView {
        val chart = SparklineView(context)
        val holder = card(column(6) {
            addView(text("${metric.title}（${metric.unit}）", 13f, pal.secondary, true))
            addView(chart, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(84)))
        }, 12)
        metricViews[metric] = holder
        parent.addView(holder)
        return chart
    }

    private fun chooseMetric() {
        val options = HardwareMonitorMetric.entries.map { metric ->
            "${if (metric in monitorConfig.selected()) "✓ " else ""}${metric.title}"
        }
        host.showChoiceDialog("调整监控项目", options) { selected ->
            val index = options.indexOf(selected)
            if (index < 0) return@showChoiceDialog
            val metric = HardwareMonitorMetric.entries[index]
            val enabled = metric !in monitorConfig.selected()
            if (!monitorConfig.set(metric, enabled)) {
                host.notify("至少保留一个监控项目")
            }
            applyMetricVisibility()
        }
    }

    private fun applyMetricVisibility() {
        val selected = monitorConfig.selected()
        metricViews.forEach { (metric, view) -> view.visibility = if (metric in selected) View.VISIBLE else View.GONE }
        if (::metricButton.isInitialized) metricButton.text = "调整监控项目 · ${selected.size} 项"
    }

    private fun startSampling() {
        stopTimer()
        val runnable = object : Runnable {
            override fun run() {
                sample()
                timer = this
                mainHandler.postDelayed(this, 3_000)
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
        val profile = graph.activeProfile
        val session = graph.activeSession
        val endpoint = profile?.let { ScreenEndpointPolicy.derive(it.endpoint, it.webEndpoint) }
        if (session == null || endpoint == null) {
            statusLabel.text = "后台监控服务地址不可用"
            return
        }
        host.runRemote({ graph.hardwareMonitor.status(endpoint, session.token, device.id) }) { result ->
            when (result) {
                is RemoteResult.Failure -> statusLabel.text = "读取失败：${result.error.message}（${result.error.errorCode}）"
                is RemoteResult.Success -> applyRemoteStatus(result.value)
            }
        }
    }

    private fun applyRemoteStatus(status: RemoteHardwareMonitorStatus) {
        recording = status.running
        recorded.clear()
        status.samples.takeLast(240).forEach { sample ->
            val memory = sample.memoryTotalKb.takeIf { it > 0 }?.let { total ->
                ((total - sample.memoryAvailableKb).toDouble() / total * 100.0).coerceIn(0.0, 100.0)
            }
            val cpuFrequency = sample.cpuFrequenciesKHz.takeIf { it.isNotEmpty() }?.average()?.div(1_000_000.0)
            recorded += Sample(sample.capturedAtEpochMs, null, memory, sample.temperaturesC.maxOrNull(), null, null, cpuFrequency, memory, null, null, null)
        }
        fun replace(target: MutableList<Double?>, values: List<Double?>) { target.clear(); target.addAll(values) }
        replace(cpuSamples, recorded.map { it.cpuPercent }); replace(cpuFrequencySamples, recorded.map { it.cpuFrequencyGhz })
        replace(memorySamples, recorded.map { it.memoryPercent }); replace(extendedMemorySamples, recorded.map { it.extendedMemoryPercent })
        replace(temperatureSamples, recorded.map { it.temperatureCelsius }); replace(refreshSamples, recorded.map { it.refreshRate })
        replace(fpsSamples, recorded.map { it.appFps }); replace(gpuUsageSamples, recorded.map { it.gpuUsagePercent })
        replace(gpuFrequencySamples, recorded.map { it.gpuFrequencyMhz }); replace(gpuMemorySamples, recorded.map { it.gpuMemoryMb })
        recordButton.text = if (recording) "停止记录" else "开始记录"
        metricButton.isEnabled = !recording
        statusLabel.setTextColor(if (recording) pal.warning else pal.muted)
        statusLabel.text = when {
            recording -> "● 后台记录中 · 已记录 ${status.sampleCount} 条；离开本页仍会继续"
            status.lastError.isNotBlank() -> "任务已停止 · 最近错误：${status.lastError}（${status.lastErrorCode}）"
            else -> "后台任务未运行 · 已保留 ${status.sampleCount} 条样本"
        }
        render()
    }

    private fun render() {
        cpuChart.setSamples(cpuSamples)
        cpuFrequencyChart.setSamples(cpuFrequencySamples)
        memoryChart.setSamples(memorySamples)
        extendedMemoryChart.setSamples(extendedMemorySamples)
        gpuUsageChart.setSamples(gpuUsageSamples)
        gpuFrequencyChart.setSamples(gpuFrequencySamples)
        gpuMemoryChart.setSamples(gpuMemorySamples)
        temperatureChart.setSamples(temperatureSamples)
        refreshChart.setSamples(refreshSamples)
        fpsChart.setSamples(fpsSamples)
        statusLabel.text = if (recording) {
            "● 后台记录中 · 已记录 ${recorded.size} 条样本；离开本页仍会继续"
        } else {
            "后台任务未运行 · 已保留 ${recorded.size} 条样本"
        }
    }

    private fun toggleRecording() {
        val profile = graph.activeProfile
        val session = graph.activeSession
        val endpoint = profile?.let { ScreenEndpointPolicy.derive(it.endpoint, it.webEndpoint) }
        if (session == null || endpoint == null) { host.notify("后台监控服务地址不可用"); return }
        recordButton.isEnabled = false
        host.runRemote({
            if (recording) graph.hardwareMonitor.stop(endpoint, session.token, device.id)
            else graph.hardwareMonitor.start(endpoint, session.token, device.id)
        }) { result ->
            recordButton.isEnabled = true
            when (result) {
                is RemoteResult.Failure -> host.notify("操作失败：${result.error.message}（${result.error.errorCode}）")
                is RemoteResult.Success -> {
                    applyRemoteStatus(result.value)
                    host.notify(if (result.value.running) "后台记录已开始，离开页面后仍会继续" else "后台记录已手动停止")
                }
            }
        }
    }

    private fun exportCsv() {
        if (recorded.isEmpty()) {
            host.notify("没有可导出的记录，请先“开始记录”")
            return
        }
        val csv = buildString {
            val selected = monitorConfig.selected()
            appendLine((listOf("time") + HardwareMonitorMetric.entries.filter { it in selected }.map { it.id }).joinToString(","))
            recorded.forEach { sample ->
                val values = mapOf(
                    HardwareMonitorMetric.CPU to sample.cpuPercent,
                    HardwareMonitorMetric.CPU_FREQUENCY to sample.cpuFrequencyGhz,
                    HardwareMonitorMetric.MEMORY to sample.memoryPercent,
                    HardwareMonitorMetric.EXTENDED_MEMORY to sample.extendedMemoryPercent,
                    HardwareMonitorMetric.GPU_USAGE to sample.gpuUsagePercent,
                    HardwareMonitorMetric.GPU_FREQUENCY to sample.gpuFrequencyMhz,
                    HardwareMonitorMetric.GPU_MEMORY to sample.gpuMemoryMb,
                    HardwareMonitorMetric.TEMPERATURE to sample.temperatureCelsius,
                    HardwareMonitorMetric.REFRESH_RATE to sample.refreshRate,
                    HardwareMonitorMetric.APP_FPS to sample.appFps,
                )
                appendLine((listOf(sample.atEpochMs.toString()) + HardwareMonitorMetric.entries.filter { it in selected }.map { values[it]?.let { value -> fmt("%.2f", value) }.orEmpty() }).joinToString(","))
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
