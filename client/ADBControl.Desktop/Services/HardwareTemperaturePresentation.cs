using ADBControl.Desktop.Models;

namespace ADBControl.Desktop.Services;

public sealed record HardwareTemperatureGroup(
    string Title,
    IReadOnlyList<HardwareTemperature> Items);

public static class HardwareTemperaturePresentation
{
    public static IReadOnlyList<HardwareTemperatureGroup> Group(
        IEnumerable<HardwareTemperature> temperatures)
    {
        return temperatures
            .GroupBy(item => Category(item.Name))
            .OrderBy(group => group.Key.Order)
            .Select(group => new HardwareTemperatureGroup(
                group.Key.Title,
                group.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();
    }

    public static string FormatSensorName(string name)
    {
        var compact = string.Join(' ', name
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var normalized = compact.ToLowerInvariant();
        if (normalized == "battery" || normalized == "bms")
            return "电池";
        if (normalized.StartsWith("cpu", StringComparison.Ordinal) ||
            normalized.StartsWith("gpu", StringComparison.Ordinal))
            return compact.ToUpperInvariant();
        if (normalized.StartsWith("soc", StringComparison.Ordinal))
            return "SoC" + compact[3..];
        return string.IsNullOrWhiteSpace(compact) ? "温度传感器" : compact;
    }

    private static (int Order, string Title) Category(string name)
    {
        var normalized = name.ToLowerInvariant();
        if (normalized.Contains("cpu", StringComparison.Ordinal) ||
            normalized.Contains("soc", StringComparison.Ordinal) ||
            normalized.StartsWith("ap", StringComparison.Ordinal))
            return (0, "处理器");
        if (normalized.Contains("gpu", StringComparison.Ordinal))
            return (1, "图形处理器");
        if (normalized.Contains("battery", StringComparison.Ordinal) ||
            normalized.Contains("bms", StringComparison.Ordinal))
            return (2, "电池");
        if (normalized.Contains("skin", StringComparison.Ordinal) ||
            normalized.Contains("shell", StringComparison.Ordinal) ||
            normalized.Contains("board", StringComparison.Ordinal) ||
            normalized.Contains("usb", StringComparison.Ordinal) ||
            normalized.Contains("modem", StringComparison.Ordinal))
            return (3, "机身");
        return (4, "其他传感器");
    }
}