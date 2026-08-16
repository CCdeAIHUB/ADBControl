using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ADBControl.Desktop.Services;

public static class MouseWheelDiagnostics
{
    private const int MaxEntriesPerProcess = 200;
    private static readonly SemaphoreSlim WriteLock = new(1, 1);
    private static int s_entryCount;

    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ADBControl",
        "logs",
        "mouse-wheel.log");

    public static void Write(
        string stage,
        int delta,
        string target,
        double? offsetBefore,
        double? offsetObservedOrRequested,
        bool handled,
        string? errorCode = null,
        string? errorMessage = null)
    {
        if (Interlocked.Increment(ref s_entryCount) > MaxEntriesPerProcess)
            return;

        var entry = JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.Now,
            processId = Environment.ProcessId,
            module = "desktop.mouse-wheel",
            stage,
            delta,
            target,
            offsetBefore,
            offsetObservedOrRequested,
            handled,
            errorCode,
            errorMessage,
        });
        _ = AppendAsync(entry);
    }

    private static async Task AppendAsync(string entry)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            await WriteLock.WaitAsync();
            try
            {
                await File.AppendAllTextAsync(
                    LogPath,
                    entry + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            finally
            {
                WriteLock.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceError($"MOUSE_WHEEL_LOG_WRITE_FAILED: {ex.Message}");
        }
    }
}