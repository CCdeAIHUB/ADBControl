using ADBControl.Desktop.Models;
using ADBControl.Desktop.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ADBControl.Desktop.Views;

public sealed class MainWindow : Window
{
    private readonly SettingsService _settings = new();
    private readonly AdbService _adb = new();
    private readonly DeviceService _devices;

    private readonly Grid _root = new();
    private readonly NavigationView _nav = new();
    private readonly Grid _contentHost = new();
    private readonly Border _aiPanel = new();
    private readonly StackPanel _messageList = new();
    private readonly TextBox _aiInput = new();
    private readonly ComboBox _modelCombo = new();
    private readonly InfoBar _info = new();

    public MainWindow()
    {
        _settings.Load();
        _devices = new DeviceService(_settings, _adb);

        Title = "ADBControl";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(BuildTitleBar());
        Content = _root;
        BuildShell();
        ShowOverview();
    }

    private UIElement BuildTitleBar()
    {
        var bar = new Grid
        {
            Height = 36,
            Background = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(12, 0, 0, 0),
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition(),
            },
        };
        bar.Children.Add(new TextBlock
        {
            Text = "ADBControl",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return bar;
    }

    private void BuildShell()
    {
        _root.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 10, 10, 10));
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        _root.RowDefinitions.Add(new RowDefinition());

        _info.IsOpen = false;
        _info.HorizontalAlignment = HorizontalAlignment.Right;
        _info.VerticalAlignment = VerticalAlignment.Top;
        _info.Margin = new Thickness(0, 48, 24, 0);
        Canvas.SetZIndex(_info, 20);

        _nav.PaneDisplayMode = NavigationViewPaneDisplayMode.LeftCompact;
        _nav.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
        _nav.IsSettingsVisible = false;
        _nav.Header = "总览";
        _nav.Background = new SolidColorBrush(ColorHelper.FromArgb(110, 20, 28, 38));
        _nav.MenuItems.Add(NavItem("总览", Symbol.Home));
        _nav.MenuItems.Add(NavItem("设备", Symbol.CellPhone));
        _nav.MenuItems.Add(NavItem("任务", Symbol.List));
        _nav.MenuItems.Add(NavItem("设置", Symbol.Setting));
        _nav.FooterMenuItems.Add(NavItem("AI", Symbol.Message));
        _nav.SelectionChanged += OnNavigationSelectionChanged;

        _contentHost.Padding = new Thickness(24);
        _nav.Content = _contentHost;

        Grid.SetRow(_nav, 1);
        _root.Children.Add(_nav);
        _root.Children.Add(_info);
        BuildAiPanel();
        _root.Children.Add(_aiPanel);
    }

    private static NavigationViewItem NavItem(string text, Symbol symbol)
    {
        return new NavigationViewItem
        {
            Content = text,
            Tag = text,
            Icon = new SymbolIcon(symbol),
        };
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag)
            return;

        if (tag == "AI")
        {
            _aiPanel.Visibility = _aiPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            return;
        }

        _nav.Header = tag;
        _aiPanel.Visibility = Visibility.Collapsed;
        switch (tag)
        {
            case "总览":
                ShowOverview();
                break;
            case "设备":
                ShowDevices();
                break;
            case "任务":
                ShowTasks();
                break;
            case "设置":
                ShowSettings();
                break;
        }
    }

    private void ShowOverview()
    {
        _contentHost.Children.Clear();
        var panel = PageStack();
        panel.Children.Add(Header("欢迎使用 ADBControl", "WinUI 3 桌面前端已接入本地 ADB 设备管理入口。"));
        var cards = new Grid { ColumnSpacing = 12 };
        cards.ColumnDefinitions.Add(new ColumnDefinition());
        cards.ColumnDefinitions.Add(new ColumnDefinition());
        cards.ColumnDefinitions.Add(new ColumnDefinition());
        cards.Children.Add(MetricCard("已连接设备", _devices.Devices.Count.ToString(), Colors.MediumSeaGreen, 0));
        cards.Children.Add(MetricCard("AI 模型", _settings.Current.AiModels.Count.ToString(), Colors.DeepSkyBlue, 1));
        cards.Children.Add(MetricCard("运行中任务", "0", Colors.Orange, 2));
        panel.Children.Add(cards);
        _contentHost.Children.Add(panel);
    }

    private void ShowDevices()
    {
        _contentHost.Children.Clear();
        var panel = PageStack();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(Header("设备", "管理已连接的 Android 设备。"));
        var add = PrimaryButton("添加设备");
        add.Click += async (_, _) => await ShowAddDeviceDialogAsync();
        Grid.SetColumn(add, 1);
        header.Children.Add(add);
        panel.Children.Add(header);

        foreach (var device in _devices.Devices)
            panel.Children.Add(DeviceCard(device));

        if (_devices.Devices.Count == 0)
            panel.Children.Add(Card(new TextBlock { Text = "还没有添加设备，点击右上角添加设备开始连接。", FontSize = 14 }));

        _contentHost.Children.Add(new ScrollViewer { Content = panel });
    }

    private UIElement DeviceCard(DeviceModel device)
    {
        var root = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var info = new StackPanel { Spacing = 4 };
        info.Children.Add(new TextBlock
        {
            Text = device.DisplayName,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 15,
        });
        info.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(device.Note) ? $"{device.IpAddress}:{device.Port}" : device.Note,
            Foreground = MutedBrush(),
            FontSize = 12,
        });
        root.Children.Add(info);
        var remove = new Button { Content = "删除" };
        remove.Click += (_, _) =>
        {
            _devices.Remove(device);
            ShowDevices();
            Notify("已删除设备", device.DisplayName, InfoBarSeverity.Informational);
        };
        Grid.SetColumn(remove, 1);
        root.Children.Add(remove);
        return Card(root);
    }

    private void ShowTasks()
    {
        _contentHost.Children.Clear();
        var panel = PageStack();
        panel.Children.Add(Header("任务", "批量任务和自动化队列将在这里显示。"));
        panel.Children.Add(Card(new TextBlock { Text = "暂无运行中任务。", FontSize = 14 }));
        _contentHost.Children.Add(panel);
    }

    private void ShowSettings()
    {
        _contentHost.Children.Clear();
        var panel = PageStack();
        panel.MaxWidth = 720;
        panel.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(Header("设置", "应用配置和 AI 模型管理。"));
        panel.Children.Add(SettingsCard());
        _contentHost.Children.Add(new ScrollViewer { Content = panel });
    }

    private UIElement SettingsCard()
    {
        var stack = new StackPanel { Spacing = 14 };
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition());
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.Children.Add(new TextBlock
        {
            Text = "AI 模型",
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        var addModel = PrimaryButton("添加模型");
        addModel.Click += async (_, _) => await ShowAddModelDialogAsync();
        Grid.SetColumn(addModel, 1);
        titleRow.Children.Add(addModel);
        stack.Children.Add(titleRow);

        if (_settings.Current.AiModels.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "暂无 AI 模型，点击添加模型开始配置。",
                Foreground = MutedBrush(),
            });
        }
        else
        {
            foreach (var model in _settings.Current.AiModels)
            {
                stack.Children.Add(Card(new TextBlock
                {
                    Text = $"{model.Name}  ·  {model.ModelId}\n{model.ApiUrl}",
                    TextWrapping = TextWrapping.Wrap,
                }));
            }
        }

        return Card(stack);
    }

    private void BuildAiPanel()
    {
        _aiPanel.Visibility = Visibility.Collapsed;
        _aiPanel.Width = 460;
        _aiPanel.Margin = new Thickness(0, 46, 16, 92);
        _aiPanel.HorizontalAlignment = HorizontalAlignment.Right;
        _aiPanel.VerticalAlignment = VerticalAlignment.Stretch;
        _aiPanel.CornerRadius = new CornerRadius(16);
        _aiPanel.BorderBrush = BorderBrush();
        _aiPanel.BorderThickness = new Thickness(1);
        _aiPanel.Background = new SolidColorBrush(ColorHelper.FromArgb(235, 18, 24, 33));
        Canvas.SetZIndex(_aiPanel, 10);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new Grid { Padding = new Thickness(16, 12, 12, 12) };
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(new TextBlock
        {
            Text = "AI Agent",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        var close = new Button { Content = "×", Width = 32, Height = 32 };
        close.Click += (_, _) => _aiPanel.Visibility = Visibility.Collapsed;
        Grid.SetColumn(close, 1);
        top.Children.Add(close);
        root.Children.Add(top);

        _messageList.Spacing = 10;
        _messageList.Children.Add(MessageBubble("AI", "请先在设置内配置 AI 模型，然后再发送消息。", false));
        Grid.SetRow(_messageList, 1);
        root.Children.Add(new ScrollViewer { Content = _messageList, Padding = new Thickness(16) });

        var inputArea = new Grid { Padding = new Thickness(12), RowSpacing = 8 };
        inputArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        inputArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _aiInput.PlaceholderText = "要求后续变更...";
        _aiInput.AcceptsReturn = true;
        _aiInput.MinHeight = 78;
        inputArea.Children.Add(_aiInput);

        var tools = new Grid { ColumnSpacing = 8 };
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tools.ColumnDefinitions.Add(new ColumnDefinition());
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _modelCombo.PlaceholderText = "选择模型";
        _modelCombo.MinWidth = 120;
        RefreshModelCombo();
        Grid.SetColumn(_modelCombo, 2);
        tools.Children.Add(_modelCombo);
        var send = PrimaryButton("发送");
        send.Click += (_, _) => SendAiMessage();
        Grid.SetColumn(send, 3);
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tools.Children.Add(send);
        Grid.SetRow(tools, 1);
        inputArea.Children.Add(tools);

        Grid.SetRow(inputArea, 2);
        root.Children.Add(inputArea);
        _aiPanel.Child = root;
    }

    private void RefreshModelCombo()
    {
        _modelCombo.Items.Clear();
        foreach (var model in _settings.Current.AiModels)
            _modelCombo.Items.Add(model.Name);
    }

    private void SendAiMessage()
    {
        if (_modelCombo.SelectedItem is null)
        {
            _messageList.Children.Add(MessageBubble("AI", "请先去设置内配置并选择 AI 模型。", false));
            return;
        }
        if (string.IsNullOrWhiteSpace(_aiInput.Text))
            return;

        _messageList.Children.Add(MessageBubble("You", _aiInput.Text.Trim(), true));
        _aiInput.Text = string.Empty;
    }

    private static UIElement MessageBubble(string role, string text, bool user)
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = 360,
            Spacing = 4,
        };
        panel.Children.Add(new TextBlock
        {
            Text = role,
            FontSize = 11,
            Foreground = user ? new SolidColorBrush(Colors.LightGray) : new SolidColorBrush(Colors.MediumSeaGreen),
            HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        });
        panel.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 8, 12, 8),
            Background = user
                ? new SolidColorBrush(ColorHelper.FromArgb(255, 16, 185, 129))
                : new SolidColorBrush(ColorHelper.FromArgb(255, 38, 48, 62)),
            Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
        });
        return panel;
    }

    private async Task ShowAddDeviceDialogAsync()
    {
        var ip = new TextBox { PlaceholderText = "192.168.1.100" };
        var pairPort = new TextBox { PlaceholderText = "配对端口" };
        var pairCode = new TextBox { PlaceholderText = "配对码" };
        var connectPort = new TextBox { PlaceholderText = "连接端口" };
        var note = new TextBox { PlaceholderText = "设备备注" };
        var status = new TextBlock { Foreground = MutedBrush(), TextWrapping = TextWrapping.Wrap };
        var pairButton = new Button { Content = "配对" };
        pairButton.Click += async (_, _) =>
        {
            if (!int.TryParse(pairPort.Text, out var p))
            {
                status.Text = "请填写有效配对端口。";
                return;
            }
            var result = await _devices.PairAsync(ip.Text.Trim(), p, pairCode.Text.Trim());
            status.Text = result.Success ? "配对成功，请填写连接端口。" : FailureText(result);
        };

        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(new TextBlock { Text = "无线 ADB（Android 11+）", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        stack.Children.Add(ip);
        stack.Children.Add(pairPort);
        stack.Children.Add(pairCode);
        stack.Children.Add(pairButton);
        stack.Children.Add(connectPort);
        stack.Children.Add(note);
        stack.Children.Add(status);

        var dialog = Dialog("添加设备", stack, "添加设备");
        var response = await dialog.ShowAsync();
        if (response != ContentDialogResult.Primary)
            return;

        if (!int.TryParse(connectPort.Text, out var port))
        {
            Notify("添加失败", "请填写有效连接端口。", InfoBarSeverity.Error);
            return;
        }

        var addResult = await _devices.ConnectAndSaveAsync(ip.Text.Trim(), port, note.Text.Trim());
        if (addResult.Success || addResult.Stdout.Contains("connected", StringComparison.OrdinalIgnoreCase))
        {
            Notify("设备已添加", $"{ip.Text}:{port}", InfoBarSeverity.Success);
            ShowDevices();
        }
        else
        {
            Notify("连接失败", FailureText(addResult), InfoBarSeverity.Error);
        }
    }

    private async Task ShowAddModelDialogAsync()
    {
        var name = new TextBox { PlaceholderText = "模型名称" };
        var modelId = new TextBox { PlaceholderText = "模型标识，如 gpt-4o" };
        var apiUrl = new TextBox { PlaceholderText = "API URL" };
        var apiKey = new PasswordBox { PlaceholderText = "API 密钥" };
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(name);
        stack.Children.Add(modelId);
        stack.Children.Add(apiUrl);
        stack.Children.Add(apiKey);

        var dialog = Dialog("添加 AI 模型", stack, "添加模型");
        var response = await dialog.ShowAsync();
        if (response != ContentDialogResult.Primary)
            return;

        if (string.IsNullOrWhiteSpace(name.Text) || string.IsNullOrWhiteSpace(modelId.Text))
        {
            Notify("添加失败", "模型名称和模型标识不能为空。", InfoBarSeverity.Error);
            return;
        }

        _settings.Current.AiModels.Add(new AiModelSettings
        {
            Name = name.Text.Trim(),
            ModelId = modelId.Text.Trim(),
            ApiUrl = apiUrl.Text.Trim(),
            ApiKey = apiKey.Password,
        });
        _settings.Save();
        RefreshModelCombo();
        ShowSettings();
        Notify("模型已添加", name.Text.Trim(), InfoBarSeverity.Success);
    }

    private ContentDialog Dialog(string title, UIElement content, string primary)
    {
        return new ContentDialog
        {
            XamlRoot = _root.XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = primary,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
    }

    private void Notify(string title, string message, InfoBarSeverity severity)
    {
        _info.Title = title;
        _info.Message = message;
        _info.Severity = severity;
        _info.IsOpen = true;
    }

    private static string FailureText(AdbCommandResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Stderr))
            return result.Stderr.Trim();
        if (!string.IsNullOrWhiteSpace(result.Stdout))
            return result.Stdout.Trim();
        return $"adb 退出码 {result.ExitCode}";
    }

    private static StackPanel PageStack()
    {
        return new StackPanel { Spacing = 18 };
    }

    private static UIElement Header(string title, string description)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 26,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
        });
        stack.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 14,
            Foreground = MutedBrush(),
        });
        return stack;
    }

    private static UIElement MetricCard(string title, string value, Windows.UI.Color accent, int column)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = title, Foreground = MutedBrush() });
        stack.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 34,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = new SolidColorBrush(accent),
        });
        var card = Card(stack);
        Grid.SetColumn(card, column);
        return card;
    }

    private static Border Card(UIElement child)
    {
        return new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18),
            BorderBrush = BorderBrush(),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(ColorHelper.FromArgb(210, 22, 30, 41)),
            Child = child,
        };
    }

    private static Button PrimaryButton(string text)
    {
        return new Button
        {
            Content = text,
            Background = new SolidColorBrush(ColorHelper.FromArgb(255, 16, 185, 129)),
            Foreground = new SolidColorBrush(Colors.White),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 8, 14, 8),
        };
    }

    private static SolidColorBrush BorderBrush() => new(ColorHelper.FromArgb(255, 55, 65, 81));
    private static SolidColorBrush MutedBrush() => new(ColorHelper.FromArgb(255, 148, 163, 184));
}
