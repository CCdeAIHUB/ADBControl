using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace ADBControl.Desktop.Controls;

/// <summary>
/// 性能图表控件，支持折线图 + 渐变填充区域 + 当前值高亮点。
/// 参考 PerfDog 风格：标题在左，当前值/平均/峰值在右，图表区域带网格线和渐变填充。
/// </summary>
public sealed class PerformanceChart : Grid
{
    private readonly Canvas _canvas;
    private readonly TextBlock _titleText;
    private readonly TextBlock _summary;
    private readonly TextBlock _currentValue;
    private readonly TextBlock _empty;
    private readonly Brush _lineBrush;
    private readonly LinearGradientBrush _fillBrush;
    private readonly Brush _gridBrush;
    private readonly Brush _textBrush;
    private readonly string _unit;
    private readonly List<double> _values = new();
    private readonly List<DateTimeOffset> _times = new();

    public PerformanceChart(string title, string unit, Brush lineBrush, Brush gridBrush, Brush textBrush, Brush surfaceBrush)
    {
        _unit = unit;
        _lineBrush = lineBrush;
        _gridBrush = gridBrush;
        _textBrush = textBrush;

        // 构建渐变填充画刷：从线条颜色到透明
        var lineColor = (lineBrush as SolidColorBrush)?.Color ?? Windows.UI.Color.FromArgb(255, 34, 197, 94);
        _fillBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop { Offset = 0, Color = Windows.UI.Color.FromArgb(80, lineColor.R, lineColor.G, lineColor.B) },
                new GradientStop { Offset = 1, Color = Windows.UI.Color.FromArgb(0, lineColor.R, lineColor.G, lineColor.B) },
            },
        };

        MinWidth = 280;
        Height = 160;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());

        // 顶部信息栏：标题 | 当前值 | 摘要
        var headerBackground = new Border
        {
            CornerRadius = new CornerRadius(10, 10, 0, 0),
            Background = surfaceBrush,
            Padding = new Thickness(14, 8, 14, 8),
        };
        var headerGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        _titleText = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = lineBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        headerGrid.Children.Add(_titleText);

        _currentValue = new TextBlock
        {
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = lineBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
        };
        Grid.SetColumn(_currentValue, 1);
        headerGrid.Children.Add(_currentValue);

        _summary = new TextBlock
        {
            FontSize = 11,
            Foreground = textBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            TextAlignment = TextAlignment.Right,
        };
        Grid.SetColumn(_summary, 2);
        headerGrid.Children.Add(_summary);

        headerBackground.Child = headerGrid;
        Children.Add(headerBackground);

        // 图表区域
        _canvas = new Canvas
        {
            Margin = new Thickness(0),
            Background = surfaceBrush,
        };
        _empty = new TextBlock
        {
            Text = "等待采样",
            FontSize = 12,
            Foreground = textBrush,
            Opacity = 0.5,
        };
        _canvas.Children.Add(_empty);
        Grid.SetRow(_canvas, 1);
        Children.Add(_canvas);
        SizeChanged += (_, _) => Redraw();
    }

    public void RefreshAccent()
    {
        var color = (_lineBrush as SolidColorBrush)?.Color ?? Windows.UI.Color.FromArgb(255, 34, 197, 94);
        _fillBrush.GradientStops[0].Color = Windows.UI.Color.FromArgb(80, color.R, color.G, color.B);
        _fillBrush.GradientStops[1].Color = Windows.UI.Color.FromArgb(0, color.R, color.G, color.B);
        Redraw();
    }

    public void AddValue(double? value, DateTimeOffset? capturedAt = null)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
            return;
        _values.Add(value.Value);
        _times.Add(capturedAt ?? DateTimeOffset.Now);
        Redraw();
    }

    public void SetSamples(IReadOnlyList<double> values, IReadOnlyList<DateTimeOffset> times)
    {
        _values.Clear();
        _times.Clear();
        var count = Math.Min(values.Count, times.Count);
        for (var index = 0; index < count; index++)
        {
            if (double.IsNaN(values[index]) || double.IsInfinity(values[index]))
                continue;
            _values.Add(values[index]);
            _times.Add(times[index]);
        }
        Redraw();
    }

    public void Clear()
    {
        _values.Clear();
        _times.Clear();
        Redraw();
    }

    private void Redraw()
    {
        var width = Math.Max(1, _canvas.ActualWidth);
        var height = Math.Max(1, _canvas.ActualHeight);
        const double timeAxisHeight = 20;
        var plotHeight = Math.Max(1, height - timeAxisHeight);
        _canvas.Children.Clear();

        // 绘制水平网格线
        for (var index = 0; index <= 4; index++)
        {
            var y = plotHeight * index / 4d;
            _canvas.Children.Add(new Line
            {
                X1 = 0,
                X2 = width,
                Y1 = y,
                Y2 = y,
                Stroke = _gridBrush,
                StrokeThickness = 1,
                Opacity = 0.25,
            });
        }

        // 绘制垂直网格线（每 30 像素一条，模拟时间刻度）
        for (var x = 0d; x < width; x += 30)
        {
            _canvas.Children.Add(new Line
            {
                X1 = x,
                X2 = x,
                Y1 = 0,
                Y2 = plotHeight,
                Stroke = _gridBrush,
                StrokeThickness = 1,
                Opacity = 0.12,
            });
        }

        if (_values.Count == 0)
        {
            Canvas.SetLeft(_empty, 12);
            Canvas.SetTop(_empty, Math.Max(0, plotHeight / 2 - 10));
            _canvas.Children.Add(_empty);
            _currentValue.Text = "--";
            _summary.Text = string.Empty;
            return;
        }

        var min = Math.Min(0, _values.Min());
        var max = _values.Max();
        if (max - min < 0.001)
            max = min + 1;

        // 计算 Y 坐标时留出上下边距，避免线条贴边
        const double verticalPadding = 6;
        var usableHeight = Math.Max(1, plotHeight - verticalPadding * 2);

        double GetY(double value)
            => plotHeight - verticalPadding - ((value - min) / (max - min) * usableHeight);

        var points = new List<Point>();
        var step = Math.Max(1, (int)Math.Ceiling(_values.Count / Math.Max(1, width)));
        for (var index = 0; index < _values.Count; index += step)
        {
            var x = _values.Count == 1 ? width : width * index / (_values.Count - 1d);
            var y = GetY(_values[index]);
            points.Add(new Point(x, y));
        }
        if (points.Count > 0 && step > 1 && points[^1].X < width)
            points.Add(new Point(width, GetY(_values[^1])));

        // 绘制渐变填充区域
        if (points.Count >= 2)
        {
            var fillPath = new Microsoft.UI.Xaml.Shapes.Path
            {
                Fill = _fillBrush,
            };
            var geometry = new PathGeometry();
            var figure = new PathFigure { StartPoint = points[0], IsClosed = true };
            // 折线部分
            for (var i = 1; i < points.Count; i++)
            {
                figure.Segments.Add(new LineSegment { Point = points[i] });
            }
            // 闭合到底部
            figure.Segments.Add(new LineSegment { Point = new Point(points[^1].X, plotHeight) });
            figure.Segments.Add(new LineSegment { Point = new Point(points[0].X, plotHeight) });
            geometry.Figures.Add(figure);
            fillPath.Data = geometry;
            _canvas.Children.Add(fillPath);
        }

        // 绘制折线
        var line = new Polyline
        {
            Stroke = _lineBrush,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
        };
        foreach (var point in points)
            line.Points.Add(point);
        _canvas.Children.Add(line);

        // 绘制当前值高亮点（最后一个点）
        if (points.Count > 0)
        {
            var lastPoint = points[^1];
            // 外圈光晕
            _canvas.Children.Add(new Ellipse
            {
                Width = 12,
                Height = 12,
                Fill = _lineBrush,
                Opacity = 0.25,
            });
            Canvas.SetLeft(_canvas.Children[^1], lastPoint.X - 6);
            Canvas.SetTop(_canvas.Children[^1], lastPoint.Y - 6);
            // 内圈实心点
            _canvas.Children.Add(new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = _lineBrush,
            });
            Canvas.SetLeft(_canvas.Children[^1], lastPoint.X - 4);
            Canvas.SetTop(_canvas.Children[^1], lastPoint.Y - 4);
            // 白色中心点
            _canvas.Children.Add(new Ellipse
            {
                Width = 3,
                Height = 3,
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
            });
            Canvas.SetLeft(_canvas.Children[^1], lastPoint.X - 1.5);
            Canvas.SetTop(_canvas.Children[^1], lastPoint.Y - 1.5);
        }

        var current = _values[^1];
        _currentValue.Text = $"{current:0.##}{_unit}";
        _summary.Text = $"平均 {_values.Average():0.##}{_unit}\n峰值 {_values.Max():0.##}{_unit}";

        if (_times.Count == _values.Count && _times.Count > 0)
        {
            AddTimeLabel(_times[0], 4, HorizontalAlignment.Left, width);
            if (_times.Count > 2)
                AddTimeLabel(_times[_times.Count / 2], 0, HorizontalAlignment.Center, width);
            AddTimeLabel(_times[^1], 4, HorizontalAlignment.Right, width);
        }
    }

    private void AddTimeLabel(DateTimeOffset time, double margin, HorizontalAlignment alignment, double width)
    {
        var label = new TextBlock
        {
            Text = time.ToString("HH:mm:ss"),
            FontSize = 9,
            Foreground = _textBrush,
            Opacity = 0.72,
            Width = Math.Max(1, width - margin * 2),
            TextAlignment = alignment switch
            {
                HorizontalAlignment.Center => TextAlignment.Center,
                HorizontalAlignment.Right => TextAlignment.Right,
                _ => TextAlignment.Left,
            },
        };
        _canvas.Children.Add(label);
        Canvas.SetLeft(label, margin);
        Canvas.SetTop(label, Math.Max(0, _canvas.ActualHeight - 17));
    }
}
