using ADBControl.Desktop.Models;

namespace ADBControl.Desktop.Services;

public readonly record struct PreviewLockPresentation(bool ShowLockOverlay, bool ShowScreenshot);

public enum DevicePreviewOverlay
{
    None,
    Locked,
    LockStateUnknown,
    ConnectionRequired,
}

public readonly record struct DevicePreviewPresentation(
    DevicePreviewOverlay Overlay,
    bool ShowScreenshot,
    bool ShowUnlockActions,
    string Title,
    string Hint);

public static class DeviceDetailRefreshPolicy
{
    public static bool ShouldRebuild(
        bool videoActive,
        bool adbStateChanged,
        bool companionStateChanged,
        bool detailOperationActive = false)
        => !detailOperationActive && (adbStateChanged || !videoActive && companionStateChanged);

    public static bool CanUseAdbTool(bool requiresAdb, bool adbConnected)
        => !requiresAdb || adbConnected;

    public static bool ShouldShowDrawerClose(bool fullScreen, bool panelVisible)
        => fullScreen && panelVisible;

    public static bool ShouldExitFullScreen(bool fullScreen, bool enteringDeviceDetail)
        => fullScreen && !enteringDeviceDetail;

    public static PreviewLockPresentation ResolvePreviewLockPresentation(DeviceLockState lockState, bool projectionActive)
    {
        var presentation = ResolvePreviewPresentation(
            adbConnected: true,
            companionConnected: false,
            lockState,
            projectionActive);
        return new PreviewLockPresentation(
            ShowLockOverlay: presentation.Overlay != DevicePreviewOverlay.None,
            ShowScreenshot: presentation.ShowScreenshot);
    }

    public static DevicePreviewPresentation ResolvePreviewPresentation(
        bool adbConnected,
        bool companionConnected,
        DeviceLockState lockState,
        bool projectionActive)
    {
        if (!adbConnected && !projectionActive)
        {
            var hint = companionConnected
                ? "ADB 未连接。可重新开启无线调试，或从控制板启动伴侣投屏。"
                : "请在设备开发者选项中开启无线调试，然后返回设备页重新连接。";
            return new DevicePreviewPresentation(
                DevicePreviewOverlay.ConnectionRequired,
                ShowScreenshot: false,
                ShowUnlockActions: false,
                "设备未连接",
                hint);
        }

        if (lockState == DeviceLockState.Locked)
        {
            return new DevicePreviewPresentation(
                DevicePreviewOverlay.Locked,
                ShowScreenshot: false,
                ShowUnlockActions: true,
                "设备处于锁屏状态",
                "请先解锁设备，再继续预览和控制。");
        }

        if (lockState == DeviceLockState.Unknown)
        {
            return new DevicePreviewPresentation(
                DevicePreviewOverlay.LockStateUnknown,
                ShowScreenshot: false,
                ShowUnlockActions: false,
                "无法确认设备锁屏状态",
                "已暂停截图以避免显示锁屏内容，请检查设备连接后重试。");
        }

        return new DevicePreviewPresentation(
            DevicePreviewOverlay.None,
            ShowScreenshot: adbConnected && !projectionActive,
            ShowUnlockActions: false,
            string.Empty,
            string.Empty);
    }
}
