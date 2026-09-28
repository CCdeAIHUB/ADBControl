package com.adbcontrol.remote.data.adb

/** 可由用户选择的监控项。ID 与 Windows 客户端保持一致。 */
enum class HardwareMonitorMetric(val id: String, val title: String, val unit: String) {
    CPU("cpu.usage", "CPU 总体占用", "%"),
    CPU_FREQUENCY("cpu.averagefrequency", "CPU 平均核心频率", "GHz"),
    MEMORY("memory.physical", "物理内存占用", "%"),
    EXTENDED_MEMORY("memory.extended", "扩展后内存占用", "%"),
    GPU_USAGE("gpu.usage", "GPU 占用率", "%"),
    GPU_FREQUENCY("gpu.frequency", "GPU 频率", "MHz"),
    GPU_MEMORY("gpu.memory", "GPU 显存占用", "MB"),
    TEMPERATURE("temperature.max", "最高硬件温度", "°C"),
    REFRESH_RATE("display.refresh", "屏幕刷新率", "Hz"),
    APP_FPS("display.appfps", "当前应用 FPS", "FPS"),
}

class HardwareMonitorConfig(initial: Set<HardwareMonitorMetric> = defaultMetrics) {
    private val selected = initial.toMutableSet()
    fun selected(): Set<HardwareMonitorMetric> = selected.toSet()
    fun set(metric: HardwareMonitorMetric, enabled: Boolean): Boolean {
        if (!enabled && selected.size == 1 && metric in selected) return false
        if (enabled) selected += metric else selected -= metric
        return true
    }
    companion object {
        val defaultMetrics = setOf(HardwareMonitorMetric.CPU, HardwareMonitorMetric.MEMORY, HardwareMonitorMetric.TEMPERATURE, HardwareMonitorMetric.REFRESH_RATE)
    }
}
