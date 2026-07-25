using System.Collections.ObjectModel;
using ADBControl.Desktop.Models;

namespace ADBControl.Desktop.Services;

public sealed class DeviceService
{
    private readonly SettingsService _settings;
    private readonly AdbService _adb;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly HashSet<DeviceModel> _autoConnectSuppressedDevices = new();

    public ObservableCollection<DeviceModel> Devices { get; } = new();

    public DeviceService(SettingsService settings, AdbService adb)
    {
        _settings = settings;
        _adb = adb;

        foreach (var saved in settings.Current.Devices)
        {
            Devices.Add(new DeviceModel
            {
                DeviceId = saved.DeviceId,
                DisplayName = saved.DisplayName,
                ConnectionKind = saved.ConnectionKind,
                IpAddress = saved.IpAddress,
                Port = saved.Port,
                MdnsServiceId = saved.MdnsServiceId,
                Note = saved.Note,
                IsConnected = false,
            });
        }
    }

    public async Task<AdbCommandResult> PairAsync(string ip, int pairingPort, string code)
    {
        return await _adb.PairAsync(ip, pairingPort, code);
    }

    public async Task<AdbCommandResult> ConnectAndSaveAsync(string ip, int port, string note)
    {
        var result = await _adb.ConnectAsync(ip, port);
        if (!IsConnectionSuccessful(result))
            return ConnectionFailure(result, $"{ip}:{port}");

        var id = $"{ip}:{port}";
        var verification = await VerifyEndpointOnlineAsync(id);
        if (!verification.Success)
            return verification;

        var existing = Devices.FirstOrDefault(d => d.DeviceId == id);
        if (existing is null)
        {
            existing = new DeviceModel
            {
                DeviceId = id,
                DisplayName = id,
                ConnectionKind = "wireless",
                IpAddress = ip,
                Port = port,
                Note = note,
                IsConnected = true,
            };
            Devices.Add(existing);
        }
        else
        {
            existing.Note = note;
            existing.IsConnected = true;
        }

        var discovery = await _adb.DiscoverMdnsServicesAsync();
        var service = discovery.Services.FirstOrDefault(candidate =>
            candidate.IsConnectService && EndpointMatches(candidate, ip, port));
        if (service is not null)
            ApplyDiscoveredEndpoint(existing, service);

        await RefreshConnectivityCoreAsync();
        if (!existing.IsConnected)
            return new AdbCommandResult(1, result.Stdout, "无线 ADB 端点短暂连接后已离线。");

        Persist();
        return result;
    }

    public async Task<AdbCommandResult> ConnectSavedWirelessDeviceAsync(DeviceModel device)
    {
        await _connectionGate.WaitAsync();
        try
        {
            if (!IsWirelessDevice(device))
                return new AdbCommandResult(1, string.Empty, "该设备不是可连接的无线 ADB 设备。");

            _autoConnectSuppressedDevices.Remove(device);
            var discovery = await _adb.DiscoverMdnsServicesAsync();
            var result = await ConnectWirelessDeviceCoreAsync(
                device,
                discovery.Services,
                Devices.Count(saved => IsWirelessDevice(saved)) == 1);
            if (!IsConnectionSuccessful(result))
            {
                device.IsConnected = false;
                return result;
            }

            await RefreshConnectivityCoreAsync();
            if (!device.IsConnected)
                return new AdbCommandResult(1, result.Stdout, "ADB 已接受连接命令，但设备未出现在在线设备列表中。");
            Persist();
            return result;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<AdbCommandResult> ConnectSavedWirelessDeviceAtEndpointAsync(DeviceModel device, string ip, int port)
    {
        await _connectionGate.WaitAsync();
        try
        {
            if (!IsWirelessDevice(device))
                return new AdbCommandResult(1, string.Empty, "该设备不是可连接的无线 ADB 设备。");
            if (string.IsNullOrWhiteSpace(ip) || port is < 1 or > 65535)
                return new AdbCommandResult(1, string.Empty, "请填写有效的无线 ADB 地址和端口。");

            _autoConnectSuppressedDevices.Remove(device);
            var result = await _adb.ConnectAsync(ip.Trim(), port);
            var endpoint = $"{ip.Trim()}:{port}";
            if (!IsConnectionSuccessful(result))
            {
                device.IsConnected = false;
                return ConnectionFailure(result, endpoint);
            }

            var verification = await VerifyEndpointOnlineAsync(endpoint);
            if (!verification.Success)
            {
                device.IsConnected = false;
                return verification;
            }

            device.DeviceId = endpoint;
            device.IpAddress = ip.Trim();
            device.Port = port;
            device.IsConnected = true;
            await RefreshConnectivityCoreAsync();
            if (!device.IsConnected)
                return new AdbCommandResult(1, result.Stdout, "无线 ADB 端点短暂连接后已离线。");
            Persist();
            return result;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<AdbCommandResult> DisconnectDeviceAsync(DeviceModel device)
    {
        await _connectionGate.WaitAsync();
        try
        {
            var result = await _adb.DisconnectAsync(device.DeviceId);
            // A manual disconnect is intentional; do not have the background mDNS poll undo it.
            _autoConnectSuppressedDevices.Add(device);
            device.IsConnected = false;
            return result;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<(AdbCommandResult Result, IReadOnlyList<DeviceModel> Devices)> ScanUsbDevicesAsync()
    {
        var result = await _adb.DevicesAsync();
        if (!result.Success)
            return (result, Array.Empty<DeviceModel>());

        var devices = ParseOnlineDevices(result.Stdout).ToList();
        return (result, devices);
    }

    public void SaveUsbDevice(DeviceModel device, string note)
    {
        var existing = Devices.FirstOrDefault(d => d.DeviceId == device.DeviceId);
        if (existing is null)
        {
            device.Note = note;
            device.ConnectionKind = "usb";
            device.IsConnected = true;
            Devices.Add(device);
        }
        else
        {
            existing.Note = note;
            existing.ConnectionKind = "usb";
            existing.IsConnected = true;
        }

        Persist();
    }

    public void Remove(DeviceModel device)
    {
        Devices.Remove(device);
        Persist();
    }

    public async Task<AdbCommandResult> RefreshConnectivityAsync()
    {
        await _connectionGate.WaitAsync();
        try
        {
            return await RefreshConnectivityCoreAsync();
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<bool> EnsureDeviceOnlineAsync(DeviceModel device)
    {
        await RefreshConnectivityAsync();
        var current = Devices.FirstOrDefault(saved => ReferenceEquals(saved, device) || saved.DeviceId.Equals(device.DeviceId, StringComparison.OrdinalIgnoreCase));
        return current?.IsConnected == true;
    }

    private async Task<AdbCommandResult> RefreshConnectivityCoreAsync()
    {
        var result = await _adb.DevicesAsync();
        if (!result.Success)
        {
            MarkAllDisconnected();
            return result;
        }

        var discovery = await _adb.DiscoverMdnsServicesAsync();
        var services = discovery.Result.Success ? discovery.Services : Array.Empty<AdbMdnsService>();
        var onlineDevices = ParseOnlineDevices(result.Stdout)
            .ToDictionary(device => device.DeviceId, StringComparer.OrdinalIgnoreCase);

        var changed = false;
        foreach (var device in Devices.Where(IsWirelessDevice).ToList())
        {
            if (FindOnlineDevice(device, onlineDevices, services) is not null)
                continue;
            if (_autoConnectSuppressedDevices.Contains(device))
                continue;

            var connect = await ConnectWirelessDeviceCoreAsync(
                device,
                services,
                Devices.Count(saved => IsWirelessDevice(saved)) == 1);
            changed |= IsConnectionSuccessful(connect);
        }

        if (changed)
        {
            result = await _adb.DevicesAsync();
            if (!result.Success)
            {
                MarkAllDisconnected();
                return result;
            }

            onlineDevices = ParseOnlineDevices(result.Stdout)
                .ToDictionary(device => device.DeviceId, StringComparer.OrdinalIgnoreCase);
        }

        var stateChanged = false;
        foreach (var device in Devices)
        {
            var online = FindOnlineDevice(device, onlineDevices, services);
            if (online is null)
            {
                stateChanged |= device.IsConnected;
                device.IsConnected = false;
                continue;
            }

            stateChanged |= !device.IsConnected || !device.DeviceId.Equals(online.DeviceId, StringComparison.OrdinalIgnoreCase);
            ApplyOnlineDevice(device, online, services);
        }

        if (changed || stateChanged)
            Persist();

        return result;
    }

    private async Task<AdbCommandResult> ConnectWirelessDeviceCoreAsync(
        DeviceModel device,
        IReadOnlyList<AdbMdnsService> services,
        bool allowUnboundMigration)
    {
        var candidates = BuildWirelessCandidates(device, services, allowUnboundMigration);
        if (candidates.Count == 0 && !string.IsNullOrWhiteSpace(device.IpAddress) && device.Port > 0)
            candidates.Add(new WirelessEndpoint(device.IpAddress, device.Port, null));

        if (candidates.Count == 0)
            return new AdbCommandResult(1, string.Empty, "该设备没有可用的无线 ADB 地址；请先通过配对或 mDNS 发现设备。");

        var failures = new List<string>();
        foreach (var candidate in candidates)
        {
            var result = await _adb.ConnectAsync(candidate.Host, candidate.Port);
            if (IsConnectionSuccessful(result))
            {
                var endpoint = $"{candidate.Host}:{candidate.Port}";
                var verification = await VerifyEndpointOnlineAsync(endpoint, candidate.Service);
                if (verification.Success)
                {
                    ApplyDiscoveredEndpoint(device, candidate.Service, candidate.Host, candidate.Port);
                    device.IsConnected = true;
                    return result;
                }

                failures.Add($"{endpoint}：{FailureText(verification)}");
                continue;
            }

            failures.Add($"{candidate.Host}:{candidate.Port}：{FailureText(result)}");
        }

        device.IsConnected = false;
        return new AdbCommandResult(1, string.Empty, string.Join(Environment.NewLine, failures));
    }

    private async Task<AdbCommandResult> VerifyEndpointOnlineAsync(string endpoint, AdbMdnsService? preferredService = null)
    {
        var devices = await _adb.DevicesAsync();
        if (!devices.Success)
            return devices;

        var discovery = await _adb.DiscoverMdnsServicesAsync();
        var services = discovery.Result.Success ? discovery.Services : Array.Empty<AdbMdnsService>();
        var matchingService = preferredService ?? services.FirstOrDefault(service =>
            service.IsConnectService && service.Endpoint.Equals(endpoint, StringComparison.OrdinalIgnoreCase));
        var serialCandidates = new[] { endpoint, matchingService?.AdbSerial, matchingService?.InstanceName }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var onlineSerial = serialCandidates.FirstOrDefault(serial => AdbConnectionEvaluator.IsEndpointOnline(devices.Stdout, serial));
        if (onlineSerial is null)
            return new AdbCommandResult(1, devices.Stdout, $"连接命令执行后，{endpoint} 未出现在 adb devices 在线列表中。");

        var state = await _adb.GetStateAsync(onlineSerial);
        if (!state.Success || !state.Stdout.Trim().Equals("device", StringComparison.OrdinalIgnoreCase))
            return new AdbCommandResult(1, state.Stdout, string.IsNullOrWhiteSpace(state.Stderr) ? $"{onlineSerial} 未通过 ADB 握手确认。" : state.Stderr);

        await Task.Delay(350);
        var stableDevices = await _adb.DevicesAsync();
        if (!stableDevices.Success || !AdbConnectionEvaluator.IsEndpointOnline(stableDevices.Stdout, onlineSerial))
            return new AdbCommandResult(1, stableDevices.Stdout, $"{endpoint} 在连接后立即离线，请确认设备无线调试端口仍有效。");
        return new AdbCommandResult(0, stableDevices.Stdout, stableDevices.Stderr);
    }

    private List<WirelessEndpoint> BuildWirelessCandidates(
        DeviceModel device,
        IReadOnlyList<AdbMdnsService> services,
        bool allowUnboundMigration)
    {
        var distinctServiceIds = services
            .Where(service => service.IsConnectService)
            .Select(service => service.StableDeviceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var canMigrate = allowUnboundMigration && string.IsNullOrWhiteSpace(device.MdnsServiceId) && distinctServiceIds.Count == 1;

        var candidates = services
            .Where(service => service.IsConnectService)
            .Where(service =>
                (!string.IsNullOrWhiteSpace(device.MdnsServiceId) &&
                 service.StableDeviceId.Equals(device.MdnsServiceId, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(device.IpAddress) &&
                 service.Host.Equals(device.IpAddress, StringComparison.OrdinalIgnoreCase)) ||
                canMigrate)
            .OrderBy(service => service.ServiceType == "_adb-tls-connect._tcp" ? 0 : 1)
            .Select(service => new WirelessEndpoint(service.Host, service.Port, service))
            .ToList();

        if (!string.IsNullOrWhiteSpace(device.IpAddress) && device.Port > 0)
            candidates.Add(new WirelessEndpoint(device.IpAddress, device.Port, null));

        return candidates
            .GroupBy(candidate => $"{candidate.Host}:{candidate.Port}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static DeviceModel? FindOnlineDevice(
        DeviceModel saved,
        IReadOnlyDictionary<string, DeviceModel> onlineDevices,
        IReadOnlyList<AdbMdnsService> services)
    {
        if (onlineDevices.TryGetValue(saved.DeviceId, out var direct))
            return direct;

        if (!string.IsNullOrWhiteSpace(saved.IpAddress) && saved.Port > 0 &&
            onlineDevices.TryGetValue($"{saved.IpAddress}:{saved.Port}", out var endpoint))
            return endpoint;

        foreach (var service in services.Where(service => service.IsConnectService && MatchesSavedDevice(saved, service)))
        {
            if (onlineDevices.TryGetValue(service.Endpoint, out var resolvedEndpoint))
                return resolvedEndpoint;
            if (onlineDevices.TryGetValue(service.InstanceName, out var resolvedInstance))
                return resolvedInstance;
            if (onlineDevices.TryGetValue(service.AdbSerial, out var resolvedSerial))
                return resolvedSerial;
        }

        return null;
    }

    private static bool MatchesSavedDevice(DeviceModel device, AdbMdnsService service)
    {
        return (!string.IsNullOrWhiteSpace(device.MdnsServiceId) &&
                service.StableDeviceId.Equals(device.MdnsServiceId, StringComparison.OrdinalIgnoreCase)) ||
            EndpointMatches(service, device.IpAddress, device.Port);
    }

    private static bool EndpointMatches(AdbMdnsService service, string host, int port)
    {
        return port > 0 && service.Port == port && service.Host.Equals(host, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWirelessDevice(DeviceModel device)
    {
        return string.Equals(device.ConnectionKind, "wireless", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsConnectionSuccessful(AdbCommandResult result)
    {
        return AdbConnectionEvaluator.IsConnectCommandAccepted(result);
    }

    private static AdbCommandResult ConnectionFailure(AdbCommandResult result, string endpoint)
    {
        var detail = FailureText(result);
        return new AdbCommandResult(
            1,
            result.Stdout,
            string.IsNullOrWhiteSpace(detail) ? $"无法连接无线 ADB 设备 {endpoint}。" : detail);
    }

    private static string FailureText(AdbCommandResult result)
    {
        return string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout.Trim() : result.Stderr.Trim();
    }

    private static void ApplyDiscoveredEndpoint(DeviceModel device, AdbMdnsService? service, string? host = null, int? port = null)
    {
        var resolvedHost = host ?? service?.Host ?? device.IpAddress;
        var resolvedPort = port ?? service?.Port ?? device.Port;
        device.IpAddress = resolvedHost;
        device.Port = resolvedPort;
        device.DeviceId = $"{resolvedHost}:{resolvedPort}";
        if (service is not null)
            device.MdnsServiceId = service.StableDeviceId;
    }

    internal static void ApplyOnlineDevice(DeviceModel saved, DeviceModel online, IReadOnlyList<AdbMdnsService> services)
    {
        var previousDeviceId = saved.DeviceId;
        var usesDefaultDisplayName = string.IsNullOrWhiteSpace(saved.DisplayName) ||
            saved.DisplayName.Equals(previousDeviceId, StringComparison.OrdinalIgnoreCase);
        saved.DeviceId = online.DeviceId;
        saved.IsConnected = true;
        saved.Brand = online.Brand;
        saved.Model = online.Model;
        if (usesDefaultDisplayName)
            saved.DisplayName = online.DisplayName;

        // Only bind discovery data that identifies the online serial itself. Falling back to
        // stale saved IP/mDNS fields here can attach another phone's endpoint to this card.
        var service = services.FirstOrDefault(candidate =>
            candidate.IsConnectService &&
            (candidate.Endpoint.Equals(online.DeviceId, StringComparison.OrdinalIgnoreCase) ||
             candidate.InstanceName.Equals(online.DeviceId, StringComparison.OrdinalIgnoreCase) ||
             candidate.AdbSerial.Equals(online.DeviceId, StringComparison.OrdinalIgnoreCase)));
        if (service is not null)
            ApplyDiscoveredEndpoint(saved, service);
        else if (TryParseAdbEndpoint(online.DeviceId, out var host, out var port))
        {
            saved.IpAddress = host;
            saved.Port = port;
        }
        saved.DeviceId = online.DeviceId;
    }

    internal static bool TryParseAdbEndpoint(string deviceId, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(deviceId) ||
            !Uri.TryCreate($"tcp://{deviceId.Trim()}", UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            uri.Port is < 1 or > 65535)
        {
            return false;
        }

        host = uri.Host;
        port = uri.Port;
        return true;
    }

    private void MarkAllDisconnected()
    {
        foreach (var device in Devices)
            device.IsConnected = false;
    }

    private void Persist()
    {
        _settings.Current.Devices = Devices
            .Select(device => new SavedDeviceSettings
            {
                DeviceId = device.DeviceId,
                DisplayName = device.DisplayName,
                ConnectionKind = device.ConnectionKind,
                IpAddress = device.IpAddress,
                Port = device.Port,
                MdnsServiceId = device.MdnsServiceId,
                Note = device.Note,
            })
            .ToList();
        _settings.Save();
    }

    private static IEnumerable<DeviceModel> ParseOnlineDevices(string stdout)
    {
        return stdout
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(ParseAdbDeviceLine)
            .Where(device => device is not null)
            .Cast<DeviceModel>();
    }

    private static DeviceModel? ParseAdbDeviceLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[1] != "device")
            return null;

        var id = parts[0];
        var model = FieldValue(parts, "model") ?? "Android";
        var brand = FieldValue(parts, "product") ?? "Unknown";
        return new DeviceModel
        {
            DeviceId = id,
            DisplayName = model == "Android" ? id : model,
            ConnectionKind = "usb",
            Brand = brand,
            Model = model,
            IsConnected = true,
        };
    }

    private static string? FieldValue(IEnumerable<string> parts, string key)
    {
        var prefix = key + ":";
        return parts.FirstOrDefault(part => part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
    }

    private sealed record WirelessEndpoint(string Host, int Port, AdbMdnsService? Service);
}
