using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace ADBControl.Desktop.Controls;

public sealed class HardwareTimeRangeSelector : Canvas
{
    private const double HandleWidth = 7;
    private readonly Brush _accent;
    private readonly Brush _track;
    private readonly List<double> _values = new();
    private readonly List<DateTimeOffset> _times = new();
    private double _startRatio;
    private double _endRatio = 1;
    private bool _draggingStart;
    private bool _draggingEnd;

    public HardwareTimeRangeSelector(Brush accent, Brush track)
    {
        _accent = accent;
        _track = track;
        Height = 64;
        MinWidth = 220;
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(1, 0, 0, 0));
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        SizeChanged += (_, _) => Redraw();
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCanceled += OnPointerReleased;
    }

    public event EventHandler<HardwareTimeRangeChangedEventArgs>? RangeChanged;

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

    public void ResetRange()
    {
        _startRatio = 0;
        _endRatio = 1;
        Redraw();
        RaiseRangeChanged();
    }

    public void RefreshAccent() => Redraw();

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this).Position;
        var width = Math.Max(1, ActualWidth);
        var startX = _startRatio * width;
        var endX = _endRatio * width;
        _draggingStart = Math.Abs(point.X - startX) <= Math.Abs(point.X - endX);
        _draggingEnd = !_draggingStart;
        CapturePointer(e.Pointer);
        UpdateDraggedHandle(point.X);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingStart && !_draggingEnd)
            return;
        UpdateDraggedHandle(e.GetCurrentPoint(this).Position.X);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingStart && !_draggingEnd)
            return;
        _draggingStart = false;
        _draggingEnd = false;
        ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void UpdateDraggedHandle(double x)
    {
        var ratio = Math.Clamp(x / Math.Max(1, ActualWidth), 0, 1);
        const double minimumRange = 0.02;
        if (_draggingStart)
            _startRatio = Math.Min(ratio, _endRatio - minimumRange);
        else if (_draggingEnd)
            _endRatio = Math.Max(ratio, _startRatio + minimumRange);
        Redraw();
        RaiseRangeChanged();
    }

    private void RaiseRangeChanged()
    {
        var (startIndex, endIndex) = SelectedIndexes();
        RangeChanged?.Invoke(this, new HardwareTimeRangeChangedEventArgs(
            _startRatio,
            _endRatio,
            startIndex,
            endIndex,
            _times.Count > 0 ? _times[startIndex] : null,
            _times.Count > 0 ? _times[endIndex] : null));
    }

    private void Redraw()
    {
        Children.Clear();
        var width = Math.Max(1, ActualWidth);
        var height = Math.Max(1, ActualHeight);
        const double top = 28;
        const double graphHeight = 24;

        if (_times.Count > 0)
        {
            var (startIndex, endIndex) = SelectedIndexes();
            var start = _times[startIndex];
            var end = _times[endIndex];
            var duration = end - start;
            var rangeLabel = new TextBlock
            {
                Text = $"{start:HH:mm:ss.fff}  →  {end:HH:mm:ss.fff}  ·  {FormatDuration(duration)}",
                FontSize = 10,
                Foreground = _accent,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Width = Math.Max(1, width),
                TextAlignment = TextAlignment.Center,
            };
            Children.Add(rangeLabel);
            Canvas.SetTop(rangeLabel, 2);
        }

        Children.Add(new Rectangle
        {
            Width = width,
            Height = graphHeight,
            Fill = _track,
            Opacity = 0.35,
        });
        Canvas.SetTop(Children[^1], top);

        if (_values.Count > 1)
        {
            var min = _values.Min();
            var max = _values.Max();
            if (max - min < 0.001)
                max = min + 1;
            var line = new Polyline { Stroke = _accent, StrokeThickness = 1, Opacity = 0.55 };
            for (var index = 0; index < _values.Count; index++)
            {
                var x = width * index / (_values.Count - 1d);
                var y = top + graphHeight - 3 - ((_values[index] - min) / (max - min) * (graphHeight - 6));
                line.Points.Add(new Point(x, y));
            }
            Children.Add(line);
        }

        var startX = _startRatio * width;
        var endX = _endRatio * width;
        var accentColor = (_accent as SolidColorBrush)?.Color ?? Windows.UI.Color.FromArgb(255, 34, 197, 94);
        Children.Add(new Rectangle
        {
            Width = Math.Max(1, endX - startX),
            Height = graphHeight + 8,
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(40, accentColor.R, accentColor.G, accentColor.B)),
            Stroke = _accent,
            StrokeThickness = 1,
        });
        Canvas.SetLeft(Children[^1], startX);
        Canvas.SetTop(Children[^1], top - 4);

        AddHandle(startX, top - 4, graphHeight + 8);
        AddHandle(endX, top - 4, graphHeight + 8);
    }

    private (int Start, int End) SelectedIndexes()
    {
        if (_times.Count == 0)
            return (0, 0);
        var start = Math.Clamp((int)Math.Floor(_startRatio * (_times.Count - 1)), 0, _times.Count - 1);
        var end = Math.Clamp((int)Math.Ceiling(_endRatio * (_times.Count - 1)), start, _times.Count - 1);
        return (start, end);
    }

    private static string FormatDuration(TimeSpan duration)
        => duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}.{duration.Milliseconds:000}"
            : $"{(int)duration.TotalMinutes:00}:{duration.Seconds:00}.{duration.Milliseconds:000}";

    private void AddHandle(double x, double y, double height)
    {
        var handle = new Border
        {
            Width = HandleWidth,
            Height = height,
            CornerRadius = new CornerRadius(2),
            Background = _accent,
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(210, 255, 255, 255)),
            BorderThickness = new Thickness(1),
        };
        Children.Add(handle);
        Canvas.SetLeft(handle, Math.Clamp(x - HandleWidth / 2, 0, Math.Max(0, ActualWidth - HandleWidth)));
        Canvas.SetTop(handle, y);
    }
}

public sealed record HardwareTimeRangeChangedEventArgs(
    double StartRatio,
    double EndRatio,
    int StartIndex,
    int EndIndex,
    DateTimeOffset? StartTime,
    DateTimeOffset? EndTime)
{
    public TimeSpan Duration => StartTime is not null && EndTime is not null ? EndTime.Value - StartTime.Value : TimeSpan.Zero;
}
