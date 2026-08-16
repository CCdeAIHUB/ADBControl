using ADBControl.Desktop.Models;

namespace ADBControl.Desktop.Services;

public interface IAiDeviceInventory
{
    IReadOnlyList<DeviceModel> GetDevices();
}
