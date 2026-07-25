namespace ADBControl.Desktop.Models;

public sealed record HardwareValue(string Key, string Label, string Value, string Source, bool IsAvailable);

public sealed record CpuCoreFrequency(int CoreIndex, long Kilohertz, long? MaximumKilohertz = null)
{
    public string DisplayValue => Kilohertz >= 1_000_000
        ? $"{Kilohertz / 1_000_000d:0.##} GHz"
        : $"{Kilohertz / 1_000d:0} MHz";

    public string MaximumDisplayValue => MaximumKilohertz is long maximum
        ? maximum >= 1_000_000
            ? $"{maximum / 1_000_000d:0.##} GHz"
            : $"{maximum / 1_000d:0} MHz"
        : "当前连接无法获取";

    public double? FrequencyUsagePercent => MaximumKilohertz is > 0
        ? Math.Clamp(Kilohertz * 100d / MaximumKilohertz.Value, 0, 100)
        : null;
}

public sealed record HardwareTemperature(string Name, double Celsius)
{
    public string DisplayValue => $"{Celsius:0.#} °C";
}

public sealed record GpuTelemetry(
    double? UsagePercent,
    long? CurrentFrequencyHz,
    long? MaximumFrequencyHz,
    string Source,
    long? MemoryBytes = null,
    string UnavailableReason = "")
{
    public double? FrequencyUsagePercent => CurrentFrequencyHz is >= 0 && MaximumFrequencyHz is > 0
        ? Math.Clamp(CurrentFrequencyHz.Value * 100d / MaximumFrequencyHz.Value, 0, 100)
        : null;

    public double? EffectiveUsagePercent => UsagePercent;
}

public sealed record HardwareMonitorSample(
    DateTimeOffset CapturedAt,
    IReadOnlyList<CpuCoreFrequency> CpuFrequencies,
    long? TotalMemoryKb,
    long? AvailableMemoryKb,
    IReadOnlyList<HardwareTemperature> Temperatures,
    double? RefreshRateHz,
    long? SwapTotalKb = null,
    long? SwapFreeKb = null,
    GpuTelemetry? Gpu = null,
    double? AppFps = null)
{
    public long? UsedMemoryKb => TotalMemoryKb is long total && AvailableMemoryKb is long available
        ? Math.Max(0, total - available)
        : null;

    public double? MemoryUsagePercent => TotalMemoryKb is > 0 && UsedMemoryKb is long used
        ? used * 100d / TotalMemoryKb.Value
        : null;

    public double? CpuFrequencyUsagePercent
    {
        get
        {
            var cores = CpuFrequencies.Where(core => core.MaximumKilohertz is > 0).ToList();
            if (cores.Count == 0)
                return null;
            var current = cores.Sum(core => (double)core.Kilohertz);
            var maximum = cores.Sum(core => (double)core.MaximumKilohertz!.Value);
            return maximum > 0 ? Math.Clamp(current * 100d / maximum, 0, 100) : null;
        }
    }

    public long? ExtendedTotalMemoryKb => TotalMemoryKb is long physical
        ? physical + Math.Max(0, SwapTotalKb ?? 0)
        : null;

    public long? ExtendedAvailableMemoryKb => AvailableMemoryKb is long physical
        ? physical + Math.Max(0, SwapFreeKb ?? 0)
        : null;

    public double? ExtendedMemoryUsagePercent => ExtendedTotalMemoryKb is > 0 &&
        ExtendedAvailableMemoryKb is long available
        ? Math.Clamp((ExtendedTotalMemoryKb.Value - available) * 100d / ExtendedTotalMemoryKb.Value, 0, 100)
        : null;
}

public sealed class DeviceHardwareSnapshot
{
    public DeviceHardwareSnapshot(
        DateTimeOffset capturedAt,
        IReadOnlyDictionary<string, HardwareValue> values,
        IReadOnlyList<CpuCoreFrequency> cpuFrequencies,
        IReadOnlyList<HardwareTemperature> temperatures,
        double? refreshRateHz,
        GpuTelemetry? gpu = null,
        double? appFps = null)
    {
        CapturedAt = capturedAt;
        Values = values;
        CpuFrequencies = cpuFrequencies;
        Temperatures = temperatures;
        RefreshRateHz = refreshRateHz;
        Gpu = gpu;
        AppFps = appFps;
    }

    public DateTimeOffset CapturedAt { get; }
    public IReadOnlyDictionary<string, HardwareValue> Values { get; }
    public IReadOnlyList<CpuCoreFrequency> CpuFrequencies { get; }
    public IReadOnlyList<HardwareTemperature> Temperatures { get; }
    public IReadOnlyList<HardwareTemperature> CpuTemperatures
        => Temperatures.Where(item => item.Name.StartsWith("CPU", StringComparison.OrdinalIgnoreCase)).ToList();
    public IReadOnlyList<HardwareTemperature> GpuTemperatures
        => Temperatures.Where(item => item.Name.StartsWith("GPU", StringComparison.OrdinalIgnoreCase)).ToList();
    public double? RefreshRateHz { get; }
    public GpuTelemetry? Gpu { get; }
    public double? AppFps { get; }

    public HardwareValue Value(string key)
    {
        return Values.TryGetValue(key, out var value)
            ? value
            : new HardwareValue(key, key, "当前连接无法获取", "当前 ADB 连接", false);
    }

    public long? TotalMemoryKb => TryGetLong("mem_total_kb");
    public long? AvailableMemoryKb => TryGetLong("mem_available_kb");
    public long? UsedMemoryKb => TotalMemoryKb is long total && AvailableMemoryKb is long available
        ? Math.Max(0, total - available)
        : null;
    public double? MemoryUsagePercent => TotalMemoryKb is > 0 && UsedMemoryKb is long used
        ? used * 100d / TotalMemoryKb.Value
        : null;
    public long? SwapTotalKb => TryGetLong("swap_total_kb");
    public long? SwapFreeKb => TryGetLong("swap_free_kb");
    public long? ExtendedTotalMemoryKb => TotalMemoryKb is long physical
        ? physical + Math.Max(0, SwapTotalKb ?? 0)
        : null;
    public long? ExtendedAvailableMemoryKb => AvailableMemoryKb is long physical
        ? physical + Math.Max(0, SwapFreeKb ?? 0)
        : null;
    public double? ExtendedMemoryUsagePercent => ExtendedTotalMemoryKb is > 0 &&
        ExtendedAvailableMemoryKb is long available
        ? Math.Clamp((ExtendedTotalMemoryKb.Value - available) * 100d / ExtendedTotalMemoryKb.Value, 0, 100)
        : null;
    public double? CpuFrequencyUsagePercent => ToMonitorSample().CpuFrequencyUsagePercent;

    public HardwareMonitorSample ToMonitorSample()
        => new(CapturedAt, CpuFrequencies, TotalMemoryKb, AvailableMemoryKb, Temperatures, RefreshRateHz, SwapTotalKb, SwapFreeKb, Gpu, AppFps);

    private long? TryGetLong(string key)
    {
        return Values.TryGetValue(key, out var value) && long.TryParse(value.Value, out var parsed)
            ? parsed
            : null;
    }
}

public enum HardwareReportFormat
{
    Excel,
    Html,
    Sqlite,
}

public enum DeviceLockState
{
    Unknown,
    Unlocked,
    Locked,
}

public static class HardwareMonitorMetricSearch
{
    public static bool Matches(string? query, string id, string title, string unit)
    {
        var normalized = query?.Trim() ?? string.Empty;
        return normalized.Length == 0 ||
            id.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
            title.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
            unit.Contains(normalized, StringComparison.OrdinalIgnoreCase);
    }
}
