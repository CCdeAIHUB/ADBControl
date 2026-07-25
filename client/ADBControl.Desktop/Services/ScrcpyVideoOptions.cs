namespace ADBControl.Desktop.Services;

public static class ScrcpyIntegration
{
    public const string UpstreamVersion = "4.0";
}

public readonly record struct ProjectionControlState(bool StartEnabled, bool StopEnabled);

public static class ProjectionControlStateEvaluator
{
    public static ProjectionControlState Resolve(bool projectionAvailable, bool ownsActiveSession, bool isReconfiguring)
    {
        var activeOrTransitioning = ownsActiveSession || isReconfiguring;
        return new ProjectionControlState(
            StartEnabled: projectionAvailable && !activeOrTransitioning,
            StopEnabled: activeOrTransitioning);
    }
}

public sealed record ScrcpyVideoOptions(int Width, int Height, int BitRate, int FrameRate)
{
    public int MaxSize => Math.Max(Width, Height);

    public static bool TryCreate(
        int width,
        int height,
        int bitRate,
        int frameRate,
        out ScrcpyVideoOptions? options,
        out string error)
    {
        options = null;
        if (width is < 320 or > 4096 || height is < 240 or > 4096)
        {
            error = "投屏分辨率必须在 320×240 到 4096×4096 之间。";
            return false;
        }
        if (bitRate is < 500_000 or > 50_000_000)
        {
            error = "投屏码率必须在 0.5 Mbps 到 50 Mbps 之间。";
            return false;
        }
        if (frameRate is < 10 or > 120)
        {
            error = "投屏帧率必须在 10 到 120 FPS 之间。";
            return false;
        }

        options = new ScrcpyVideoOptions(width, height, bitRate, frameRate);
        error = string.Empty;
        return true;
    }
}

public readonly record struct ScrcpyFrameSize(int Width, int Height);
