namespace ADBControl.Desktop.Models;

public sealed class AppSettings
{
    public int AdbPort { get; set; } = 15037;
    public bool StartWithSystem { get; set; }
    public bool? DarkMode { get; set; }
    public List<SavedDeviceSettings> Devices { get; set; } = new();
    public List<AiModelSettings> AiModels { get; set; } = new();
}

public sealed class SavedDeviceSettings
{
    public string DeviceId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ConnectionKind { get; set; } = "wireless";
    public string IpAddress { get; set; } = string.Empty;
    public int Port { get; set; }
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
    public bool IsUser { get; set; }
    public List<AiAttachment> Attachments { get; set; } = new();
}

public sealed class AiModelSettings
{
    public string Name { get; set; } = string.Empty;
    public string ApiUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
}
