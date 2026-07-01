using ADBControl.Desktop.Models;
using ADBControl.Desktop.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ADBControl.Desktop.Views;

public sealed class MainWindow : Window
{
    private readonly SettingsService _settings = new();
    private readonly AdbService _adb = new();
    private readonly DeviceService _devices;

    private readonly Grid _root = new();
    private readonly Grid _contentHost = new();
    private readonly Border _aiPanel = new();
    private readonly StackPanel _messageList = new();
    private readonly StackPanel _pendingAttachmentList = new();
    private readonly TextBox _aiInput = new();
    private readonly ComboBox _modelCombo = new();
    private readonly ComboBox _permissionCombo = new();
    private readonly InfoBar _info = new();
    private readonly List<AiAttachment> _pendingAttachments = new();
    private readonly List<Button> _navButtons = new();

    private string _currentPage = "总览";

    public MainWindow()
    {
        _settings.Load();
        _devices = new DeviceService(_settings, _adb);

        Title = "ADBControl";
        ExtendsContentIntoTitleBar = true;
        Content = _root;
        BuildShell();
        Navigate("总览");
    }

    private FrameworkElement BuildTitleBar()
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
        _root.Background = AppBrush();
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        _root.RowDefinitions.Add(new RowDefinition());
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(80) });

        var background = new GridBackground
        {
            Fill = AppBrush(),
            GridLineBrush = GridLineBrush(),
            GridSize = 24,
        };
        Grid.SetRowSpan(background, 3);
        _root.Children.Add(background);

        var titleBar = BuildTitleBar();
        Grid.SetRow(titleBar, 0);
        _root.Children.Add(titleBar);
        SetTitleBar(titleBar);

        _contentHost.Padding = new Thickness(0);
        Grid.SetRow(_contentHost, 1);
        _root.Children.Add(_contentHost);

        var bottomBar = BuildBottomNavigation();
        Grid.SetRow(bottomBar, 2);
        _root.Children.Add(bottomBar);

        _info.HorizontalAlignment = HorizontalAlignment.Right;
        _info.VerticalAlignment = VerticalAlignment.Top;
        _info.Margin = new Thickness(0, 48, 24, 0);
        Canvas.SetZIndex(_info, 30);
        _root.Children.Add(_info);

        BuildAiPanel();
        Grid.SetRow(_aiPanel, 1);
        Grid.SetRowSpan(_aiPanel, 2);
        _root.Children.Add(_aiPanel);
    }

    private FrameworkElement BuildBottomNavigation()
    {
        var shell = new Grid
        {
            Padding = new Thickness(12, 0, 12, 12),
            ColumnDefinitions =
            {
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition(),
            },
        };

        var dock = new Border
        {
            CornerRadius = new CornerRadius(20),
            BorderBrush = BorderLightBrush(),
            BorderThickness = new Thickness(1),
            Background = NavBrush(),
            Padding = new Thickness(8, 6, 8, 6),
        };
        var nav = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };

        nav.Children.Add(NavButton("总览", Symbol.Home));
        nav.Children.Add(NavButton("设备", Symbol.CellPhone));
        nav.Children.Add(NavButton("任务", Symbol.List));
        nav.Children.Add(NavButton("设置", Symbol.Setting));

        dock.Child = nav;
        Grid.SetColumn(dock, 1);
        shell.Children.Add(dock);

        var aiDock = new Border
        {
            CornerRadius = new CornerRadius(20),
            BorderBrush = BorderLightBrush(),
            BorderThickness = new Thickness(1),
            Background = NavBrush(),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(8, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var ai = IconTextButton("AI", Symbol.Message);
        ai.Click += (_, _) => ToggleAiPanel();
        aiDock.Child = ai;
        Grid.SetColumn(aiDock, 2);
        shell.Children.Add(aiDock);
        return shell;
    }

    private Button NavButton(string text, Symbol symbol)
    {
        var button = IconTextButton(text, symbol);
        button.Tag = text;
        button.Click += (_, _) => Navigate(text);
        _navButtons.Add(button);
        return button;
    }

    private static Button IconTextButton(string text, Symbol symbol)
    {
        return new Button
        {
            MinWidth = 64,
            MinHeight = 56,
            Padding = new Thickness(10, 6, 10, 6),
            BorderThickness = new Thickness(0),
            Background = TransparentBrush(),
            Foreground = SecondaryTextBrush(),
            CornerRadius = new CornerRadius(14),
            Content = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new SymbolIcon(symbol) { Width = 22, Height = 22 },
                    new TextBlock { Text = text, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center },
                },
            },
        };
    }

    private void Navigate(string page)
    {
        _currentPage = page;
        _aiPanel.Visibility = Visibility.Collapsed;
        foreach (var button in _navButtons)
        {
            var active = string.Equals(button.Tag as string, page, StringComparison.Ordinal);
            button.Background = new SolidColorBrush(active
                ? ColorHelper.FromArgb(255, 34, 197, 94)
                : Colors.Transparent);
            button.Foreground = active ? OnPrimaryBrush() : SecondaryTextBrush();
        }

        switch (page)
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
        panel.Children.Add(Header("欢迎使用 ADBControl", "设备连接、任务执行与 AI Agent 都可以从底部导航进入。"));
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
        var toolbar = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        var filters = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
        };
        filters.Children.Add(new TextBox
        {
            PlaceholderText = "搜索设备备注、信息、IP...",
            Width = 280,
            CornerRadius = new CornerRadius(14),
        });
        filters.Children.Add(SecondaryButton("仅已连接"));
        filters.Children.Add(SecondaryButton("仅伴侣 APK"));
        toolbar.Children.Add(filters);

        var add = PrimaryButton("+ 添加设备");
        add.Click += async (_, _) => await ShowAddDeviceDialogAsync();
        Grid.SetColumn(add, 1);
        toolbar.Children.Add(add);
        panel.Children.Add(toolbar);

        if (_devices.Devices.Count == 0)
        {
            var empty = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Spacing = 12,
                Margin = new Thickness(0, 40, 0, 40),
            };
            empty.Children.Add(new Border
            {
                Width = 64,
                Height = 64,
                CornerRadius = new CornerRadius(32),
                Background = PrimaryLightBrush(),
                Child = new TextBlock
                {
                    Text = "📱",
                    FontSize = 28,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
            empty.Children.Add(new TextBlock
            {
                Text = "还没有添加设备",
                FontSize = 18,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = PrimaryTextBrush(),
            });
            empty.Children.Add(new TextBlock
            {
                Text = "点击右上角「添加设备」开始连接你的 Android 设备",
                Foreground = SecondaryTextBrush(),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            panel.Children.Add(Card(empty));
        }
        else
        {
            var list = new WrapPanel();
            foreach (var device in _devices.Devices)
                list.Children.Add(DeviceCard(device));
            panel.Children.Add(list);
        }

        _contentHost.Children.Add(new ScrollViewer { Content = panel });
    }

    private UIElement DeviceCard(DeviceModel device)
    {
        var root = new StackPanel { Spacing = 12 };
        var head = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        head.Children.Add(new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(20),
            Background = PrimaryLightBrush(),
            Child = new TextBlock
            {
                Text = "📱",
                FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });

        var info = new StackPanel { Spacing = 2, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = device.DisplayName,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 14,
            Foreground = PrimaryTextBrush(),
        });
        info.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(device.Note) ? device.DeviceId : device.Note,
            Foreground = SecondaryTextBrush(),
            FontSize = 12,
        });
        Grid.SetColumn(info, 1);
        head.Children.Add(info);
        var status = new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(device.IsConnected ? Colors.MediumSeaGreen : Colors.Orange),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(status, 2);
        head.Children.Add(status);
        root.Children.Add(head);

        var meta = new StackPanel { Spacing = 4 };
        meta.Children.Add(BodyText($"品牌: {device.Brand}"));
        meta.Children.Add(BodyText($"型号: {device.Model}"));
        meta.Children.Add(BodyText(device.ConnectionKind == "usb" ? $"设备 ID: {device.DeviceId}" : $"IP: {device.IpAddress}"));
        meta.Children.Add(BodyText($"Android: {device.AndroidVersion}"));
        meta.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = SurfaceAltBrush(),
            Padding = new Thickness(8, 2, 8, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text = device.ConnectionKind == "usb" ? "有线 ADB" : "无线 ADB",
                FontSize = 10,
                Foreground = SecondaryTextBrush(),
            },
        });
        root.Children.Add(meta);

        var button = new Button
        {
            Width = 280,
            Margin = new Thickness(0, 0, 16, 16),
            Background = SurfaceBrush(),
            BorderBrush = BorderBrush(),
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(20),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            BorderThickness = new Thickness(1),
            Content = root,
        };
        button.Click += (_, _) => ShowDeviceDetail(device);
        return button;
    }

    private void ShowDeviceDetail(DeviceModel device)
    {
        _contentHost.Children.Clear();
        var panel = PageStack();
        var back = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { new SymbolIcon(Symbol.Back), new TextBlock { Text = "返回设备列表" } },
            },
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        back.Click += (_, _) => ShowDevices();
        panel.Children.Add(back);
        panel.Children.Add(Header(device.DisplayName, device.ConnectionKind == "usb" ? "有线 ADB 设备详情" : "无线 ADB 设备详情"));

        var details = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        details.ColumnDefinitions.Add(new ColumnDefinition());
        details.ColumnDefinitions.Add(new ColumnDefinition());
        details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        details.Children.Add(InfoCard("连接方式", device.ConnectionKind == "usb" ? "有线 ADB" : "无线 ADB", 0, 0));
        details.Children.Add(InfoCard("设备 ID", device.DeviceId, 1, 0));
        details.Children.Add(InfoCard("地址", device.ConnectionKind == "usb" ? "-" : $"{device.IpAddress}:{device.Port}", 0, 1));
        details.Children.Add(InfoCard("状态", device.IsConnected ? "已连接" : "未连接", 1, 1));
        panel.Children.Add(details);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var remove = new Button { Content = "删除设备", Padding = new Thickness(14, 8, 14, 8) };
        remove.Click += (_, _) =>
        {
            _devices.Remove(device);
            Notify("已删除设备", device.DisplayName, InfoBarSeverity.Informational);
            ShowDevices();
        };
        actions.Children.Add(remove);
        panel.Children.Add(Card(actions));
        _contentHost.Children.Add(new ScrollViewer { Content = panel });
    }

    private static UIElement InfoCard(string title, string value, int column, int row)
    {
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(new TextBlock { Text = title, Foreground = MutedBrush(), FontSize = 12 });
        stack.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var card = Card(stack);
        Grid.SetColumn(card, column);
        Grid.SetRow(card, row);
        return card;
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
        panel.MaxWidth = 640;
        panel.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(SettingsSection("ADB 配置", new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "ADB 私有端口", Foreground = PrimaryTextBrush() },
                new TextBlock { Text = "与其他程序可能存在的 ADB 服务隔离，防止设备下线互相影响", FontSize = 12, Foreground = MutedBrush(), TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Children =
                    {
                        new NumberBox { Value = 15037, Minimum = 1024, Maximum = 65535, Width = 180, CornerRadius = new CornerRadius(14) },
                        SecondaryButton("恢复默认 (15037)"),
                    },
                },
            },
        }));
        panel.Children.Add(SettingsSection("通用", new StackPanel
        {
            Spacing = 16,
            Children =
            {
                SettingToggleRow("开机启动", "系统启动时自动运行"),
                new Border { Height = 1, Background = BorderBrush(), Margin = new Thickness(0, 4, 0, 4) },
                SettingToggleRow("深色模式", "关闭则跟随系统"),
            },
        }));
        panel.Children.Add(SettingsCard());
        panel.Children.Add(SettingsSection("关于", new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "ADBControl Desktop", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = PrimaryTextBrush() },
                BodyText("版本 0.1.0"),
                new TextBlock { Text = "基于 .NET 8 + WinUI 3", Foreground = MutedBrush() },
            },
        }));
        _contentHost.Children.Add(new ScrollViewer { Content = panel });
    }

    private static UIElement SettingsSection(string title, UIElement body)
    {
        var stack = new StackPanel { Spacing = 16 };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = PrimaryTextBrush(),
        });
        stack.Children.Add(body);
        return Card(stack);
    }

    private static Grid SettingToggleRow(string title, string description)
    {
        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = PrimaryTextBrush() });
        text.Children.Add(new TextBlock { Text = description, FontSize = 12, Foreground = SecondaryTextBrush() });
        row.Children.Add(text);
        var toggle = new ToggleSwitch();
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);
        return row;
    }

    private UIElement SettingsCard()
    {
        var stack = new StackPanel { Spacing = 14 };
        var titleRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
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
        _aiPanel.Width = 400;
        _aiPanel.Margin = new Thickness(0, 0, 0, 80);
        _aiPanel.HorizontalAlignment = HorizontalAlignment.Right;
        _aiPanel.VerticalAlignment = VerticalAlignment.Stretch;
        _aiPanel.CornerRadius = new CornerRadius(0);
        _aiPanel.BorderBrush = BorderLightBrush();
        _aiPanel.BorderThickness = new Thickness(1);
        _aiPanel.Background = SurfaceBrush();
        Canvas.SetZIndex(_aiPanel, 20);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new Grid
        {
            Padding = new Thickness(16, 12, 16, 12),
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        top.Children.Add(new SymbolIcon(Symbol.Message)
        {
            Width = 20,
            Height = 20,
            Foreground = PrimaryBrush(),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var title = new StackPanel { Spacing = 2, Margin = new Thickness(10, 0, 0, 0) };
        title.Children.Add(new TextBlock
        {
            Text = "AI Agent",
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = PrimaryTextBrush(),
        });
        title.Children.Add(new TextBlock
        {
            Text = "本地开发助手",
            FontSize = 12,
            Foreground = SecondaryTextBrush(),
        });
        Grid.SetColumn(title, 1);
        top.Children.Add(title);
        var close = new Button
        {
            Content = new SymbolIcon(Symbol.Cancel),
            Width = 34,
            Height = 34,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Background = TransparentBrush(),
            Foreground = SecondaryTextBrush(),
            BorderThickness = new Thickness(0),
        };
        close.Click += (_, _) => _aiPanel.Visibility = Visibility.Collapsed;
        Grid.SetColumn(close, 2);
        top.Children.Add(close);
        root.Children.Add(top);

        _messageList.Spacing = 12;
        _messageList.Children.Add(MessageBubble(new AiChatMessage
        {
            Role = "AI",
            Text = "请先在设置内配置 AI 模型，然后再发送消息。",
            IsUser = false,
        }));
        var messageScroller = new ScrollViewer
        {
            Content = _messageList,
            Padding = new Thickness(16, 12, 16, 12),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetRow(messageScroller, 1);
        root.Children.Add(messageScroller);

        var inputArea = new Grid
        {
            Margin = new Thickness(12, 8, 12, 12),
            Padding = new Thickness(0),
            RowSpacing = 8,
        };
        inputArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        inputArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        inputArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _pendingAttachmentList.Spacing = 8;
        inputArea.Children.Add(_pendingAttachmentList);

        _aiInput.PlaceholderText = "要求后续变更...";
        _aiInput.AcceptsReturn = true;
        _aiInput.MinHeight = 52;
        _aiInput.MaxHeight = 160;
        _aiInput.FontSize = 14;
        _aiInput.Padding = new Thickness(16, 14, 16, 4);
        _aiInput.CornerRadius = new CornerRadius(20);
        _aiInput.Background = SurfaceAltBrush();
        _aiInput.BorderBrush = BorderLightBrush();
        Grid.SetRow(_aiInput, 1);
        inputArea.Children.Add(_aiInput);

        var tools = BuildAiToolbar();
        Grid.SetRow(tools, 2);
        inputArea.Children.Add(tools);

        Grid.SetRow(inputArea, 2);
        root.Children.Add(inputArea);
        _aiPanel.Child = root;
    }

    private FrameworkElement BuildAiToolbar()
    {
        var tools = new Grid { ColumnSpacing = 10, Padding = new Thickness(10, 6, 10, 10) };
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tools.ColumnDefinitions.Add(new ColumnDefinition());
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var attach = new Button
        {
            Content = new SymbolIcon(Symbol.Attach),
            MinWidth = 0,
            MinHeight = 32,
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(8),
            Background = TransparentBrush(),
            Foreground = SecondaryTextBrush(),
            BorderThickness = new Thickness(0),
        };
        attach.Click += async (_, _) => await PickAttachmentsAsync();
        tools.Children.Add(attach);

        _permissionCombo.Width = 150;
        _permissionCombo.CornerRadius = new CornerRadius(8);
        _permissionCombo.Background = TransparentBrush();
        _permissionCombo.BorderBrush = BorderLightBrush();
        _permissionCombo.Items.Add(PermissionItem("只读", Symbol.View, "只允许读取当前项目上下文"));
        _permissionCombo.Items.Add(PermissionItem("询问", Symbol.Help, "执行敏感操作前询问"));
        _permissionCombo.Items.Add(PermissionItem("允许", Symbol.Accept, "允许执行本地开发操作"));
        _permissionCombo.SelectedIndex = 1;
        Grid.SetColumn(_permissionCombo, 1);
        tools.Children.Add(_permissionCombo);

        _modelCombo.PlaceholderText = "选择模型";
        _modelCombo.Width = 108;
        _modelCombo.CornerRadius = new CornerRadius(8);
        _modelCombo.Background = TransparentBrush();
        _modelCombo.BorderBrush = BorderLightBrush();
        RefreshModelCombo();
        Grid.SetColumn(_modelCombo, 3);
        tools.Children.Add(_modelCombo);

        var send = new Button
        {
            Content = new SymbolIcon(Symbol.Upload),
            Width = 34,
            Height = 34,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(17),
            Background = PrimaryBrush(),
            Foreground = OnPrimaryBrush(),
            BorderThickness = new Thickness(0),
        };
        send.Click += (_, _) => SendAiMessage();
        Grid.SetColumn(send, 4);
        tools.Children.Add(send);
        return tools;
    }

    private static ComboBoxItem PermissionItem(string name, Symbol icon, string description)
    {
        return new ComboBoxItem
        {
            Tag = name,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new SymbolIcon(icon),
                    new StackPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = name, FontSize = 13 },
                            new TextBlock { Text = description, FontSize = 11, Foreground = MutedBrush() },
                        },
                    },
                },
            },
        };
    }

    private void RefreshModelCombo()
    {
        _modelCombo.SelectionChanged -= OnModelSelectionChanged;
        _modelCombo.Items.Clear();
        foreach (var model in _settings.Current.AiModels)
        {
            _modelCombo.Items.Add(new ComboBoxItem
            {
                Tag = model,
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = model.Name, FontSize = 13 },
                        new TextBlock { Text = model.ModelId, FontSize = 11, Foreground = MutedBrush() },
                    },
                },
            });
        }

        _modelCombo.Items.Add(new ComboBoxItem
        {
            Tag = "__add_model__",
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { new SymbolIcon(Symbol.Add), new TextBlock { Text = "添加模型" } },
            },
        });
        _modelCombo.SelectionChanged += OnModelSelectionChanged;
    }

    private async void OnModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_modelCombo.SelectedItem is ComboBoxItem { Tag: string tag } && tag == "__add_model__")
        {
            _modelCombo.SelectedItem = null;
            await ShowAddModelDialogAsync();
        }
    }

    private async Task PickAttachmentsAsync()
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".webp");
        picker.FileTypeFilter.Add(".bmp");

        var files = await picker.PickMultipleFilesAsync();
        foreach (var file in files)
        {
            _pendingAttachments.Add(new AiAttachment
            {
                Name = file.Name,
                Path = file.Path,
                IsImage = true,
            });
        }

        RenderPendingAttachments();
    }

    private void RenderPendingAttachments()
    {
        _pendingAttachmentList.Children.Clear();
        if (_pendingAttachments.Count == 0)
            return;

        var wrap = new ItemsControl();
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var attachment in _pendingAttachments)
            panel.Children.Add(AttachmentChip(attachment, true));
        _pendingAttachmentList.Children.Add(panel);
    }

    private UIElement AttachmentChip(AiAttachment attachment, bool removable)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (attachment.IsImage)
        {
            row.Children.Add(new Image
            {
                Source = new BitmapImage(new Uri(attachment.Path)),
                Width = 46,
                Height = 46,
                Stretch = Stretch.UniformToFill,
            });
        }
        row.Children.Add(new TextBlock
        {
            Text = attachment.Name,
            MaxWidth = 140,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (removable)
        {
            var remove = new Button
            {
                Content = new SymbolIcon(Symbol.Cancel),
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
            };
            remove.Click += (_, _) =>
            {
                _pendingAttachments.Remove(attachment);
                RenderPendingAttachments();
            };
            row.Children.Add(remove);
        }

        return new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderBrush = BorderBrush(),
            BorderThickness = new Thickness(1),
            Background = SurfaceAltBrush(),
            Padding = new Thickness(8),
            Child = row,
        };
    }

    private void SendAiMessage()
    {
        var selectedModel = _modelCombo.SelectedItem as ComboBoxItem;
        if (selectedModel?.Tag is not AiModelSettings)
        {
            _messageList.Children.Add(MessageBubble(new AiChatMessage
            {
                Role = "AI",
                Text = "请先去设置内配置并选择 AI 模型。",
                IsUser = false,
            }));
            return;
        }

        if (string.IsNullOrWhiteSpace(_aiInput.Text) && _pendingAttachments.Count == 0)
            return;

        var permission = (_permissionCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "询问";
        var text = string.IsNullOrWhiteSpace(_aiInput.Text)
            ? $"已发送附件。权限：{permission}"
            : $"{_aiInput.Text.Trim()}\n\n权限：{permission}";

        _messageList.Children.Add(MessageBubble(new AiChatMessage
        {
            Role = "You",
            Text = text,
            IsUser = true,
            Attachments = _pendingAttachments.ToList(),
        }));
        _aiInput.Text = string.Empty;
        _pendingAttachments.Clear();
        RenderPendingAttachments();
    }

    private UIElement MessageBubble(AiChatMessage message)
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = message.IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = 340,
            Spacing = 4,
            Margin = new Thickness(0, 0, 0, 16),
        };
        panel.Children.Add(new TextBlock
        {
            Text = message.IsUser ? "You" : "AI",
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = message.IsUser ? MutedBrush() : PrimaryBrush(),
            HorizontalAlignment = HorizontalAlignment.Left,
        });

        var body = new StackPanel { Spacing = 8 };
        if (!string.IsNullOrWhiteSpace(message.Text))
        {
            body.Children.Add(new TextBlock
            {
                Text = message.Text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Foreground = PrimaryTextBrush(),
            });
        }
        foreach (var attachment in message.Attachments)
            body.Children.Add(AttachmentChip(attachment, false));

        panel.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 10, 14, 10),
            Background = message.IsUser
                ? PrimaryBrush()
                : SurfaceAltBrush(),
            Child = body,
        });
        return panel;
    }

    private async Task ShowAddDeviceDialogAsync()
    {
        var wirelessPanel = new StackPanel { Spacing = 12 };
        var usbPanel = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed };
        var selectedMode = "wireless";
        DeviceModel? selectedUsbDevice = null;

        var mode = new StackPanel
        {
            Spacing = 8,
        };
        mode.Children.Add(new TextBlock { Text = "添加方式", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = PrimaryTextBrush() });
        var radioRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var wirelessMode = new RadioButton { Content = "无线 ADB（Android 11+）", IsChecked = true };
        var usbMode = new RadioButton { Content = "有线 ADB" };
        void SelectMode(string value)
        {
            selectedMode = value;
            var isWireless = value == "wireless";
            wirelessPanel.Visibility = isWireless ? Visibility.Visible : Visibility.Collapsed;
            usbPanel.Visibility = isWireless ? Visibility.Collapsed : Visibility.Visible;
            wirelessMode.IsChecked = isWireless;
            usbMode.IsChecked = !isWireless;
        }

        wirelessMode.Checked += (_, _) => SelectMode("wireless");
        usbMode.Checked += (_, _) => SelectMode("usb");
        radioRow.Children.Add(wirelessMode);
        radioRow.Children.Add(usbMode);
        mode.Children.Add(radioRow);

        var ip = RoundedTextBox("192.168.1.100");
        var pairPort = RoundedTextBox("端口");
        var pairCode = RoundedTextBox("123456");
        var connectIp = RoundedTextBox("192.168.1.100");
        var connectPort = RoundedTextBox("5555");
        var deviceNote = RoundedTextBox("为设备添加备注名...");
        var wirelessStatus = new TextBlock { Foreground = MutedBrush(), TextWrapping = TextWrapping.Wrap };
        var pairButton = SecondaryButton("开始配对");
        pairButton.Click += async (_, _) =>
        {
            if (!int.TryParse(pairPort.Text, out var p))
            {
                wirelessStatus.Text = "请填写有效配对端口。";
                return;
            }
            var result = await _devices.PairAsync(ip.Text.Trim(), p, pairCode.Text.Trim());
            wirelessStatus.Text = result.Success ? "配对成功，请填写连接端口并添加设备。" : FailureText(result);
        };
        wirelessPanel.Children.Add(HintCard("适用于 Android 11 及以上设备。需在开发者选项中开启「无线调试」，然后使用配对码配对。"));
        wirelessPanel.Children.Add(SectionTitle("步骤 1：配对设备"));
        wirelessPanel.Children.Add(BodyText("在手机无线调试页面点击「使用配对码配对设备」，输入显示的 IP、端口和配对码"));
        var pairGrid = TwoColumnGrid();
        pairGrid.Children.Add(LabeledField("IP 地址", ip, 0));
        pairGrid.Children.Add(LabeledField("端口", pairPort, 1));
        wirelessPanel.Children.Add(pairGrid);
        wirelessPanel.Children.Add(LabeledField("配对码", pairCode));
        wirelessPanel.Children.Add(pairButton);
        wirelessPanel.Children.Add(wirelessStatus);
        wirelessPanel.Children.Add(SectionTitle("步骤 2：连接设备"));
        wirelessPanel.Children.Add(BodyText("配对成功后，在无线调试页面查看 IP 地址和端口并连接"));
        var connectGrid = TwoColumnGrid();
        connectGrid.Children.Add(LabeledField("IP 地址", connectIp, 0));
        connectGrid.Children.Add(LabeledField("端口", connectPort, 1));
        wirelessPanel.Children.Add(connectGrid);

        var usbStatus = new TextBlock { Foreground = MutedBrush(), TextWrapping = TextWrapping.Wrap };
        var usbList = new StackPanel { Spacing = 8 };
        var scanUsb = SecondaryButton("检测 USB 设备");
        scanUsb.Click += async (_, _) =>
        {
            usbList.Children.Clear();
            var scan = await _devices.ScanUsbDevicesAsync();
            if (!scan.Result.Success)
            {
                usbStatus.Text = FailureText(scan.Result);
                return;
            }
            if (scan.Devices.Count == 0)
            {
                usbStatus.Text = "未发现 USB ADB 设备，请确认设备已授权 USB 调试。";
                return;
            }
            usbStatus.Text = $"发现 {scan.Devices.Count} 台设备。";
            foreach (var device in scan.Devices)
            {
                var choose = new RadioButton
                {
                    Content = $"{device.DisplayName}  ·  {device.DeviceId}",
                    Tag = device,
                };
                choose.Checked += (_, _) => selectedUsbDevice = device;
                usbList.Children.Add(choose);
            }
        };
        usbPanel.Children.Add(HintCard("适用于 Android 10 及以下设备。先用 USB 连接设备，然后开启无线调试端口，最后拔掉 USB 进行无线连接。"));
        usbPanel.Children.Add(SectionTitle("步骤 1：USB 连接"));
        usbPanel.Children.Add(BodyText("用 USB 线连接设备，确保已开启 USB 调试"));
        usbPanel.Children.Add(scanUsb);
        usbPanel.Children.Add(usbList);
        usbPanel.Children.Add(usbStatus);
        usbPanel.Children.Add(SectionTitle("步骤 2：开启无线调试"));
        usbPanel.Children.Add(BodyText("在 USB 连接状态下，设置设备的无线调试端口"));
        var tcpip = SecondaryButton("设置 TCP/IP 端口 (5555)");
        tcpip.Click += async (_, _) =>
        {
            var result = await _adb.TcpIpAsync(5555);
            usbStatus.Text = result.Success ? "TCP/IP 端口已设置为 5555，可以拔掉 USB 进行无线连接。" : FailureText(result);
        };
        usbPanel.Children.Add(tcpip);
        usbPanel.Children.Add(SectionTitle("步骤 3：选择设备"));

        var stack = new StackPanel { Spacing = 14 };
        stack.Width = 432;
        stack.Children.Add(new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(0, 0, 0, 2),
            Children =
            {
                new TextBlock { Text = "添加设备", FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = PrimaryTextBrush() },
                new TextBlock { Text = "选择添加方式连接你的 Android 设备", FontSize = 13, Foreground = SecondaryTextBrush() },
            },
        });
        stack.Children.Add(mode);
        stack.Children.Add(wirelessPanel);
        stack.Children.Add(usbPanel);
        stack.Children.Add(new StackPanel
        {
            Spacing = 6,
            Children =
            {
                SectionTitle("设备备注（可选）"),
                deviceNote,
            },
        });
        SelectMode("wireless");

        var dialog = Dialog(string.Empty, new ScrollViewer { Content = stack, MaxHeight = 456, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, "添加设备");
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                args.Cancel = true;
                if (selectedMode == "usb")
                {
                    if (selectedUsbDevice is null)
                    {
                        usbStatus.Text = "请先扫描并选择一台 USB ADB 设备。";
                        return;
                    }
                    _devices.SaveUsbDevice(selectedUsbDevice, deviceNote.Text.Trim());
                    Notify("设备已添加", selectedUsbDevice.DisplayName, InfoBarSeverity.Success);
                    dialog.Hide();
                    ShowDevices();
                    return;
                }

                if (!int.TryParse(connectPort.Text, out var port))
                {
                    wirelessStatus.Text = "请填写有效连接端口。";
                    return;
                }

                var addResult = await _devices.ConnectAndSaveAsync(connectIp.Text.Trim(), port, deviceNote.Text.Trim());
                if (addResult.Success || addResult.Stdout.Contains("connected", StringComparison.OrdinalIgnoreCase))
                {
                    Notify("设备已添加", $"{connectIp.Text}:{port}", InfoBarSeverity.Success);
                    dialog.Hide();
                    ShowDevices();
                }
                else
                {
                    wirelessStatus.Text = FailureText(addResult);
                }
            }
            finally
            {
                deferral.Complete();
            }
        };
        await dialog.ShowAsync();
    }

    private async Task ShowAddModelDialogAsync()
    {
        var name = RoundedTextBox("模型名称");
        var modelId = RoundedTextBox("模型标识，如 gpt-4o");
        var apiUrl = RoundedTextBox("API URL");
        var apiKey = new PasswordBox
        {
            PlaceholderText = "API 密钥",
            CornerRadius = new CornerRadius(10),
            Background = SurfaceBrush(),
            BorderBrush = BorderBrush(),
        };
        var stack = new StackPanel
        {
            Width = 432,
            Spacing = 14,
            Padding = new Thickness(0, 12, 0, 0),
        };
        stack.Children.Add(new TextBlock
        {
            Text = "添加 AI 模型",
            FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = PrimaryTextBrush(),
        });
        stack.Children.Add(new TextBlock
        {
            Text = "配置模型名称、模型标识和 API 访问参数。",
            FontSize = 13,
            Foreground = SecondaryTextBrush(),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, -8, 0, 4),
        });
        stack.Children.Add(LabeledField("模型名称", name));
        stack.Children.Add(LabeledField("模型标识", modelId));
        stack.Children.Add(LabeledField("API URL", apiUrl));
        stack.Children.Add(LabeledControl("API 密钥", apiKey));

        var dialog = Dialog(string.Empty, stack, "添加模型");
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
        if (_currentPage == "设置")
            ShowSettings();
        Notify("模型已添加", name.Text.Trim(), InfoBarSeverity.Success);
    }

    private void ToggleAiPanel()
    {
        _aiPanel.Visibility = _aiPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
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

    private static string DeviceSubtitle(DeviceModel device)
    {
        var kind = device.ConnectionKind == "usb" ? "有线 ADB" : "无线 ADB";
        var address = device.ConnectionKind == "usb" ? device.DeviceId : $"{device.IpAddress}:{device.Port}";
        return string.IsNullOrWhiteSpace(device.Note) ? $"{kind} · {address}" : $"{kind} · {device.Note}";
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
        return new StackPanel
        {
            Spacing = 16,
            Margin = new Thickness(24),
        };
    }

    private static UIElement Header(string title, string description)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = PrimaryTextBrush(),
        });
        stack.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 13,
            Foreground = SecondaryTextBrush(),
            TextWrapping = TextWrapping.Wrap,
        });
        return stack;
    }

    private static UIElement MetricCard(string title, string value, Windows.UI.Color accent, int column)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = title, Foreground = SecondaryTextBrush() });
        stack.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 32,
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
            Padding = new Thickness(20),
            BorderBrush = BorderBrush(),
            BorderThickness = new Thickness(1),
            Background = SurfaceBrush(),
            Child = child,
        };
    }

    private static Button PrimaryButton(string text)
    {
        return new Button
        {
            Content = text,
            Background = PrimaryBrush(),
            Foreground = OnPrimaryBrush(),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 8, 14, 8),
        };
    }

    private static Button SecondaryButton(string text)
    {
        return new Button
        {
            Content = text,
            Background = SurfaceBrush(),
            Foreground = PrimaryTextBrush(),
            BorderBrush = BorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 8, 14, 8),
        };
    }

    private static TextBlock BodyText(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = SecondaryTextBrush(),
            TextWrapping = TextWrapping.Wrap,
        };
    }

    private static TextBlock SectionTitle(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = PrimaryTextBrush(),
        };
    }

    private static TextBox RoundedTextBox(string placeholder)
    {
        return new TextBox
        {
            PlaceholderText = placeholder,
            CornerRadius = new CornerRadius(10),
            Background = SurfaceBrush(),
            BorderBrush = BorderBrush(),
        };
    }

    private static Grid TwoColumnGrid()
    {
        return new Grid
        {
            ColumnSpacing = 12,
            Margin = new Thickness(0, 4, 0, 0),
            ColumnDefinitions =
            {
                new ColumnDefinition(),
                new ColumnDefinition(),
            },
        };
    }

    private static UIElement LabeledField(string label, TextBox box, int column = 0)
    {
        return LabeledControl(label, box, column);
    }

    private static UIElement LabeledControl(string label, Control control, int column = 0)
    {
        var stack = new StackPanel
        {
            Spacing = 4,
        };
        stack.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = SecondaryTextBrush() });
        stack.Children.Add(control);
        Grid.SetColumn(stack, column);
        return stack;
    }

    private static Border HintCard(string text)
    {
        return new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = PrimaryLightBrush(),
            Padding = new Thickness(12),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = PrimaryBrush(),
                TextWrapping = TextWrapping.Wrap,
            },
        };
    }

    private static SolidColorBrush AppBrush() => new(ColorHelper.FromArgb(255, 15, 23, 42));
    private static SolidColorBrush GridLineBrush() => new(ColorHelper.FromArgb(24, 148, 163, 184));
    private static SolidColorBrush SurfaceBrush() => new(ColorHelper.FromArgb(232, 30, 41, 59));
    private static SolidColorBrush SurfaceAltBrush() => new(ColorHelper.FromArgb(232, 51, 65, 85));
    private static SolidColorBrush NavBrush() => new(ColorHelper.FromArgb(184, 30, 41, 59));
    private static SolidColorBrush PrimaryBrush() => new(ColorHelper.FromArgb(255, 34, 197, 94));
    private static SolidColorBrush PrimaryLightBrush() => new(ColorHelper.FromArgb(40, 34, 197, 94));
    private static SolidColorBrush OnPrimaryBrush() => new(Colors.White);
    private static SolidColorBrush PrimaryTextBrush() => new(ColorHelper.FromArgb(255, 248, 250, 252));
    private static SolidColorBrush SecondaryTextBrush() => new(ColorHelper.FromArgb(255, 203, 213, 225));
    private static SolidColorBrush MutedBrush() => new(ColorHelper.FromArgb(255, 148, 163, 184));
    private static SolidColorBrush BorderBrush() => new(ColorHelper.FromArgb(255, 51, 65, 85));
    private static SolidColorBrush BorderLightBrush() => new(ColorHelper.FromArgb(102, 148, 163, 184));
    private static SolidColorBrush TransparentBrush() => new(Colors.Transparent);
}
