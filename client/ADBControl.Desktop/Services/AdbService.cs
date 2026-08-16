using System.Diagnostics;
using System.Text;

namespace ADBControl.Desktop.Services;

public sealed record AdbCommandResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Success => ExitCode == 0;
}

public sealed class AdbService : IAdbConnectionGateway
{
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FileTransferTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ScreenshotTimeout = TimeSpan.FromSeconds(15);

    public async Task<AdbCommandResult> PairAsync(string ip, int port, string code)
    {
        return await RunAsync("pair", $"{ip}:{port}", code);
    }

    public async Task<AdbCommandResult> ConnectAsync(string ip, int port)
    {
        return await RunAsync("connect", $"{ip}:{port}");
    }

    public async Task<AdbCommandResult> DisconnectAsync(string deviceId)
    {
        return await RunAsync("disconnect", deviceId);
    }

    public async Task<AdbCommandResult> DevicesAsync()
    {
        return await RunAsync("devices", "-l");
    }

    public async Task<AdbCommandResult> GetStateAsync(string deviceId)
    {
        return await RunAsync("-s", deviceId, "get-state");
    }

    public async Task<(AdbCommandResult Result, IReadOnlyList<AdbMdnsService> Services)> DiscoverMdnsServicesAsync()
    {
        var result = await RunAsync("mdns", "services");
        return (result, result.Success ? AdbMdnsServiceParser.Parse(result.Stdout) : Array.Empty<AdbMdnsService>());
    }

    public async Task<AdbCommandResult> TcpIpAsync(int port)
    {
        return await RunAsync("tcpip", port.ToString());
    }

    public async Task<AdbCommandResult> ShellAsync(string deviceId, string command)
    {
        return await ShellAsync(deviceId, command, CancellationToken.None);
    }

    public async Task<AdbCommandResult> ShellAsync(string deviceId, string command, CancellationToken cancellationToken)
    {
        return await RunAsync(DefaultCommandTimeout, cancellationToken, "-s", deviceId, "shell", NormalizeShellCommand(command));
    }

    internal static string NormalizeShellCommand(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    public async Task<AdbCommandResult> TapAsync(string deviceId, int x, int y)
    {
        return await TapAsync(deviceId, x, y, CancellationToken.None);
    }

    public async Task<AdbCommandResult> TapAsync(string deviceId, int x, int y, CancellationToken cancellationToken)
    {
        return await ShellAsync(deviceId, $"input tap {x} {y}", cancellationToken);
    }

    public async Task<AdbCommandResult> SwipeAsync(string deviceId, int startX, int startY, int endX, int endY, int durationMs)
    {
        return await SwipeAsync(deviceId, startX, startY, endX, endY, durationMs, CancellationToken.None);
    }

    public async Task<AdbCommandResult> SwipeAsync(string deviceId, int startX, int startY, int endX, int endY, int durationMs, CancellationToken cancellationToken)
    {
        return await ShellAsync(deviceId, $"input swipe {startX} {startY} {endX} {endY} {Math.Max(1, durationMs)}", cancellationToken);
    }

    public async Task<AdbCommandResult> InstallAsync(string deviceId, string apkPath)
    {
        return await RunAsync(FileTransferTimeout, CancellationToken.None, "-s", deviceId, "install", "-r", apkPath);
    }

    public async Task<AdbCommandResult> UninstallAsync(string deviceId, string packageName)
    {
        return await RunAsync(FileTransferTimeout, CancellationToken.None, "-s", deviceId, "uninstall", packageName);
    }

    public async Task<AdbCommandResult> PushAsync(string deviceId, string localPath, string remotePath)
    {
        return await RunAsync(FileTransferTimeout, CancellationToken.None, "-s", deviceId, "push", localPath, remotePath);
    }

    public async Task<AdbCommandResult> PullAsync(string deviceId, string remotePath, string localPath)
    {
        return await RunAsync(FileTransferTimeout, CancellationToken.None, "-s", deviceId, "pull", remotePath, localPath);
    }

    public async Task<byte[]> ScreencapPngAsync(string deviceId)
    {
        return await RunBytesAsync(ScreenshotTimeout, "-s", deviceId, "exec-out", "screencap", "-p");
    }

    public async Task<(int Width, int Height)> GetCurrentScreenSizeAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var png = await RunBytesAsync(ScreenshotTimeout, cancellationToken, "-s", deviceId, "exec-out", "screencap", "-p");
        if (!TryReadPngSize(png, out var width, out var height))
            throw new InvalidOperationException("无法从当前设备截图读取屏幕尺寸。");

        return (width, height);
    }

    private static async Task<AdbCommandResult> RunAsync(params string[] args)
    {
        return await RunAsync(DefaultCommandTimeout, CancellationToken.None, args);
    }

    private static async Task<AdbCommandResult> RunAsync(TimeSpan timeout, CancellationToken cancellationToken, params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "adb",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 adb，请确认 Android Platform Tools 已加入 PATH。");

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedSource.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(linkedSource.Token);
        try
        {
            await process.WaitForExitAsync(linkedSource.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new AdbCommandResult(process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            KillProcessTree(process);
            return new AdbCommandResult(-1, string.Empty, $"adb 命令执行超时（{timeout.TotalSeconds:0} 秒）：{string.Join(' ', args)}");
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            throw;
        }
    }

    private static async Task<byte[]> RunBytesAsync(TimeSpan timeout, params string[] args)
    {
        return await RunBytesAsync(timeout, CancellationToken.None, args);
    }

    private static async Task<byte[]> RunBytesAsync(TimeSpan timeout, CancellationToken cancellationToken, params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "adb",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 adb，请确认 Android Platform Tools 已加入 PATH。");
        await using var memory = new MemoryStream();
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(memory, linkedSource.Token);
            await process.WaitForExitAsync(linkedSource.Token);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            KillProcessTree(process);
            throw new TimeoutException($"adb 命令执行超时（{timeout.TotalSeconds:0} 秒）：{string.Join(' ', args)}");
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? $"adb 退出码 {process.ExitCode}" : stderr.Trim());
        }

        return memory.ToArray();
    }

    private static bool TryReadPngSize(byte[] png, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (png.Length < 24 ||
            png[0] != 0x89 ||
            png[1] != 0x50 ||
            png[2] != 0x4E ||
            png[3] != 0x47)
            return false;

        width = ReadBigEndianInt32(png, 16);
        height = ReadBigEndianInt32(png, 20);
        return width > 0 && height > 0;
    }

    private static int ReadBigEndianInt32(byte[] bytes, int offset)
    {
        return (bytes[offset] << 24) |
            (bytes[offset + 1] << 16) |
            (bytes[offset + 2] << 8) |
            bytes[offset + 3];
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
