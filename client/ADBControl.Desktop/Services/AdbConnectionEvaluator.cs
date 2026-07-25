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
}
