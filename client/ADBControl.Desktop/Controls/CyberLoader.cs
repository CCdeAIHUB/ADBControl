using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ADBControl.Desktop.Controls;

// Exact WinUI redraw of the referenced Uiverse loader: a glowing aqua square
// that flips around X, then Y, then Z over one two-second cycle.
public sealed class CyberLoader : Grid
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _clock = new();
    private readonly PlaneProjection _projection = new();

    public CyberLoader(string message, Brush accent, Brush foreground, Brush surface, Brush border)
    {
        Width = 176;
        Height = 92;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 使用传入的主题色 accent 替代硬编码青色，让 loading 随主题色变化。
        var accentSolid = accent as SolidColorBrush ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 34, 197, 94));
        var accentColor = accentSolid.Color;
        var stage = new Grid
        {
            Width = 50,
            Height = 50,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Projection = _projection,
        };
        // 修复阴影断层：将多层 GlowSquare 合并为统一大小、仅透明度递增的层，
        // 避免不同尺寸 Border 在旋转时因边缘不对齐产生视觉断层。
        stage.Children.Add(GlowSquare(44, 2, 0.06, accentSolid));
        stage.Children.Add(GlowSquare(40, 2, 0.14, accentSolid));
        stage.Children.Add(new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(5),
            BorderBrush = accentSolid,
            BorderThickness = new Thickness(2),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(18, accentColor.R, accentColor.G, accentColor.B)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Children.Add(stage);

        var text = new TextBlock
        {
            Text = message,
            FontSize = 12,
            Foreground = foreground,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 5, 0, 0),
        };
        Grid.SetRow(text, 1);
        Children.Add(text);

        _timer.Tick += (_, _) => AdvanceFrame();
        Loaded += (_, _) =>
        {
            _clock.Restart();
            _timer.Start();
        };
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            _clock.Stop();
        };
    }

    private static Border GlowSquare(double size, double thickness, double opacity, Brush brush)
    {
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(7),
            BorderBrush = brush,
            BorderThickness = new Thickness(thickness),
            Opacity = opacity,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private void AdvanceFrame()
    {
        var progress = (_clock.Elapsed.TotalSeconds % 2d) / 2d;
        if (progress < 0.33d)
        {
            _projection.RotationX = 180d * EaseInOut(progress / 0.33d);
            _projection.RotationY = 0;
            _projection.RotationZ = 0;
            return;
        }
        if (progress < 0.67d)
        {
            _projection.RotationX = 180;
            _projection.RotationY = 180d * EaseInOut((progress - 0.33d) / 0.34d);
            _projection.RotationZ = 0;
            return;
        }

        _projection.RotationX = 180;
        _projection.RotationY = 180;
        _projection.RotationZ = 180d * EaseInOut((progress - 0.67d) / 0.33d);
    }

    private static double EaseInOut(double value)
    {
        var clamped = Math.Clamp(value, 0d, 1d);
        return clamped * clamped * (3d - 2d * clamped);
    }
}
