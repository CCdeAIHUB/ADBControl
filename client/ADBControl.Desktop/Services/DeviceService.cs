using System.Collections.ObjectModel;
using ADBControl.Desktop.Models;

namespace ADBControl.Desktop.Services;

public sealed class DeviceService
{
    private readonly SettingsService _settings;
    private readonly AdbService _adb;

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
        if (!result.Success && !result.Stdout.Contains("connected", StringComparison.OrdinalIgnoreCase))
            return result;

        var id = $"{ip}:{port}";
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

        Persist();
        return result;
    }

    public async Task<(AdbCommandResult Result, IReadOnlyList<DeviceModel> Devices)> ScanUsbDevicesAsync()
    {
        var result = await _adb.DevicesAsync();
        if (!result.Success)
            return (result, Array.Empty<DeviceModel>());

        var devices = result.Stdout
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(ParseAdbDeviceLine)
            .Where(device => device is not null)
            .Cast<DeviceModel>()
            .ToList();

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
        var result = await _adb.DevicesAsync();
        if (!result.Success)
        {
            foreach (var device in Devices)
                device.IsConnected = false;
            return result;
        }

        var onlineDevices = ParseOnlineDevices(result.Stdout).ToDictionary(device => device.DeviceId, StringComparer.OrdinalIgnoreCase);
        var missingWireless = Devices
            .Where(device => device.ConnectionKind == "wireless"
                && !onlineDevices.ContainsKey(device.DeviceId)
                && !string.IsNullOrWhiteSpace(device.IpAddress)
                && device.Port > 0)
            .ToList();

        foreach (var device in missingWireless)
            await _adb.ConnectAsync(device.IpAddress, device.Port);

        if (missingWireless.Count > 0)
        {
            result = await _adb.DevicesAsync();
            if (!result.Success)
            {
                foreach (var device in Devices)
                    device.IsConnected = false;
                return result;
            }

            onlineDevices = ParseOnlineDevices(result.Stdout).ToDictionary(device => device.DeviceId, StringComparer.OrdinalIgnoreCase);
        }

        var changedIdentity = false;
        var unmatchedOnlineDevices = onlineDevices.Values
            .Where(online => Devices.All(saved => !saved.DeviceId.Equals(online.DeviceId, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var unmatchedWirelessDevices = Devices
            .Where(device => device.ConnectionKind == "wireless" && !onlineDevices.ContainsKey(device.DeviceId))
            .ToList();

        // Wireless debugging can expose an mDNS adb serial instead of the saved ip:port.
        // When there is exactly one saved wireless device and one unmatched online serial, bind them.
        if (unmatchedWirelessDevices.Count == 1 && unmatchedOnlineDevices.Count == 1)
        {
            var saved = unmatchedWirelessDevices[0];
            var online = unmatchedOnlineDevices[0];
            saved.DeviceId = online.DeviceId;
            saved.DisplayName = string.IsNullOrWhiteSpace(online.DisplayName) ? saved.DisplayName : online.DisplayName;
            saved.Brand = online.Brand;
            saved.Model = online.Model;
            changedIdentity = true;
            onlineDevices[saved.DeviceId] = online;
        }

        foreach (var device in Devices)
        {
            if (onlineDevices.TryGetValue(device.DeviceId, out var online))
            {
                device.IsConnected = true;
                device.Brand = online.Brand;
                device.Model = online.Model;
                if (string.IsNullOrWhiteSpace(device.DisplayName) || device.DisplayName == device.DeviceId)
                    device.DisplayName = online.DisplayName;
            }
            else
            {
                device.IsConnected = false;
            }
        }

        if (changedIdentity)
            Persist();

        return result;
    }

    public async Task<bool> EnsureDeviceOnlineAsync(DeviceModel device)
    {
        await RefreshConnectivityAsync();
        var current = Devices.FirstOrDefault(saved => saved.DeviceId.Equals(device.DeviceId, StringComparison.OrdinalIgnoreCase));
        return current?.IsConnected == true;
    }

    private void Persist()
    {
        _settings.Current.Devices = Devices
            .Select(d => new SavedDeviceSettings
            {
                DeviceId = d.DeviceId,
                DisplayName = d.DisplayName,
                ConnectionKind = d.ConnectionKind,
                IpAddress = d.IpAddress,
                Port = d.Port,
                Note = d.Note,
            })
            .ToList();
        _settings.Save();
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

    private static IEnumerable<DeviceModel> ParseOnlineDevices(string stdout)
    {
        return stdout
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(ParseAdbDeviceLine)
            .Where(device => device is not null)
            .Cast<DeviceModel>();
    }

    private static string? FieldValue(IEnumerable<string> parts, string key)
    {
        var prefix = key + ":";
        return parts.FirstOrDefault(part => part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
    }
}
