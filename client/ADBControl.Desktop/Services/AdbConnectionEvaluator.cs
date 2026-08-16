namespace ADBControl.Desktop.Services;

public static class AdbConnectionEvaluator
{
    private static readonly string[] FailureMarkers =
    {
        "failed to connect",
        "unable to connect",
        "cannot connect",
        "connection refused",
        "no route to host",
        "timed out",
        "offline",
    };

    private static readonly string[] DeviceUnavailableMarkers =
    {
        "device offline",
        "device is offline",
        "device unauthorized",
        "no devices/emulators found",
        "transport error",
        "transport is closing",
        "connection reset",
        "connection aborted",
        "cannot connect",
        "failed to connect",
        "unable to connect",
        "adb 命令执行超时",
        "error: closed",
    };

    public static bool IsConnectCommandAccepted(AdbCommandResult result)
    {
        if (!result.Success)
            return false;

        var output = $"{result.Stdout}\n{result.Stderr}";
        if (FailureMarkers.Any(marker => output.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            return false;

        return output.Contains("connected to", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("already connected", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsEndpointOnline(string devicesOutput, string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return false;

        return (devicesOutput ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Any(parts => parts.Length >= 2 &&
                parts[0].Equals(endpoint, StringComparison.OrdinalIgnoreCase) &&
                parts[1].Equals("device", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsDeviceUnavailable(AdbCommandResult result)
    {
        if (result.Success)
            return false;
        if (result.ExitCode == -1)
            return true;

        return IsDeviceUnavailable($"{result.Stdout}\n{result.Stderr}");
    }

    public static bool IsDeviceUnavailable(Exception exception) =>
        exception is TimeoutException || IsDeviceUnavailable(exception.ToString());

    private static bool IsDeviceUnavailable(string output)
    {
        if (DeviceUnavailableMarkers.Any(marker => output.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (!output.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return false;

        return output.Contains("error: device", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("adb: device", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("device not found", StringComparison.OrdinalIgnoreCase);
    }
}
