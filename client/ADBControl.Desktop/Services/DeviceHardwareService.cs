using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using ADBControl.Desktop.Models;

namespace ADBControl.Desktop.Services;

public sealed class DeviceHardwareService
{
    private readonly AdbService _adb;
    private readonly ConcurrentDictionary<string, long> _lastAppFrameTimestamp = new(StringComparer.Ordinal);

    public DeviceHardwareService(AdbService adb)
    {
        _adb = adb;
    }

    public async Task<DeviceHardwareSnapshot> CollectAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var result = await _adb.ShellAsync(deviceId, SnapshotCommand, cancellationToken);
        if (!result.Success && IsTransientHardwareFailure(result))
        {
            await Task.Delay(250, cancellationToken);
            result = await _adb.ShellAsync(deviceId, SnapshotCommand, cancellationToken);
        }
        if (!result.Success)
        {
            var detail = string.Join(Environment.NewLine, new[] { result.Stderr, result.Stdout }
                .Where(value => !string.IsNullOrWhiteSpace(value)))
                .Trim();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail) ? "无法读取设备硬件信息。" : detail);
        }

        // Some vendor adbd implementations become unstable when two long shell commands share
        // one wireless transport. Collect frame telemetry after the required hardware snapshot.
        AdbCommandResult? frameResult = null;
        try
        {
            frameResult = await _adb.ShellAsync(deviceId, AppFrameCommand, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // Frame telemetry is optional; the hardware snapshot remains valid and visible.
        }

        var capturedAt = DateTimeOffset.Now;
        var frameRaw = frameResult?.Success == true
            ? ParseKeyValueOutput(frameResult.Stdout)
            : new Dictionary<string, string>();
        var appFps = CalculateAppFps(deviceId, Value(frameRaw, "app_frame_times"));
        return ParseSnapshot(result.Stdout, capturedAt, appFps);
    }

    public static bool IsTransientHardwareFailure(AdbCommandResult result)
    {
        var message = $"{result.Stdout}\n{result.Stderr}";
        return message.Contains("device offline", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("device not found", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("no devices", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("closed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("transport", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("timeout", StringComparison.OrdinalIgnoreCase);
    }
    public static DeviceHardwareSnapshot ParseSnapshot(string output, DateTimeOffset? capturedAt = null, double? appFps = null)
    {
        var raw = ParseKeyValueOutput(output);
        var values = new Dictionary<string, HardwareValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["brand"] = CreateValue(raw, "brand", "品牌"),
            ["model"] = CreateValue(raw, "model", "型号"),
            ["device"] = CreateValue(raw, "device", "设备代号"),
            ["android"] = CreateValue(raw, "android", "Android 版本"),
            ["sdk"] = CreateValue(raw, "sdk", "SDK"),
            ["abi"] = CreateValue(raw, "abi", "CPU ABI"),
            ["cpu_model"] = CreateValue(raw, "cpu_model", "CPU 处理器型号"),
            ["cpu_cores"] = CreateValue(raw, "cpu_cores", "CPU 核心数"),
            ["load"] = CreateValue(raw, "load", "系统负载"),
            ["mem_total_kb"] = CreateValue(raw, "mem_total_kb", "内存总量 (kB)"),
            ["mem_available_kb"] = CreateValue(raw, "mem_available_kb", "可用内存 (kB)"),
            ["swap_total_kb"] = CreateValue(raw, "swap_total_kb", "虚拟内存总量 (kB)"),
            ["swap_free_kb"] = CreateValue(raw, "swap_free_kb", "虚拟内存可用量 (kB)"),
            ["zram_disk_bytes"] = CreateValue(raw, "zram_disk_bytes", "ZRAM 配置容量 (Bytes)"),
            ["battery_level"] = CreateValue(raw, "battery_level", "电池电量"),
            ["battery_status"] = CreateValue(raw, "battery_status", "电池状态"),
            ["battery_temp"] = CreateValue(raw, "battery_temp", "电池温度"),
            ["refresh_rate"] = CreateValue(raw, "refresh_rate", "屏幕刷新率 (Hz)"),
            ["app_fps"] = new HardwareValue(
                "app_fps",
                "当前应用 FPS",
                appFps is double fps ? fps.ToString("0.##", CultureInfo.InvariantCulture) : "未获取到前台应用帧数据",
                "ADB SurfaceFlinger",
                appFps is not null),
        };

        var cpuFrequencies = ParseCpuFrequencies(Value(raw, "cpu_freqs"), Value(raw, "cpu_max_freqs"));
        if (!values["cpu_cores"].IsAvailable && cpuFrequencies.Count > 0)
            values["cpu_cores"] = values["cpu_cores"] with { Value = cpuFrequencies.Count.ToString(CultureInfo.InvariantCulture), IsAvailable = true };

        var temperatures = ParseTemperatures(Value(raw, "temperatures"))
            .Concat(ParseTemperatures(Value(raw, "thermal_service")))
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
        if (!temperatures.Any(item => item.Name.Contains("battery", StringComparison.OrdinalIgnoreCase)) &&
            ParseBatteryTemperature(Value(raw, "battery_temp")) is double batteryTemperature)
        {
            temperatures.Add(new HardwareTemperature("BATTERY", batteryTemperature));
        }
        var refreshRate = ParseDouble(Value(raw, "refresh_rate"));
        var gpu = ParseGpu(raw);
        values["gpu_usage"] = new HardwareValue(
            "gpu_usage",
            "GPU 占用率",
            gpu?.EffectiveUsagePercent is double usage ? $"{usage:0.##}" : "当前连接无法获取",
            gpu?.Source ?? "当前 ADB 连接",
            gpu?.EffectiveUsagePercent is not null);
        values["gpu_memory_bytes"] = new HardwareValue(
            "gpu_memory_bytes",
            "GPU 显存占用 (Bytes)",
            gpu?.MemoryBytes?.ToString(CultureInfo.InvariantCulture) ?? "当前连接无法获取",
            gpu?.MemoryBytes is not null ? "ADB GPU Service" : "当前 ADB 连接",
            gpu?.MemoryBytes is not null);
        return new DeviceHardwareSnapshot(capturedAt ?? DateTimeOffset.Now, values, cpuFrequencies, temperatures, refreshRate, gpu, appFps);
    }

    public static IReadOnlyDictionary<string, string> ParseKeyValueOutput(string output)
    {
        return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
            .GroupBy(parts => parts[0].Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last()[1].Trim(), StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<CpuCoreFrequency> ParseCpuFrequencies(string raw, string maximumRaw = "")
    {
        var maximums = Regex.Matches(maximumRaw ?? string.Empty, @"cpu(?<core>\d+):(?<frequency>\d+)", RegexOptions.IgnoreCase)
            .Where(match => int.TryParse(match.Groups["core"].Value, out _) && long.TryParse(match.Groups["frequency"].Value, out _))
            .ToDictionary(
                match => int.Parse(match.Groups["core"].Value, CultureInfo.InvariantCulture),
                match => long.Parse(match.Groups["frequency"].Value, CultureInfo.InvariantCulture));
        var frequencies = new List<CpuCoreFrequency>();
        foreach (Match match in Regex.Matches(raw ?? string.Empty, @"cpu(?<core>\d+):(?<frequency>\d+)", RegexOptions.IgnoreCase))
        {
            if (int.TryParse(match.Groups["core"].Value, out var core) &&
                long.TryParse(match.Groups["frequency"].Value, out var frequency))
                frequencies.Add(new CpuCoreFrequency(core, frequency, maximums.GetValueOrDefault(core) is > 0 ? maximums[core] : null));
        }

        return frequencies.OrderBy(frequency => frequency.CoreIndex).ToList();
    }

    public static IReadOnlyList<HardwareTemperature> ParseTemperatures(string raw)
    {
        var temperatures = new List<HardwareTemperature>();
        foreach (var token in (raw ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = token.LastIndexOf(':');
            if (separator <= 0 || !double.TryParse(token[(separator + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                continue;

            if (Math.Abs(value) >= 1_000)
                value /= 1_000d;
            if (value is < -80 or > 200)
                continue;
            temperatures.Add(new HardwareTemperature(token[..separator], value));
        }

        return temperatures;
    }

    private static HardwareValue CreateValue(IReadOnlyDictionary<string, string> values, string key, string label)
    {
        var value = Value(values, key);
        var available = !string.IsNullOrWhiteSpace(value) && !string.Equals(value, "null", StringComparison.OrdinalIgnoreCase);
        return new HardwareValue(key, label, available ? value : "当前连接无法获取", available ? "ADB" : "当前 ADB 连接", available);
    }

    private static string Value(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out var value) ? value.Trim() : string.Empty;

    private static double? ParseDouble(string value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : null;

    private static double? ParseBatteryTemperature(string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return null;
        if (Math.Abs(parsed) > 200)
            parsed /= 10d;
        return parsed is >= -80 and <= 200 ? parsed : null;
    }

    private static GpuTelemetry? ParseGpu(IReadOnlyDictionary<string, string> raw)
    {
        var usage = ParseDouble(Value(raw, "gpu_usage"));
        var current = NormalizeGpuFrequency(ParseLong(Value(raw, "gpu_cur_freq")));
        var maximum = NormalizeGpuFrequency(ParseLong(Value(raw, "gpu_max_freq")));
        var memory = ParseLong(Value(raw, "gpu_memory_bytes"));
        var access = Value(raw, "gpu_access");
        var unavailableReason = access switch
        {
            "permission_denied" => "设备系统限制访问 GPU 性能节点（ADB shell 与普通 App 均无读取权限）",
            "unsupported" => "设备内核未暴露可读取的 GPU 性能节点",
            _ when usage is null && current is null => "未找到兼容的 GPU 性能数据源",
            _ => string.Empty,
        };
        if (usage is null && current is null && memory is null && string.IsNullOrWhiteSpace(unavailableReason))
            return null;
        var source = usage is not null || current is not null ? "ADB sysfs" : "ADB GPU Service";
        return new GpuTelemetry(
            usage is double percent ? Math.Clamp(percent, 0, 100) : null,
            current,
            maximum,
            source,
            memory,
            unavailableReason);
    }

    private double? CalculateAppFps(string deviceId, string rawFrameTimes)
    {
        var timestamps = rawFrameTimes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timestamp) ? timestamp : 0)
            .Where(timestamp => timestamp is > 0 and < 9_000_000_000_000_000_000)
            .Distinct()
            .Order()
            .ToList();
        if (timestamps.Count == 0)
            return null;

        var latest = timestamps[^1];
        if (_lastAppFrameTimestamp.TryGetValue(deviceId, out var previous) && latest <= previous)
            return 0;
        _lastAppFrameTimestamp[deviceId] = latest;

        var recent = timestamps.Where(timestamp => latest - timestamp <= 1_000_000_000).ToList();
        if (recent.Count < 2)
            return 0;
        var elapsedSeconds = (recent[^1] - recent[0]) / 1_000_000_000d;
        return elapsedSeconds > 0 ? Math.Clamp((recent.Count - 1) / elapsedSeconds, 0, 1000) : 0;
    }

    private static long? NormalizeGpuFrequency(long? value)
        => value is > 0 and < 10_000_000 ? value * 1_000 : value;

    private static long? ParseLong(string value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0 ? parsed : null;

    private const string SnapshotCommand = """
        echo brand=$(getprop ro.product.brand)
        echo model=$(getprop ro.product.model)
        echo device=$(getprop ro.product.device)
        echo android=$(getprop ro.build.version.release)
        echo sdk=$(getprop ro.build.version.sdk)
        echo abi=$(getprop ro.product.cpu.abi)
        cpu_model="$(getprop ro.soc.model)"
        [ -n "$cpu_model" ] || cpu_model="$(getprop ro.board.platform)"
        [ -n "$cpu_model" ] || cpu_model="$(getprop ro.hardware)"
        [ -n "$cpu_model" ] || cpu_model="$(grep -im1 -E 'Hardware|model name|Processor|processor' /proc/cpuinfo 2>/dev/null | cut -d: -f2-)"
        echo cpu_model="$cpu_model"
        cpu_cores=$(getconf _NPROCESSORS_ONLN 2>/dev/null)
        [ -n "$cpu_cores" ] || cpu_cores=$(grep -c '^processor' /proc/cpuinfo 2>/dev/null)
        echo cpu_cores="$cpu_cores"
        echo cpu_freqs="$(for path in /sys/devices/system/cpu/cpu[0-9]*/cpufreq/scaling_cur_freq; do [ -r "$path" ] || continue; core=${path#/sys/devices/system/cpu/}; core=${core%%/*}; value=""; IFS= read -r value < "$path" 2>/dev/null || true; [ -n "$value" ] && printf '%s:%s,' "$core" "$value"; done)"
        echo cpu_max_freqs="$(for path in /sys/devices/system/cpu/cpu[0-9]*/cpufreq/cpuinfo_max_freq; do [ -r "$path" ] || continue; core=${path#/sys/devices/system/cpu/}; core=${core%%/*}; value=""; IFS= read -r value < "$path" 2>/dev/null || true; [ -n "$value" ] && printf '%s:%s,' "$core" "$value"; done)"
        battery_dump="$(dumpsys battery)"
        echo battery_level=$(printf '%s\n' "$battery_dump" | grep -m1 'level:' | cut -d: -f2-)
        echo battery_status=$(printf '%s\n' "$battery_dump" | grep -m1 'status:' | cut -d: -f2-)
        echo battery_temp=$(printf '%s\n' "$battery_dump" | grep -m1 'temperature:' | cut -d: -f2-)
        echo mem_total_kb=$(awk '/MemTotal/ { print $2; exit }' /proc/meminfo)
        echo mem_available_kb=$(awk '/MemAvailable/ { print $2; exit }' /proc/meminfo)
        echo swap_total_kb=$(awk '/SwapTotal/ { print $2; exit }' /proc/meminfo)
        echo swap_free_kb=$(awk '/SwapFree/ { print $2; exit }' /proc/meminfo)
        echo zram_disk_bytes=$(cat /sys/block/zram0/disksize 2>/dev/null)
        echo load=$(cut -d' ' -f1-3 /proc/loadavg)
        thermal_service="$(dumpsys thermalservice 2>/dev/null | sed -n '/Current temperatures from HAL:/,/Current cooling devices/p' | grep 'Temperature{' | grep -E 'mType=(0|1|2|3|4|5|9),' | sed -E 's/.*mValue=([^,]+).*mName=([^,]+).*/\2:\1/' | tr '\n' ',')"
        echo thermal_service="$thermal_service"
        temperatures=""
        if [ -z "$thermal_service" ]; then
            temperatures="$(for path in /sys/class/thermal/thermal_zone*/temp; do [ -r "$path" ] || continue; zone=${path%/temp}; name=""; value=""; IFS= read -r name < "$zone/type" 2>/dev/null || true; IFS= read -r value < "$path" 2>/dev/null || true; [ -n "$value" ] && printf '%s:%s,' "${name:-thermal}" "$value"; done)"
        fi
        echo temperatures="$temperatures"
        gpu_path=""
        gpu_access=unsupported
        for candidate in /sys/class/devfreq/13000000.mali /sys/class/devfreq/3d00000.qcom,kgsl-3d0 /sys/class/kgsl/kgsl-3d0 /sys/class/devfreq/*mali* /sys/class/devfreq/*gpu*; do
            [ -d "$candidate" ] || continue
            if [ -r "$candidate/cur_freq" ] || [ -r "$candidate/gpuclk" ] || [ -r "$candidate/devfreq/cur_freq" ]; then
                gpu_path="$candidate"
                gpu_access=available
                break
            fi
            gpu_access=permission_denied
        done
        echo gpu_access="$gpu_access"
        gpu_cur_freq=$(cat "$gpu_path/cur_freq" 2>/dev/null || cat "$gpu_path/gpuclk" 2>/dev/null)
        [ -n "$gpu_cur_freq" ] || gpu_cur_freq=$(cat "$gpu_path/devfreq/cur_freq" 2>/dev/null)
        echo gpu_cur_freq="$gpu_cur_freq"
        gpu_max_freq=$(cat "$gpu_path/max_freq" 2>/dev/null || cat "$gpu_path/max_gpuclk" 2>/dev/null)
        [ -n "$gpu_max_freq" ] || gpu_max_freq=$(cat "$gpu_path/devfreq/max_freq" 2>/dev/null)
        echo gpu_max_freq="$gpu_max_freq"
        gpu_usage=$(cat "$gpu_path/load" 2>/dev/null | grep -o -E '[0-9]+([.][0-9]+)?' | head -n1)
        [ -n "$gpu_usage" ] || gpu_usage=$(cat "$gpu_path/gpu_busy_percentage" 2>/dev/null)
        [ -n "$gpu_usage" ] || gpu_usage=$(cat "$gpu_path/devfreq/load" 2>/dev/null | grep -o -E '[0-9]+([.][0-9]+)?' | head -n1)
        echo gpu_usage="$gpu_usage"
        echo gpu_memory_bytes=$(dumpsys gpu 2>/dev/null | grep -m1 '^Global total:' | grep -o -E '[0-9]+' | head -n1)
        display_dump="$(dumpsys display 2>/dev/null)"
        refresh_rate=$(printf '%s\n' "$display_dump" | sed -n 's/.*mActiveSfDisplayMode=.*refreshRate=\([0-9.]*\).*/\1/p' | head -n1)
        [ -n "$refresh_rate" ] || refresh_rate=$(printf '%s\n' "$display_dump" | sed -n 's/.*DisplayDeviceInfo{"[^"]*".*renderFrameRate \([0-9.]*\).*/\1/p' | head -n1)
        [ "$refresh_rate" = "null" ] && refresh_rate=""
        [ -n "$refresh_rate" ] || refresh_rate=$(printf '%s\n' "$display_dump" | grep -m1 -E 'mRefreshRate|refreshRate' | grep -o -E '[0-9]+(\\.[0-9]+)?' | head -n1)
        [ "$refresh_rate" = "null" ] && refresh_rate=""
        [ -n "$refresh_rate" ] || refresh_rate=$(settings get system peak_refresh_rate 2>/dev/null)
        echo refresh_rate="$refresh_rate"
        """;

    private const string AppFrameCommand = """
        app_package=$(dumpsys window windows 2>/dev/null | awk '/mCurrentFocus=Window/ { for (i=1; i<=NF; i++) if (index($i, "/") > 0) { split($i, value, "/"); print value[1]; exit } }')
        [ -n "$app_package" ] || app_package=$(dumpsys activity activities 2>/dev/null | awk '/topResumedActivity=/ { for (i=1; i<=NF; i++) if (index($i, "/") > 0) { split($i, value, "/"); print value[1]; exit } }')
        app_layer=""
        if [ -n "$app_package" ]; then
            surface_layers="$(dumpsys SurfaceFlinger --list 2>/dev/null)"
            app_layer=$(printf '%s\n' "$surface_layers" | grep -F "$app_package" | grep -m1 -E 'SurfaceView.*BLAST' | sed -E 's/^RequestedLayerState[{]//; s/ parentId=.*[}]$//; s/[}]$//')
            [ -n "$app_layer" ] || app_layer=$(printf '%s\n' "$surface_layers" | grep -F "$app_package" | grep -m1 -v -E 'ActivityRecord|InputSink' | sed -E 's/^RequestedLayerState[{]//; s/ parentId=.*[}]$//; s/[}]$//')
        fi
        echo app_package="$app_package"
        echo app_layer="$app_layer"
        echo app_frame_times="$(dumpsys SurfaceFlinger --latency "$app_layer" 2>/dev/null | awk 'NR > 1 && $1 ~ /^[0-9]+$/ && $1 > 0 && $1 < 9000000000000000000 { printf "%s,", $1 }')"
        """;
}
