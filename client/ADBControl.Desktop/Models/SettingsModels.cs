namespace ADBControl.Desktop.Models;

public sealed class AppSettings
{
    public int AdbPort { get; set; } = 15037;
    public int QuicPort { get; set; } = 15038;
    public bool StartWithSystem { get; set; }
    public bool? DarkMode { get; set; }
    public List<SavedDeviceSettings> Devices { get; set; } = new();
    public List<AiModelSettings> AiModels { get; set; } = new();
    public List<AiChatMessage> AiMessages { get; set; } = new();
    public List<string> HardwareMonitorMetrics { get; set; } =
    [
        "cpu.usage",
        "memory.physical",
        "temperature.max",
        "display.refresh",
    ];
    public Dictionary<string, string> HardwareMonitorMetricColors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SavedDeviceSettings
{
    public string DeviceId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ConnectionKind { get; set; } = "wireless";
    public string IpAddress { get; set; } = string.Empty;
    public int Port { get; set; }
    public string MdnsServiceId { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
}

public sealed class AiAttachment
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsImage { get; set; }
}

public sealed class AiChatMessage
{
    public string Role { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string ThinkingText { get; set; } = string.Empty;
    public double? ProcessingSeconds { get; set; }
    public bool IsUser { get; set; }
    public List<AiAttachment> Attachments { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class AiModelSettings
{
    public string Name { get; set; } = string.Empty;
    public string ApiUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
}

public sealed class AiConversationMessage
{
    public string Role { get; set; } = "user";
    public string Text { get; set; } = string.Empty;
    public string ThinkingText { get; set; } = string.Empty;
    public string? ToolCallId { get; set; }
    public string? ToolName { get; set; }
    public List<AiAttachment> Attachments { get; set; } = new();
    public List<AiAgentToolCall> ToolCalls { get; set; } = new();
}

public sealed class AiAgentToolCall
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ArgumentsJson { get; set; } = "{}";
}

public sealed class AiAgentToolResult
{
    public string ToolCallId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Content { get; set; } = string.Empty;
}

public sealed class AiAgentRequest
{
    public required AiModelSettings Model { get; init; }
    public required IReadOnlyList<AiConversationMessage> Messages { get; init; }
    public string PermissionMode { get; init; } = "请求批准";
    public string? CurrentDeviceId { get; init; }
    public string? CurrentDeviceName { get; init; }
}

public sealed class AiAgentResponse
{
    public string Text { get; set; } = string.Empty;
    public string ThinkingText { get; set; } = string.Empty;
    public List<AiConversationMessage> NewMessages { get; set; } = new();
}

public sealed class AiStreamDelta
{
    public string Text { get; init; } = string.Empty;
    public bool IsThinking { get; init; }
}
