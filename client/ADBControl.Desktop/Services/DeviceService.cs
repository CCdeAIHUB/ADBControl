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
                IpAddress = d.IpAddress,
                Port = d.Port,
                Note = d.Note,
            })
            .ToList();
        _settings.Save();
    }
}
