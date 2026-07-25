using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace ADBControl.Desktop.Controls;

/// <summary>
/// 迷你折线图，用于实时指标卡片中显示趋势。
/// 不含坐标轴和标签，只显示折线 + 渐变填充。
/// </summary>
public sealed class MiniSparkline : Canvas
{
    private readonly List<double> _values = new();
    private readonly Brush _lineBrush;
    private readonly LinearGradientBrush _fillBrush;

    public MiniSparkline(Brush lineBrush)
    {
        _lineBrush = lineBrush;
        Height = 32;
        var lineColor = (lineBrush as SolidColorBrush)?.Color ?? Windows.UI.Color.FromArgb(255, 34, 197, 94);
        _fillBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop { Offset = 0, Color = Windows.UI.Color.FromArgb(60, lineColor.R, lineColor.G, lineColor.B) },
                new GradientStop { Offset = 1, Color = Windows.UI.Color.FromArgb(0, lineColor.R, lineColor.G, lineColor.B) },
            },
        };
        SizeChanged += (_, _) => Redraw();
    }

    public void RefreshAccent()
    {
        var color = (_lineBrush as SolidColorBrush)?.Color ?? Windows.UI.Color.FromArgb(255, 34, 197, 94);
        _fillBrush.GradientStops[0].Color = Windows.UI.Color.FromArgb(60, color.R, color.G, color.B);
        _fillBrush.GradientStops[1].Color = Windows.UI.Color.FromArgb(0, color.R, color.G, color.B);
        Redraw();
    }

    public void AddValue(double? value)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
            return;
        _values.Add(value.Value);
        if (_values.Count > 60)
            _values.RemoveAt(0);
        Redraw();
    }

    public void Clear()
    {
        _values.Clear();
        Redraw();
    }

    private void Redraw()
    {
        var width = Math.Max(1, ActualWidth);
        var height = Math.Max(1, ActualHeight);
        Children.Clear();

        if (_values.Count < 2)
            return;

        var min = _values.Min();
        var max = _values.Max();
        if (max - min < 0.001)
            max = min + 1;

        var points = new List<Point>();
        for (var i = 0; i < _values.Count; i++)
        {
            var x = width * i / (_values.Count - 1d);
            var y = height - ((_values[i] - min) / (max - min) * (height - 4)) - 2;
            points.Add(new Point(x, y));
        }

        // 渐变填充
        var fillPath = new Microsoft.UI.Xaml.Shapes.Path { Fill = _fillBrush };
        var geometry = new PathGeometry();
        var figure = new PathFigure { StartPoint = points[0], IsClosed = true };
        for (var i = 1; i < points.Count; i++)
            figure.Segments.Add(new LineSegment { Point = points[i] });
        figure.Segments.Add(new LineSegment { Point = new Point(points[^1].X, height) });
        figure.Segments.Add(new LineSegment { Point = new Point(points[0].X, height) });
        geometry.Figures.Add(figure);
        fillPath.Data = geometry;
        Children.Add(fillPath);

        // 折线
        var line = new Polyline
        {
            Stroke = _lineBrush,
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
        };
        foreach (var p in points)
            line.Points.Add(p);
        Children.Add(line);
    }
}
