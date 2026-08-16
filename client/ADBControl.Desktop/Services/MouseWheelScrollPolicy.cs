namespace ADBControl.Desktop.Services;

public static class MouseWheelScrollPolicy
{
    public const double DefaultStep = 64;
    public const double OffsetChangeTolerance = 0.01;
    public const uint MouseWheelMessage = 0x020A;
    public static TimeSpan LowLevelFallbackDelay { get; } = TimeSpan.FromMilliseconds(60);

    public static int DecodeDelta(IntPtr wParam)
        => unchecked((short)(((long)wParam >> 16) & 0xffff));

    public static bool TryCalculateTarget(
        double verticalOffset,
        double scrollableHeight,
        int delta,
        out double target,
        double step = DefaultStep)
    {
        var distance = step * delta / 120d;
        target = Math.Clamp(verticalOffset - distance, 0, Math.Max(0, scrollableHeight));
        return Math.Abs(target - verticalOffset) > OffsetChangeTolerance;
    }

    public static bool ShouldScheduleDeferredFallback(
        bool isOutermostNativeDispatch,
        double? offsetBeforeDefaultHandling,
        double? offsetAfterDefaultHandling)
    {
        if (!isOutermostNativeDispatch)
            return false;

        if (!offsetBeforeDefaultHandling.HasValue || !offsetAfterDefaultHandling.HasValue)
            return true;

        return Math.Abs(offsetAfterDefaultHandling.Value - offsetBeforeDefaultHandling.Value)
            <= OffsetChangeTolerance;
    }

    public static bool ShouldInstallNativeHook(
        nint windowHandle,
        nint currentWindowProc,
        nint desiredWindowProc)
        => windowHandle != nint.Zero &&
           currentWindowProc != nint.Zero &&
           desiredWindowProc != nint.Zero &&
           currentWindowProc != desiredWindowProc;

    public static bool ShouldObserveLowLevelWheel(
        int hookCode,
        uint message,
        int delta,
        uint targetProcessId,
        uint currentProcessId)
        => hookCode >= 0 &&
           message == MouseWheelMessage &&
           delta != 0 &&
           targetProcessId != 0 &&
           targetProcessId == currentProcessId;
}