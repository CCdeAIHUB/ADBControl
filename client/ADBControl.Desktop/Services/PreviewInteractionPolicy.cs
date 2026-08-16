namespace ADBControl.Desktop.Services;

public static class PreviewInteractionPolicy
{
    private const int MinimumSwipeDurationMs = 80;
    private const int MaximumSwipeDurationMs = 250;
    private const double TapMovementToleranceDips = 6;

    public static int CalculateSwipeDurationMs(int pointerDurationMs)
        => Math.Clamp(pointerDurationMs, MinimumSwipeDurationMs, MaximumSwipeDurationMs);

    public static bool RequiresConnectivityProbe(bool isConnected) => !isConnected;

    public static bool IsLatestPendingGesture(long gestureVersion, long latestGestureVersion)
        => gestureVersion == latestGestureVersion;

    public static bool IsTapGesture(double startX, double startY, double endX, double endY)
    {
        var deltaX = endX - startX;
        var deltaY = endY - startY;
        return deltaX * deltaX + deltaY * deltaY <= TapMovementToleranceDips * TapMovementToleranceDips;
    }
}
