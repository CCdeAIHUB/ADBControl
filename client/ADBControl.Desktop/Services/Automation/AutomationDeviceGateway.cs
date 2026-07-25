using ADBControl.Desktop.Models;

namespace ADBControl.Desktop.Services.Automation;

public interface IAutomationDeviceGateway
{
    Task<bool> IsConnectedAsync(string deviceId, CancellationToken cancellationToken);
    Task<AutomationCommandResult> ShellAsync(string deviceId, string command, CancellationToken cancellationToken);
    Task<AutomationCommandResult> CompanionAsync(
        string deviceId,
        string capabilityId,
        string operation,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken);
    Task<(int Width, int Height)> GetScreenSizeAsync(string deviceId, CancellationToken cancellationToken);
}

public sealed class AdbAutomationDeviceGateway : IAutomationDeviceGateway
{
    private readonly AdbService _adb;
    private readonly CompanionAppService _companion;

    public AdbAutomationDeviceGateway(AdbService adb, CompanionAppService companion)
    {
        _adb = adb;
        _companion = companion;
    }

    public async Task<bool> IsConnectedAsync(string deviceId, CancellationToken cancellationToken)
    {
        var result = await _adb.ShellAsync(deviceId, "echo adbcontrol-online", cancellationToken);
        return result.Success && result.Stdout.Contains("adbcontrol-online", StringComparison.Ordinal);
    }

    public async Task<AutomationCommandResult> ShellAsync(string deviceId, string command, CancellationToken cancellationToken)
    {
        var result = await _adb.ShellAsync(deviceId, command, cancellationToken);
        return new AutomationCommandResult(result.Success, result.ExitCode, result.Stdout, result.Stderr);
    }

    public async Task<AutomationCommandResult> CompanionAsync(
        string deviceId,
        string capabilityId,
        string operation,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var result = await _companion.ExecuteCommandAsync(
            new DeviceModel { DeviceId = deviceId, DisplayName = deviceId, IsConnected = true },
            capabilityId,
            operation,
            arguments,
            cancellationToken);
        return new AutomationCommandResult(result.Success, result.ExitCode, result.Stdout, result.Stderr);
    }

    public Task<(int Width, int Height)> GetScreenSizeAsync(string deviceId, CancellationToken cancellationToken) =>
        _adb.GetCurrentScreenSizeAsync(deviceId, cancellationToken);
}
