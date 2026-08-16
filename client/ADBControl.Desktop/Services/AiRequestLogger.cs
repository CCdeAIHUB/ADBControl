using System.Text.Json;

namespace ADBControl.Desktop.Services;

public sealed record AiRequestLogEntry(
    string TraceId,
    string ProviderHost,
    string ModelId,
    string Stage,
    int? StatusCode,
    string ErrorCode,
    string ProviderErrorType,
    string ProviderErrorCode,
    string ProviderMessage,
    long ElapsedMs);

public interface IAiRequestLogger
{
    Task WriteAsync(AiRequestLogEntry entry, CancellationToken cancellationToken = default);
}

public sealed class AiRequestLogger : IAiRequestLogger
{
    private static readonly SemaphoreSlim WriteGate = new(1, 1);

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ADBControl",
        "logs",
        "ai-request.log");

    public async Task WriteAsync(AiRequestLogEntry entry, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(LogPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await WriteGate.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(
                LogPath,
                FormatEntry(entry, DateTimeOffset.Now) + Environment.NewLine,
                cancellationToken);
        }
        finally
        {
            WriteGate.Release();
        }
    }

    public static string FormatEntry(AiRequestLogEntry entry, DateTimeOffset timestamp)
    {
        // The schema intentionally excludes API keys, prompts, tool arguments, and attachment data.
        return JsonSerializer.Serialize(new
        {
            timestamp = timestamp.ToString("O"),
            traceId = entry.TraceId,
            providerHost = entry.ProviderHost,
            modelId = entry.ModelId,
            stage = entry.Stage,
            statusCode = entry.StatusCode,
            errorCode = entry.ErrorCode,
            providerErrorType = Sanitize(entry.ProviderErrorType),
            providerErrorCode = Sanitize(entry.ProviderErrorCode),
            providerMessage = Sanitize(entry.ProviderMessage),
            elapsedMs = entry.ElapsedMs,
        });
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var flattened = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flattened.Length <= 1000 ? flattened : flattened[..1000] + "...";
    }
}

public sealed class NullAiRequestLogger : IAiRequestLogger
{
    public static NullAiRequestLogger Instance { get; } = new();

    public Task WriteAsync(AiRequestLogEntry entry, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
