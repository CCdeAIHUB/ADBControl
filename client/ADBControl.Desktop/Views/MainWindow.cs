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
        _root.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 10, 12, 16));
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        _root.RowDefinitions.Add(new RowDefinition());
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(82) });

        var titleBar = BuildTitleBar();
        Grid.SetRow(titleBar, 0);
        _root.Children.Add(titleBar);
        SetTitleBar(titleBar);

        _contentHost.Padding = new Thickness(32, 26, 32, 10);
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
            Padding = new Thickness(24, 8, 24, 18),
            ColumnDefinitions =
            {
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition(),
            },
        };

        var dock = new Border
        {
            CornerRadius = new CornerRadius(18),
            BorderBrush = BorderBrush(),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(ColorHelper.FromArgb(210, 18, 24, 33)),
            Padding = new Thickness(8),
        };
        var nav = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        nav.Children.Add(NavButton("总览", Symbol.Home));
        nav.Children.Add(NavButton("设备", Symbol.CellPhone));
        nav.Children.Add(NavButton("任务", Symbol.List));
        nav.Children.Add(NavButton("设置", Symbol.Setting));

        var divider = new Border
        {
            Width = 1,
            Height = 28,
            Margin = new Thickness(4, 0, 4, 0),
            Background = BorderBrush(),
        };
        nav.Children.Add(divider);

        var ai = IconTextButton("AI", Symbol.Message);
        ai.Click += (_, _) => ToggleAiPanel();
        nav.Children.Add(ai);

        dock.Child = nav;
        Grid.SetColumn(dock, 1);
        shell.Children.Add(dock);
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
            MinWidth = 70,
            Height = 44,
            Padding = new Thickness(12, 0, 12, 0),
            BorderThickness = new Thickness(1),
            BorderBrush = BorderBrush(),
            Background = new SolidColorBrush(ColorHelper.FromArgb(80, 31, 41, 55)),
            CornerRadius = new CornerRadius(12),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new SymbolIcon(symbol),
                    new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center },
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
                ? ColorHelper.FromArgb(255, 16, 185, 129)
                : ColorHelper.FromArgb(80, 31, 41, 55));
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
        var header = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        header.Children.Add(Header("设备", "点击设备卡片进入详情页，或添加有线/无线 ADB 设备。"));
        var add = PrimaryButton("添加设备");
        add.Click += async (_, _) => await ShowAddDeviceDialogAsync();
        Grid.SetColumn(add, 1);
        header.Children.Add(add);
        panel.Children.Add(header);

        if (_devices.Devices.Count == 0)
        {
            panel.Children.Add(Card(new TextBlock { Text = "还没有添加设备。无线设备需要 IP、配对端口、配对码和连接端口；有线设备可直接扫描 USB ADB。", TextWrapping = TextWrapping.Wrap }));
        }
        else
        {
            var list = new StackPanel { Spacing = 10 };
            foreach (var device in _devices.Devices)
                list.Children.Add(DeviceCard(device));
            panel.Children.Add(list);
        }

        _contentHost.Children.Add(new ScrollViewer { Content = panel });
    }

    private UIElement DeviceCard(DeviceModel device)
    {
        var root = new Grid
        {
            ColumnSpacing = 16,
            ColumnDefinitions =
            {
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        var info = new StackPanel { Spacing = 5 };
        info.Children.Add(new TextBlock
        {
            Text = device.DisplayName,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 16,
        });
        info.Children.Add(new TextBlock
        {
            Text = DeviceSubtitle(device),
            Foreground = MutedBrush(),
            FontSize = 12,
        });
        root.Children.Add(info);
        var status = new TextBlock
        {
            Text = device.IsConnected ? "已连接" : "未连接",
            Foreground = new SolidColorBrush(device.IsConnected ? Colors.MediumSeaGreen : Colors.Orange),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(status, 1);
        root.Children.Add(status);

        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Content = Card(root),
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
        panel.MaxWidth = 760;
        panel.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(Header("设置", "应用配置和 AI 模型管理。"));
        panel.Children.Add(SettingsCard());
        _contentHost.Children.Add(new ScrollViewer { Content = panel });
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
        _aiPanel.Width = 500;
        _aiPanel.Margin = new Thickness(0, 10, 24, 96);
        _aiPanel.HorizontalAlignment = HorizontalAlignment.Right;
        _aiPanel.VerticalAlignment = VerticalAlignment.Stretch;
        _aiPanel.CornerRadius = new CornerRadius(16);
        _aiPanel.BorderBrush = BorderBrush();
        _aiPanel.BorderThickness = new Thickness(1);
        _aiPanel.Background = new SolidColorBrush(ColorHelper.FromArgb(245, 14, 20, 29));
        Canvas.SetZIndex(_aiPanel, 20);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new Grid
        {
            Padding = new Thickness(16, 12, 12, 12),
            ColumnDefinitions =
            {
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        top.Children.Add(new TextBlock
        {
            Text = "AI Agent",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        var close = new Button
        {
            Content = new SymbolIcon(Symbol.Cancel),
            Width = 34,
            Height = 34,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(8),
        };
        close.Click += (_, _) => _aiPanel.Visibility = Visibility.Collapsed;
        Grid.SetColumn(close, 1);
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
            Padding = new Thickness(16, 4, 16, 8),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetRow(messageScroller, 1);
        root.Children.Add(messageScroller);

        var inputArea = new Grid { Padding = new Thickness(12), RowSpacing = 8 };
        inputArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        inputArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        inputArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _pendingAttachmentList.Spacing = 8;
        inputArea.Children.Add(_pendingAttachmentList);

        _aiInput.PlaceholderText = "要求后续变更...";
        _aiInput.AcceptsReturn = true;
        _aiInput.MinHeight = 82;
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
        var tools = new Grid { ColumnSpacing = 8 };
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tools.ColumnDefinitions.Add(new ColumnDefinition());
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var attach = new Button
        {
            Content = new SymbolIcon(Symbol.Attach),
            Width = 38,
            Height = 34,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(8),
        };
        attach.Click += async (_, _) => await PickAttachmentsAsync();
        tools.Children.Add(attach);

        _permissionCombo.MinWidth = 132;
        _permissionCombo.Items.Add(PermissionItem("只读", Symbol.View, "只允许读取当前项目上下文"));
        _permissionCombo.Items.Add(PermissionItem("询问", Symbol.Help, "执行敏感操作前询问"));
        _permissionCombo.Items.Add(PermissionItem("允许", Symbol.Accept, "允许执行本地开发操作"));
        _permissionCombo.SelectedIndex = 1;
        Grid.SetColumn(_permissionCombo, 1);
        tools.Children.Add(_permissionCombo);

        _modelCombo.PlaceholderText = "选择模型";
        _modelCombo.MinWidth = 138;
        RefreshModelCombo();
        Grid.SetColumn(_modelCombo, 3);
        tools.Children.Add(_modelCombo);

        var send = PrimaryButton("发送");
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
            Background = new SolidColorBrush(ColorHelper.FromArgb(180, 31, 41, 55)),
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
            MaxWidth = 390,
            Spacing = 5,
        };
        panel.Children.Add(new TextBlock
        {
            Text = message.Role,
            FontSize = 11,
            Foreground = message.IsUser ? new SolidColorBrush(Colors.LightGray) : new SolidColorBrush(Colors.MediumSeaGreen),
            HorizontalAlignment = message.IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        });

        var body = new StackPanel { Spacing = 8 };
        if (!string.IsNullOrWhiteSpace(message.Text))
        {
            body.Children.Add(new TextBlock
            {
                Text = message.Text,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        foreach (var attachment in message.Attachments)
            body.Children.Add(AttachmentChip(attachment, false));

        panel.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 9, 12, 9),
            Background = message.IsUser
                ? new SolidColorBrush(ColorHelper.FromArgb(255, 16, 185, 129))
                : new SolidColorBrush(ColorHelper.FromArgb(255, 38, 48, 62)),
            Child = body,
        });
        return panel;
    }

    private async Task ShowAddDeviceDialogAsync()
    {
        var wirelessPanel = new StackPanel { Spacing = 10 };
        var usbPanel = new StackPanel { Spacing = 10, Visibility = Visibility.Collapsed };
        var selectedMode = "wireless";
        DeviceModel? selectedUsbDevice = null;

        var mode = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition(),
                new ColumnDefinition(),
            },
        };
        var wirelessMode = PrimaryButton("无线 ADB");
        var usbMode = new Button { Content = "有线 ADB", Padding = new Thickness(14, 8, 14, 8), CornerRadius = new CornerRadius(8) };
        void SelectMode(string value)
        {
            selectedMode = value;
            var isWireless = value == "wireless";
            wirelessPanel.Visibility = isWireless ? Visibility.Visible : Visibility.Collapsed;
            usbPanel.Visibility = isWireless ? Visibility.Collapsed : Visibility.Visible;
            wirelessMode.Background = new SolidColorBrush(isWireless ? ColorHelper.FromArgb(255, 16, 185, 129) : ColorHelper.FromArgb(80, 31, 41, 55));
            usbMode.Background = new SolidColorBrush(!isWireless ? ColorHelper.FromArgb(255, 16, 185, 129) : ColorHelper.FromArgb(80, 31, 41, 55));
            wirelessMode.Foreground = new SolidColorBrush(Colors.White);
            usbMode.Foreground = new SolidColorBrush(Colors.White);
        }

        wirelessMode.Click += (_, _) => SelectMode("wireless");
        usbMode.Click += (_, _) => SelectMode("usb");
        Grid.SetColumn(usbMode, 1);
        mode.Children.Add(wirelessMode);
        mode.Children.Add(usbMode);

        var ip = new TextBox { PlaceholderText = "192.168.1.100" };
        var pairPort = new TextBox { PlaceholderText = "配对端口" };
        var pairCode = new TextBox { PlaceholderText = "配对码" };
        var connectPort = new TextBox { PlaceholderText = "连接端口" };
        var wirelessNote = new TextBox { PlaceholderText = "设备备注" };
        var wirelessStatus = new TextBlock { Foreground = MutedBrush(), TextWrapping = TextWrapping.Wrap };
        var pairButton = new Button { Content = "开始配对" };
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
        wirelessPanel.Children.Add(new TextBlock { Text = "无线 ADB（Android 11+）", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        wirelessPanel.Children.Add(ip);
        wirelessPanel.Children.Add(pairPort);
        wirelessPanel.Children.Add(pairCode);
        wirelessPanel.Children.Add(pairButton);
        wirelessPanel.Children.Add(connectPort);
        wirelessPanel.Children.Add(wirelessNote);
        wirelessPanel.Children.Add(wirelessStatus);

        var usbStatus = new TextBlock { Foreground = MutedBrush(), TextWrapping = TextWrapping.Wrap };
        var usbList = new StackPanel { Spacing = 8 };
        var usbNote = new TextBox { PlaceholderText = "设备备注" };
        var scanUsb = new Button { Content = "扫描 USB ADB 设备" };
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
        usbPanel.Children.Add(new TextBlock { Text = "有线 ADB（USB 调试）", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        usbPanel.Children.Add(scanUsb);
        usbPanel.Children.Add(usbList);
        usbPanel.Children.Add(usbNote);
        usbPanel.Children.Add(usbStatus);

        var stack = new StackPanel { Spacing = 14 };
        stack.Children.Add(mode);
        stack.Children.Add(wirelessPanel);
        stack.Children.Add(usbPanel);
        SelectMode("wireless");

        var dialog = Dialog("添加设备", new ScrollViewer { Content = stack, MaxHeight = 520 }, "添加设备");
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
                    _devices.SaveUsbDevice(selectedUsbDevice, usbNote.Text.Trim());
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

                var addResult = await _devices.ConnectAndSaveAsync(ip.Text.Trim(), port, wirelessNote.Text.Trim());
                if (addResult.Success || addResult.Stdout.Contains("connected", StringComparison.OrdinalIgnoreCase))
                {
                    Notify("设备已添加", $"{ip.Text}:{port}", InfoBarSeverity.Success);
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
            TextWrapping = TextWrapping.Wrap,
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
