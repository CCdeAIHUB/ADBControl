using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
using System.Diagnostics;
using System.Threading;
using WinRT;

namespace ADBControl.Desktop;

public static class Program
{
    private const string UiWorkerArgument = "--adbcontrol-ui-worker";
    private const int GraphicsDeviceUnavailableExitCode = unchecked((int)0x8898008D);
    private static App? _app;

    [STAThread]
    public static void Main(string[] args)
    {
        if (!args.Contains(UiWorkerArgument, StringComparer.Ordinal))
        {
            LaunchUiProcess(args);
            return;
        }

        ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _app = new App();
        });
    }

    private static void LaunchUiProcess(string[] args)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            StartupLog.Write("Unable to resolve the desktop executable path.");
            Environment.ExitCode = 1;
            return;
        }

        var originalArguments = args.Where(argument => !string.Equals(argument, UiWorkerArgument, StringComparison.Ordinal)).ToArray();
        var retryHardware = string.Equals(
            Environment.GetEnvironmentVariable("ADBCONTROL_RETRY_HARDWARE_UI"),
            "1",
            StringComparison.Ordinal);

        if (retryHardware)
            WinUiStartupGraphicsCompatibility.ClearRequiredMarker();

        if (WinUiStartupGraphicsCompatibility.IsSoftwareRenderingRequired())
        {
            LaunchWithSoftwareRendering(executablePath, originalArguments, "remembered_compatibility");
            return;
        }

        using var hardwareProcess = StartUiWorker(executablePath, originalArguments);
        if (WaitForStableWindow(hardwareProcess, TimeSpan.FromSeconds(8)))
        {
            StartupLog.Write($"Hardware UI startup succeeded. pid={hardwareProcess.Id}");
            return;
        }

        var exitCode = hardwareProcess.HasExited ? hardwareProcess.ExitCode : 0;
        StartupLog.Write($"Hardware UI startup ended before a stable window. pid={hardwareProcess.Id} exit=0x{unchecked((uint)exitCode):X8}");
        if (exitCode != GraphicsDeviceUnavailableExitCode)
        {
            Environment.ExitCode = exitCode == 0 ? 1 : exitCode;
            return;
        }

        WinUiStartupGraphicsCompatibility.RememberSoftwareRenderingRequired(exitCode);
        LaunchWithSoftwareRendering(executablePath, originalArguments, "graphics_device_unavailable");
    }

    private static void LaunchWithSoftwareRendering(string executablePath, IReadOnlyList<string> args, string reason)
    {
        using var compatibility = WinUiStartupGraphicsCompatibility.EnableSoftwareRendering(executablePath);
        if (compatibility is null)
        {
            StartupLog.Write($"Unable to enable software UI rendering. reason={reason}");
            Environment.ExitCode = 1;
            return;
        }

        using var softwareProcess = StartUiWorker(executablePath, args);
        if (!WaitForStableWindow(softwareProcess, TimeSpan.FromSeconds(12)))
        {
            var exitCode = softwareProcess.HasExited ? softwareProcess.ExitCode : 1;
            StartupLog.Write($"Software UI startup failed. pid={softwareProcess.Id} exit=0x{unchecked((uint)exitCode):X8} reason={reason}");
            Environment.ExitCode = exitCode == 0 ? 1 : exitCode;
            return;
        }

        StartupLog.Write($"Software UI startup succeeded. pid={softwareProcess.Id} reason={reason}");
    }

    private static Process StartUiWorker(string executablePath, IReadOnlyList<string> args)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        startInfo.ArgumentList.Add(UiWorkerArgument);
        foreach (var argument in args)
            startInfo.ArgumentList.Add(argument);

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start the ADBControl UI process.");
    }

    private static bool WaitForStableWindow(Process process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        DateTime? windowReadyAt = null;
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
                return false;

            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero && process.Responding)
            {
                windowReadyAt ??= DateTime.UtcNow;
                if (DateTime.UtcNow - windowReadyAt >= TimeSpan.FromSeconds(5))
                    return true;
            }
            else
            {
                windowReadyAt = null;
            }

            Thread.Sleep(100);
        }

        return !process.HasExited && process.MainWindowHandle != IntPtr.Zero && process.Responding;
    }
}
