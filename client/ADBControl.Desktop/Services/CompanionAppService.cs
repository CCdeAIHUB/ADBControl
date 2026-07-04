using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using ADBControl.Desktop.Models;

namespace ADBControl.Desktop.Services;

public sealed class CompanionAppService
{
    public const string PackageName = "com.adbcontrol.companion";
    private const string ConfigureAction = "com.adbcontrol.companion.CONFIGURE_CONNECTION";
    private const string ExecuteCommandAction = "com.adbcontrol.companion.EXECUTE_COMMAND";
    private readonly AdbService _adb;

    public CompanionAppService(AdbService adb)
    {
        _adb = adb;
    }

    public async Task<bool> IsInstalledAsync(DeviceModel device)
    {
        var result = await _adb.ShellAsync(device.DeviceId, $"cmd package list packages {PackageName}");
        return result.Success && result.Stdout.Contains(PackageName, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<AdbCommandResult> InstallAsync(DeviceModel device)
    {
        var apkPath = ResolveCompanionApkPath();
        if (apkPath is null)
        {
            return new AdbCommandResult(
                1,
                string.Empty,
                "未找到内置 Companion APK。请先构建 android/companion-app 的 debug APK，或确认安装包内包含 companion\\ADBControl.Companion.apk。");
        }

        return await _adb.InstallAsync(device.DeviceId, apkPath);
    }

    public async Task<AdbCommandResult> OpenAsync(DeviceModel device)
    {
        return await _adb.ShellAsync(device.DeviceId, $"monkey -p {PackageName} 1");
    }

    public async Task<AdbCommandResult> ConfigureConnectionAsync(DeviceModel device, int quicPort)
    {
        var host = ResolveLanIPv4Address();
        if (host is null)
            return new AdbCommandResult(1, string.Empty, "未找到可用于局域网连接的本机 IPv4 地址。");

        var endpoint = $"quic://{host}:{quicPort}";
        var command =
            $"am broadcast -a {ConfigureAction} -n {PackageName}/.quic.CompanionConnectionReceiver " +
            $"--es host {EscapeShellToken(host)} --ei port {quicPort} --es endpoint {EscapeShellToken(endpoint)} --es deviceId {EscapeShellToken(device.DeviceId)}";
        return await _adb.ShellAsync(device.DeviceId, command);
    }

    public async Task<AdbCommandResult> OpenAndConfigureAsync(DeviceModel device, int quicPort)
    {
        var open = await OpenAsync(device);
        if (!open.Success)
            return open;

        return await ConfigureConnectionAsync(device, quicPort);
    }

    public async Task<AdbCommandResult> ExecuteCommandAsync(
        DeviceModel device,
        string capabilityId,
        string operation,
        IReadOnlyDictionary<string, object?>? args,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(capabilityId))
            return new AdbCommandResult(1, string.Empty, "Companion 能力标识不能为空。");
        if (string.IsNullOrWhiteSpace(operation))
            return new AdbCommandResult(1, string.Empty, "Companion 操作不能为空。");

        var requestId = Guid.NewGuid().ToString("N");
        var argsJson = JsonSerializer.Serialize(args ?? new Dictionary<string, object?>(), JsonOptions);
        var command =
            $"am broadcast -a {ExecuteCommandAction} -n {PackageName}/.commands.CompanionCommandReceiver " +
            $"--es requestId {EscapeShellToken(requestId)} " +
            $"--es capabilityId {EscapeShellToken(capabilityId)} " +
            $"--es operation {EscapeShellToken(operation)} " +
            $"--es argsJson {EscapeShellToken(argsJson)}";
        return await _adb.ShellAsync(device.DeviceId, command, cancellationToken);
    }

    private static string? ResolveCompanionApkPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "companion", "ADBControl.Companion.apk"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "android", "companion-app", "app", "build", "outputs", "apk", "debug", "app-debug.apk")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "android", "companion-app", "app", "build", "outputs", "apk", "debug", "app-debug.apk")),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? ResolveLanIPv4Address()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                adapter.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(address => address.Address)
            .Where(address => !IPAddress.IsLoopback(address) && !address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .Select(address => address.ToString())
            .ToList();

        return candidates.FirstOrDefault();
    }

    private static string EscapeShellToken(string value)
    {
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };
}
