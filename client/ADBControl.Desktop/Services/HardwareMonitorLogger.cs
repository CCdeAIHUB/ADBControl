using System.Text;

namespace ADBControl.Desktop.Services;

public static class HardwareMonitorLogger
{
    private static readonly SemaphoreSlim WriteLock = new(1, 1);
    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ADBControl",
        "logs",
        "hardware-monitor.log");

    public static string FormatEntry(string deviceId, string level, string message, DateTimeOffset? timestamp = null)
    {
        var time = timestamp ?? DateTimeOffset.Now;
        return $"[{time:O}] [{level.ToUpperInvariant()}] device={deviceId} {message}";
    }

    public static async Task AppendAsync(string entry, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(LogPath)!;
        Directory.CreateDirectory(directory);
        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(LogPath, entry + Environment.NewLine, new UTF8Encoding(false), cancellationToken);
        }
        finally
        {
            WriteLock.Release();
        }
    }
}
