using ADBControl.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace ADBControl.Desktop.Controls;

public sealed record CompanionInstallStatusPalette(
    Brush Surface,
    Brush Border,
    Brush PrimaryText,
    Brush SecondaryText,
    Brush Active,
    Brush Success,
    Brush Warning,
    Brush Error);

public sealed class CompanionInstallStatusView
{
    private readonly CompanionInstallStatusPalette _palette;
    private readonly StackPanel _activityDots;
    private readonly TextBlock _terminalLabel;
    private readonly TextBlock _title;
    private readonly TextBlock _message;
    private readonly TextBlock _metadata;
    private readonly Storyboard _activityStoryboard = new();

    public Border Root { get; }

    public CompanionInstallStatusView(CompanionInstallStatusPalette palette)
    {
        _palette = palette;
        Root = new Border
        {
            Visibility = Visibility.Collapsed,
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            BorderBrush = palette.Border,
            Background = palette.Surface,
            Padding = new Thickness(14, 11, 14, 11),
        };

        _activityDots = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        for (var index = 0; index < 3; index++)
        {
            var dot = new Border
            {
                Width = 6,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = palette.Active,
                Opacity = 0.25,
            };
            _activityDots.Children.Add(dot);
            var pulse = new DoubleAnimation
            {
                From = 0.25,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(420)),
                AutoReverse = true,
                BeginTime = TimeSpan.FromMilliseconds(index * 140),
                RepeatBehavior = RepeatBehavior.Forever,
            };
            Storyboard.SetTarget(pulse, dot);
            Storyboard.SetTargetProperty(pulse, "Opacity");
            _activityStoryboard.Children.Add(pulse);
        }

        _terminalLabel = new TextBlock
        {
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        var indicator = new Grid
        {
            Width = 46,
            MinHeight = 24,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _activityDots, _terminalLabel },
        };

        _title = new TextBlock
        {
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = palette.PrimaryText,
            TextWrapping = TextWrapping.Wrap,
        };
        _message = new TextBlock
        {
            FontSize = 12,
            Foreground = palette.SecondaryText,
            TextWrapping = TextWrapping.Wrap,
        };
        _metadata = new TextBlock
        {
            FontSize = 10,
            Foreground = palette.SecondaryText,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        var copy = new StackPanel
        {
            Spacing = 3,
            Children = { _title, _message, _metadata },
        };
        var layout = new Grid
        {
            ColumnSpacing = 10,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition(),
            },
            Children = { indicator, copy },
        };
        Grid.SetColumn(copy, 1);
        Root.Child = layout;
    }

    public void Apply(CompanionInstallStatus status)
    {
        Root.Visibility = Visibility.Visible;
        _title.Text = status.Title;
        _message.Text = status.Message;
        _activityDots.Visibility = status.IsActive ? Visibility.Visible : Visibility.Collapsed;
        _terminalLabel.Visibility = status.IsActive ? Visibility.Collapsed : Visibility.Visible;
        if (status.IsActive)
        {
            Root.BorderBrush = _palette.Active;
            _activityStoryboard.Begin();
        }
        else
        {
            _activityStoryboard.Stop();
            var (label, brush) = status.Stage switch
            {
                CompanionInstallStage.Succeeded => ("完成", _palette.Success),
                CompanionInstallStage.CompletedWithWarning => ("注意", _palette.Warning),
                _ => ("失败", _palette.Error),
            };
            _terminalLabel.Text = label;
            _terminalLabel.Foreground = brush;
            Root.BorderBrush = brush;
        }

        _metadata.Text = status.Error is null
            ? $"trace {ShortTrace(status.TraceId)} · {FormatElapsed(status.ElapsedMilliseconds)}"
            : $"错误码 {status.Error.ErrorCode} · trace {ShortTrace(status.TraceId)} · {FormatElapsed(status.ElapsedMilliseconds)}";
        _metadata.Visibility = status.IsActive ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string ShortTrace(string traceId) => traceId.Length <= 12 ? traceId : traceId[..12];

    private static string FormatElapsed(long elapsedMilliseconds) =>
        elapsedMilliseconds < 1000
            ? $"{elapsedMilliseconds} ms"
            : $"{elapsedMilliseconds / 1000d:0.0} s";
}
