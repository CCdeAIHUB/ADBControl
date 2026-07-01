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
                IsConnected = true,
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

    private static string? FieldValue(IEnumerable<string> parts, string key)
    {
        var prefix = key + ":";
        return parts.FirstOrDefault(part => part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
    }
}
