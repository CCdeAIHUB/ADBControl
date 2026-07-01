using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace ADBControl.Desktop.Views;

public sealed class WrapPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var maxWidth = double.IsInfinity(availableSize.Width) ? double.MaxValue : availableSize.Width;
        var lineWidth = 0d;
        var lineHeight = 0d;
        var totalWidth = 0d;
        var totalHeight = 0d;

        foreach (var child in Children)
        {
            child.Measure(new Size(maxWidth, availableSize.Height));
            var desired = child.DesiredSize;

            if (lineWidth > 0 && lineWidth + desired.Width > maxWidth)
            {
                totalWidth = Math.Max(totalWidth, lineWidth);
                totalHeight += lineHeight;
                lineWidth = desired.Width;
                lineHeight = desired.Height;
                continue;
            }

            lineWidth += desired.Width;
            lineHeight = Math.Max(lineHeight, desired.Height);
        }

        totalWidth = Math.Max(totalWidth, lineWidth);
        totalHeight += lineHeight;

        return new Size(double.IsInfinity(availableSize.Width) ? totalWidth : availableSize.Width, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0d;
        var y = 0d;
        var lineHeight = 0d;

        foreach (var child in Children)
        {
            var desired = child.DesiredSize;

            if (x > 0 && x + desired.Width > finalSize.Width)
            {
                x = 0;
                y += lineHeight;
                lineHeight = 0;
            }

            child.Arrange(new Rect(x, y, desired.Width, desired.Height));
            x += desired.Width;
            lineHeight = Math.Max(lineHeight, desired.Height);
        }

        return finalSize;
    }
}
