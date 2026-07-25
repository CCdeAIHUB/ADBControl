using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace ADBControl.Desktop.Views;

public sealed class WrapPanel : Panel
{
    /// <summary>
    /// 子元素之间的水平间距。
    /// </summary>
    public static readonly DependencyProperty HorizontalSpacingProperty =
        DependencyProperty.Register(nameof(HorizontalSpacing), typeof(double), typeof(WrapPanel), new PropertyMetadata(0d, OnSpacingChanged));

    /// <summary>
    /// 行之间的垂直间距。
    /// </summary>
    public static readonly DependencyProperty VerticalSpacingProperty =
        DependencyProperty.Register(nameof(VerticalSpacing), typeof(double), typeof(WrapPanel), new PropertyMetadata(0d, OnSpacingChanged));

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    private static void OnSpacingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((WrapPanel)d).InvalidateMeasure();

    protected override Size MeasureOverride(Size availableSize)
    {
        var maxWidth = double.IsInfinity(availableSize.Width) ? double.MaxValue : availableSize.Width;
        var hSpacing = HorizontalSpacing;
        var vSpacing = VerticalSpacing;
        var lineWidth = 0d;
        var lineHeight = 0d;
        var totalWidth = 0d;
        var totalHeight = 0d;

        foreach (var child in Children)
        {
            child.Measure(new Size(maxWidth, availableSize.Height));
            var desired = child.DesiredSize;

            // 行内首个元素不加水平间距；后续元素加上间距再判断是否换行。
            var candidateWidth = lineWidth > 0 ? lineWidth + hSpacing + desired.Width : desired.Width;
            if (lineWidth > 0 && candidateWidth > maxWidth)
            {
                totalWidth = Math.Max(totalWidth, lineWidth);
                totalHeight += lineHeight + vSpacing;
                lineWidth = desired.Width;
                lineHeight = desired.Height;
                continue;
            }

            lineWidth = candidateWidth;
            lineHeight = Math.Max(lineHeight, desired.Height);
        }

        totalWidth = Math.Max(totalWidth, lineWidth);
        totalHeight += lineHeight;

        return new Size(double.IsInfinity(availableSize.Width) ? totalWidth : availableSize.Width, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var hSpacing = HorizontalSpacing;
        var vSpacing = VerticalSpacing;
        var x = 0d;
        var y = 0d;
        var lineHeight = 0d;

        foreach (var child in Children)
        {
            var desired = child.DesiredSize;

            // 换行时加上水平间距判断
            var candidateX = x > 0 ? x + hSpacing + desired.Width : desired.Width;
            if (x > 0 && candidateX > finalSize.Width)
            {
                x = 0;
                y += lineHeight + vSpacing;
                lineHeight = 0;
                child.Arrange(new Rect(x, y, desired.Width, desired.Height));
                x = desired.Width;
            }
            else
            {
                var arrangeX = x > 0 ? x + hSpacing : x;
                child.Arrange(new Rect(arrangeX, y, desired.Width, desired.Height));
                x = arrangeX + desired.Width;
            }

            lineHeight = Math.Max(lineHeight, desired.Height);
        }

        return finalSize;
    }
}
