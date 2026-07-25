using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using ADBControl.Desktop.Models;

namespace ADBControl.Desktop.Services;

public sealed class CompanionAppService
{
    public const string PackageName = "com.adbcontrol.companion";
    private const string ExecuteCommandAction = "com.adbcontrol.companion.EXECUTE_COMMAND";
    private const string CommandResultDirectory = "/sdcard/Android/data/com.adbcontrol.companion/files/command-results";
    private readonly AdbService _adb;
    private readonly CompanionQuicServer? _quicServer;

    public CompanionAppService(AdbService adb, CompanionQuicServer? quicServer = null)
    {
        _adb = adb;
        _quicServer = quicServer;
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

    public Task<AdbCommandResult> UninstallAsync(DeviceModel device)
        => _adb.UninstallAsync(device.DeviceId, PackageName);

    public static bool IsSignatureMismatch(AdbCommandResult result)
        => !result.Success && ($"{result.Stdout}\n{result.Stderr}").Contains(
            "INSTALL_FAILED_UPDATE_INCOMPATIBLE",
            StringComparison.OrdinalIgnoreCase);

    public async Task<AdbCommandResult> OpenAsync(DeviceModel device)
    {
        return await _adb.ShellAsync(device.DeviceId, $"am start -W -n {PackageName}/.MainActivity");
    }

    public async Task<AdbCommandResult> ConfigureConnectionAsync(DeviceModel device, int quicPort)
    {
        if (_quicServer is null)
            return new AdbCommandResult(1, string.Empty, "桌面端 QUIC 服务尚未配置。");
        try
        {
            await _quicServer.StartAsync();
        }
        catch (Exception ex)
        {
            return new AdbCommandResult(1, string.Empty, $"桌面端 QUIC 服务启动失败：{ex.Message}");
        }
        var host = ResolveLanIPv4Address(device.DeviceId);
        if (host is null)
            return new AdbCommandResult(1, string.Empty, "未找到可用于局域网连接的本机 IPv4 地址。");

        var activeQuicPort = _quicServer.Port;
        var endpoint = $"quic://{host}:{activeQuicPort}";
        var command =
            $"am start -W -n {PackageName}/.quic.CompanionConnectionActivity " +
            $"--es host {EscapeShellToken(host)} --ei port {activeQuicPort} --es endpoint {EscapeShellToken(endpoint)} " +
            $"--es deviceId {EscapeShellToken(device.DeviceId)} " +
            $"--es serverName {EscapeShellToken(CompanionQuicServer.TlsServerName)} " +
            $"--es certificateDerBase64 {EscapeShellToken(_quicServer.CertificateDerBase64)}";
        return await _adb.ShellAsync(device.DeviceId, command);
    }

    public async Task<AdbCommandResult> OpenAndConfigureAsync(DeviceModel device, int quicPort)
    {
        var open = await OpenAsync(device);
        if (!open.Success)
            return open;

        await Task.Delay(400);
        return await ConfigureConnectionAsync(device, quicPort);
    }

    public async Task<bool> IsResponsiveAsync(DeviceModel device, CancellationToken cancellationToken = default)
    {
        if (_quicServer is not null)
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                if (_quicServer.IsDeviceConnected(device.DeviceId))
                    return true;
                await Task.Delay(100, cancellationToken);
            }
            return false;
        }
        var result = await ExecuteCommandAsync(
            device,
            "android.accessibility.control",
            "accessibility.status",
            new Dictionary<string, object?>(),
            cancellationToken);

        // APP 连接状态表示桌面端能触达伴侣 App 的命令入口；具体能力是否已授权由返回 payload 再表达。
        return result.Success &&
            result.Stdout.Contains("Broadcast completed", StringComparison.OrdinalIgnoreCase) &&
            result.Stdout.Contains("\"requestId\"", StringComparison.OrdinalIgnoreCase);
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
            $"am broadcast --receiver-foreground -a {ExecuteCommandAction} -n {PackageName}/.commands.CompanionCommandReceiver " +
            $"--es requestId {EscapeShellToken(requestId)} " +
            $"--es capabilityId {EscapeShellToken(capabilityId)} " +
            $"--es operation {EscapeShellToken(operation)} " +
            $"--es argsJson {EscapeShellToken(argsJson)}";
        var broadcast = await _adb.ShellAsync(device.DeviceId, command, cancellationToken);
        if (!broadcast.Success)
            return broadcast;

        var snapshot = await ReadCommandResultSnapshotAsync(device, requestId, cancellationToken);
        return string.IsNullOrWhiteSpace(snapshot)
            ? broadcast
            : broadcast with { Stdout = $"{broadcast.Stdout}\n data={snapshot}" };
    }

    private async Task<string?> ReadCommandResultSnapshotAsync(DeviceModel device, string requestId, CancellationToken cancellationToken)
    {
        var resultPath = $"{CommandResultDirectory}/{requestId}.json";
        var result = await _adb.ShellAsync(device.DeviceId, $"cat {EscapeShellToken(resultPath)}", cancellationToken);
        return result.Success && !string.IsNullOrWhiteSpace(result.Stdout)
            ? result.Stdout.Trim()
            : null;
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

    public static string? ResolveLanIPv4Address(string? remoteDeviceId = null)
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                adapter.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(address => address.Address)
            .Where(address => !IPAddress.IsLoopback(address) && !address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .ToList();
        if (candidates.Count == 0)
            return null;

        var remoteText = remoteDeviceId?.Split(':', 2)[0];
        if (!IPAddress.TryParse(remoteText, out var remote) || remote.AddressFamily != AddressFamily.InterNetwork)
            return candidates[0].ToString();

        var remoteBytes = remote.GetAddressBytes();
        return candidates
            .OrderByDescending(candidate => CommonPrefixBits(candidate.GetAddressBytes(), remoteBytes))
            .First()
            .ToString();
    }

    private static int CommonPrefixBits(byte[] left, byte[] right)
    {
        var bits = 0;
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            var difference = left[index] ^ right[index];
            if (difference == 0)
            {
                bits += 8;
                continue;
            }
            bits += System.Numerics.BitOperations.LeadingZeroCount((uint)difference) - 24;
            break;
        }
        return bits;
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
