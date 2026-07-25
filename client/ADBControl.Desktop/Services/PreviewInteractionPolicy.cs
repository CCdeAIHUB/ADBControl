namespace ADBControl.Desktop.Services;

public static class PreviewInteractionPolicy
{
    private const int MinimumSwipeDurationMs = 80;
    private const int MaximumSwipeDurationMs = 250;

    public static int CalculateSwipeDurationMs(int pointerDurationMs)
        => Math.Clamp(pointerDurationMs, MinimumSwipeDurationMs, MaximumSwipeDurationMs);

    public static bool RequiresConnectivityProbe(bool isConnected) => !isConnected;

    public static bool IsLatestPendingGesture(long gestureVersion, long latestGestureVersion)
        => gestureVersion == latestGestureVersion;
}
