using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ADBControl.Desktop.Controls;

/// <summary>
/// 自绘下拉选择器：用 Button + Flyout + ListView 实现，
/// 替代原生 ComboBox 以保持与整体 UI 风格（圆角、自绘边框）一致。
/// Flyout 自动处理定位和dismiss，比 Popup 更可靠。
/// </summary>
public sealed class DropdownSelector : ContentControl
{
    private readonly Button _trigger;
    private readonly ListView _listView;
    private readonly TextBlock _displayText;
    private readonly FontIcon _chevron;
    private readonly Flyout _flyout;

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(object), typeof(DropdownSelector), new PropertyMetadata(null, OnItemsSourceChanged));

    public static readonly DependencyProperty SelectedIndexProperty =
        DependencyProperty.Register(nameof(SelectedIndex), typeof(int), typeof(DropdownSelector), new PropertyMetadata(-1, OnSelectedIndexChanged));

    public event SelectionChangedEventHandler? SelectionChanged;

    public object? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    public new double MinWidth
    {
        get => _trigger.MinWidth;
        set => _trigger.MinWidth = value;
    }

    public DropdownSelector()
    {
        _chevron = new FontIcon
        {
            Glyph = "\uE70D",
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _displayText = new TextBlock
        {
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _trigger = new Button
        {
            Padding = new Thickness(12, 7, 8, 7),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _trigger.Content = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto },
            },
            Children =
            {
                WithColumn(_displayText, 0),
                WithColumn(_chevron, 1),
            },
        };

        _listView = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            Padding = new Thickness(4),
            MaxHeight = 300,
        };
        _listView.SelectionChanged += OnListViewSelectionChanged;

        // 使用 Flyout 承载下拉列表，Flyout 自动处理定位和轻量关闭。
        _flyout = new Flyout
        {
            Content = _listView,
            Placement = FlyoutPlacementMode.Bottom,
        };
        FlyoutBase.SetAttachedFlyout(_trigger, _flyout);
        _trigger.Click += (_, _) => FlyoutBase.ShowAttachedFlyout(_trigger);
        _flyout.Closed += (_, _) => _chevron.Glyph = "\uE70D";
        _flyout.Opened += (_, _) => _chevron.Glyph = "\uE70E";

        var container = new Grid { Children = { _trigger } };
        DefaultStyleKey = typeof(ContentControl);
        Content = container;
    }

    private static T WithColumn<T>(T element, int column) where T : FrameworkElement
    {
        Grid.SetColumn(element, column);
        return element;
    }

    /// <summary>
    /// 注入主题色画刷，由外部调用。避免在此控件中硬编码主题逻辑，
    /// 保持与 MainWindow 的主题系统一致。
    /// </summary>
    public void ApplyTheme(
        Brush foreground,
        Brush background,
        Brush border,
        Brush hover,
        Brush accent,
        Brush accentText,
        Brush popupBackground)
    {
        _trigger.Background = background;
        _trigger.Foreground = foreground;
        _trigger.BorderBrush = border;
        _trigger.Resources["ButtonBackgroundPointerOver"] = hover;
        _trigger.Resources["ButtonBackgroundPressed"] = hover;
        _trigger.Resources["ButtonBorderBrushPointerOver"] = accent;
        _trigger.Resources["ButtonBorderBrushPressed"] = accent;
        _displayText.Foreground = foreground;
        _chevron.Foreground = foreground;

        _listView.Background = popupBackground;
        _listView.Resources["ListViewItemBackgroundPointerOver"] = hover;
        _listView.Resources["ListViewItemBackgroundSelected"] = accent;
        _listView.Resources["ListViewItemBackgroundSelectedPointerOver"] = accent;
        _listView.Resources["ListViewItemSelectedBackground"] = accent;
        _listView.Resources["ListViewItemSelectedPointerOverBackground"] = accent;
        _listView.Resources["ListViewItemSelectedPressedBackground"] = accent;
        _listView.Resources["ListViewItemSelectionIndicatorBrush"] = accent;
        _listView.Resources["ListViewItemForegroundSelected"] = accentText;
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DropdownSelector selector)
            selector._listView.ItemsSource = e.NewValue;
    }

    private static void OnSelectedIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DropdownSelector selector && e.NewValue is int index)
        {
            if (index >= 0 && selector._listView.Items is not null && index < selector._listView.Items.Count)
            {
                selector._listView.SelectedIndex = index;
                selector._displayText.Text = selector._listView.Items[index]?.ToString() ?? string.Empty;
            }
            else
            {
                selector._listView.SelectedIndex = -1;
                selector._displayText.Text = string.Empty;
            }
        }
    }

    private void OnListViewSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_listView.SelectedIndex >= 0 && _listView.Items is not null && _listView.SelectedIndex < _listView.Items.Count)
        {
            SelectedIndex = _listView.SelectedIndex;
            _displayText.Text = _listView.Items[_listView.SelectedIndex]?.ToString() ?? string.Empty;
            SelectionChanged?.Invoke(this, e);
        }
        // 选中后关闭弹出层
        _flyout.Hide();
    }
}
