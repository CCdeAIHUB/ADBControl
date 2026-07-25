using System.Text.RegularExpressions;

namespace ADBControl.Desktop.Services;

/// <summary>
/// A resolved ADB service announced on the local network through mDNS.
/// </summary>
public sealed record AdbMdnsService(string InstanceName, string ServiceType, string Host, int Port)
{
    public string Endpoint => $"{Host}:{Port}";
    public string AdbSerial => $"{InstanceName}.{ServiceType}";

    public bool IsConnectService => ServiceType is "_adb-tls-connect._tcp" or "_adb._tcp";

    // ADB appends a transient mDNS suffix to the device serial. Persist the stable prefix instead.
    public string StableDeviceId
    {
        get
        {
            if (!InstanceName.StartsWith("adb-", StringComparison.OrdinalIgnoreCase))
                return InstanceName;

            var suffixStart = InstanceName.LastIndexOf('-');
            return suffixStart > "adb-".Length ? InstanceName[..suffixStart] : InstanceName;
        }
    }
}

public static partial class AdbMdnsServiceParser
{
    [GeneratedRegex(@"^(?<instance>.+?)(?:\s+\(\d+\))?\s+(?<service>_adb(?:-tls-(?:connect|pairing))?\._tcp)\s+(?<host>\[[^\]]+\]|[^:\s]+):(?<port>\d+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceLine();

    public static IReadOnlyList<AdbMdnsService> Parse(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return Array.Empty<AdbMdnsService>();

        var services = new List<AdbMdnsService>();
        foreach (var line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var match = ServiceLine().Match(line.Trim());
            if (!match.Success || !int.TryParse(match.Groups["port"].Value, out var port) || port is < 1 or > 65535)
                continue;

            services.Add(new AdbMdnsService(
                match.Groups["instance"].Value.Trim(),
                match.Groups["service"].Value,
                match.Groups["host"].Value.Trim('[', ']'),
                port));
        }

        return services;
    }
}
