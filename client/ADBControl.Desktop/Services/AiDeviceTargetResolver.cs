using ADBControl.Desktop.Models;

namespace ADBControl.Desktop.Services;

public sealed record AiDeviceTargetResolution(DeviceModel? Device, string ErrorCode, string Message)
{
    public bool Success => Device is not null;
}

public static class AiDeviceTargetResolver
{
    public static AiDeviceTargetResolution Resolve(
        IAiDeviceInventory? inventory,
        DeviceModel? currentDevice,
        string? requestedDeviceId)
    {
        if (string.IsNullOrWhiteSpace(requestedDeviceId))
        {
            return currentDevice is not null && !string.IsNullOrWhiteSpace(currentDevice.DeviceId)
                ? new AiDeviceTargetResolution(currentDevice, string.Empty, string.Empty)
                : new AiDeviceTargetResolution(null, "DEVICE_SELECTION_REQUIRED", "该操作需要明确目标设备，请先查询设备清单并让用户选择。");
        }

        var requested = requestedDeviceId.Trim();
        var devices = inventory?.GetDevices() ?? Array.Empty<DeviceModel>();
        var byId = devices.FirstOrDefault(device => string.Equals(device.DeviceId, requested, StringComparison.OrdinalIgnoreCase));
        if (byId is not null)
            return new AiDeviceTargetResolution(byId, string.Empty, string.Empty);

        if (currentDevice is not null && string.Equals(currentDevice.DeviceId, requested, StringComparison.OrdinalIgnoreCase))
            return new AiDeviceTargetResolution(currentDevice, string.Empty, string.Empty);

        var byLabel = devices
            .Where(device =>
                string.Equals(device.DisplayName, requested, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(device.Model, requested, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return byLabel.Length switch
        {
            1 => new AiDeviceTargetResolution(byLabel[0], string.Empty, string.Empty),
            > 1 => new AiDeviceTargetResolution(null, "DEVICE_SELECTION_AMBIGUOUS", $"设备名称“{requested}”对应多个设备，请改用 deviceId。"),
            _ => new AiDeviceTargetResolution(null, "DEVICE_NOT_FOUND", $"设备清单中不存在“{requested}”。"),
        };
    }
}
