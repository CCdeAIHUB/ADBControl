using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ADBControl.Desktop.Services;

public sealed record DeviceConnectionLogEntry(
    string TraceId,
    string DeviceId,
    string Stage,
    long ElapsedMilliseconds,
    long QueueWaitMilliseconds,
    string Outcome,
    string? ErrorCode = null);

public interface IDeviceConnectionLogger
{
    Task WriteAsync(DeviceConnectionLogEntry entry);
}

public sealed class NullDeviceConnectionLogger : IDeviceConnectionLogger
{
    public static NullDeviceConnectionLogger Instance { get; } = new();

    private NullDeviceConnectionLogger()
    {
    }

    public Task WriteAsync(DeviceConnectionLogEntry entry) => Task.CompletedTask;
}

public sealed class DeviceConnectionLogger : IDeviceConnectionLogger
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ADBControl",
        "logs",
        "device-connection.log");

    public async Task WriteAsync(DeviceConnectionLogEntry entry)
    {
        try
        {
            var directory = Path.GetDirectoryName(LogPath)
                ?? throw new InvalidOperationException("无法确定设备连接日志目录。");
            Directory.CreateDirectory(directory);
            await _writeLock.WaitAsync();
            try
            {
                await File.AppendAllTextAsync(
                    LogPath,
                    FormatEntry(entry, DateTimeOffset.Now) + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Device control must remain available if only diagnostic storage is unavailable.
            Trace.TraceError($"DEVICE_CONNECTION_LOG_WRITE_FAILED: {ex.Message}");
        }
    }

    public static string FormatEntry(DeviceConnectionLogEntry entry, DateTimeOffset timestamp)
    {
        var deviceKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.DeviceId)))[..12];
        return JsonSerializer.Serialize(new
        {
            timestamp,
            traceId = entry.TraceId,
            module = "device.connection",
            deviceKey,
            stage = entry.Stage,
            elapsedMs = entry.ElapsedMilliseconds,
            queueWaitMs = entry.QueueWaitMilliseconds,
            outcome = entry.Outcome,
            errorCode = entry.ErrorCode,
        });
    }
}