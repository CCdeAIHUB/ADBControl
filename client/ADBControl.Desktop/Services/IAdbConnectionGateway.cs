namespace ADBControl.Desktop.Services;

public interface IAdbConnectionGateway
{
    Task<AdbCommandResult> PairAsync(string ip, int port, string code);

    Task<AdbCommandResult> ConnectAsync(string ip, int port);

    Task<AdbCommandResult> DisconnectAsync(string deviceId);

    Task<AdbCommandResult> DevicesAsync();

    Task<AdbCommandResult> GetStateAsync(string deviceId);

    Task<(AdbCommandResult Result, IReadOnlyList<AdbMdnsService> Services)> DiscoverMdnsServicesAsync();
}