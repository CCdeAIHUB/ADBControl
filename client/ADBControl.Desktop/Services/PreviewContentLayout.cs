namespace ADBControl.Desktop.Services;

public readonly record struct PreviewContentSize(double Width, double Height);

public static class PreviewContentLayout
{
    public static PreviewContentSize FitUniform(
        double availableWidth,
        double availableHeight,
        double contentWidth,
        double contentHeight)
    {
        if (availableWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(availableWidth));
        if (availableHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(availableHeight));
        if (contentWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(contentWidth));
        if (contentHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(contentHeight));

        var scale = Math.Min(availableWidth / contentWidth, availableHeight / contentHeight);
        return new PreviewContentSize(contentWidth * scale, contentHeight * scale);
    }
}
