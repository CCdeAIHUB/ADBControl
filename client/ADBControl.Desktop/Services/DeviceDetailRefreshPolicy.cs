namespace ADBControl.Desktop.Services;

public static class DeviceDetailRefreshPolicy
{
    public static bool ShouldRebuild(bool videoActive, bool adbStateChanged, bool companionStateChanged)
        => adbStateChanged || !videoActive && companionStateChanged;
}
