using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ADBControl.Desktop.Services;

public sealed class CompanionInstallLogger : ICompanionInstallLogger
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ADBControl",
        "logs",
        "companion-install.log");

    public async Task WriteAsync(CompanionInstallStatus status, string deviceId)
    {
        var directory = Path.GetDirectoryName(LogPath)
            ?? throw new InvalidOperationException("无法确定 Companion 安装日志目录。");
        Directory.CreateDirectory(directory);

        await _writeLock.WaitAsync();
        try
        {
            await File.AppendAllTextAsync(
                LogPath,
                FormatEntry(status, deviceId, DateTimeOffset.Now) + Environment.NewLine,
                Encoding.UTF8);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public static string FormatEntry(CompanionInstallStatus status, string deviceId, DateTimeOffset timestamp)
    {
        var deviceKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceId)))[..12];
        return JsonSerializer.Serialize(new
        {
            timestamp,
            traceId = status.TraceId,
            module = "companion.install",
            deviceKey,
            stage = status.Stage.ToString(),
            elapsedMs = status.ElapsedMilliseconds,
            active = status.IsActive,
            errorCode = status.Error?.ErrorCode,
            recoverable = status.Error?.Recoverable,
            technicalDetail = status.Error?.TechnicalDetail,
        });
    }
}
