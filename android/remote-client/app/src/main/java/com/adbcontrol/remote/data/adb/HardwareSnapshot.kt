package com.adbcontrol.remote.data.adb

/**
 * 硬件快照：采集命令逐字对齐桌面端 `DeviceHardwareService.cs` 的 SnapshotCommand/AppFrameCommand。
 * Kotlin raw string 无法转义 `$`，这里用 `§` 占位并在运行期替换为 `$`，保证命令文本逐字等价。
 */
object HardwareSnapshot {

    private fun script(raw: String): String = raw.trimIndent().replace("§", "$")

    val snapshotCommand: String = script("""
        echo brand=§(getprop ro.product.brand)
        echo model=§(getprop ro.product.model)
        echo device=§(getprop ro.product.device)
        echo android=§(getprop ro.build.version.release)
        echo sdk=§(getprop ro.build.version.sdk)
        echo abi=§(getprop ro.product.cpu.abi)
        cpu_model="§(getprop ro.soc.model)"
        [ -n "§cpu_model" ] || cpu_model="§(getprop ro.board.platform)"
        [ -n "§cpu_model" ] || cpu_model="§(getprop ro.hardware)"
        [ -n "§cpu_model" ] || cpu_model="§(grep -im1 -E 'Hardware|model name|Processor|processor' /proc/cpuinfo 2>/dev/null | cut -d: -f2-)"
        echo cpu_model="§cpu_model"
        cpu_cores=§(getconf _NPROCESSORS_ONLN 2>/dev/null)
        [ -n "§cpu_cores" ] || cpu_cores=§(grep -c '^processor' /proc/cpuinfo 2>/dev/null)
        echo cpu_cores="§cpu_cores"
        echo cpu_freqs="§(for path in /sys/devices/system/cpu/cpu[0-9]*/cpufreq/scaling_cur_freq; do [ -r "§path" ] || continue; core=§{path#/sys/devices/system/cpu/}; core=§{core%%/*}; value=""; IFS= read -r value < "§path" 2>/dev/null || true; [ -n "§value" ] && printf '%s:%s,' "§core" "§value"; done)"
        echo cpu_max_freqs="§(for path in /sys/devices/system/cpu/cpu[0-9]*/cpufreq/cpuinfo_max_freq; do [ -r "§path" ] || continue; core=§{path#/sys/devices/system/cpu/}; core=§{core%%/*}; value=""; IFS= read -r value < "§path" 2>/dev/null || true; [ -n "§value" ] && printf '%s:%s,' "§core" "§value"; done)"
        battery_dump="§(dumpsys battery)"
        echo battery_level=§(printf '%s\n' "§battery_dump" | grep -m1 'level:' | cut -d: -f2-)
        echo battery_status=§(printf '%s\n' "§battery_dump" | grep -m1 'status:' | cut -d: -f2-)
        echo battery_temp=§(printf '%s\n' "§battery_dump" | grep -m1 'temperature:' | cut -d: -f2-)
        echo mem_total_kb=§(awk '/MemTotal/ { print §2; exit }' /proc/meminfo)
        echo mem_available_kb=§(awk '/MemAvailable/ { print §2; exit }' /proc/meminfo)
        echo swap_total_kb=§(awk '/SwapTotal/ { print §2; exit }' /proc/meminfo)
        echo swap_free_kb=§(awk '/SwapFree/ { print §2; exit }' /proc/meminfo)
        echo zram_disk_bytes=§(cat /sys/block/zram0/disksize 2>/dev/null)
        echo load=§(cut -d' ' -f1-3 /proc/loadavg)
        thermal_service="§(dumpsys thermalservice 2>/dev/null | sed -n '/Current temperatures from HAL:/,/Current cooling devices/p' | grep 'Temperature{' | grep -E 'mType=(0|1|2|3|4|5|9),' | sed -E 's/.*mValue=([^,]+).*mName=([^,]+).*/\2:\1/' | tr '\n' ',')"
        echo thermal_service="§thermal_service"
        temperatures=""
        if [ -z "§thermal_service" ]; then
            temperatures="§(for path in /sys/class/thermal/thermal_zone*/temp; do [ -r "§path" ] || continue; zone=§{path%/temp}; name=""; value=""; IFS= read -r name < "§zone/type" 2>/dev/null || true; IFS= read -r value < "§path" 2>/dev/null || true; [ -n "§value" ] && printf '%s:%s,' "§{name:-thermal}" "§value"; done)"
        fi
        echo temperatures="§temperatures"
        gpu_path=""
        gpu_access=unsupported
        for candidate in /sys/class/devfreq/13000000.mali /sys/class/devfreq/3d00000.qcom,kgsl-3d0 /sys/class/kgsl/kgsl-3d0 /sys/class/devfreq/*mali* /sys/class/devfreq/*gpu*; do
            [ -d "§candidate" ] || continue
            if [ -r "§candidate/cur_freq" ] || [ -r "§candidate/gpuclk" ] || [ -r "§candidate/devfreq/cur_freq" ]; then
                gpu_path="§candidate"
                gpu_access=available
                break
            fi
            gpu_access=permission_denied
        done
        echo gpu_access="§gpu_access"
        gpu_cur_freq=§(cat "§gpu_path/cur_freq" 2>/dev/null || cat "§gpu_path/gpuclk" 2>/dev/null)
        [ -n "§gpu_cur_freq" ] || gpu_cur_freq=§(cat "§gpu_path/devfreq/cur_freq" 2>/dev/null)
        echo gpu_cur_freq="§gpu_cur_freq"
        gpu_max_freq=§(cat "§gpu_path/max_freq" 2>/dev/null || cat "§gpu_path/max_gpuclk" 2>/dev/null)
        [ -n "§gpu_max_freq" ] || gpu_max_freq=§(cat "§gpu_path/devfreq/max_freq" 2>/dev/null)
        echo gpu_max_freq="§gpu_max_freq"
        gpu_usage=§(cat "§gpu_path/load" 2>/dev/null | grep -o -E '[0-9]+([.][0-9]+)?' | head -n1)
        [ -n "§gpu_usage" ] || gpu_usage=§(cat "§gpu_path/gpu_busy_percentage" 2>/dev/null)
        [ -n "§gpu_usage" ] || gpu_usage=§(cat "§gpu_path/devfreq/load" 2>/dev/null | grep -o -E '[0-9]+([.][0-9]+)?' | head -n1)
        echo gpu_usage="§gpu_usage"
        echo gpu_memory_bytes=§(dumpsys gpu 2>/dev/null | grep -m1 '^Global total:' | grep -o -E '[0-9]+' | head -n1)
        display_dump="§(dumpsys display 2>/dev/null)"
        refresh_rate=§(printf '%s\n' "§display_dump" | sed -n 's/.*mActiveSfDisplayMode=.*refreshRate=\([0-9.]*\).*/\1/p' | head -n1)
        [ "§refresh_rate" = "null" ] && refresh_rate=""
        [ -n "§refresh_rate" ] || refresh_rate=§(printf '%s\n' "§display_dump" | grep -m1 -E 'mRefreshRate|refreshRate' | grep -o -E '[0-9]+(\.[0-9]+)?' | head -n1)
        [ "§refresh_rate" = "null" ] && refresh_rate=""
        [ -n "§refresh_rate" ] || refresh_rate=§(settings get system peak_refresh_rate 2>/dev/null)
        echo refresh_rate="§refresh_rate"
    """)

    val appFrameCommand: String = script("""
        app_package=§(dumpsys window windows 2>/dev/null | awk '/mCurrentFocus=Window/ { for (i=1; i<=NF; i++) if (index(§i, "/") > 0) { split(§i, value, "/"); print value[1]; exit } }')
        [ -n "§app_package" ] || app_package=§(dumpsys activity activities 2>/dev/null | awk '/topResumedActivity=/ { for (i=1; i<=NF; i++) if (index(§i, "/") > 0) { split(§i, value, "/"); print value[1]; exit } }')
        app_layer=""
        if [ -n "§app_package" ]; then
            surface_layers="§(dumpsys SurfaceFlinger --list 2>/dev/null)"
            app_layer=§(printf '%s\n' "§surface_layers" | grep -F "§app_package" | grep -m1 -E 'SurfaceView.*BLAST' | sed -E 's/^RequestedLayerState[{]//; s/ parentId=.*[}]§//; s/[}]§//')
            [ -n "§app_layer" ] || app_layer=§(printf '%s\n' "§surface_layers" | grep -F "§app_package" | grep -m1 -v -E 'ActivityRecord|InputSink' | sed -E 's/^RequestedLayerState[{]//; s/ parentId=.*[}]§//; s/[}]§//')
        fi
        echo app_package="§app_package"
        echo app_layer="§app_layer"
        echo app_frame_times=§(dumpsys SurfaceFlinger --latency "§app_layer" 2>/dev/null | awk 'NR > 1 && §1 ~ /^[0-9]+§/ && §1 > 0 && §1 < 9000000000000000000 { printf "%s,", §1 }')
    """)
}

/** key=value 快照解析（纯 Kotlin，可被契约测试锁定）。 */
data class HardwareSnapshotData(
    val brand: String = "",
    val model: String = "",
    val device: String = "",
    val android: String = "",
    val sdk: String = "",
    val abi: String = "",
    val cpuModel: String = "",
    val cpuCores: Int = 0,
    val cpuFreqs: Map<String, Long> = emptyMap(),
    val cpuMaxFreqs: Map<String, Long> = emptyMap(),
    val batteryLevel: Int? = null,
    val batteryStatus: Int? = null,
    val batteryTempTenths: Long? = null,
    val memTotalKb: Long = 0,
    val memAvailableKb: Long = 0,
    val swapTotalKb: Long = 0,
    val swapFreeKb: Long = 0,
    val zramDiskBytes: Long = 0,
    val load: String = "",
    val temperaturesCelsius: List<Pair<String, Double>> = emptyList(),
    val gpuAccess: String = "",
    val gpuUsagePercent: Double? = null,
    val gpuCurFreqHz: Long? = null,
    val gpuMaxFreqHz: Long? = null,
    val gpuMemoryBytes: Long? = null,
    val refreshRate: Double? = null,
) {
    val memUsedPercent: Double?
        get() = if (memTotalKb > 0) {
            ((memTotalKb - memAvailableKb).toDouble() / memTotalKb * 100).coerceIn(0.0, 100.0)
        } else null

    val swapUsedPercent: Double?
        get() = if (swapTotalKb > 0) {
            ((swapTotalKb - swapFreeKb).toDouble() / swapTotalKb * 100).coerceIn(0.0, 100.0)
        } else null

    // 桌面端语义：CPU 总体占用率按 当前/最高 频率估算。
    val cpuUsageEstimatePercent: Double?
        get() {
            if (cpuFreqs.isEmpty() || cpuMaxFreqs.isEmpty()) return null
            var used = 0.0
            var counted = 0
            cpuFreqs.forEach { (core, freq) ->
                val max = cpuMaxFreqs[core] ?: return@forEach
                if (max > 0) {
                    used += (freq.toDouble() / max).coerceIn(0.0, 1.0)
                    counted++
                }
            }
            return if (counted > 0) used / counted * 100 else null
        }

    val batteryCharging: Boolean
        get() = batteryStatus == 2 || batteryStatus == 5

    companion object {
        fun parse(output: String): HardwareSnapshotData {
            val values = output.lineSequence()
                .mapNotNull { line ->
                    val index = line.indexOf('=')
                    if (index <= 0) null else line.substring(0, index).trim() to line.substring(index + 1).trim()
                }
                .toMap()

            fun str(key: String) = values[key].orEmpty().removeSurrounding("\"")
            fun long(key: String) = values[key]?.toLongOrNull()
            fun double(key: String) = values[key]?.toDoubleOrNull()

            return HardwareSnapshotData(
                brand = str("brand"),
                model = str("model"),
                device = str("device"),
                android = str("android"),
                sdk = str("sdk"),
                abi = str("abi"),
                cpuModel = str("cpu_model"),
                cpuCores = values["cpu_cores"]?.toIntOrNull() ?: 0,
                cpuFreqs = parseCoreFreqs(str("cpu_freqs")),
                cpuMaxFreqs = parseCoreFreqs(str("cpu_max_freqs")),
                batteryLevel = values["battery_level"]?.trim()?.toIntOrNull(),
                batteryStatus = values["battery_status"]?.trim()?.toIntOrNull(),
                batteryTempTenths = values["battery_temp"]?.trim()?.toLongOrNull(),
                memTotalKb = long("mem_total_kb") ?: 0,
                memAvailableKb = long("mem_available_kb") ?: 0,
                swapTotalKb = long("swap_total_kb") ?: 0,
                swapFreeKb = long("swap_free_kb") ?: 0,
                zramDiskBytes = long("zram_disk_bytes") ?: 0,
                load = str("load"),
                temperaturesCelsius = parseTemperatures(str("thermal_service").ifBlank { str("temperatures") }),
                gpuAccess = str("gpu_access"),
                gpuUsagePercent = double("gpu_usage")?.coerceIn(0.0, 100.0),
                gpuCurFreqHz = normalizeGpuFrequency(long("gpu_cur_freq")),
                gpuMaxFreqHz = normalizeGpuFrequency(long("gpu_max_freq")),
                gpuMemoryBytes = long("gpu_memory_bytes"),
                refreshRate = double("refresh_rate"),
            )
        }

        /** cpu0:1804800,cpu1:1804800,… → map；非法段跳过。 */
        fun parseCoreFreqs(text: String): Map<String, Long> = text.split(',')
            .mapNotNull { segment ->
                val index = segment.indexOf(':')
                if (index <= 0) return@mapNotNull null
                val core = segment.substring(0, index).trim()
                val value = segment.substring(index + 1).trim().toLongOrNull() ?: return@mapNotNull null
                core to value
            }
            .toMap()

        /**
         * 温度表 name:value,name:value…。thermalservice 输出的 mValue 已是摄氏度；
         * sysfs 回退输出 0.1 摄氏度。两者理论上都 <200，>200 的值视为毫度并折算。
         */
        fun parseTemperatures(text: String): List<Pair<String, Double>> = text.split(',')
            .mapNotNull { segment ->
                val index = segment.indexOf(':')
                if (index <= 0) return@mapNotNull null
                val name = segment.substring(0, index).trim()
                val value = segment.substring(index + 1).trim().toDoubleOrNull() ?: return@mapNotNull null
                name to if (value > 200) value / 1000.0 else value
            }
            .filter { it.first.isNotBlank() }

        /** 桌面端 NormalizeGpuFrequency：<10_000_000 的值视为 kHz，×1000 转 Hz。 */
        fun normalizeGpuFrequency(value: Long?): Long? =
            value?.takeIf { it > 0 }?.let { if (it < 10_000_000) it * 1000 else it }
    }
}

/** 前台应用帧时间解析：1 秒窗口内 FPS（对齐桌面端 CalculateAppFps）。 */
object AppFrameParser {
    fun parseFrameTimes(output: String): List<Long> = output.lineSequence()
        .firstOrNull { it.startsWith("app_frame_times=") }
        ?.substringAfter('=')
        ?.split(',')
        ?.mapNotNull { it.trim().toLongOrNull() }
        ?.filter { it > 0 }
        ?.distinct()
        ?.sorted()
        .orEmpty()

    fun parseForegroundPackage(output: String): String = output.lineSequence()
        .firstOrNull { it.startsWith("app_package=") }
        ?.substringAfter('=')
        ?.trim()
        .orEmpty()

    fun fps(frameTimes: List<Long>, lastTimestampRef: LongArray): Double? {
        if (frameTimes.isEmpty()) return null
        val latest = frameTimes.last()
        val previous = lastTimestampRef[0]
        if (latest <= previous) return 0.0
        lastTimestampRef[0] = latest
        val recent = frameTimes.filter { latest - it <= 1_000_000_000L }
        if (recent.size < 2) return 0.0
        val elapsedSeconds = (recent.last() - recent.first()) / 1_000_000_000.0
        return if (elapsedSeconds > 0) ((recent.size - 1) / elapsedSeconds).coerceIn(0.0, 1000.0) else 0.0
    }
}
