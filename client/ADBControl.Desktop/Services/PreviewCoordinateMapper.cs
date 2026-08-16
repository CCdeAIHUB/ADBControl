namespace ADBControl.Desktop.Services;

public readonly record struct PreviewCoordinateSpace(int Width, int Height)
{
    public bool IsValid => Width > 0 && Height > 0;
}

public readonly record struct ProjectionTouchPosition(int X, int Y, PreviewCoordinateSpace Space);

public static class PreviewCoordinateMapper
{
    public static PreviewCoordinateSpace ResolveActiveSpace(
        PreviewCoordinateSpace screenshotSpace,
        PreviewCoordinateSpace projectionSpace,
        bool projectionActive)
    {
        // A live control channel must never inherit coordinates from an older screenshot.
        if (projectionActive)
            return projectionSpace.IsValid ? projectionSpace : default;
        return screenshotSpace.IsValid ? screenshotSpace : default;
    }

    public static bool TryMapUniform(
        double viewportWidth,
        double viewportHeight,
        PreviewCoordinateSpace contentSpace,
        double pointX,
        double pointY,
        out ProjectionTouchPosition position)
    {
        position = default;
        if (!double.IsFinite(viewportWidth) || !double.IsFinite(viewportHeight) ||
            !double.IsFinite(pointX) || !double.IsFinite(pointY) ||
            viewportWidth <= 0 || viewportHeight <= 0 || !contentSpace.IsValid)
            return false;

        var fitted = PreviewContentLayout.FitUniform(
            viewportWidth,
            viewportHeight,
            contentSpace.Width,
            contentSpace.Height);
        var offsetX = (viewportWidth - fitted.Width) / 2d;
        var offsetY = (viewportHeight - fitted.Height) / 2d;
        var contentX = pointX - offsetX;
        var contentY = pointY - offsetY;
        if (contentX < 0 || contentY < 0 || contentX >= fitted.Width || contentY >= fitted.Height)
            return false;

        var x = Math.Min(contentSpace.Width - 1, (int)Math.Floor(contentX * contentSpace.Width / fitted.Width));
        var y = Math.Min(contentSpace.Height - 1, (int)Math.Floor(contentY * contentSpace.Height / fitted.Height));
        position = new ProjectionTouchPosition(x, y, contentSpace);
        return true;
    }

    public static ProjectionTouchPosition ScaleToSpace(
        ProjectionTouchPosition position,
        PreviewCoordinateSpace targetSpace)
    {
        ValidatePosition(position);
        if (!targetSpace.IsValid)
            throw new ArgumentOutOfRangeException(nameof(targetSpace));

        return new ProjectionTouchPosition(
            ScaleAxis(position.X, position.Space.Width, targetSpace.Width),
            ScaleAxis(position.Y, position.Space.Height, targetSpace.Height),
            targetSpace);
    }

    public static ProjectionTouchPosition ResolveGesturePosition(
        ProjectionTouchPosition lastValidPosition,
        ProjectionTouchPosition? candidatePosition)
    {
        ValidatePosition(lastValidPosition);
        if (candidatePosition is not { } candidate || candidate.Space != lastValidPosition.Space)
            return lastValidPosition;
        ValidatePosition(candidate);
        return candidate;
    }

    private static int ScaleAxis(int value, int sourceExtent, int targetExtent)
    {
        if (sourceExtent == 1 || targetExtent == 1)
            return 0;
        return Math.Clamp(
            (int)Math.Floor(value * (targetExtent - 1d) / (sourceExtent - 1d)),
            0,
            targetExtent - 1);
    }

    private static void ValidatePosition(ProjectionTouchPosition position)
    {
        if (!position.Space.IsValid)
            throw new ArgumentOutOfRangeException(nameof(position));
        if (position.X < 0 || position.X >= position.Space.Width)
            throw new ArgumentOutOfRangeException(nameof(position), "触控 X 坐标超出来源坐标空间。");
        if (position.Y < 0 || position.Y >= position.Space.Height)
            throw new ArgumentOutOfRangeException(nameof(position), "触控 Y 坐标超出来源坐标空间。");
    }
}