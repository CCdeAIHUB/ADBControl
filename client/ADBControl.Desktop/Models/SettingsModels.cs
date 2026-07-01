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
    public string IpAddress { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Note { get; set; } = string.Empty;
}

public sealed class AiModelSettings
{
    public string Name { get; set; } = string.Empty;
    public string ApiUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
}
