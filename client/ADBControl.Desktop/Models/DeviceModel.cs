namespace ADBControl.Desktop.Models;

public sealed class DeviceModel
{
    public string DeviceId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ConnectionKind { get; set; } = "wireless";
    public string IpAddress { get; set; } = string.Empty;
    public int Port { get; set; }
    public string MdnsServiceId { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public string Brand { get; set; } = "Unknown";
    public string Model { get; set; } = "Android";
    public string AndroidVersion { get; set; } = "-";
    public bool IsConnected { get; set; }
    public bool IsCompanionInstalled { get; set; }
    public bool IsCompanionConnected { get; set; }
}
