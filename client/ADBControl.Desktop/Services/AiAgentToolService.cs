using System.Text.Encodings.Web;
using System.Text.Json;
using ADBControl.Desktop.Models;
using ADBControl.Desktop.Services.Automation;

namespace ADBControl.Desktop.Services;

public sealed class AiAgentToolService
{
    private static readonly JsonSerializerOptions ReadableJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly AdbService _adb;
    private readonly CompanionAppService _companion;
    private readonly IAutomationTaskManager? _automation;
    private readonly IAiDeviceInventory? _inventory;

    public AiAgentToolService(
        AdbService adb,
        CompanionAppService companion,
        IAutomationTaskManager? automation = null,
        IAiDeviceInventory? inventory = null)
    {
        _adb = adb;
        _companion = companion;
        _automation = automation;
        _inventory = inventory;
    }

    public async Task<AiAgentToolResult> ExecuteAsync(
        AiAgentToolCall toolCall,
        string permissionMode,
        DeviceModel? currentDevice,
        Func<AiAgentToolCall, string, Task<bool>> requestApproval,
        CancellationToken cancellationToken = default)
    {
        if (toolCall.Name.StartsWith("task_", StringComparison.Ordinal))
            return await ExecuteAutomationToolAsync(toolCall, permissionMode, requestApproval, cancellationToken);

        if (string.Equals(toolCall.Name, "device_list", StringComparison.Ordinal))
            return ExecuteDeviceList(toolCall, currentDevice);

        if (string.Equals(toolCall.Name, "ask_user_choice", StringComparison.Ordinal))
            return Failure(toolCall, "AI_CHOICE_UI_REQUIRED: 该工具必须由 AI 对话界面处理。");

        if (!string.Equals(toolCall.Name, "adb_shell", StringComparison.Ordinal) &&
            !string.Equals(toolCall.Name, "adb_ui_dump", StringComparison.Ordinal) &&
            !string.Equals(toolCall.Name, "adb_tap", StringComparison.Ordinal) &&
            !string.Equals(toolCall.Name, "adb_swipe", StringComparison.Ordinal) &&
            !string.Equals(toolCall.Name, "companion_call", StringComparison.Ordinal))
        {
            return Failure(toolCall, $"不支持的工具：{toolCall.Name}");
        }

        var target = AiDeviceTargetResolver.Resolve(
            _inventory,
            currentDevice,
            ReadStringArgument(toolCall.ArgumentsJson, "deviceId"));
        if (!target.Success)
            return Failure(toolCall, $"{target.ErrorCode}: {target.Message}");
        currentDevice = target.Device!;

        if (string.Equals(toolCall.Name, "companion_call", StringComparison.Ordinal))
            return await ExecuteCompanionCallAsync(toolCall, permissionMode, currentDevice, requestApproval, cancellationToken);

        if (string.Equals(toolCall.Name, "adb_ui_dump", StringComparison.Ordinal))
            return await ExecuteUiDumpAsync(toolCall, permissionMode, currentDevice, requestApproval, cancellationToken);

        if (string.Equals(toolCall.Name, "adb_tap", StringComparison.Ordinal))
            return await ExecuteTapAsync(toolCall, permissionMode, currentDevice, requestApproval, cancellationToken);

        if (string.Equals(toolCall.Name, "adb_swipe", StringComparison.Ordinal))
            return await ExecuteSwipeAsync(toolCall, permissionMode, currentDevice, requestApproval, cancellationToken);

        var command = ReadStringArgument(toolCall.ArgumentsJson, "command");
        if (string.IsNullOrWhiteSpace(command))
            return Failure(toolCall, "adb_shell 缺少 command 参数。");
        if (IsRawInputTouchCommand(command))
            return Failure(toolCall, "为了避免坐标误点，AI 不允许通过 adb_shell 执行 input tap/swipe/touchscreen。请改用 adb_tap 或 adb_swipe 专用工具。");

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

    private AiAgentToolResult ExecuteDeviceList(AiAgentToolCall toolCall, DeviceModel? currentDevice)
    {
        var devices = _inventory?.GetDevices() ??
            (currentDevice is null ? Array.Empty<DeviceModel>() : new[] { currentDevice });
        return new AiAgentToolResult
        {
            ToolCallId = toolCall.Id,
            Name = toolCall.Name,
            Success = true,
            Content = JsonSerializer.Serialize(devices.Select(device => new
            {
                deviceId = device.DeviceId,
                displayName = string.IsNullOrWhiteSpace(device.DisplayName) ? device.DeviceId : device.DisplayName,
                device.Model,
                device.AndroidVersion,
                connectionKind = device.ConnectionKind,
                adbConnected = device.IsConnected,
                companionConnected = device.IsCompanionConnected,
            }), ReadableJsonOptions),
        };
    }

    private async Task<AiAgentToolResult> ExecuteAutomationToolAsync(
        AiAgentToolCall toolCall,
        string permissionMode,
        Func<AiAgentToolCall, string, Task<bool>> requestApproval,
        CancellationToken cancellationToken)
    {
        if (_automation is null)
            return Failure(toolCall, "自动化任务服务尚未初始化。");

        try
        {
            string content;
            switch (toolCall.Name)
            {
                case "task_list":
                    content = await _automation.ListAsJsonAsync(cancellationToken);
                    break;
                case "task_get":
                    content = await _automation.GetAsJsonAsync(RequiredStringArgument(toolCall, "taskId"), cancellationToken);
                    break;
                case "task_create":
                    if (!await ApproveTaskMutationAsync(toolCall, permissionMode, requestApproval, "创建自动化任务"))
                        return Failure(toolCall, "创建任务已被权限策略拦截。");
                    content = await _automation.CreateFromJsonAsync(RequiredObjectArgument(toolCall, "definition"), cancellationToken);
                    break;
                case "task_update":
                    if (!await ApproveTaskMutationAsync(toolCall, permissionMode, requestApproval, "修改自动化任务"))
                        return Failure(toolCall, "修改任务已被权限策略拦截。");
                    content = await _automation.UpdateFromJsonAsync(
                        RequiredStringArgument(toolCall, "taskId"),
                        RequiredObjectArgument(toolCall, "definition"),
                        cancellationToken);
                    break;
                case "task_run":
                    if (!await ApproveTaskMutationAsync(toolCall, permissionMode, requestApproval, "立即运行自动化任务"))
                        return Failure(toolCall, "运行任务已被权限策略拦截。");
                    content = await _automation.RunFromAiAsync(RequiredStringArgument(toolCall, "taskId"), cancellationToken);
                    break;
                case "task_set_enabled":
                    if (!await ApproveTaskMutationAsync(toolCall, permissionMode, requestApproval, "启用或停用自动化任务"))
                        return Failure(toolCall, "启停任务已被权限策略拦截。");
                    content = await _automation.SetEnabledFromAiAsync(
                        RequiredStringArgument(toolCall, "taskId"),
                        ReadBoolArgument(toolCall.ArgumentsJson, "enabled"),
                        cancellationToken);
                    break;
                case "task_delete":
                    if (!await ApproveTaskMutationAsync(toolCall, permissionMode, requestApproval, "删除自动化任务"))
                        return Failure(toolCall, "删除任务已被权限策略拦截。");
                    content = await _automation.DeleteFromAiAsync(RequiredStringArgument(toolCall, "taskId"), cancellationToken);
                    break;
                default:
                    return Failure(toolCall, $"不支持的任务工具：{toolCall.Name}");
            }

            return new AiAgentToolResult
            {
                ToolCallId = toolCall.Id,
                Name = toolCall.Name,
                Success = true,
                Content = content,
            };
        }
        catch (AutomationExecutionException ex)
        {
            return Failure(toolCall, $"{ex.ErrorCode}: {ex.Message}");
        }
        catch (Exception ex)
        {
            return Failure(toolCall, ex.Message);
        }
    }

    private static async Task<bool> ApproveTaskMutationAsync(
        AiAgentToolCall toolCall,
        string permissionMode,
        Func<AiAgentToolCall, string, Task<bool>> requestApproval,
        string action)
    {
        return permissionMode switch
        {
            "完全访问" => true,
            _ => await requestApproval(toolCall, action),
        };
    }

    private async Task<AiAgentToolResult> ExecuteCompanionCallAsync(
        AiAgentToolCall toolCall,
        string permissionMode,
        DeviceModel currentDevice,
        Func<AiAgentToolCall, string, Task<bool>> requestApproval,
        CancellationToken cancellationToken)
    {
        var capabilityId = ReadStringArgument(toolCall.ArgumentsJson, "capabilityId");
        var operation = ReadStringArgument(toolCall.ArgumentsJson, "operation");
        if (string.IsNullOrWhiteSpace(capabilityId) || string.IsNullOrWhiteSpace(operation))
            return Failure(toolCall, "companion_call 缺少 capabilityId 或 operation 参数。");

        var approvalText = $"Companion 能力调用：{capabilityId}/{operation}";
        var approval = await DecideCompanionApprovalAsync(toolCall, capabilityId, operation, permissionMode, requestApproval, approvalText);
        if (!approval)
            return Failure(toolCall, $"工具执行已被权限策略拦截：{approvalText}");

        try
        {
            var args = ReadObjectArgument(toolCall.ArgumentsJson, "args");
            var result = await _companion.ExecuteCommandAsync(currentDevice, capabilityId, operation, args, cancellationToken);
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

    private async Task<AiAgentToolResult> ExecuteUiDumpAsync(
        AiAgentToolCall toolCall,
        string permissionMode,
        DeviceModel currentDevice,
        Func<AiAgentToolCall, string, Task<bool>> requestApproval,
        CancellationToken cancellationToken)
    {
        var approvalText = "读取当前 Android 界面结构（uiautomator dump）";
        var approval = await DecideUiDumpApprovalAsync(toolCall, permissionMode, requestApproval, approvalText);
        if (!approval)
            return Failure(toolCall, $"工具执行已被权限策略拦截：{approvalText}");

        try
        {
            // UI 结构读取必须独立成工具，避免模型在屏幕变化后只凭历史上下文猜测当前页面。
            var result = await _adb.ShellAsync(
                currentDevice.DeviceId,
                "sh -c 'uiautomator dump --compressed /sdcard/adbcontrol-window.xml >/dev/null && cat /sdcard/adbcontrol-window.xml && rm /sdcard/adbcontrol-window.xml'",
                cancellationToken);
            return new AiAgentToolResult
            {
                ToolCallId = toolCall.Id,
                Name = toolCall.Name,
                Success = result.Success,
                Content = result.Success ? TrimLongToolOutput(result.Stdout) : FormatAdbResult(result),
            };
        }
        catch (Exception ex)
        {
            return Failure(toolCall, ex.Message);
        }
    }

    private async Task<AiAgentToolResult> ExecuteTapAsync(
        AiAgentToolCall toolCall,
        string permissionMode,
        DeviceModel currentDevice,
        Func<AiAgentToolCall, string, Task<bool>> requestApproval,
        CancellationToken cancellationToken)
    {
        var x = ReadIntArgument(toolCall.ArgumentsJson, "x");
        var y = ReadIntArgument(toolCall.ArgumentsJson, "y");
        if (x is null || y is null)
            return Failure(toolCall, "adb_tap 缺少有效的 x 或 y 参数。");

        var target = ReadStringArgument(toolCall.ArgumentsJson, "target");
        var approvalText = $"点击设备坐标 ({x.Value}, {y.Value})" + (string.IsNullOrWhiteSpace(target) ? string.Empty : $"：{target}");
        var approval = await DecideTouchApprovalAsync(toolCall, permissionMode, requestApproval, approvalText);
        if (!approval)
            return Failure(toolCall, $"工具执行已被权限策略拦截：{approvalText}");

        try
        {
            var screen = await _adb.GetCurrentScreenSizeAsync(currentDevice.DeviceId, cancellationToken);
            if (!IsPointInsideScreen(x.Value, y.Value, screen.Width, screen.Height))
                return Failure(toolCall, $"点击坐标 ({x.Value}, {y.Value}) 超出当前截图范围 {screen.Width}x{screen.Height}。请先重新观察截图和 UI dump。");

            var result = await _adb.TapAsync(currentDevice.DeviceId, x.Value, y.Value, cancellationToken);
            return new AiAgentToolResult
            {
                ToolCallId = toolCall.Id,
                Name = toolCall.Name,
                Success = result.Success,
                Content = FormatStructuredResult(result, new Dictionary<string, object?>
                {
                    ["x"] = x.Value,
                    ["y"] = y.Value,
                    ["screenWidth"] = screen.Width,
                    ["screenHeight"] = screen.Height,
                    ["target"] = target,
                }),
            };
        }
        catch (Exception ex)
        {
            return Failure(toolCall, ex.Message);
        }
    }

    private async Task<AiAgentToolResult> ExecuteSwipeAsync(
        AiAgentToolCall toolCall,
        string permissionMode,
        DeviceModel currentDevice,
        Func<AiAgentToolCall, string, Task<bool>> requestApproval,
        CancellationToken cancellationToken)
    {
        var startX = ReadIntArgument(toolCall.ArgumentsJson, "startX");
        var startY = ReadIntArgument(toolCall.ArgumentsJson, "startY");
        var endX = ReadIntArgument(toolCall.ArgumentsJson, "endX");
        var endY = ReadIntArgument(toolCall.ArgumentsJson, "endY");
        var durationMs = ReadIntArgument(toolCall.ArgumentsJson, "durationMs") ?? 250;
        if (startX is null || startY is null || endX is null || endY is null)
            return Failure(toolCall, "adb_swipe 缺少有效的 startX、startY、endX 或 endY 参数。");

        durationMs = Math.Clamp(durationMs, 1, 3000);
        var target = ReadStringArgument(toolCall.ArgumentsJson, "target");
        var approvalText = $"滑动设备坐标 ({startX.Value}, {startY.Value}) -> ({endX.Value}, {endY.Value})" +
            (string.IsNullOrWhiteSpace(target) ? string.Empty : $"：{target}");
        var approval = await DecideTouchApprovalAsync(toolCall, permissionMode, requestApproval, approvalText);
        if (!approval)
            return Failure(toolCall, $"工具执行已被权限策略拦截：{approvalText}");

        try
        {
            var screen = await _adb.GetCurrentScreenSizeAsync(currentDevice.DeviceId, cancellationToken);
            if (!IsPointInsideScreen(startX.Value, startY.Value, screen.Width, screen.Height) ||
                !IsPointInsideScreen(endX.Value, endY.Value, screen.Width, screen.Height))
            {
                return Failure(toolCall, $"滑动坐标超出当前截图范围 {screen.Width}x{screen.Height}。请先重新观察截图和 UI dump。");
            }

            var result = await _adb.SwipeAsync(currentDevice.DeviceId, startX.Value, startY.Value, endX.Value, endY.Value, durationMs, cancellationToken);
            return new AiAgentToolResult
            {
                ToolCallId = toolCall.Id,
                Name = toolCall.Name,
                Success = result.Success,
                Content = FormatStructuredResult(result, new Dictionary<string, object?>
                {
                    ["startX"] = startX.Value,
                    ["startY"] = startY.Value,
                    ["endX"] = endX.Value,
                    ["endY"] = endY.Value,
                    ["durationMs"] = durationMs,
                    ["screenWidth"] = screen.Width,
                    ["screenHeight"] = screen.Height,
                    ["target"] = target,
                }),
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

    private static async Task<bool> DecideTouchApprovalAsync(
        AiAgentToolCall toolCall,
        string permissionMode,
        Func<AiAgentToolCall, string, Task<bool>> requestApproval,
        string approvalText)
    {
        return permissionMode switch
        {
            "完全访问" => true,
            "替我审批" => true,
            _ => await requestApproval(toolCall, approvalText),
        };
    }

    private static async Task<bool> DecideUiDumpApprovalAsync(
        AiAgentToolCall toolCall,
        string permissionMode,
        Func<AiAgentToolCall, string, Task<bool>> requestApproval,
        string approvalText)
    {
        return permissionMode switch
        {
            "完全访问" => true,
            "替我审批" => true,
            _ => await requestApproval(toolCall, approvalText),
        };
    }

    private static async Task<bool> DecideCompanionApprovalAsync(
        AiAgentToolCall toolCall,
        string capabilityId,
        string operation,
        string permissionMode,
        Func<AiAgentToolCall, string, Task<bool>> requestApproval,
        string approvalText)
    {
        return permissionMode switch
        {
            "完全访问" => true,
            "替我审批" => IsLowRiskCompanionOperation(capabilityId, operation),
            _ => await requestApproval(toolCall, approvalText),
        };
    }

    private static bool IsLowRiskCompanionOperation(string capabilityId, string operation)
    {
        return string.Equals(operation, "accessibility.status", StringComparison.Ordinal) ||
            string.Equals(operation, "input.text", StringComparison.Ordinal) ||
            string.Equals(operation, "input.key", StringComparison.Ordinal) ||
            operation.StartsWith("accessibility.global.", StringComparison.Ordinal) ||
            operation.StartsWith("accessibility.touch.", StringComparison.Ordinal) ||
            string.Equals(operation, "volume.get", StringComparison.Ordinal) ||
            string.Equals(operation, "app.list", StringComparison.Ordinal) ||
            string.Equals(operation, "screenshot.capture", StringComparison.Ordinal) ||
            (string.Equals(capabilityId, "android.sensor.motion", StringComparison.Ordinal) &&
                string.Equals(operation, "sensor.unsubscribe", StringComparison.Ordinal));
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

    private static bool IsRawInputTouchCommand(string command)
    {
        var normalized = command.Trim().ToLowerInvariant();
        return normalized.StartsWith("input tap ", StringComparison.Ordinal) ||
            normalized.StartsWith("input swipe ", StringComparison.Ordinal) ||
            normalized.StartsWith("input touchscreen tap ", StringComparison.Ordinal) ||
            normalized.StartsWith("input touchscreen swipe ", StringComparison.Ordinal);
    }

    private static bool IsPointInsideScreen(int x, int y, int width, int height)
    {
        return x >= 0 && y >= 0 && x < width && y < height;
    }

    private static string FormatStructuredResult(AdbCommandResult result, IReadOnlyDictionary<string, object?> metadata)
    {
        return JsonSerializer.Serialize(new
        {
            adb = new
            {
                success = result.Success,
                exitCode = result.ExitCode,
                stdout = result.Stdout.Trim(),
                stderr = result.Stderr.Trim(),
            },
            metadata,
        });
    }

    private static string TrimLongToolOutput(string output)
    {
        var trimmed = output.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return "adb_ui_dump 未返回界面节点。";

        const int maxLength = 80000;
        if (trimmed.Length <= maxLength)
            return trimmed;

        var edgeLength = maxLength / 2;
        return trimmed[..edgeLength] +
            $"{Environment.NewLine}... adb_ui_dump 输出过长，已截断中间内容，保留开头和结尾用于页面判断 ...{Environment.NewLine}" +
            trimmed[^edgeLength..];
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

    private static int? ReadIntArgument(string argumentsJson, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            if (!document.RootElement.TryGetProperty(name, out var value))
                return null;

            return value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt32(out var intValue) => intValue,
                JsonValueKind.Number when value.TryGetDouble(out var doubleValue) => (int)Math.Round(doubleValue),
                JsonValueKind.String when int.TryParse(value.GetString(), out var stringValue) => stringValue,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool ReadBoolArgument(string argumentsJson, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            if (!document.RootElement.TryGetProperty(name, out var value))
                return false;
            return value.ValueKind == JsonValueKind.True ||
                value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed) && parsed;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string RequiredStringArgument(AiAgentToolCall toolCall, string name)
    {
        var value = ReadStringArgument(toolCall.ArgumentsJson, name);
        if (string.IsNullOrWhiteSpace(value))
            throw new AutomationExecutionException("AI_TOOL_ARGUMENT_MISSING", $"{toolCall.Name} 缺少 {name}。", "automation.ai");
        return value;
    }

    private static string RequiredObjectArgument(AiAgentToolCall toolCall, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(toolCall.ArgumentsJson) ? "{}" : toolCall.ArgumentsJson);
            if (!document.RootElement.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
                throw new AutomationExecutionException("AI_TOOL_ARGUMENT_MISSING", $"{toolCall.Name} 缺少对象参数 {name}。", "automation.ai");
            return value.GetRawText();
        }
        catch (JsonException ex)
        {
            throw new AutomationExecutionException("AI_TOOL_ARGUMENT_INVALID", $"{toolCall.Name} 参数 JSON 无效：{ex.Message}", "automation.ai", innerException: ex);
        }
    }

    private static IReadOnlyDictionary<string, object?> ReadObjectArgument(string argumentsJson, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            if (!document.RootElement.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
                return new Dictionary<string, object?>();

            return value.EnumerateObject().ToDictionary(
                property => property.Name,
                property => ReadJsonValue(property.Value));
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>();
        }
    }

    private static object? ReadJsonValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when value.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => value.GetRawText(),
        };
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
