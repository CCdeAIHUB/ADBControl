using System.Text.Json;
using ADBControl.Desktop.Models;

namespace ADBControl.Desktop.Services;

public sealed class AiAgentToolService
{
    private readonly AdbService _adb;

    public AiAgentToolService(AdbService adb)
    {
        _adb = adb;
    }

    public async Task<AiAgentToolResult> ExecuteAsync(
        AiAgentToolCall toolCall,
        string permissionMode,
        DeviceModel? currentDevice,
        Func<AiAgentToolCall, string, Task<bool>> requestApproval,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(toolCall.Name, "adb_shell", StringComparison.Ordinal))
        {
            return Failure(toolCall, $"不支持的工具：{toolCall.Name}");
        }

        if (currentDevice is null || string.IsNullOrWhiteSpace(currentDevice.DeviceId))
            return Failure(toolCall, "当前没有打开的设备详情页，无法执行 ADB 工具。");

        var command = ReadStringArgument(toolCall.ArgumentsJson, "command");
        if (string.IsNullOrWhiteSpace(command))
            return Failure(toolCall, "adb_shell 缺少 command 参数。");

        var approval = await DecideApprovalAsync(toolCall, command, permissionMode, requestApproval);
        if (!approval)
            return Failure(toolCall, $"工具执行已被权限策略拦截：{command}");

        try
        {
            var result = await _adb.ShellAsync(currentDevice.DeviceId, command, cancellationToken);
            return new AiAgentToolResult
            {
                ToolCallId = toolCall.Id,
                Name = toolCall.Name,
                Success = result.Success,
                Content = FormatAdbResult(result),
            };
        }
        catch (Exception ex)
        {
            return Failure(toolCall, ex.Message);
        }
    }

    private static async Task<bool> DecideApprovalAsync(
        AiAgentToolCall toolCall,
        string command,
        string permissionMode,
        Func<AiAgentToolCall, string, Task<bool>> requestApproval)
    {
        return permissionMode switch
        {
            "完全访问" => true,
            "替我审批" => IsLowRiskCommand(command),
            _ => await requestApproval(toolCall, command),
        };
    }

    private static bool IsLowRiskCommand(string command)
    {
        var normalized = command.Trim().ToLowerInvariant();
        if (normalized.Length == 0)
            return false;

        // "替我审批" 只能自动放行只读命令；会改变设备状态的命令必须由用户确认或完全访问模式授权。
        var deniedPrefixes = new[]
        {
            "rm ", "reboot", "svc power", "input ", "am force-stop", "pm clear", "pm uninstall",
            "pm disable", "pm enable", "settings put", "setprop", "cmd package", "monkey ",
        };
        if (deniedPrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.Ordinal)))
            return false;

        var allowedPrefixes = new[]
        {
            "getprop", "dumpsys ", "pm list ", "cmd package list", "ls", "cat /proc/",
            "df", "du", "wm size", "wm density", "ip addr", "date", "id", "whoami",
        };
        return allowedPrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static string ReadStringArgument(string argumentsJson, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            return document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static AiAgentToolResult Failure(AiAgentToolCall toolCall, string message)
    {
        return new AiAgentToolResult
        {
            ToolCallId = toolCall.Id,
            Name = toolCall.Name,
            Success = false,
            Content = message,
        };
    }

    private static string FormatAdbResult(AdbCommandResult result)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(result.Stdout))
            parts.Add(result.Stdout.Trim());
        if (!string.IsNullOrWhiteSpace(result.Stderr))
            parts.Add(result.Stderr.Trim());
        if (parts.Count == 0)
            parts.Add($"adb 退出码 {result.ExitCode}");
        return string.Join(Environment.NewLine, parts);
    }
}
