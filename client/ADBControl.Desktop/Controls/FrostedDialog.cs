using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ADBControl.Desktop.Controls;

public sealed record FrostedDialogPalette(
    Windows.UI.Color TintColor,
    Windows.UI.Color FallbackColor,
    Brush BorderBrush,
    Brush TextBrush,
    Brush MutedBrush,
    Brush ButtonBrush,
    Brush ButtonHoverBrush,
    Brush AccentBrush,
    Brush AccentTextBrush);

public sealed class FrostedDialog
{
    private static readonly object PresentationSync = new();
    private static FrostedDialog? s_activeDialog;
    private readonly ContentDialog _dialog;
    private ContentDialogResult _result;

    public FrostedDialog(
        XamlRoot xamlRoot,
        string title,
        UIElement content,
        FrostedDialogPalette palette,
        string? primaryText = null,
        string? cancelText = null)
    {
        var close = IconButton("\uE711", "关闭", palette);
        var header = new Grid
        {
            Margin = new Thickness(20, 16, 14, 10),
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } },
        };
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = palette.TextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = string.IsNullOrWhiteSpace(title) ? Visibility.Collapsed : Visibility.Visible,
        });
        Grid.SetColumn(close, 1);
        header.Children.Add(close);

        var body = new StackPanel { Spacing = 0 };
        body.Children.Add(header);
        body.Children.Add(new Border { Padding = new Thickness(20, 0, 20, 18), Child = content });
        if (!string.IsNullOrWhiteSpace(primaryText))
        {
            var cancel = TextButton(cancelText ?? "取消", palette, false);
            var primary = TextButton(primaryText, palette, true);
            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Margin = new Thickness(20, 0, 20, 18),
                Children = { cancel, primary },
            };
            cancel.Click += (_, _) => Hide(ContentDialogResult.None);
            primary.Click += (_, _) => Hide(ContentDialogResult.Primary);
            body.Children.Add(footer);
        }

        var acrylic = new AcrylicBrush
        {
            TintColor = palette.TintColor,
            TintOpacity = 0.72,
            FallbackColor = palette.FallbackColor,
        };
        // 使用 ThemeShadow 为弹窗添加与圆角匹配的阴影，替代 ContentDialog 默认的矩形阴影。
        var shadow = new ThemeShadow();
        var shadowReceiver = new Border();
        var shell = new Border
        {
            MinWidth = 380,
            MaxWidth = 1040,
            CornerRadius = new CornerRadius(20),
            BorderBrush = palette.BorderBrush,
            BorderThickness = new Thickness(1),
            Background = acrylic,
            Child = body,
            Shadow = shadow,
            Translation = new System.Numerics.Vector3(0, 0, 32),
        };
        _dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Content = shell,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        // 覆盖 ContentDialog 默认的阴影、边框和内边距资源，消除弹窗与窗口之间的间距断层。
        _dialog.Resources["ContentDialogMaxWidth"] = 1080d;
        _dialog.Resources["ContentDialogMinWidth"] = 0d;
        _dialog.Resources["ContentDialogBackground"] = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        _dialog.Resources["ContentDialogBorderBrush"] = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        _dialog.Resources["ContentDialogBorderThickness"] = new Thickness(0);
        _dialog.Resources["ContentDialogPadding"] = new Thickness(0);
        // 移除 ContentDialog 默认阴影，避免与自定义 shell 的圆角不匹配。
        _dialog.Resources["ContentDialogShadow"] = null;
        // 确保 ContentDialog 自身的圆角为 0，不产生额外圆角剪切。
        _dialog.Resources["ContentDialogCornerRadius"] = new CornerRadius(0);
        close.Click += (_, _) => Hide(ContentDialogResult.None);
    }

    public async Task<ContentDialogResult> ShowAsync()
    {
        lock (PresentationSync)
        {
            // WinUI permits one ContentDialog per XamlRoot. Treat repeated clicks as a no-op
            // instead of allowing the COM exception to escape through an async event handler.
            if (s_activeDialog is not null)
                return ContentDialogResult.None;
            s_activeDialog = this;
        }

        _result = ContentDialogResult.None;
        try
        {
            await _dialog.ShowAsync();
            return _result;
        }
        finally
        {
            lock (PresentationSync)
            {
                if (ReferenceEquals(s_activeDialog, this))
                    s_activeDialog = null;
            }
        }
    }

    public void Hide() => Hide(ContentDialogResult.None);

    private void Hide(ContentDialogResult result)
    {
        _result = result;
        _dialog.Hide();
    }

    private static Button IconButton(string glyph, string automationName, FrostedDialogPalette palette)
    {
        var button = new Button
        {
            Width = 34,
            Height = 34,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(17),
            Background = palette.ButtonBrush,
            BorderBrush = palette.BorderBrush,
            BorderThickness = new Thickness(1),
            Foreground = palette.TextBrush,
            Content = new FontIcon { Glyph = glyph, FontSize = 13 },
        };
        button.Resources["ButtonBackgroundPointerOver"] = palette.ButtonHoverBrush;
        ToolTipService.SetToolTip(button, automationName);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, automationName);
        return button;
    }

    private static Button TextButton(string text, FrostedDialogPalette palette, bool primary)
    {
        var button = new Button
        {
            Content = text,
            MinHeight = 36,
            Padding = new Thickness(16, 7, 16, 7),
            CornerRadius = new CornerRadius(10),
            Background = primary ? palette.AccentBrush : palette.ButtonBrush,
            BorderBrush = primary ? palette.AccentBrush : palette.BorderBrush,
            BorderThickness = new Thickness(1),
            Foreground = primary ? palette.AccentTextBrush : palette.TextBrush,
        };
        button.Resources["ButtonBackgroundPointerOver"] = primary ? palette.AccentBrush : palette.ButtonHoverBrush;
        return button;
    }
}
