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
}
