using System.Diagnostics;
using System.Text;

namespace ADBControl.Desktop.Services;

public sealed record AdbCommandResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Success => ExitCode == 0;
}

public sealed class AdbService
{
    public async Task<AdbCommandResult> PairAsync(string ip, int port, string code)
    {
        return await RunAsync("pair", $"{ip}:{port}", code);
    }

    public async Task<AdbCommandResult> ConnectAsync(string ip, int port)
    {
        return await RunAsync("connect", $"{ip}:{port}");
    }

    public async Task<AdbCommandResult> DevicesAsync()
    {
        return await RunAsync("devices", "-l");
    }

    public async Task<AdbCommandResult> TcpIpAsync(int port)
    {
        return await RunAsync("tcpip", port.ToString());
    }

    public async Task<AdbCommandResult> ShellAsync(string deviceId, string command)
    {
        return await RunAsync("-s", deviceId, "shell", command);
    }

    public async Task<AdbCommandResult> InstallAsync(string deviceId, string apkPath)
    {
        return await RunAsync("-s", deviceId, "install", "-r", apkPath);
    }

    public async Task<AdbCommandResult> PushAsync(string deviceId, string localPath, string remotePath)
    {
        return await RunAsync("-s", deviceId, "push", localPath, remotePath);
    }

    public async Task<AdbCommandResult> PullAsync(string deviceId, string remotePath, string localPath)
    {
        return await RunAsync("-s", deviceId, "pull", remotePath, localPath);
    }

    public async Task<byte[]> ScreencapPngAsync(string deviceId)
    {
        return await RunBytesAsync("-s", deviceId, "exec-out", "screencap", "-p");
    }

    private static async Task<AdbCommandResult> RunAsync(params string[] args)
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

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new AdbCommandResult(process.ExitCode, stdout, stderr);
    }

    private static async Task<byte[]> RunBytesAsync(params string[] args)
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
        await process.StandardOutput.BaseStream.CopyToAsync(memory);
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? $"adb 退出码 {process.ExitCode}" : stderr.Trim());
        }

        return memory.ToArray();
    }
}
