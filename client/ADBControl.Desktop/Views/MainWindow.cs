using ADBControl.Desktop.Models;
using ADBControl.Desktop.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;
using WinRT.Interop;

namespace ADBControl.Desktop.Views;

public sealed class MainWindow : Window
{
    private readonly SettingsService _settings = new();
    private readonly AdbService _adb = new();
    private readonly AiService _ai = new();
    private readonly AiAgentToolService _aiTools;
    private readonly DeviceService _devices;
    private readonly CompanionAppService _companion;

    private readonly Grid _root = new();
    private readonly Grid _contentHost = new();
    private readonly Border _contentFrame = new();
    private readonly Border _aiPanel = new();
    private readonly StackPanel _messageList = new();
    private readonly StackPanel _pendingAttachmentList = new();
    private readonly TextBox _aiInput = new();
    private readonly ComboBox _modelCombo = new();
    private readonly ComboBox _permissionCombo = new();
    private readonly Button _permissionSelector = new();
    private readonly Button _modelSelector = new();
    private readonly InfoBar _info = new();
    private readonly List<AiAttachment> _pendingAttachments = new();
    private readonly List<AiConversationMessage> _aiConversation = new();
    private readonly List<Button> _navButtons = new();

    private ScrollViewer? _activePageScroller;
    private IntPtr _windowHandle;
    private readonly Dictionary<IntPtr, IntPtr> _hookedWndProcs = new();
    private WndProcDelegate? _wndProcDelegate;
    private GridBackground? _background;
    private Border? _navDock;
    private Border? _deviceDock;
    private Border? _aiDock;
    private Button? _aiButton;
    private Button? _deviceNavButton;
    private DispatcherTimer? _devicePreviewTimer;
    private DispatcherTimer? _deviceListPreviewTimer;
    private readonly Dictionary<string, (Image Image, TextBlock Status)> _deviceCardPreviews = new();
    private readonly Dictionary<string, string> _previewFrameHashes = new();
    private readonly Dictionary<string, (int Width, int Height)> _previewFrameSizes = new();
    private PreviewTouchState? _previewTouch;
    private DeviceModel? _currentDetailDevice;
    private DeviceModel? _pinnedDeviceNavDevice;
    private bool _deviceListPreviewEnabled;
    private int _deviceListPreviewSeconds = 3;
    private static bool s_darkTheme = true;
    private static bool s_followSystemTheme;
    private string _selectedAiPermission = "请求批准";
    private AiModelSettings? _selectedAiModel;
    private Button? _aiSendButton;
    private Border? _aiSendCircle;
    private FontIcon? _aiSendIcon;
    private Button? _aiScrollBottomButton;
    private Storyboard? _aiThinkingStoryboard;
    private ScrollViewer? _aiMessageScroller;
    private CancellationTokenSource? _aiCancellation;
    private bool _aiIsSending;
    private bool _aiInputKeyHandlerAttached;
    private bool _aiScrollBottomButtonVisible;
    private bool _aiStreamFlushQueued;
    private bool _aiStreamHasAnswer;
    private readonly object _aiStreamLock = new();
    private readonly StringBuilder _pendingAiAnswerDelta = new();
    private readonly StringBuilder _pendingAiThinkingDelta = new();
    private string _currentPage = "总览";

    private const int MinimumWindowWidthForDeviceDetail = 1180;
    private const int MinimumWindowHeightForDeviceTabs = 720;

    private sealed record NavButtonInfo(string Text, Symbol Symbol, bool UsesDeviceGlyph = false, bool IsTablet = false);

    private sealed record PreviewTouchState(DeviceModel Device, Image Image, Point StartPoint, DateTimeOffset StartedAt, uint PointerId);

    private sealed record AiStreamingMessageUi(
        AiChatMessage Message,
        TextBlock AnswerText,
        Border ThinkingShell,
        TextBlock ThinkingText,
        TextBlock ThinkingSummary,
        Border ThinkingBody,
        TextBlock ThinkingArrow,
        TextBlock DurationText);

    public MainWindow()
    {
        _settings.Load();
        _devices = new DeviceService(_settings, _adb);
        _companion = new CompanionAppService(_adb);
        _aiTools = new AiAgentToolService(_adb, _companion);

        Title = "ADBControl";
        ExtendsContentIntoTitleBar = true;
        Content = _root;
        BuildShell();
        InstallNativeWheelHook();
        _root.Loaded += (_, _) =>
        {
            RefreshNativeWheelHooks();
            PolishRoundedEdges(_root);
        };
        ApplyTitleBarTheme();
        Navigate("总览");
        Closed += (_, _) => RestoreNativeWheelHook();
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
                new ColumnDefinition { Width = GridLength.Auto },
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
        _root.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnRootPointerWheelChanged), true);
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        _root.RowDefinitions.Add(new RowDefinition());
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(88) });

        _background = new GridBackground
        {
            Fill = AppBrush(),
            GridLineBrush = GridLineBrush(),
            GridSize = 20,
        };
        Grid.SetRowSpan(_background, 3);
        _root.Children.Add(_background);

        var titleBar = BuildTitleBar();
        Grid.SetRow(titleBar, 0);
        _root.Children.Add(titleBar);
        SetTitleBar(titleBar);

        _contentFrame.Margin = new Thickness(10, 4, 10, 8);
        _contentFrame.CornerRadius = new CornerRadius(16);
        _contentFrame.Background = TransparentBrush();
        _contentFrame.Child = _contentHost;
        _contentHost.Padding = new Thickness(0);
        Grid.SetRow(_contentFrame, 1);
        _root.Children.Add(_contentFrame);

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
            Padding = new Thickness(12, 2, 12, 12),
            ColumnDefinitions =
            {
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition(),
            },
        };

        _navDock = new Border
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

        _navDock.Child = nav;
        Grid.SetColumn(_navDock, 1);
        shell.Children.Add(_navDock);

        _deviceDock = new Border
        {
            CornerRadius = new CornerRadius(20),
            BorderBrush = BorderLightBrush(),
            BorderThickness = new Thickness(1),
            Background = NavBrush(),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(8, 0, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        _deviceNavButton = IconTextButton("设备", DeviceSolarIcon(false));
        _deviceNavButton.DataContext = new NavButtonInfo("设备", Symbol.CellPhone, true);
        _deviceDock.Child = _deviceNavButton;
        Grid.SetColumn(_deviceDock, 2);
        shell.Children.Add(_deviceDock);

        _aiDock = new Border
        {
            CornerRadius = new CornerRadius(20),
            BorderBrush = BorderLightBrush(),
            BorderThickness = new Thickness(1),
            Background = NavBrush(),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(8, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _aiButton = IconTextButton("AI", Symbol.Message);
        _aiButton.DataContext = new NavButtonInfo("AI", Symbol.Message);
        _aiButton.Click += (_, _) => ToggleAiPanel();
        _aiDock.Child = _aiButton;
        Grid.SetColumn(_aiDock, 3);
        shell.Children.Add(_aiDock);
        return shell;
    }

    private Button NavButton(string text, Symbol symbol)
    {
        var button = IconTextButton(text, symbol);
        button.Tag = text;
        button.DataContext = new NavButtonInfo(text, symbol);
        button.Click += (_, _) =>
        {
            Navigate(text);
            _ = DispatcherQueue.TryEnqueue(() => ApplyNavButtonState(button, true));
        };
        _navButtons.Add(button);
        return button;
    }

    private static Button IconTextButton(string text, Symbol symbol)
    {
        return IconTextButton(text, NavIcon(symbol, false, SecondaryTextBrush()));
    }

    private static Button IconTextButton(string text, UIElement icon)
    {
        var button = new Button
        {
            Width = 56,
            Height = 56,
            MinWidth = 56,
            MinHeight = 56,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = TransparentBrush(),
            Foreground = SecondaryTextBrush(),
            CornerRadius = new CornerRadius(16),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    icon,
                    new TextBlock { Text = text, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center },
                },
            },
        };
        ApplyButtonResources(button, TransparentBrush(), SecondaryTextBrush(), HoverBrush(), SurfaceAltBrush(), BorderLightBrush(), new Thickness(0));
        button.PointerEntered += (_, _) => ReapplyNavPointerState(button);
        button.PointerMoved += (_, _) => ReapplyNavPointerState(button);
        button.PointerExited += (_, _) => ReapplyNavPointerState(button);
        button.PointerReleased += (_, _) => ReapplyNavPointerState(button);
        return button;
    }

    private static UIElement NavIcon(Symbol symbol, bool active, Brush foreground)
    {
        var root = new Grid
        {
            Width = 22,
            Height = 22,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        root.Children.Add(new SymbolIcon(symbol)
        {
            Width = 22,
            Height = 22,
            Foreground = foreground,
        });
        if (active)
        {
            root.Children.Add(new SymbolIcon(symbol)
            {
                Width = 22,
                Height = 22,
                Foreground = foreground,
                Opacity = 0.92,
                Margin = new Thickness(0.7, 0, 0, 0),
            });
        }
        return root;
    }

    private static UIElement DeviceSolarIcon(bool tablet, Brush? foreground = null, bool active = false)
    {
        var brush = foreground ?? SecondaryTextBrush();
        var shell = new Grid
        {
            Width = 22,
            Height = 22,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var outline = new Border
        {
            Tag = "solar-icon",
            Width = tablet ? 18 : 12,
            Height = 20,
            CornerRadius = new CornerRadius(tablet ? 3 : 4),
            BorderBrush = brush,
            BorderThickness = new Thickness(active ? 2.4 : 1.6),
            Background = active ? brush : TransparentBrush(),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var inner = new Grid();
        inner.Children.Add(new Border
        {
            Tag = "solar-icon",
            Width = tablet ? 4 : 3,
            Height = 1.5,
            CornerRadius = new CornerRadius(1),
            Background = active ? OnPrimaryBrush() : brush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 2.4),
        });
        outline.Child = inner;
        shell.Children.Add(outline);
        return shell;
    }

    private void Navigate(string page)
    {
        StopDevicePreview();
        StopDeviceListPreview();
        _currentDetailDevice = null;
        UpdateDeviceNav(_pinnedDeviceNavDevice);
        _currentPage = page;
        _aiPanel.Visibility = Visibility.Collapsed;
        _root.Background = AppBrush();
        foreach (var button in _navButtons)
        {
            var active = string.Equals(button.Tag as string, page, StringComparison.Ordinal);
            ApplyNavButtonState(button, active);
        }
        if (_aiButton is not null)
            ApplyNavButtonState(_aiButton, false);

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
        _ = DispatcherQueue.TryEnqueue(() => PolishRoundedEdges(_root));
    }

    private static void ApplyNavButtonState(Button button, bool active)
    {
        button.Resources["NavActive"] = active;
        var foreground = active ? OnPrimaryBrush() : SecondaryTextBrush();
        button.Background = TransparentBrush();
        button.Foreground = foreground;
        ApplyButtonResources(
            button,
            TransparentBrush(),
            foreground,
            TransparentBrush(),
            TransparentBrush(),
            TransparentBrush(),
            new Thickness(0));
        if (button.DataContext is NavButtonInfo info)
            button.Content = NavButtonContent(info, active, foreground);
        SetTaggedBorder(button.Content as UIElement, "selection-surface", active ? PrimaryBrush() : TransparentBrush());
        if (button.Content is UIElement content)
            ApplyForeground(content, foreground);
        button.UpdateLayout();
    }

    private static void ReapplyNavPointerState(Button button)
    {
        if (button.Resources.TryGetValue("NavActive", out var value) && value is true)
            button.DispatcherQueue.TryEnqueue(() => ApplyNavButtonState(button, true));
    }

    private static UIElement NavButtonContent(NavButtonInfo info, bool active, Brush foreground)
    {
        var icon = info.UsesDeviceGlyph
            ? DeviceSolarIcon(info.IsTablet, foreground, active)
            : NavIcon(info.Symbol, active, foreground);
        return new Grid
        {
            Width = 56,
            Height = 56,
            Children =
            {
                new Border
                {
                    Tag = "selection-surface",
                    CornerRadius = new CornerRadius(16),
                    Background = active ? PrimaryBrush() : TransparentBrush(),
                },
                new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Spacing = 4,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        icon,
                        new TextBlock
                        {
                            Text = info.Text,
                            FontSize = 10,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            MaxWidth = 50,
                        },
                    },
                },
            },
        };
    }

    private static void SetTaggedBorder(UIElement? element, object tag, Brush background)
    {
        switch (element)
        {
            case Border border when Equals(border.Tag, tag):
                border.Background = background;
                if (HasVisibleBrush(background))
                {
                    border.BorderThickness = new Thickness(1);
                    border.BorderBrush = RoundedEdgeBrush();
                }
                else
                {
                    border.BorderThickness = new Thickness(0);
                    border.BorderBrush = TransparentBrush();
                }
                break;
            case Border border when border.Child is not null:
                SetTaggedBorder(border.Child, tag, background);
                break;
            case Panel panel:
                foreach (var child in panel.Children)
                    SetTaggedBorder(child, tag, background);
                break;
            case ContentControl control when control.Content is UIElement child:
                SetTaggedBorder(child, tag, background);
                break;
        }
    }

    private static void ApplyForeground(UIElement element, Brush foreground)
    {
        switch (element)
        {
            case TextBlock text:
                text.Foreground = foreground;
                break;
            case IconElement icon:
                icon.Foreground = foreground;
                break;
            case Panel panel:
                foreach (var child in panel.Children)
                    ApplyForeground(child, foreground);
                break;
            case Border border when border.Child is not null:
                if (Equals(border.Tag, "solar-icon"))
                {
                    border.BorderBrush = foreground;
                    if (border.Background is not null)
                        border.Background = foreground;
                }
                ApplyForeground(border.Child, foreground);
                break;
            case Border border:
                if (Equals(border.Tag, "solar-icon"))
                {
                    border.BorderBrush = foreground;
                    if (border.Background is not null)
                        border.Background = foreground;
                }
                break;
        }
    }

    private void ShowOverview()
    {
        _activePageScroller = null;
        _contentHost.Children.Clear();
        _deviceCardPreviews.Clear();
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

    private void ShowDevices(bool refreshState = true)
    {
        _contentHost.Children.Clear();
        _deviceCardPreviews.Clear();
        var panel = PageStack();
        var toolbar = new Grid
        {
            ColumnSpacing = 10,
            ColumnDefinitions =
            {
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        var filters = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
        };
        var search = RoundedTextBox("搜索设备备注、信息、IP...");
        search.Width = 280;
        filters.Children.Add(search);
        filters.Children.Add(SecondaryButton("仅已连接"));
        filters.Children.Add(SecondaryButton("仅伴侣 APK"));
        toolbar.Children.Add(filters);

        var previewControls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var previewSeconds = RoundedTextBox("3");
        previewSeconds.Text = _deviceListPreviewSeconds.ToString();
        previewSeconds.Width = 72;
        previewSeconds.InputScope = new InputScope
        {
            Names = { new InputScopeName(InputScopeNameValue.Number) },
        };
        previewSeconds.BeforeTextChanging += (_, args) =>
        {
            args.Cancel = args.NewText.Any(ch => !char.IsDigit(ch));
        };
        previewControls.Children.Add(previewSeconds);
        var previewToggle = SecondaryButton(_deviceListPreviewEnabled ? "关闭预览" : "开启预览");
        previewToggle.Click += (_, _) =>
        {
            if (int.TryParse(previewSeconds.Text, out var seconds) && seconds > 0)
                _deviceListPreviewSeconds = seconds;
            _deviceListPreviewEnabled = !_deviceListPreviewEnabled;
            ShowDevices();
        };
        previewControls.Children.Add(previewToggle);
        Grid.SetColumn(previewControls, 1);
        toolbar.Children.Add(previewControls);

        var add = PrimaryButton("+ 添加设备");
        add.Click += async (_, _) => await ShowAddDeviceDialogAsync();
        Grid.SetColumn(add, 2);
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
            if (_deviceListPreviewEnabled)
                StartDeviceListPreview();
            else
                StopDeviceListPreview();
        }

        _contentHost.Children.Add(PageScroller(panel));
        if (refreshState)
            _ = RefreshDeviceStatesAndReloadAsync();
    }

    private async Task RefreshDeviceStatesAndReloadAsync()
    {
        var result = await _devices.RefreshConnectivityAsync();
        if (!result.Success)
            Notify("设备状态刷新失败", FailureText(result), InfoBarSeverity.Warning);

        if (_currentPage == "设备" && _currentDetailDevice is null)
            ShowDevices(false);
    }

    private void DeleteDevice(DeviceModel device)
    {
        var wasCurrentDetail = _currentDetailDevice?.DeviceId == device.DeviceId;
        var wasPinned = _pinnedDeviceNavDevice?.DeviceId == device.DeviceId;
        _devices.Remove(device);
        StopDevicePreview();
        StopDeviceListPreview();
        if (wasCurrentDetail)
            _currentDetailDevice = null;
        if (wasPinned)
        {
            _pinnedDeviceNavDevice = null;
            UpdateDeviceNav(null);
        }

        Notify("设备已删除", device.DisplayName, InfoBarSeverity.Success);
        ShowDevices(false);
    }

    private UIElement DeviceCard(DeviceModel device)
    {
        var root = new StackPanel { Spacing = 12 };
        var head = new Grid
        {
            ColumnSpacing = 10,
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
            Child = DeviceSolarIcon(IsTabletDevice(device), PrimaryBrush()),
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
        var delete = DangerButton("删除");
        delete.MinHeight = 30;
        delete.Padding = new Thickness(10, 5, 10, 5);
        delete.Click += (_, args) =>
        {
            DeleteDevice(device);
        };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { status, delete },
        };
        Grid.SetColumn(actions, 2);
        head.Children.Add(actions);
        root.Children.Add(head);

        if (_deviceListPreviewEnabled && device.IsConnected)
        {
            var previewImage = new Image
            {
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            var previewStatus = new TextBlock
            {
                Text = "等待截图...",
                FontSize = 12,
                Foreground = SecondaryTextBrush(),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var preview = new Border
            {
                Width = 132,
                Height = 286,
                CornerRadius = new CornerRadius(18),
                BorderBrush = BorderLightBrush(),
                BorderThickness = new Thickness(1),
                Background = ShellBrush(),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new Grid
                {
                    Children =
                    {
                        previewImage,
                        previewStatus,
                    },
                },
            };
            root.Children.Add(preview);
            _deviceCardPreviews[device.DeviceId] = (previewImage, previewStatus);
        }

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

        var card = new Border
        {
            Width = 320,
            Margin = new Thickness(0, 0, 16, 16),
            CornerRadius = new CornerRadius(20),
            BorderBrush = BorderBrush(),
            BorderThickness = new Thickness(1),
            Background = SurfaceBrush(),
            Padding = new Thickness(20),
            Child = root,
        };
        card.PointerEntered += (_, _) =>
        {
            card.BorderBrush = TransparentBrush();
            card.Shadow = new ThemeShadow();
            card.Translation = new System.Numerics.Vector3(0, 0, 24);
        };
        card.PointerExited += (_, _) =>
        {
            card.BorderBrush = BorderBrush();
            card.Shadow = null;
            card.Translation = System.Numerics.Vector3.Zero;
        };
        card.Tapped += (_, _) => ShowDeviceDetail(device);
        return card;
    }

    private void ShowDeviceDetail(DeviceModel device)
    {
        _activePageScroller = null;
        StopDevicePreview();
        StopDeviceListPreview();
        _currentDetailDevice = device;
        _pinnedDeviceNavDevice = device;
        UpdateDeviceNav(device);
        foreach (var button in _navButtons)
            ApplyNavButtonState(button, false);
        _contentHost.Children.Clear();
        var panel = new Grid
        {
            Margin = PageMargin(),
            RowSpacing = 12,
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition(),
            },
        };
        var headerRow = new Grid
        {
            ColumnSpacing = 14,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        var back = IconSquareButton("‹", 56);
        back.HorizontalAlignment = HorizontalAlignment.Left;
        back.Click += (_, _) => Navigate("设备");
        headerRow.Children.Add(back);
        var detailHeader = (FrameworkElement)Header(device.DisplayName, device.ConnectionKind == "usb" ? "有线 ADB 设备详情" : "无线 ADB 设备详情");
        Grid.SetColumn(detailHeader, 1);
        headerRow.Children.Add(detailHeader);
        var statusBadges = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                ConnectionBadge("ADB连接", device.IsConnected),
                ConnectionBadge("APP连接", device.IsCompanionConnected),
            },
        };
        Grid.SetColumn(statusBadges, 2);
        headerRow.Children.Add(statusBadges);
        var companionInstall = SecondaryButton("安装伴侣 App");
        companionInstall.VerticalAlignment = VerticalAlignment.Center;
        companionInstall.Visibility = Visibility.Collapsed;
        companionInstall.Click += async (_, _) => await InstallCompanionFromDetailAsync(device, companionInstall);
        Grid.SetColumn(companionInstall, 3);
        headerRow.Children.Add(companionInstall);
        var disconnect = SecondaryButton("断开连接");
        disconnect.VerticalAlignment = VerticalAlignment.Center;
        disconnect.Click += async (_, _) => await DisconnectDeviceAsync(device);
        Grid.SetColumn(disconnect, 4);
        headerRow.Children.Add(disconnect);
        var detailDelete = DangerButton("删除设备");
        detailDelete.VerticalAlignment = VerticalAlignment.Center;
        detailDelete.Click += (_, _) => DeleteDevice(device);
        Grid.SetColumn(detailDelete, 5);
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerRow.Children.Add(detailDelete);
        panel.Children.Add(headerRow);

        var layout = new Grid
        {
            ColumnSpacing = 16,
            RowSpacing = 16,
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(400) },
            },
        };

        var previewImage = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        var previewStatus = new TextBlock
        {
            Text = "正在获取设备截图...",
            Foreground = SecondaryTextBrush(),
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var previewLayer = new Grid
        {
            MinHeight = 0,
            Background = TransparentBrush(),
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children =
            {
                previewImage,
                previewStatus,
            },
        };
        AttachPreviewTouchHandlers(previewLayer, previewImage, device);
        var previewControls = BuildPreviewControls(device, previewImage, previewStatus);
        var previewShell = new Grid
        {
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ColumnSpacing = 12,
            ColumnDefinitions =
            {
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto },
            },
            Children =
            {
                previewLayer,
                previewControls,
            },
        };
        Grid.SetColumn(previewLayer, 0);
        Grid.SetColumn(previewControls, 1);
        var previewContent = new Grid
        {
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Stretch,
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition(),
            },
            Children =
            {
                new TextBlock
                {
                    Text = "设备预览",
                    FontSize = 18,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = PrimaryTextBrush(),
                    Margin = new Thickness(0, 0, 0, 12),
                },
                previewShell,
            },
        };
        var previewCard = Card(previewContent);
        previewCard.VerticalAlignment = VerticalAlignment.Stretch;
        previewCard.MinHeight = 0;
        Grid.SetRow(previewShell, 1);
        Grid.SetColumn(previewCard, 0);
        layout.Children.Add(previewCard);

        var toolsCard = Card(BuildDeviceToolTabs(device));
        toolsCard.VerticalAlignment = VerticalAlignment.Stretch;
        toolsCard.MinHeight = 0;
        Grid.SetColumn(toolsCard, 1);
        layout.Children.Add(toolsCard);
        Grid.SetRow(layout, 1);
        panel.Children.Add(layout);
        _contentHost.Children.Add(panel);
        StartDevicePreview(device, previewImage, previewStatus);
        _ = RefreshCompanionInstallStateAsync(device, companionInstall);
    }

    private FrameworkElement BuildPreviewControls(DeviceModel device, Image previewImage, TextBlock previewStatus)
    {
        var controls = new StackPanel
        {
            Spacing = 8,
            Width = 48,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        controls.Children.Add(PreviewControlButton("⏻", "电源", async () => await _adb.ShellAsync(device.DeviceId, "input keyevent KEYCODE_POWER"), device));
        controls.Children.Add(PreviewControlButton("+", "音量加", async () => await _adb.ShellAsync(device.DeviceId, "input keyevent KEYCODE_VOLUME_UP"), device));
        controls.Children.Add(PreviewControlButton("-", "音量键", async () => await _adb.ShellAsync(device.DeviceId, "input keyevent KEYCODE_VOLUME_DOWN"), device));
        controls.Children.Add(PreviewControlButton("□", "截图", async () => await RefreshPreviewOnceAsync(device, previewImage, previewStatus), device, showSuccess: false));
        controls.Children.Add(new Border { Height = 18, Background = TransparentBrush() });
        controls.Children.Add(PreviewControlButton("‹", "返回键", async () => await _adb.ShellAsync(device.DeviceId, "input keyevent KEYCODE_BACK"), device));
        controls.Children.Add(PreviewControlButton("⌂", "主页键", async () => await _adb.ShellAsync(device.DeviceId, "input keyevent KEYCODE_HOME"), device));
        controls.Children.Add(PreviewControlButton("≡", "多任务", async () => await _adb.ShellAsync(device.DeviceId, "input keyevent KEYCODE_APP_SWITCH"), device));
        return controls;
    }

    private Button PreviewControlButton(string glyph, string label, Func<Task<AdbCommandResult>> action, DeviceModel device, bool showSuccess = true)
    {
        var button = new Button
        {
            Width = 44,
            Height = 44,
            MinWidth = 44,
            MinHeight = 44,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(14),
            Content = new TextBlock
            {
                Text = glyph,
                FontSize = 18,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                LineHeight = 22,
            },
        };
        ApplyButtonResources(button, SurfaceAltBrush(), PrimaryTextBrush(), HoverBrush(), SurfaceBrush(), BorderLightBrush(), new Thickness(1));
        ToolTipService.SetToolTip(button, label);
        button.Click += async (_, _) =>
        {
            if (!await EnsureDeviceReadyAsync(device))
                return;

            try
            {
                var result = await action();
                if (!result.Success)
                    device.IsConnected = false;
                if (showSuccess || !result.Success)
                    Notify(result.Success ? "操作已执行" : "操作失败", result.Success ? label : FormatCommandResult(result), result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            }
            catch (Exception ex)
            {
                device.IsConnected = false;
                Notify("操作失败", ex.Message, InfoBarSeverity.Error);
            }
        };
        return button;
    }

    private async Task<AdbCommandResult> RefreshPreviewOnceAsync(DeviceModel device, Image previewImage, TextBlock status)
    {
        try
        {
            status.Text = "正在刷新截图...";
            status.Visibility = Visibility.Visible;
            var png = await _adb.ScreencapPngAsync(device.DeviceId);
            await ApplyPreviewFrameAsync(previewImage, device.DeviceId, png, "detail", force: true);
            status.Visibility = Visibility.Collapsed;
            return new AdbCommandResult(0, "截图已刷新。", string.Empty);
        }
        catch (Exception ex)
        {
            device.IsConnected = false;
            status.Text = $"截图失败：{ex.Message}";
            status.Visibility = Visibility.Visible;
            return new AdbCommandResult(1, string.Empty, ex.Message);
        }
    }

    private static UIElement ConnectionBadge(string label, bool connected)
    {
        var accent = connected ? Colors.MediumSeaGreen : Colors.Orange;
        var brush = new SolidColorBrush(accent);
        return new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderBrush = brush,
            BorderThickness = new Thickness(1),
            Background = connected ? PrimaryLightBrush() : SurfaceAltBrush(),
            Padding = new Thickness(10, 5, 10, 5),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new Border
                    {
                        Width = 7,
                        Height = 7,
                        CornerRadius = new CornerRadius(3.5),
                        Background = brush,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text = $"{label} {(connected ? "已连接" : "未连接")}",
                        FontSize = 11,
                        Foreground = connected ? PrimaryBrush() : SecondaryTextBrush(),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
        };
    }

    private async Task DisconnectDeviceAsync(DeviceModel device)
    {
        if (string.IsNullOrWhiteSpace(device.DeviceId))
            return;

        var result = await _adb.DisconnectAsync(device.DeviceId);
        device.IsConnected = false;
        StopDevicePreview();
        Notify(result.Success ? "已断开连接" : "断开连接失败", FormatCommandResult(result), result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        ShowDeviceDetail(device);
    }

    private async Task RefreshCompanionInstallStateAsync(DeviceModel device, Button installButton)
    {
        if (!await EnsureDeviceReadyAsync(device))
        {
            installButton.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            var installed = await _companion.IsInstalledAsync(device);
            device.IsCompanionInstalled = installed;
            installButton.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
            if (installed && !device.IsCompanionConnected)
            {
                var result = await _companion.OpenAndConfigureAsync(device, _settings.Current.QuicPort);
                Notify(
                    result.Success ? "伴侣 App 已启动" : "伴侣 App 连接配置失败",
                    result.Success ? $"已下发 QUIC 地址，端口 {_settings.Current.QuicPort}。" : FormatCommandResult(result),
                    result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
            }

            var wasConnected = device.IsCompanionConnected;
            device.IsCompanionConnected = installed && await _companion.IsResponsiveAsync(device);
            if (wasConnected != device.IsCompanionConnected && _currentDetailDevice?.DeviceId == device.DeviceId)
                ShowDeviceDetail(device);
        }
        catch (Exception ex)
        {
            device.IsCompanionConnected = false;
            installButton.Visibility = Visibility.Visible;
            Notify("伴侣 App 检测失败", ex.Message, InfoBarSeverity.Warning);
        }
    }

    private async Task InstallCompanionFromDetailAsync(DeviceModel device, Button installButton)
    {
        if (!await EnsureDeviceReadyAsync(device))
        {
            Notify("需要 ADB 连接", "请先让该设备处于 ADB 已连接状态，再安装伴侣 App。", InfoBarSeverity.Warning);
            return;
        }

        var originalContent = installButton.Content;
        installButton.IsEnabled = false;
        installButton.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "•••", Foreground = PrimaryBrush(), FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock
                {
                    Text = "安装中...",
                    Foreground = SecondaryTextBrush(),
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };

        try
        {
            var install = await _companion.InstallAsync(device);
            if (!install.Success)
            {
                Notify("伴侣 App 安装失败", FormatCommandResult(install), InfoBarSeverity.Error);
                return;
            }

            device.IsCompanionInstalled = true;
            installButton.Visibility = Visibility.Collapsed;
            var configure = await _companion.OpenAndConfigureAsync(device, _settings.Current.QuicPort);
            device.IsCompanionConnected = configure.Success && await _companion.IsResponsiveAsync(device);
            Notify(
                configure.Success ? "伴侣 App 已安装" : "伴侣 App 已安装但连接配置失败",
                configure.Success ? $"已通过 ADB 下发 QUIC 连接地址，端口 {_settings.Current.QuicPort}。" : FormatCommandResult(configure),
                configure.Success ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
            if (_currentDetailDevice?.DeviceId == device.DeviceId)
                ShowDeviceDetail(device);
        }
        catch (Exception ex)
        {
            Notify("伴侣 App 安装失败", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            installButton.IsEnabled = true;
            installButton.Content = originalContent;
        }
    }

    private void AttachPreviewTouchHandlers(Grid previewLayer, Image previewImage, DeviceModel device)
    {
        previewLayer.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(previewLayer).Properties.IsLeftButtonPressed)
                return;

            var point = e.GetCurrentPoint(previewLayer);
            _previewTouch = new PreviewTouchState(device, previewImage, point.Position, DateTimeOffset.Now, point.PointerId);
            previewLayer.CapturePointer(e.Pointer);
            e.Handled = true;
        };

        previewLayer.PointerReleased += async (_, e) =>
        {
            if (_previewTouch is null || _previewTouch.PointerId != e.Pointer.PointerId)
                return;

            var touch = _previewTouch;
            _previewTouch = null;
            previewLayer.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
            await SendPreviewTouchAsync(previewLayer, touch, e.GetCurrentPoint(previewLayer).Position);
        };

        previewLayer.PointerCanceled += (_, e) =>
        {
            if (_previewTouch?.PointerId == e.Pointer.PointerId)
                _previewTouch = null;
            previewLayer.ReleasePointerCapture(e.Pointer);
        };

        previewLayer.PointerCaptureLost += (_, _) => _previewTouch = null;
    }

    private async Task SendPreviewTouchAsync(FrameworkElement previewLayer, PreviewTouchState touch, Point endPoint)
    {
        if (!TryMapPreviewPoint(previewLayer, touch.Image, touch.Device.DeviceId, touch.StartPoint, out var start) ||
            !TryMapPreviewPoint(previewLayer, touch.Image, touch.Device.DeviceId, endPoint, out var end))
            return;

        if (!await EnsureDeviceReadyAsync(touch.Device))
            return;

        var elapsed = Math.Max(1, (int)(DateTimeOffset.Now - touch.StartedAt).TotalMilliseconds);
        var distance = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2));
        var result = distance < 8
            ? elapsed >= 500
                ? await _adb.SwipeAsync(touch.Device.DeviceId, start.X, start.Y, start.X, start.Y, Math.Max(650, elapsed))
                : await _adb.TapAsync(touch.Device.DeviceId, start.X, start.Y)
            : await _adb.SwipeAsync(touch.Device.DeviceId, start.X, start.Y, end.X, end.Y, Math.Max(120, elapsed));

        if (!result.Success)
        {
            touch.Device.IsConnected = false;
            Notify("触控失败", FormatCommandResult(result), InfoBarSeverity.Error);
        }
    }

    private bool TryMapPreviewPoint(FrameworkElement previewLayer, Image image, string deviceId, Point point, out (int X, int Y) devicePoint)
    {
        devicePoint = default;
        if (!_previewFrameSizes.TryGetValue($"detail:{deviceId}", out var frame) || frame.Width <= 0 || frame.Height <= 0)
            return false;

        var layerWidth = previewLayer.ActualWidth;
        var layerHeight = previewLayer.ActualHeight;
        if (layerWidth <= 0 || layerHeight <= 0)
            return false;

        // The preview image uses Stretch.Uniform, so clicks on letterboxed margins must not be sent to the device.
        var scale = Math.Min(layerWidth / frame.Width, layerHeight / frame.Height);
        var renderedWidth = frame.Width * scale;
        var renderedHeight = frame.Height * scale;
        var offsetX = (layerWidth - renderedWidth) / 2d;
        var offsetY = (layerHeight - renderedHeight) / 2d;
        var imageX = point.X - offsetX;
        var imageY = point.Y - offsetY;
        if (imageX < 0 || imageY < 0 || imageX > renderedWidth || imageY > renderedHeight)
            return false;

        var x = (int)Math.Clamp(Math.Round(imageX / scale), 0, frame.Width - 1);
        var y = (int)Math.Clamp(Math.Round(imageY / scale), 0, frame.Height - 1);
        devicePoint = (x, y);
        return true;
    }

    private UIElement BuildDeviceToolTabs(DeviceModel device)
    {
        var contentHost = new Border
        {
            Background = TransparentBrush(),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(14, 0, 0, 14),
            Padding = new Thickness(0),
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        var contentScroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        StyleScrollViewer(contentScroller);
        AttachWheelScrolling(contentScroller);
        contentHost.Child = contentScroller;
        var tabs = new StackPanel
        {
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8, 0, 0, 0),
        };
        var buttons = new List<Button>();
        var items = new (string Title, Symbol Icon, Func<UIElement> Build)[]
        {
            ("快捷", Symbol.Favorite, () => BuildQuickActions(device)),
            ("终端", Symbol.Keyboard, () => BuildAdbTerminal(device)),
            ("软件", Symbol.AllApps, () => BuildPackageManager(device)),
            ("文件", Symbol.Folder, () => BuildFileManager(device)),
            ("硬件", Symbol.Setting, () => BuildHardwareInfo(device)),
            ("重启", Symbol.Refresh, () => BuildRebootActions(device)),
        };

        void Select(int index)
        {
            contentScroller.Content = items[index].Build();
            for (var i = 0; i < buttons.Count; i++)
                ApplyDeviceTabState(buttons[i], i == index);
        }

        for (var i = 0; i < items.Length; i++)
        {
            var index = i;
            var tab = DeviceToolTabButton(items[i].Title, items[i].Icon);
            tab.Click += (_, _) => Select(index);
            buttons.Add(tab);
            tabs.Children.Add(tab);
        }

        var root = new Grid
        {
            ColumnSpacing = 0,
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ColumnDefinitions =
            {
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        root.Children.Add(contentHost);
        Grid.SetColumn(tabs, 1);
        root.Children.Add(tabs);
        Select(0);
        return root;
    }

    private void UpdateDeviceNav(DeviceModel? device)
    {
        if (_deviceDock is null || _deviceNavButton is null)
            return;

        if (device is null)
        {
            _deviceDock.Visibility = Visibility.Collapsed;
            ApplyNavButtonState(_deviceNavButton, false);
            return;
        }

        _deviceDock.Visibility = Visibility.Visible;
        _deviceDock.Background = NavBrush();
        _deviceDock.BorderBrush = BorderLightBrush();
        _deviceNavButton.DataContext = new NavButtonInfo(ShortDeviceName(device.DisplayName), Symbol.CellPhone, true, IsTabletDevice(device));
        _deviceNavButton.Click -= DeviceNavButtonClick;
        _deviceNavButton.Click += DeviceNavButtonClick;
        ApplyNavButtonState(_deviceNavButton, _currentDetailDevice is not null);
    }

    private void DeviceNavButtonClick(object sender, RoutedEventArgs e)
    {
        if (_pinnedDeviceNavDevice is not null)
            ShowDeviceDetail(_pinnedDeviceNavDevice);
    }

    private static bool IsTabletDevice(DeviceModel device)
    {
        return device.Model.Contains("tablet", StringComparison.OrdinalIgnoreCase)
            || device.DisplayName.Contains("pad", StringComparison.OrdinalIgnoreCase)
            || device.DisplayName.Contains("tablet", StringComparison.OrdinalIgnoreCase);
    }

    private static string ShortDeviceName(string name)
    {
        return string.IsNullOrWhiteSpace(name) ? "设备" : name.Length <= 8 ? name : name[..8];
    }

    private UIElement BuildQuickActions(DeviceModel device)
    {
        var stack = ToolStack();
        stack.Children.Add(BodyText("最近使用的高频 ADB 操作。"));
        stack.Children.Add(ActionGrid(
            DeviceActionButton(device, "截屏刷新", async () => await _adb.ScreencapPngAsync(device.DeviceId), "截图命令已执行。"),
            DeviceActionButton(device, "返回", async () => await _adb.ShellAsync(device.DeviceId, "input keyevent KEYCODE_BACK")),
            DeviceActionButton(device, "主页", async () => await _adb.ShellAsync(device.DeviceId, "input keyevent KEYCODE_HOME")),
            DeviceActionButton(device, "任务视图", async () => await _adb.ShellAsync(device.DeviceId, "input keyevent KEYCODE_APP_SWITCH")),
            DeviceActionButton(device, "点亮屏幕", async () => await _adb.ShellAsync(device.DeviceId, "input keyevent KEYCODE_WAKEUP")),
            DeviceActionButton(device, "锁屏", async () => await _adb.ShellAsync(device.DeviceId, "input keyevent KEYCODE_SLEEP"))));
        return stack;
    }

    private UIElement BuildAdbTerminal(DeviceModel device)
    {
        var input = RoundedTextBox("wm size 或 pm list packages");
        var output = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 300,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            Background = ShellBrush(),
            Foreground = ShellTextBrush(),
            BorderBrush = TransparentBrush(),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
        };
        StyleTextBox(output);
        output.Background = ShellBrush();
        output.Foreground = ShellTextBrush();
        output.BorderBrush = TransparentBrush();
        var run = PrimaryButton("运行");
        run.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(input.Text))
                return;
            if (!await EnsureDeviceReadyAsync(device))
                return;
            var command = input.Text.Trim();
            output.Text = $"adb -s {device.DeviceId} shell {command}\r\n执行中...";
            try
            {
                var result = await _adb.ShellAsync(device.DeviceId, command);
                output.Text = $"adb -s {device.DeviceId} shell {command}\r\n{FormatCommandResult(result)}";
                if (!result.Success)
                    device.IsConnected = false;
            }
            catch (Exception ex)
            {
                device.IsConnected = false;
                output.Text = $"adb -s {device.DeviceId} shell {command}\r\n{ex.Message}";
            }
        };
        var prompt = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        prompt.Children.Add(new TextBlock
        {
            Text = "$ adb shell",
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            Foreground = PrimaryBrush(),
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(input, 1);
        prompt.Children.Add(input);
        Grid.SetColumn(run, 2);
        prompt.Children.Add(run);

        var stack = ToolStack();
        stack.Children.Add(BodyText("输入的内容会拼接到当前设备的 adb shell 后执行。"));
        stack.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(14),
            BorderBrush = BorderBrush(),
            BorderThickness = new Thickness(1),
            Background = SurfaceBrush(),
            Padding = new Thickness(12),
            Child = new StackPanel
            {
                Spacing = 10,
                Children = { prompt, output },
            },
        });
        return stack;
    }

    private UIElement BuildPackageManager(DeviceModel device)
    {
        var packages = new ListView { MinHeight = 250, MaxHeight = 280 };
        StyleListView(packages);
        var status = BodyText("点击刷新获取已安装软件包。");
        var refresh = PrimaryButton("刷新软件包");
        refresh.Click += async (_, _) => await LoadPackagesAsync(device, packages, status);
        var install = SecondaryButton("安装 APK");
        install.Click += async (_, _) => await InstallApkAsync(device);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { refresh, install } };
        var actions = ActionGrid(
            DevicePackageButton(device, "运行", packages, packageName => _adb.ShellAsync(device.DeviceId, $"monkey -p {packageName} 1")),
            DevicePackageButton(device, "强制停止", packages, packageName => _adb.ShellAsync(device.DeviceId, $"am force-stop {packageName}")),
            DevicePackageButton(device, "禁用", packages, packageName => _adb.ShellAsync(device.DeviceId, $"pm disable-user {packageName}")),
            DevicePackageButton(device, "启用", packages, packageName => _adb.ShellAsync(device.DeviceId, $"pm enable {packageName}")),
            DevicePackageButton(device, "提取 APK", packages, packageName => PullPackageApkAsync(device, packageName)),
            DevicePackageButton(device, "清除数据", packages, packageName => _adb.ShellAsync(device.DeviceId, $"pm clear {packageName}")));
        var detail = SecondaryButton("查看软件信息");
        detail.Click += async (_, _) =>
        {
            if (packages.SelectedItem is not string packageName)
            {
                Notify("请选择软件包", "先在列表中选择一个软件包。", InfoBarSeverity.Warning);
                return;
            }

            if (!await EnsureDeviceReadyAsync(device))
                return;
            var result = await _adb.ShellAsync(device.DeviceId, $"dumpsys package {packageName}");
            await ShowTextDialogAsync($"软件信息 - {packageName}", FormatCommandResult(result));
        };

        var stack = ToolStack();
        stack.Children.Add(BodyText("ADB 可稳定读取包名；友好应用名和图标需要 Companion/系统权限补充，当前先保证包级操作可用。"));
        stack.Children.Add(row);
        stack.Children.Add(packages);
        stack.Children.Add(status);
        stack.Children.Add(actions);
        stack.Children.Add(detail);
        return stack;
    }

    private UIElement BuildFileManager(DeviceModel device)
    {
        var path = RoundedTextBox("/sdcard/");
        var listing = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 260,
            Background = SurfaceAltBrush(),
            BorderBrush = BorderBrush(),
            CornerRadius = new CornerRadius(12),
        };
        StyleTextBox(listing);
        var list = PrimaryButton("查看目录");
        list.Click += async (_, _) =>
        {
            if (!await EnsureDeviceReadyAsync(device))
                return;
            var result = await _adb.ShellAsync(device.DeviceId, $"ls -la \"{EscapeShell(path.Text)}\"");
            listing.Text = FormatCommandResult(result);
            if (!result.Success)
                device.IsConnected = false;
        };
        var send = SecondaryButton("发送文件到此目录");
        send.Click += async (_, _) => await PushFileAsync(device, path.Text.Trim());
        var delete = SecondaryButton("删除路径");
        delete.Click += async (_, _) =>
        {
            if (!await EnsureDeviceReadyAsync(device))
                return;
            var result = await _adb.ShellAsync(device.DeviceId, $"rm -rf \"{EscapeShell(path.Text)}\"");
            if (!result.Success)
                device.IsConnected = false;
            Notify(result.Success ? "删除命令已执行" : "删除失败", FormatCommandResult(result), result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        };

        var stack = ToolStack();
        stack.Children.Add(BodyText("输入设备端路径后可以查看、上传文件或删除该路径。删除会直接作用于设备文件系统。"));
        stack.Children.Add(path);
        stack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { list, send, delete } });
        stack.Children.Add(listing);
        return stack;
    }

    private UIElement BuildHardwareInfo(DeviceModel device)
    {
        var output = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 420,
            Background = SurfaceAltBrush(),
            BorderBrush = BorderBrush(),
            CornerRadius = new CornerRadius(12),
        };
        StyleTextBox(output);
        var refresh = PrimaryButton("刷新硬件信息");
        refresh.Click += async (_, _) =>
        {
            if (!await EnsureDeviceReadyAsync(device))
                return;
            var command = "printf '品牌: '; getprop ro.product.brand; printf '型号: '; getprop ro.product.model; printf '系统: '; getprop ro.build.version.release; printf 'SDK: '; getprop ro.build.version.sdk; printf 'CPU ABI: '; getprop ro.product.cpu.abi; printf 'CPU 型号: '; cat /proc/cpuinfo | grep -m 1 'Hardware\\|model name\\|Processor'; printf '\\n电池:\\n'; dumpsys battery | head -n 20; printf '\\n内存:\\n'; cat /proc/meminfo | head -n 8; printf '\\nCPU 负载:\\n'; cat /proc/loadavg";
            var result = await _adb.ShellAsync(device.DeviceId, command);
            output.Text = FormatCommandResult(result);
            if (!result.Success)
                device.IsConnected = false;
        };

        var stack = ToolStack();
        stack.Children.Add(BodyText("通过 getprop、dumpsys battery、/proc 信息读取品牌、型号、系统、CPU、电池、内存和负载。"));
        stack.Children.Add(refresh);
        stack.Children.Add(output);
        return stack;
    }

    private UIElement BuildRebootActions(DeviceModel device)
    {
        var stack = ToolStack();
        stack.Children.Add(BodyText("这些操作会改变设备启动状态，请确认设备可恢复后再执行。"));
        stack.Children.Add(ActionGrid(
            DeviceActionButton(device, "重启系统", async () => await _adb.ShellAsync(device.DeviceId, "reboot")),
            DeviceActionButton(device, "Fastboot", async () => await _adb.ShellAsync(device.DeviceId, "reboot bootloader")),
            DeviceActionButton(device, "Fastbootd", async () => await _adb.ShellAsync(device.DeviceId, "reboot fastboot")),
            DeviceActionButton(device, "Recovery", async () => await _adb.ShellAsync(device.DeviceId, "reboot recovery")),
            DeviceActionButton(device, "EDL", async () => await _adb.ShellAsync(device.DeviceId, "reboot edl")),
            DeviceActionButton(device, "关机", async () => await _adb.ShellAsync(device.DeviceId, "reboot -p"))));
        return stack;
    }

    private static StackPanel ToolStack()
    {
        return new StackPanel
        {
            Spacing = 12,
            Padding = new Thickness(2, 12, 2, 2),
        };
    }

    private static WrapPanel ActionGrid(params UIElement[] actions)
    {
        var panel = new WrapPanel();
        foreach (var action in actions)
            panel.Children.Add(action);
        return panel;
    }

    private Button DeviceActionButton(DeviceModel device, string text, Func<Task<AdbCommandResult>> action)
    {
        var button = SecondaryButton(text);
        button.Margin = new Thickness(0, 0, 8, 8);
        button.Click += async (_, _) =>
        {
            if (!await EnsureDeviceReadyAsync(device))
                return;
            try
            {
                var result = await action();
                if (!result.Success)
                    device.IsConnected = false;
                Notify(result.Success ? "操作已执行" : "操作失败", FormatCommandResult(result), result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            }
            catch (Exception ex)
            {
                device.IsConnected = false;
                Notify("操作失败", ex.Message, InfoBarSeverity.Error);
            }
        };
        return button;
    }

    private Button DeviceActionButton(DeviceModel device, string text, Func<Task<byte[]>> action, string successMessage)
    {
        var button = SecondaryButton(text);
        button.Margin = new Thickness(0, 0, 8, 8);
        button.Click += async (_, _) =>
        {
            if (!await EnsureDeviceReadyAsync(device))
                return;
            try
            {
                await action();
                Notify("操作已执行", successMessage, InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                device.IsConnected = false;
                Notify("操作失败", ex.Message, InfoBarSeverity.Error);
            }
        };
        return button;
    }

    private Button DevicePackageButton(DeviceModel device, string text, ListView packages, Func<string, Task<AdbCommandResult>> action)
    {
        var button = SecondaryButton(text);
        button.Margin = new Thickness(0, 0, 8, 8);
        button.Click += async (_, _) =>
        {
            if (packages.SelectedItem is not string packageName)
            {
                Notify("请选择软件包", "先在列表中选择一个软件包。", InfoBarSeverity.Warning);
                return;
            }

            if (!await EnsureDeviceReadyAsync(device))
                return;
            var result = await action(packageName);
            if (!result.Success)
                device.IsConnected = false;
            Notify(result.Success ? "操作已执行" : "操作失败", FormatCommandResult(result), result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        };
        return button;
    }

    private async Task<bool> EnsureDeviceReadyAsync(DeviceModel device, bool notify = true)
    {
        if (string.IsNullOrWhiteSpace(device.DeviceId))
            return false;

        var online = await _devices.EnsureDeviceOnlineAsync(device);
        if (!online)
        {
            device.IsConnected = false;
            if (notify)
                Notify("设备离线", $"ADB 当前无法访问 {device.DeviceId}，请重新连接或确认无线调试端口仍有效。", InfoBarSeverity.Warning);
        }

        return online;
    }

    private async Task LoadPackagesAsync(DeviceModel device, ListView packages, TextBlock status)
    {
        status.Text = "正在读取软件包...";
        if (!await EnsureDeviceReadyAsync(device))
        {
            status.Text = $"设备离线：{device.DeviceId}";
            return;
        }
        var result = await _adb.ShellAsync(device.DeviceId, "pm list packages -3");
        if (!result.Success)
        {
            device.IsConnected = false;
            status.Text = FormatCommandResult(result);
            return;
        }

        var items = result.Stdout
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Replace("package:", string.Empty, StringComparison.OrdinalIgnoreCase).Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .OrderBy(line => line, StringComparer.OrdinalIgnoreCase)
            .ToList();
        packages.ItemsSource = items;
        status.Text = items.Count == 0 ? "未读取到第三方软件包。" : $"已读取 {items.Count} 个第三方软件包。";
    }

    private async Task InstallApkAsync(DeviceModel device)
    {
        if (!await EnsureDeviceReadyAsync(device))
            return;

        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".apk");
        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return;

        var result = await _adb.InstallAsync(device.DeviceId, file.Path);
        if (!result.Success)
            device.IsConnected = false;
        Notify(result.Success ? "APK 已安装" : "安装失败", FormatCommandResult(result), result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private async Task<AdbCommandResult> PullPackageApkAsync(DeviceModel device, string packageName)
    {
        if (!await EnsureDeviceReadyAsync(device))
            return new AdbCommandResult(1, string.Empty, $"设备离线：{device.DeviceId}");

        var pathResult = await _adb.ShellAsync(device.DeviceId, $"pm path {packageName}");
        if (!pathResult.Success)
        {
            device.IsConnected = false;
            return pathResult;
        }

        var remote = pathResult.Stdout
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Replace("package:", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        if (string.IsNullOrWhiteSpace(remote))
            return new AdbCommandResult(1, string.Empty, "未找到 APK 路径。");

        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
            return new AdbCommandResult(0, "已取消提取。", string.Empty);

        var local = Path.Combine(folder.Path, $"{packageName}.apk");
        return await _adb.PullAsync(device.DeviceId, remote, local);
    }

    private async Task PushFileAsync(DeviceModel device, string remoteDirectory)
    {
        if (!await EnsureDeviceReadyAsync(device))
            return;

        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return;

        var remote = $"{remoteDirectory.TrimEnd('/')}/{file.Name}";
        var result = await _adb.PushAsync(device.DeviceId, file.Path, remote);
        if (!result.Success)
            device.IsConnected = false;
        Notify(result.Success ? "文件已发送" : "发送失败", FormatCommandResult(result), result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private void StartDevicePreview(DeviceModel device, Image previewImage, TextBlock status)
    {
        async void Tick()
        {
            try
            {
                if (!await EnsureDeviceReadyAsync(device, false))
                {
                    status.Visibility = Visibility.Visible;
                    status.Text = $"设备离线：{device.DeviceId}";
                    return;
                }
                var png = await _adb.ScreencapPngAsync(device.DeviceId);
                await ApplyPreviewFrameAsync(previewImage, device.DeviceId, png, "detail");
                status.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                device.IsConnected = false;
                status.Visibility = Visibility.Visible;
                status.Text = $"截图失败：{ex.Message}";
            }
        }

        _devicePreviewTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _devicePreviewTimer.Tick += (_, _) => Tick();
        _devicePreviewTimer.Start();
        Tick();
    }

    private void StopDevicePreview()
    {
        _devicePreviewTimer?.Stop();
        _devicePreviewTimer = null;
        _previewFrameHashes.Keys
            .Where(key => key.StartsWith("detail:", StringComparison.Ordinal))
            .ToList()
            .ForEach(key => _previewFrameHashes.Remove(key));
    }

    private void StartDeviceListPreview()
    {
        StopDeviceListPreview();
        if (_deviceCardPreviews.Count == 0)
            return;

        _deviceListPreviewTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Max(1, _deviceListPreviewSeconds)),
        };
        _deviceListPreviewTimer.Tick += async (_, _) => await RefreshDeviceListPreviewsAsync();
        _deviceListPreviewTimer.Start();
        _ = RefreshDeviceListPreviewsAsync();
    }

    private void StopDeviceListPreview()
    {
        _deviceListPreviewTimer?.Stop();
        _deviceListPreviewTimer = null;
    }

    private async Task RefreshDeviceListPreviewsAsync()
    {
        foreach (var device in _devices.Devices.Where(device => _deviceCardPreviews.ContainsKey(device.DeviceId)).ToList())
            await RefreshDeviceCardPreviewAsync(device);
    }

    private async Task RefreshDeviceCardPreviewAsync(DeviceModel device)
    {
        if (!_deviceCardPreviews.TryGetValue(device.DeviceId, out var target))
            return;

        try
        {
            target.Status.Text = "刷新中...";
            target.Status.Visibility = Visibility.Visible;
            if (!await EnsureDeviceReadyAsync(device, false))
            {
                target.Status.Text = $"设备离线：{device.DeviceId}";
                return;
            }
            var png = await _adb.ScreencapPngAsync(device.DeviceId);
            await ApplyPreviewFrameAsync(target.Image, device.DeviceId, png, "card");
            target.Status.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            device.IsConnected = false;
            target.Status.Visibility = Visibility.Visible;
            target.Status.Text = $"截图失败：{ex.Message}";
        }
    }

    private async Task<bool> ApplyPreviewFrameAsync(Image image, string deviceId, byte[] png, string scope, bool force = false)
    {
        var key = $"{scope}:{deviceId}";
        if (TryReadPngSize(png, out var width, out var height))
            _previewFrameSizes[key] = (width, height);
        var hash = Convert.ToHexString(SHA256.HashData(png));
        if (!force && _previewFrameHashes.TryGetValue(key, out var previousHash) && previousHash == hash)
            return false;

        _previewFrameHashes[key] = hash;
        var bitmap = new BitmapImage();
        using var stream = new MemoryStream(png);
        await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
        image.Source = bitmap;
        return true;
    }

    private static bool TryReadPngSize(byte[] png, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (png.Length < 24 ||
            png[0] != 0x89 ||
            png[1] != 0x50 ||
            png[2] != 0x4E ||
            png[3] != 0x47)
            return false;

        width = ReadBigEndianInt32(png, 16);
        height = ReadBigEndianInt32(png, 20);
        return width > 0 && height > 0;
    }

    private static int ReadBigEndianInt32(byte[] bytes, int offset)
    {
        return (bytes[offset] << 24) |
               (bytes[offset + 1] << 16) |
               (bytes[offset + 2] << 8) |
               bytes[offset + 3];
    }

    private async Task ShowTextDialogAsync(string title, string text)
    {
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 420,
            MaxHeight = 520,
            Background = SurfaceAltBrush(),
            BorderBrush = BorderBrush(),
            CornerRadius = new CornerRadius(12),
        };
        StyleTextBox(box);
        await Dialog(title, box, "关闭").ShowAsync();
    }

    private static string FormatCommandResult(AdbCommandResult result)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(result.Stdout))
            parts.Add(result.Stdout.Trim());
        if (!string.IsNullOrWhiteSpace(result.Stderr))
            parts.Add(result.Stderr.Trim());
        if (parts.Count == 0)
            parts.Add($"adb 退出码 {result.ExitCode}");
        return string.Join(Environment.NewLine, parts);
    }

    private static string EscapeShell(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static string SanitizeFileName(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');
        return value.Replace(':', '_');
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
        _activePageScroller = null;
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
        var adbPortBox = StyledNumberBox(_settings.Current.AdbPort, 1024, 65535, 180);
        adbPortBox.ValueChanged += (_, args) =>
        {
            if (!double.IsNaN(args.NewValue))
            {
                _settings.Current.AdbPort = (int)Math.Clamp(args.NewValue, 1024, 65535);
                _settings.Save();
            }
        };
        var quicPortBox = StyledNumberBox(_settings.Current.QuicPort, 1024, 65535, 180);
        quicPortBox.ValueChanged += (_, args) =>
        {
            if (!double.IsNaN(args.NewValue))
            {
                _settings.Current.QuicPort = (int)Math.Clamp(args.NewValue, 1024, 65535);
                _settings.Save();
            }
        };
        var resetAdbPort = SecondaryButton("恢复默认 (15037)");
        resetAdbPort.Click += (_, _) =>
        {
            _settings.Current.AdbPort = 15037;
            _settings.Save();
            adbPortBox.Value = 15037;
        };
        var resetQuicPort = SecondaryButton("恢复默认 (15038)");
        resetQuicPort.Click += (_, _) =>
        {
            _settings.Current.QuicPort = 15038;
            _settings.Save();
            quicPortBox.Value = 15038;
        };
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
                        adbPortBox,
                        resetAdbPort,
                    },
                },
                new Border { Height = 1, Background = BorderBrush(), Margin = new Thickness(0, 8, 0, 8) },
                new TextBlock { Text = "Companion QUIC 端口", Foreground = PrimaryTextBrush() },
                new TextBlock { Text = "桌面端通过 ADB 下发给伴侣 App 的局域网 QUIC 连接端口", FontSize = 12, Foreground = MutedBrush(), TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Children =
                    {
                        quicPortBox,
                        resetQuicPort,
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
                FollowSystemToggleRow(),
                new Border { Height = 1, Background = BorderBrush(), Margin = new Thickness(0, 4, 0, 4) },
                ThemeToggleRow(),
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
        _contentHost.Children.Add(PageScroller(panel));
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
        var isOn = false;
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
        var toggle = SwitchButton(isOn);
        toggle.Click += (_, _) =>
        {
            isOn = !isOn;
            ApplySwitchState(toggle, isOn);
        };
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);
        return row;
    }

    private Grid FollowSystemToggleRow()
    {
        var row = ToggleSettingRow("跟随系统", "跟随 Windows 当前深浅色设置", s_followSystemTheme);
        if (row.Children.LastOrDefault() is Button toggle)
        {
            toggle.Click += (_, _) =>
            {
                s_followSystemTheme = !s_followSystemTheme;
                ApplySwitchState(toggle, s_followSystemTheme);
                if (s_followSystemTheme)
                    s_darkTheme = Application.Current.RequestedTheme != ApplicationTheme.Light;
                ApplyThemeAndRefreshSettings();
            };
        }
        return row;
    }

    private Grid ThemeToggleRow()
    {
        var row = ToggleSettingRow("深色模式", "切换后立即刷新窗口色板", s_darkTheme);
        if (row.Children.LastOrDefault() is Button toggle)
        {
            toggle.IsEnabled = !s_followSystemTheme;
            toggle.Opacity = s_followSystemTheme ? 0.55 : 1;
            toggle.Click += (_, _) =>
            {
                s_darkTheme = !s_darkTheme;
                ApplySwitchState(toggle, s_darkTheme);
                ApplyThemeAndRefreshSettings();
            };
        }
        return row;
    }

    private Grid ToggleSettingRow(string title, string description, bool isOn)
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
        var toggle = SwitchButton(isOn);
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);
        return row;
    }

    private void ApplyThemeAndRefreshSettings()
    {
        _root.RequestedTheme = s_darkTheme ? ElementTheme.Dark : ElementTheme.Light;
        BuildShellTheme();
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (_currentDetailDevice is not null)
                ShowDeviceDetail(_currentDetailDevice);
            else
                Navigate("设置");
        });
    }

    private void BuildShellTheme()
    {
        _root.Background = AppBrush();
        _contentFrame.Background = TransparentBrush();
        if (_background is not null)
        {
            _background.Fill = AppBrush();
            _background.GridLineBrush = GridLineBrush();
            _background.GridSize = 20;
            _background.Refresh();
        }
        ApplyTitleBarTheme();
        foreach (var dock in new[] { _navDock, _deviceDock, _aiDock })
        {
            if (dock is null)
                continue;
            dock.Background = NavBrush();
            dock.BorderBrush = BorderLightBrush();
        }
        foreach (var button in _navButtons)
            ApplyNavButtonState(button, string.Equals(button.Tag as string, _currentPage, StringComparison.Ordinal));
        if (_deviceNavButton is not null)
            ApplyNavButtonState(_deviceNavButton, _currentDetailDevice is not null);
        if (_aiButton is not null)
            ApplyNavButtonState(_aiButton, _aiPanel.Visibility == Visibility.Visible);
        RefreshAiPanelTheme();
        _ = DispatcherQueue.TryEnqueue(() => PolishRoundedEdges(_root));
    }

    private void ApplyTitleBarTheme()
    {
        var titleBar = AppWindow.TitleBar;
        var foreground = s_darkTheme
            ? ColorHelper.FromArgb(255, 248, 250, 252)
            : ColorHelper.FromArgb(255, 15, 23, 42);
        var hoverBackground = s_darkTheme
            ? ColorHelper.FromArgb(90, 51, 65, 85)
            : ColorHelper.FromArgb(120, 226, 232, 240);
        var pressedBackground = s_darkTheme
            ? ColorHelper.FromArgb(150, 51, 65, 85)
            : ColorHelper.FromArgb(190, 203, 213, 225);

        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = foreground;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonHoverBackgroundColor = hoverBackground;
        titleBar.ButtonPressedBackgroundColor = pressedBackground;
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
            foreach (var model in _settings.Current.AiModels.ToList())
            {
                var row = new Grid
                {
                    ColumnSpacing = 12,
                    ColumnDefinitions =
                    {
                        new ColumnDefinition(),
                        new ColumnDefinition { Width = GridLength.Auto },
                    },
                };
                row.Children.Add(new TextBlock
                {
                    Text = $"{model.Name}  ·  {model.ModelId}\n{model.ApiUrl}",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = PrimaryTextBrush(),
                });
                var delete = DangerButton("删除");
                delete.Click += (_, _) => DeleteAiModel(model);
                Grid.SetColumn(delete, 1);
                row.Children.Add(delete);
                stack.Children.Add(Card(row));
            }
        }

        return Card(stack);
    }

    private void DeleteAiModel(AiModelSettings model)
    {
        _settings.Current.AiModels.Remove(model);
        if (_selectedAiModel is not null &&
            (ReferenceEquals(_selectedAiModel, model) || _selectedAiModel.ModelId == model.ModelId))
        {
            _selectedAiModel = null;
        }
        _settings.Save();
        RefreshModelCombo();
        ShowSettings();
        Notify("模型已删除", model.Name, InfoBarSeverity.Success);
    }

    private void BuildAiPanel()
    {
        var wasVisible = _aiPanel.Child is not null && _aiPanel.Visibility == Visibility.Visible;
        _aiPanel.Child = null;
        _messageList.Children.Clear();
        _pendingAttachmentList.Children.Clear();
        _aiPanel.Visibility = Visibility.Collapsed;
        _aiPanel.Width = 400;
        _aiPanel.Margin = new Thickness(0, 10, 12, 98);
        _aiPanel.HorizontalAlignment = HorizontalAlignment.Right;
        _aiPanel.VerticalAlignment = VerticalAlignment.Stretch;
        _aiPanel.CornerRadius = new CornerRadius(20);
        _aiPanel.BorderBrush = BorderLightBrush();
        _aiPanel.BorderThickness = new Thickness(1);
        _aiPanel.Background = FrostedSurfaceBrush();
        _aiPanel.RenderTransform = new TranslateTransform { X = 32 };
        _aiPanel.Opacity = 0;
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
            Tag = "ai-close",
            Content = new Grid
            {
                Width = 34,
                Height = 34,
                Children =
                {
                    new SymbolIcon(Symbol.Cancel)
                    {
                        Width = 15,
                        Height = 15,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
            Width = 34,
            Height = 34,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Background = TransparentBrush(),
            Foreground = SecondaryTextBrush(),
            BorderThickness = new Thickness(0),
        };
        ApplyButtonResources(close, TransparentBrush(), SecondaryTextBrush(), HoverBrush(), SurfaceAltBrush(), BorderLightBrush(), new Thickness(0));
        close.Click += (_, _) => HideAiPanel();
        Grid.SetColumn(close, 2);
        top.Children.Add(close);
        root.Children.Add(top);

        _messageList.Spacing = 12;
        if (_settings.Current.AiMessages.Count == 0)
        {
            _messageList.Children.Add(MessageBubble(new AiChatMessage
            {
                Role = "AI",
                Text = "请先在设置内配置 AI 模型，然后再发送消息。",
                IsUser = false,
            }));
        }
        else
        {
            foreach (var message in _settings.Current.AiMessages)
                _messageList.Children.Add(MessageBubble(message));
        }
        RebuildAiConversationFromVisibleMessages();
        var messageScroller = new ScrollViewer
        {
            Content = _messageList,
            Padding = new Thickness(16, 12, 16, 12),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _aiMessageScroller = messageScroller;
        StyleScrollViewer(messageScroller);
        AttachWheelScrolling(messageScroller);
        messageScroller.ViewChanged -= OnAiMessageScrollerViewChanged;
        messageScroller.ViewChanged += OnAiMessageScrollerViewChanged;
        var messageLayer = new Grid();
        messageLayer.Children.Add(messageScroller);
        _aiScrollBottomButton = BuildAiScrollBottomButton();
        messageLayer.Children.Add(_aiScrollBottomButton);
        Grid.SetRow(messageLayer, 1);
        root.Children.Add(messageLayer);

        var inputArea = new Grid
        {
            Margin = new Thickness(0),
            Padding = new Thickness(0, 0, 0, 8),
            RowSpacing = 4,
            Background = SurfaceAltBrush(),
        };
        inputArea.Tag = "ai-input-shell";
        inputArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        inputArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        inputArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _pendingAttachmentList.Spacing = 8;
        inputArea.Children.Add(_pendingAttachmentList);

        _aiInput.PlaceholderText = "要求后续变更...";
        _aiInput.AcceptsReturn = true;
        _aiInput.TextWrapping = TextWrapping.Wrap;
        _aiInput.MinHeight = 44;
        _aiInput.MaxHeight = 180;
        _aiInput.FontSize = 14;
        _aiInput.Padding = new Thickness(12, 10, 12, 6);
        _aiInput.CornerRadius = new CornerRadius(0);
        _aiInput.Background = TransparentBrush();
        _aiInput.BorderBrush = TransparentBrush();
        _aiInput.BorderThickness = new Thickness(0);
        _aiInput.TextChanged -= OnAiInputTextChanged;
        _aiInput.TextChanged += OnAiInputTextChanged;
        _aiInput.SizeChanged -= OnAiInputSizeChanged;
        _aiInput.SizeChanged += OnAiInputSizeChanged;
        _aiInput.KeyDown -= OnAiInputKeyDown;
        _aiInput.KeyDown += OnAiInputKeyDown;
        if (!_aiInputKeyHandlerAttached)
        {
            _aiInput.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnAiInputKeyDown), true);
            _aiInputKeyHandlerAttached = true;
        }
        StyleAiInputBox();
        UpdateAiInputHeight();
        Grid.SetRow(_aiInput, 1);
        inputArea.Children.Add(_aiInput);

        var tools = BuildAiToolbar();
        Grid.SetRow(tools, 2);
        inputArea.Children.Add(tools);

        var inputShell = new Border
        {
            Tag = "ai-input-shell",
            Margin = new Thickness(12, 8, 12, 12),
            CornerRadius = new CornerRadius(18),
            BorderBrush = BorderLightBrush(),
            BorderThickness = new Thickness(1),
            Background = SurfaceAltBrush(),
            Child = inputArea,
        };
        Grid.SetRow(inputShell, 2);
        root.Children.Add(inputShell);
        _aiPanel.Child = root;
        _aiPanel.Visibility = wasVisible ? Visibility.Visible : Visibility.Collapsed;
        if (wasVisible)
        {
            _aiPanel.Opacity = 1;
            if (_aiPanel.RenderTransform is TranslateTransform transform)
                transform.X = 0;
        }
    }

    private void RefreshAiPanelTheme()
    {
        _aiPanel.Background = FrostedSurfaceBrush();
        _aiPanel.BorderBrush = BorderLightBrush();
        _aiInput.Background = TransparentBrush();
        _aiInput.BorderBrush = TransparentBrush();
        _aiInput.Foreground = PrimaryTextBrush();
        _aiInput.PlaceholderForeground = MutedBrush();
        StyleAiInputBox();
        StyleSelectorButton(_permissionSelector, 150);
        StyleSelectorButton(_modelSelector, 108);
        RefreshSelectorLabels();

        if (_aiPanel.Child is UIElement child)
            RefreshThemeBrushes(child);
    }

    private static void RefreshThemeBrushes(UIElement element)
    {
        switch (element)
        {
            case TextBlock text:
                if (Equals(text.Tag, "send-icon"))
                {
                    text.Foreground = OnPrimaryBrush();
                    break;
                }
                text.Foreground = text.FontSize <= 12 ? SecondaryTextBrush() : PrimaryTextBrush();
                break;
            case Border border:
                if (Equals(border.Tag, "ai-bubble"))
                    border.Background = SurfaceAltBrush();
                else if (Equals(border.Tag, "ai-bubble-user"))
                    border.Background = PrimaryBrush();
                else if (Equals(border.Tag, "ai-input-shell"))
                    border.Background = SurfaceAltBrush();
                else if (Equals(border.Tag, "ai-surface"))
                    border.Background = SurfaceBrush();
                else if (Equals(border.Tag, "ai-thinking-body"))
                    border.Background = SurfaceBrush();
                else if (Equals(border.Tag, "ai-thinking-shell"))
                    border.Background = TransparentBrush();
                if (border.BorderThickness.Left > 0 || border.BorderThickness.Top > 0)
                    border.BorderBrush = BorderLightBrush();
                if (border.Child is not null)
                    RefreshThemeBrushes(border.Child);
                break;
            case Button button:
                if (Equals(button.Tag, "primary-action"))
                {
                    ApplyButtonResources(button, TransparentBrush(), OnPrimaryBrush(), TransparentBrush(), TransparentBrush(), TransparentBrush(), new Thickness(0));
                    button.Foreground = OnPrimaryBrush();
                    if (button.Content is Border sendCircle)
                    {
                        sendCircle.Background = PrimaryBrush();
                        sendCircle.BorderBrush = PrimaryHoverBrush();
                    }
                }
                else if (Equals(button.Tag, "ai-close"))
                {
                    ApplyButtonResources(button, TransparentBrush(), SecondaryTextBrush(), HoverBrush(), SurfaceAltBrush(), BorderLightBrush(), new Thickness(0));
                }
                else if (Equals(button.Tag, "ai-selector"))
                {
                    StyleSelectorButton(button, button.Width);
                }
                else if (Equals(button.Tag, "ai-thinking-toggle"))
                {
                    ApplyButtonResources(button, TransparentBrush(), SecondaryTextBrush(), HoverBrush(), SurfaceBrush(), TransparentBrush(), new Thickness(0));
                }
                else
                {
                    button.Foreground = SecondaryTextBrush();
                    button.Background = TransparentBrush();
                }
                if (button.Content is UIElement buttonContent)
                    RefreshThemeBrushes(buttonContent);
                break;
            case IconElement icon:
                icon.Foreground = icon is FrameworkElement iconElement && Equals(iconElement.Tag, "send-icon")
                    ? OnPrimaryBrush()
                    : SecondaryTextBrush();
                break;
            case Grid grid:
                if (Equals(grid.Tag, "ai-input-shell"))
                    grid.Background = SurfaceAltBrush();
                foreach (var child in grid.Children)
                    RefreshThemeBrushes(child);
                break;
            case TextBox textBox:
                textBox.Foreground = PrimaryTextBrush();
                textBox.PlaceholderForeground = MutedBrush();
                textBox.Background = TransparentBrush();
                textBox.BorderBrush = TransparentBrush();
                break;
            case ComboBox comboBox:
                StyleComboBox(comboBox);
                break;
            case Panel panel:
                foreach (var child in panel.Children)
                    RefreshThemeBrushes(child);
                break;
            case ContentControl contentControl when contentControl.Content is UIElement controlContent:
                RefreshThemeBrushes(controlContent);
                break;
            case ScrollViewer scrollViewer when scrollViewer.Content is UIElement scrollContent:
                RefreshThemeBrushes(scrollContent);
                break;
        }
    }

    private FrameworkElement BuildAiToolbar()
    {
        var tools = new Grid
        {
            ColumnSpacing = 8,
            Padding = new Thickness(8, 2, 8, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tools.ColumnDefinitions.Add(new ColumnDefinition());
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var attach = new Button
        {
            Content = ToolbarGlyph(Symbol.Attach, 18, 13),
            Width = 32,
            Height = 32,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(8),
            Background = TransparentBrush(),
            Foreground = SecondaryTextBrush(),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        ApplyButtonResources(attach, TransparentBrush(), SecondaryTextBrush(), HoverBrush(), SurfaceBrush(), BorderLightBrush(), new Thickness(0));
        attach.Click += async (_, _) => await PickAttachmentsAsync();
        tools.Children.Add(attach);

        StyleSelectorButton(_permissionSelector, 150);
        _permissionSelector.Click -= OnPermissionSelectorClick;
        _permissionSelector.Click += OnPermissionSelectorClick;
        Grid.SetColumn(_permissionSelector, 1);
        tools.Children.Add(_permissionSelector);

        StyleSelectorButton(_modelSelector, 108);
        _modelSelector.Click -= OnModelSelectorClick;
        _modelSelector.Click += OnModelSelectorClick;
        RefreshModelCombo();
        Grid.SetColumn(_modelSelector, 3);
        tools.Children.Add(_modelSelector);

        var sendIcon = new FontIcon
        {
            Tag = "send-icon",
            Glyph = "\uE74A",
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Width = 18,
            Height = 18,
            Foreground = OnPrimaryBrush(),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var sendCircle = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(16),
            Background = PrimaryBrush(),
            BorderBrush = PrimaryHoverBrush(),
            BorderThickness = new Thickness(1),
            Child = sendIcon,
        };
        var send = new Button
        {
            Tag = "primary-action",
            Content = sendCircle,
            Width = 32,
            Height = 32,
            MinWidth = 32,
            MinHeight = 32,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(16),
            Background = TransparentBrush(),
            Foreground = OnPrimaryBrush(),
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
        };
        ApplyButtonResources(send, TransparentBrush(), OnPrimaryBrush(), TransparentBrush(), TransparentBrush(), TransparentBrush(), new Thickness(0));
        send.PointerEntered += (_, _) => sendCircle.Background = PrimaryHoverBrush();
        send.PointerExited += (_, _) => sendCircle.Background = PrimaryBrush();
        send.PointerPressed += (_, _) => sendCircle.Background = PrimaryPressedBrush();
        send.PointerReleased += (_, _) => sendCircle.Background = PrimaryHoverBrush();
        _aiSendButton = send;
        _aiSendCircle = sendCircle;
        _aiSendIcon = sendIcon;
        send.Click += async (_, _) =>
        {
            if (_aiIsSending)
                CancelAiGeneration();
            else
                await SendAiMessageAsync();
        };
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

    private void StyleAiInputBox()
    {
        _aiInput.Background = TransparentBrush();
        _aiInput.BorderBrush = TransparentBrush();
        _aiInput.BorderThickness = new Thickness(0);
        _aiInput.Foreground = PrimaryTextBrush();
        _aiInput.PlaceholderForeground = MutedBrush();
        _aiInput.UseSystemFocusVisuals = false;
        _aiInput.Resources["TextControlBackground"] = TransparentBrush();
        _aiInput.Resources["TextControlBackgroundPointerOver"] = TransparentBrush();
        _aiInput.Resources["TextControlBackgroundFocused"] = TransparentBrush();
        _aiInput.Resources["TextControlBackgroundDisabled"] = TransparentBrush();
        _aiInput.Resources["TextControlForeground"] = PrimaryTextBrush();
        _aiInput.Resources["TextControlForegroundPointerOver"] = PrimaryTextBrush();
        _aiInput.Resources["TextControlForegroundFocused"] = PrimaryTextBrush();
        _aiInput.Resources["TextControlPlaceholderForeground"] = MutedBrush();
        _aiInput.Resources["TextControlPlaceholderForegroundPointerOver"] = MutedBrush();
        _aiInput.Resources["TextControlPlaceholderForegroundFocused"] = MutedBrush();
        _aiInput.Resources["TextControlBorderBrush"] = TransparentBrush();
        _aiInput.Resources["TextControlBorderBrushPointerOver"] = TransparentBrush();
        _aiInput.Resources["TextControlBorderBrushFocused"] = TransparentBrush();
        _aiInput.Resources["FocusVisualPrimaryBrush"] = TransparentBrush();
        _aiInput.Resources["FocusVisualSecondaryBrush"] = TransparentBrush();
    }

    private void OnAiInputTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateAiInputHeight();
    }

    private async void OnAiInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;

        var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        var shiftDown = (shiftState & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
        if (shiftDown)
            return;

        e.Handled = true;
        await SendAiMessageAsync();
    }

    private void OnAiInputSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateAiInputHeight();
    }

    private void UpdateAiInputHeight()
    {
        var lineCount = EstimateAiInputVisualLineCount();
        var targetHeight = Math.Clamp(24 + lineCount * 22, 44, 180);
        if (Math.Abs(_aiInput.Height - targetHeight) < 0.5)
            return;

        _aiInput.Height = targetHeight;
        _aiInput.InvalidateMeasure();
        _aiInput.UpdateLayout();
    }

    private int EstimateAiInputVisualLineCount()
    {
        var text = string.IsNullOrEmpty(_aiInput.Text) ? string.Empty : _aiInput.Text.Replace("\r", string.Empty, StringComparison.Ordinal);
        var availableWidth = _aiInput.ActualWidth > 0 ? _aiInput.ActualWidth : 340;
        availableWidth = Math.Max(80, availableWidth - _aiInput.Padding.Left - _aiInput.Padding.Right);
        var columnWidth = Math.Max(6, _aiInput.FontSize * 0.58);
        var columnsPerLine = Math.Max(8, (int)Math.Floor(availableWidth / columnWidth));
        var totalLines = 0;

        foreach (var rawLine in text.Split('\n'))
        {
            var visualColumns = MeasureAiInputColumns(rawLine);
            totalLines += Math.Max(1, (int)Math.Ceiling(visualColumns / columnsPerLine));
        }

        return Math.Clamp(totalLines, 1, 8);
    }

    private static double MeasureAiInputColumns(string line)
    {
        if (string.IsNullOrEmpty(line))
            return 0;

        var columns = 0d;
        foreach (var c in line)
            columns += c <= 0x7f ? 1d : 1.75d;
        return columns;
    }

    private static void StyleSelectorButton(Button button, double width)
    {
        button.Tag = "ai-selector";
        button.Width = width;
        button.Height = 30;
        button.MinHeight = 30;
        button.Padding = new Thickness(8, 0, 6, 0);
        button.CornerRadius = new CornerRadius(8);
        button.Background = TransparentBrush();
        button.Foreground = SecondaryTextBrush();
        button.BorderBrush = TransparentBrush();
        button.BorderThickness = new Thickness(0);
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.VerticalContentAlignment = VerticalAlignment.Center;
        ApplyButtonResources(button, TransparentBrush(), SecondaryTextBrush(), SelectorHoverBrush(), SelectorPressedBrush(), TransparentBrush(), new Thickness(0));
        button.Resources["ButtonBackgroundPointerOver"] = SelectorHoverBrush();
        button.Resources["ButtonBackgroundFocused"] = SelectorHoverBrush();
        button.Resources["ButtonBackgroundPressed"] = SelectorPressedBrush();
        button.PointerEntered -= OnSelectorPointerEntered;
        button.PointerExited -= OnSelectorPointerExited;
        button.PointerPressed -= OnSelectorPointerPressed;
        button.PointerReleased -= OnSelectorPointerEntered;
        button.PointerEntered += OnSelectorPointerEntered;
        button.PointerExited += OnSelectorPointerExited;
        button.PointerPressed += OnSelectorPointerPressed;
        button.PointerReleased += OnSelectorPointerEntered;
    }

    private static void OnSelectorPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button)
            button.Background = SelectorHoverBrush();
    }

    private static void OnSelectorPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button)
            button.Background = TransparentBrush();
    }

    private static void OnSelectorPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button)
            button.Background = SelectorPressedBrush();
    }

    private void RefreshSelectorLabels()
    {
        _permissionSelector.Content = SelectorLabel(Symbol.Help, _selectedAiPermission, 11);
        _modelSelector.Content = SelectorLabel(Symbol.Find, _selectedAiModel?.Name ?? "选择模型", 11);
    }

    private static FontIcon ToolbarGlyph(Symbol icon, double viewport, double fontSize)
    {
        return new FontIcon
        {
            Glyph = ToolbarGlyphCode(icon),
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = fontSize,
            Width = viewport,
            Height = viewport,
            Foreground = SecondaryTextBrush(),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static string ToolbarGlyphCode(Symbol icon)
    {
        return icon switch
        {
            Symbol.Attach => "\uE723",
            Symbol.Help => "\uE897",
            Symbol.Find => "\uE721",
            Symbol.Accept => "\uE73E",
            Symbol.Permissions => "\uE8D7",
            Symbol.Add => "\uE710",
            _ => "\uE897",
        };
    }

    private static UIElement SelectorLabel(Symbol icon, string text, double fontSize)
    {
        var root = new Grid
        {
            Height = 30,
            VerticalAlignment = VerticalAlignment.Center,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        root.Children.Add(ToolbarGlyph(icon, 18, 13));
        var label = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            Foreground = SecondaryTextBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            LineHeight = 14,
            Margin = new Thickness(6, 0, 4, 0),
        };
        Grid.SetColumn(label, 1);
        root.Children.Add(label);
        var arrow = new TextBlock
        {
            Text = "⌄",
            FontSize = 10,
            Foreground = SecondaryTextBrush(),
            VerticalAlignment = VerticalAlignment.Center,
            LineHeight = 12,
        };
        Grid.SetColumn(arrow, 2);
        root.Children.Add(arrow);
        return root;
    }

    private void OnPermissionSelectorClick(object sender, RoutedEventArgs e)
    {
        var flyout = SelectorFlyout(210, 320, out var panel);
        AddSelectorOption(panel, flyout, Symbol.Help, "请求批准", "敏感操作由用户审批同意", _selectedAiPermission == "请求批准", true, () => SelectPermission("请求批准"));
        AddSelectorOption(panel, flyout, Symbol.Accept, "替我审批", "AI 自行根据情况审批", _selectedAiPermission == "替我审批", true, () => SelectPermission("替我审批"));
        AddSelectorOption(panel, flyout, Symbol.Permissions, "完全访问", "放开全部权限，不需要审批", _selectedAiPermission == "完全访问", true, () => SelectPermission("完全访问"));
        flyout.ShowAt(_permissionSelector);
    }

    private void OnModelSelectorClick(object sender, RoutedEventArgs e)
    {
        var flyout = SelectorFlyout(180, 300, out var panel);
        foreach (var model in _settings.Current.AiModels)
        {
            var captured = model;
            AddSelectorOption(panel, flyout, Symbol.Find, model.Name, model.ModelId, _selectedAiModel?.ModelId == model.ModelId, true, () =>
            {
                _selectedAiModel = captured;
                RefreshSelectorLabels();
            });
        }
        AddSelectorOption(panel, flyout, Symbol.Add, "添加模型", "进入设置添加模型", false, false, async () => await ShowAddModelDialogAsync());
        flyout.ShowAt(_modelSelector);
    }

    private static Flyout SelectorFlyout(double minWidth, double maxWidth, out StackPanel panel)
    {
        panel = new StackPanel
        {
            MinWidth = minWidth,
            MaxWidth = maxWidth,
            Spacing = 4,
            Padding = new Thickness(6),
            Background = TransparentBrush(),
        };
        var shell = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = SurfaceBrush(),
            BorderBrush = BorderLightBrush(),
            BorderThickness = new Thickness(1),
            Child = panel,
        };
        var flyout = new Flyout
        {
            Content = shell,
            Placement = FlyoutPlacementMode.TopEdgeAlignedRight,
            FlyoutPresenterStyle = SelectorFlyoutPresenterStyle(),
        };
        return flyout;
    }

    private static Style SelectorFlyoutPresenterStyle()
    {
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(Control.BackgroundProperty, TransparentBrush()));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, TransparentBrush()));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        return style;
    }

    private static void AddSelectorOption(StackPanel panel, Flyout flyout, Symbol icon, string title, string description, bool selected, bool showSelection, Action action)
    {
        panel.Children.Add(SelectorOptionButton(icon, title, description, panel.MaxWidth, selected, showSelection, () =>
        {
            action();
            flyout.Hide();
        }));
    }

    private static void AddSelectorOption(StackPanel panel, Flyout flyout, Symbol icon, string title, string description, bool selected, bool showSelection, Func<Task> action)
    {
        panel.Children.Add(SelectorOptionButton(icon, title, description, panel.MaxWidth, selected, showSelection, async () =>
        {
            await action();
            flyout.Hide();
        }));
    }

    private static Button SelectorOptionButton(Symbol icon, string title, string description, double maxWidth, bool selected, bool showSelection, Action action)
    {
        return SelectorOptionButton(icon, title, description, maxWidth, selected, showSelection, () =>
        {
            action();
            return Task.CompletedTask;
        });
    }

    private static Button SelectorOptionButton(Symbol icon, string title, string description, double maxWidth, bool selected, bool showSelection, Func<Task> action)
    {
        var button = new Button
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 6, 8, 6),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(0),
            Background = TransparentBrush(),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = showSelection && selected ? "✓" : string.Empty,
                        Width = 16,
                        FontSize = 12,
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                        Foreground = PrimaryBrush(),
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    ToolbarGlyph(icon, 18, 13),
                    new StackPanel
                    {
                        MaxWidth = Math.Max(120, maxWidth - 78),
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            new TextBlock { Text = title, FontSize = 11, Foreground = PrimaryTextBrush(), VerticalAlignment = VerticalAlignment.Center, LineHeight = 14 },
                            new TextBlock { Text = description, FontSize = 10, Foreground = MutedBrush(), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, LineHeight = 13 },
                        },
                    },
                },
            },
        };
        ApplyButtonResources(button, TransparentBrush(), PrimaryTextBrush(), HoverBrush(), SurfaceAltBrush(), TransparentBrush(), new Thickness(0));
        button.Click += async (_, _) => await action();
        return button;
    }

    private void SelectPermission(string permission)
    {
        _selectedAiPermission = permission;
        RefreshSelectorLabels();
    }

    private void RefreshModelCombo()
    {
        if (_selectedAiModel is not null &&
            !_settings.Current.AiModels.Any(model => ReferenceEquals(model, _selectedAiModel) || model.ModelId == _selectedAiModel.ModelId))
        {
            _selectedAiModel = null;
        }
        _selectedAiModel ??= _settings.Current.AiModels.FirstOrDefault();
        RefreshSelectorLabels();
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
                CornerRadius = new CornerRadius(8),
            };
            ApplyButtonResources(remove, TransparentBrush(), SecondaryTextBrush(), HoverBrush(), SurfaceAltBrush(), BorderLightBrush(), new Thickness(0));
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

    private async Task SendAiMessageAsync()
    {
        if (_aiIsSending)
            return;

        if (_selectedAiModel is null)
        {
            AddAiVisibleMessage(new AiChatMessage
            {
                Role = "AI",
                Text = "请先去设置内配置并选择 AI 模型。",
                IsUser = false,
            }, persist: false);
            return;
        }

        if (string.IsNullOrWhiteSpace(_aiInput.Text) && _pendingAttachments.Count == 0)
            return;

        var permission = _selectedAiPermission;
        var userText = _aiInput.Text.Trim();
        var attachments = _pendingAttachments.ToList();

        var visibleUserMessage = new AiChatMessage
        {
            Role = "You",
            Text = userText,
            IsUser = true,
            Attachments = attachments,
        };
        AddAiVisibleMessage(visibleUserMessage);
        _aiConversation.Add(new AiConversationMessage
        {
            Role = "user",
            Text = userText,
            Attachments = attachments,
        });
        _aiInput.Text = string.Empty;
        _pendingAttachments.Clear();
        RenderPendingAttachments();
        CompressAiContextIfNeeded();

        _aiCancellation?.Dispose();
        _aiCancellation = new CancellationTokenSource();
        ResetAiStreamBuffers();
        SetAiSending(true);
        var stopwatch = Stopwatch.StartNew();
        var streamingMessage = AddAiStreamingMessage();
        try
        {
            var response = await _ai.SendStreamingAsync(
                new AiAgentRequest
                {
                    Model = _selectedAiModel,
                    Messages = _aiConversation.ToList(),
                    PermissionMode = permission,
                    CurrentDeviceId = _currentDetailDevice?.DeviceId,
                    CurrentDeviceName = _currentDetailDevice?.DisplayName,
                },
                delta =>
                {
                    QueueAiStreamDelta(streamingMessage, delta);
                    return Task.CompletedTask;
                },
                async toolCall => await _aiTools.ExecuteAsync(toolCall, permission, _currentDetailDevice, RequestAiToolApprovalAsync, _aiCancellation.Token),
                _aiCancellation.Token);

            FlushAiStreamDeltas(streamingMessage);
            stopwatch.Stop();
            _aiConversation.AddRange(response.NewMessages);
            var text = string.IsNullOrWhiteSpace(response.Text)
                ? streamingMessage.Message.Text
                : response.Text.Trim();
            if (string.IsNullOrWhiteSpace(text))
                text = "操作已完成。";
            var thinking = string.IsNullOrWhiteSpace(response.ThinkingText)
                ? streamingMessage.Message.ThinkingText
                : response.ThinkingText.Trim();
            streamingMessage.Message.Text = text;
            streamingMessage.Message.ThinkingText = thinking;
            streamingMessage.Message.ProcessingSeconds = stopwatch.Elapsed.TotalSeconds;
            streamingMessage.AnswerText.Text = text;
            streamingMessage.DurationText.Text = $"处理用时 {FormatDuration(stopwatch.Elapsed.TotalSeconds)}";
            CompleteStreamingThinking(streamingMessage);
            _settings.Current.AiMessages.Add(CloneVisibleAiMessage(streamingMessage.Message));
            SaveAiConversation();
            ScrollAiMessagesToBottom();
            Notify("AI 思考完成", $"处理用时 {FormatDuration(stopwatch.Elapsed.TotalSeconds)}", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            FlushAiStreamDeltas(streamingMessage);
            stopwatch.Stop();
            streamingMessage.Message.ProcessingSeconds = stopwatch.Elapsed.TotalSeconds;
            streamingMessage.Message.Text = string.IsNullOrWhiteSpace(streamingMessage.Message.Text)
                ? "已暂停生成。"
                : streamingMessage.Message.Text + "\n\n已暂停生成。";
            streamingMessage.AnswerText.Text = streamingMessage.Message.Text;
            streamingMessage.DurationText.Text = $"处理用时 {FormatDuration(stopwatch.Elapsed.TotalSeconds)}";
            CompleteStreamingThinking(streamingMessage);
            _settings.Current.AiMessages.Add(CloneVisibleAiMessage(streamingMessage.Message));
            SaveAiConversation();
            ScrollAiMessagesToBottom();
        }
        catch (Exception ex)
        {
            FlushAiStreamDeltas(streamingMessage);
            stopwatch.Stop();
            streamingMessage.Message.ProcessingSeconds = stopwatch.Elapsed.TotalSeconds;
            streamingMessage.Message.Text = $"AI 请求失败：{ex.Message}";
            streamingMessage.AnswerText.Text = streamingMessage.Message.Text;
            streamingMessage.DurationText.Text = $"处理用时 {FormatDuration(stopwatch.Elapsed.TotalSeconds)}";
            CompleteStreamingThinking(streamingMessage);
            _settings.Current.AiMessages.Add(CloneVisibleAiMessage(streamingMessage.Message));
            SaveAiConversation();
            ScrollAiMessagesToBottom();
        }
        finally
        {
            SetAiSending(false);
            _aiCancellation?.Dispose();
            _aiCancellation = null;
        }
    }

    private AiStreamingMessageUi AddAiStreamingMessage()
    {
        var message = new AiChatMessage
        {
            Role = "AI",
            Text = string.Empty,
            IsUser = false,
            CreatedAt = DateTimeOffset.Now,
        };
        var textBlock = new TextBlock
        {
            Text = string.Empty,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Foreground = PrimaryTextBrush(),
        };
        var durationText = new TextBlock
        {
            Text = "正在思考...",
            FontSize = 10,
            Foreground = MutedBrush(),
        };
        var thinkingText = new TextBlock
        {
            Text = string.Empty,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = SecondaryTextBrush(),
            MaxHeight = 110,
        };
        var thinkingSummary = new TextBlock
        {
            Text = "思考中",
            FontSize = 11,
            Foreground = SecondaryTextBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var thinkingBody = new Border
        {
            Tag = "ai-thinking-body",
            Margin = new Thickness(0, 6, 0, 0),
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(10),
            Background = SurfaceBrush(),
            BorderBrush = BorderLightBrush(),
            BorderThickness = new Thickness(1),
            Child = thinkingText,
            Visibility = Visibility.Visible,
        };
        var thinkingArrow = new TextBlock
        {
            Text = "⌃",
            FontSize = 12,
            Foreground = SecondaryTextBrush(),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var thinkingToggle = new Button
        {
            Tag = "ai-thinking-toggle",
            Padding = new Thickness(10, 7, 10, 7),
            CornerRadius = new CornerRadius(10),
            Background = TransparentBrush(),
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(),
                    new ColumnDefinition { Width = GridLength.Auto },
                },
                Children =
                {
                    thinkingSummary,
                    thinkingArrow,
                },
            },
        };
        Grid.SetColumn(thinkingArrow, 1);
        ApplyButtonResources(thinkingToggle, TransparentBrush(), SecondaryTextBrush(), HoverBrush(), SurfaceBrush(), TransparentBrush(), new Thickness(0));
        var thinkingShell = new Border
        {
            Tag = "ai-thinking-shell",
            CornerRadius = new CornerRadius(12),
            BorderBrush = BorderLightBrush(),
            BorderThickness = new Thickness(1),
            Background = TransparentBrush(),
            Visibility = Visibility.Collapsed,
            Child = new StackPanel
            {
                Children =
                {
                    thinkingToggle,
                    thinkingBody,
                },
            },
        };
        var thinkingExpanded = true;
        thinkingToggle.Click += (_, _) =>
        {
            thinkingExpanded = !thinkingExpanded;
            thinkingBody.Visibility = thinkingExpanded ? Visibility.Visible : Visibility.Collapsed;
            thinkingArrow.Text = thinkingExpanded ? "⌃" : "⌄";
        };
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = 340,
            Spacing = 4,
            Margin = new Thickness(0, 0, 0, 16),
        };
        panel.Children.Add(new TextBlock
        {
            Text = "AI",
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = PrimaryBrush(),
        });
        panel.Children.Add(new Border
        {
            Tag = "ai-bubble",
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 10, 14, 10),
            Background = SurfaceAltBrush(),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    durationText,
                    thinkingShell,
                    textBlock,
                },
            },
        });
        _messageList.Children.Add(panel);
        ScrollAiMessagesToBottom();
        return new AiStreamingMessageUi(message, textBlock, thinkingShell, thinkingText, thinkingSummary, thinkingBody, thinkingArrow, durationText);
    }

    private void ResetAiStreamBuffers()
    {
        lock (_aiStreamLock)
        {
            _pendingAiAnswerDelta.Clear();
            _pendingAiThinkingDelta.Clear();
            _aiStreamFlushQueued = false;
            _aiStreamHasAnswer = false;
        }
    }

    private void QueueAiStreamDelta(AiStreamingMessageUi streamingMessage, AiStreamDelta delta)
    {
        if (string.IsNullOrEmpty(delta.Text))
            return;

        lock (_aiStreamLock)
        {
            if (delta.IsThinking)
                _pendingAiThinkingDelta.Append(delta.Text);
            else
                _pendingAiAnswerDelta.Append(delta.Text);

            if (_aiStreamFlushQueued)
                return;

            _aiStreamFlushQueued = true;
        }

        _ = Task.Delay(40, _aiCancellation?.Token ?? CancellationToken.None)
            .ContinueWith(
                _ =>
                {
                    DispatcherQueue.TryEnqueue(() => FlushAiStreamDeltas(streamingMessage));
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    private void FlushAiStreamDeltas(AiStreamingMessageUi streamingMessage)
    {
        string thinkingDelta;
        string answerDelta;
        lock (_aiStreamLock)
        {
            thinkingDelta = _pendingAiThinkingDelta.ToString();
            answerDelta = _pendingAiAnswerDelta.ToString();
            _pendingAiThinkingDelta.Clear();
            _pendingAiAnswerDelta.Clear();
            _aiStreamFlushQueued = false;
        }

        if (thinkingDelta.Length > 0)
        {
            streamingMessage.Message.ThinkingText += thinkingDelta;
            streamingMessage.ThinkingText.Text = streamingMessage.Message.ThinkingText;
            streamingMessage.ThinkingSummary.Text = $"思考中 · {BuildThinkingSummary(streamingMessage.Message.ThinkingText)}";
            streamingMessage.ThinkingShell.Visibility = Visibility.Visible;
            streamingMessage.ThinkingBody.Visibility = Visibility.Visible;
            streamingMessage.ThinkingArrow.Text = "⌃";
        }

        if (answerDelta.Length > 0)
        {
            streamingMessage.Message.Text += answerDelta;
            streamingMessage.AnswerText.Text = streamingMessage.Message.Text;
            if (!_aiStreamHasAnswer && !string.IsNullOrWhiteSpace(streamingMessage.Message.ThinkingText))
            {
                _aiStreamHasAnswer = true;
                CollapseStreamingThinking(streamingMessage);
            }
        }

        if (thinkingDelta.Length > 0 || answerDelta.Length > 0)
            ScrollAiMessagesToBottom();
    }

    private static void CollapseStreamingThinking(AiStreamingMessageUi streamingMessage)
    {
        streamingMessage.ThinkingBody.Visibility = Visibility.Collapsed;
        streamingMessage.ThinkingArrow.Text = "⌄";
    }

    private static void CompleteStreamingThinking(AiStreamingMessageUi streamingMessage)
    {
        if (string.IsNullOrWhiteSpace(streamingMessage.Message.ThinkingText))
        {
            streamingMessage.ThinkingShell.Visibility = Visibility.Collapsed;
            return;
        }

        streamingMessage.ThinkingShell.Visibility = Visibility.Visible;
        streamingMessage.ThinkingSummary.Text = $"思考完成 · {BuildThinkingSummary(streamingMessage.Message.ThinkingText)}";
        CollapseStreamingThinking(streamingMessage);
    }

    private static UIElement BuildCollapsedThinkingBlock(string thinkingText)
    {
        var content = new TextBlock
        {
            Text = thinkingText,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = SecondaryTextBrush(),
            MaxHeight = 120,
        };
        var body = new Border
        {
            Tag = "ai-thinking-body",
            Margin = new Thickness(0, 6, 0, 0),
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(10),
            Background = SurfaceBrush(),
            BorderBrush = BorderLightBrush(),
            BorderThickness = new Thickness(1),
            Child = content,
            Visibility = Visibility.Collapsed,
        };
        var title = new TextBlock
        {
            Text = $"思考完成 · {BuildThinkingSummary(thinkingText)}",
            FontSize = 11,
            Foreground = SecondaryTextBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var arrow = new TextBlock
        {
            Text = "⌄",
            FontSize = 12,
            Foreground = SecondaryTextBrush(),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var header = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto },
            },
            Children = { title, arrow },
        };
        Grid.SetColumn(arrow, 1);
        var expanded = false;
        var toggle = new Button
        {
            Tag = "ai-thinking-toggle",
            Padding = new Thickness(10, 7, 10, 7),
            CornerRadius = new CornerRadius(10),
            Background = TransparentBrush(),
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = header,
        };
        ApplyButtonResources(toggle, TransparentBrush(), SecondaryTextBrush(), HoverBrush(), SurfaceBrush(), TransparentBrush(), new Thickness(0));
        toggle.Click += (_, _) =>
        {
            expanded = !expanded;
            body.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            arrow.Text = expanded ? "⌃" : "⌄";
        };
        return new Border
        {
            Tag = "ai-thinking-shell",
            CornerRadius = new CornerRadius(12),
            BorderBrush = BorderLightBrush(),
            BorderThickness = new Thickness(1),
            Background = TransparentBrush(),
            Child = new StackPanel
            {
                Children =
                {
                    toggle,
                    body,
                },
            },
        };
    }

    private static string BuildThinkingSummary(string text)
    {
        var compact = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(compact))
            return "查看思考过程";
        return compact.Length <= 28 ? compact : compact[..28] + "...";
    }

    private static string FormatDuration(double seconds)
    {
        return seconds < 60
            ? $"{seconds:0.0} 秒"
            : $"{Math.Floor(seconds / 60):0} 分 {seconds % 60:0} 秒";
    }

    private static string FormatMessageTime(DateTimeOffset time)
    {
        return time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    }

    private void AddAiVisibleMessage(AiChatMessage message, bool persist = true)
    {
        message.CreatedAt = DateTimeOffset.Now;
        _messageList.Children.Add(MessageBubble(message));
        ScrollAiMessagesToBottom();
        if (persist)
        {
            _settings.Current.AiMessages.Add(CloneVisibleAiMessage(message));
            SaveAiConversation();
        }
    }

    private void RebuildAiConversationFromVisibleMessages()
    {
        _aiConversation.Clear();
        foreach (var message in _settings.Current.AiMessages)
        {
            _aiConversation.Add(new AiConversationMessage
            {
                Role = message.IsUser ? "user" : "assistant",
                Text = message.Text,
                ThinkingText = message.ThinkingText,
                Attachments = message.Attachments.ToList(),
            });
        }
    }

    private void CompressAiContextIfNeeded()
    {
        const int keepMessages = 24;
        const int maxCharacters = 16000;
        var totalCharacters = _aiConversation.Sum(message => message.Text?.Length ?? 0);
        if (_aiConversation.Count <= keepMessages + 1 && totalCharacters <= maxCharacters)
            return;

        var older = _aiConversation.Take(Math.Max(0, _aiConversation.Count - keepMessages)).ToList();
        var recent = _aiConversation.Skip(Math.Max(0, _aiConversation.Count - keepMessages)).ToList();
        var summary = BuildLocalContextSummary(older);
        _aiConversation.Clear();
        _aiConversation.Add(new AiConversationMessage
        {
            Role = "system",
            Text = "以下是较早对话的本地压缩摘要，用于保持上下文连续：" + Environment.NewLine + summary,
        });
        _aiConversation.AddRange(recent);
    }

    private static string BuildLocalContextSummary(IEnumerable<AiConversationMessage> messages)
    {
        var lines = new List<string>();
        foreach (var message in messages)
        {
            if (string.IsNullOrWhiteSpace(message.Text) && message.Attachments.Count == 0)
                continue;

            var role = message.Role switch
            {
                "assistant" => "AI",
                "tool" => "工具",
                "system" => "系统",
                _ => "用户",
            };
            var text = string.IsNullOrWhiteSpace(message.Text) ? "[附件或工具上下文]" : message.Text.Trim();
            if (text.Length > 420)
                text = text[..420] + "...";
            lines.Add($"{role}: {text}");
        }

        var summary = string.Join(Environment.NewLine, lines);
        return summary.Length > 5000 ? summary[^5000..] : summary;
    }

    private void SaveAiConversation()
    {
        const int maxVisibleMessages = 80;
        if (_settings.Current.AiMessages.Count > maxVisibleMessages)
            _settings.Current.AiMessages = _settings.Current.AiMessages.TakeLast(maxVisibleMessages).ToList();
        _settings.Save();
    }

    private static AiChatMessage CloneVisibleAiMessage(AiChatMessage message)
    {
        return new AiChatMessage
        {
            Role = message.Role,
            Text = message.Text,
            ThinkingText = message.ThinkingText,
            ProcessingSeconds = message.ProcessingSeconds,
            IsUser = message.IsUser,
            CreatedAt = message.CreatedAt,
            Attachments = message.Attachments.Select(attachment => new AiAttachment
            {
                Name = attachment.Name,
                Path = attachment.Path,
                IsImage = attachment.IsImage,
            }).ToList(),
        };
    }

    private void SetAiSending(bool sending)
    {
        _aiIsSending = sending;
        if (_aiSendButton is not null)
        {
            _aiSendButton.IsEnabled = true;
            _aiSendButton.Opacity = 1;
        }
        if (_aiSendIcon is not null)
            _aiSendIcon.Glyph = sending ? "\uE769" : "\uE74A";
        if (_aiSendCircle is not null)
            _aiSendCircle.Background = sending ? PrimaryPressedBrush() : PrimaryBrush();
        _aiInput.PlaceholderText = sending ? "AI 正在生成..." : "要求后续变更...";
        if (sending)
            StartAiThinkingAnimation();
        else
            StopAiThinkingAnimation();
    }

    private void StartAiThinkingAnimation()
    {
        if (_aiButton is null)
            return;

        _aiThinkingStoryboard?.Stop();
        _aiButton.Opacity = 1;
        var pulse = new DoubleAnimation
        {
            From = 1,
            To = 0.58,
            Duration = new Duration(TimeSpan.FromMilliseconds(520)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(pulse, _aiButton);
        Storyboard.SetTargetProperty(pulse, "Opacity");
        _aiThinkingStoryboard = new Storyboard();
        _aiThinkingStoryboard.Children.Add(pulse);
        _aiThinkingStoryboard.Begin();
    }

    private void StopAiThinkingAnimation()
    {
        _aiThinkingStoryboard?.Stop();
        _aiThinkingStoryboard = null;
        if (_aiButton is not null)
            _aiButton.Opacity = 1;
    }

    private void CancelAiGeneration()
    {
        _aiCancellation?.Cancel();
    }

    private Button BuildAiScrollBottomButton()
    {
        var glyph = new FontIcon
        {
            Glyph = "\uE70D",
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 14,
            Foreground = OnPrimaryBrush(),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var circle = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(17),
            Background = PrimaryBrush(),
            BorderBrush = PrimaryHoverBrush(),
            BorderThickness = new Thickness(1),
            Child = glyph,
        };
        var button = new Button
        {
            Content = circle,
            Width = 34,
            Height = 34,
            MinWidth = 34,
            MinHeight = 34,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(17),
            Background = TransparentBrush(),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 22, 16),
            Opacity = 0,
            Visibility = Visibility.Collapsed,
        };
        ApplyButtonResources(button, TransparentBrush(), OnPrimaryBrush(), TransparentBrush(), TransparentBrush(), TransparentBrush(), new Thickness(0));
        button.PointerEntered += (_, _) => circle.Background = PrimaryHoverBrush();
        button.PointerExited += (_, _) => circle.Background = PrimaryBrush();
        button.Click += (_, _) => ScrollAiMessagesToBottom();
        return button;
    }

    private void OnAiMessageScrollerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_aiMessageScroller is null)
            return;

        var atBottom = _aiMessageScroller.ScrollableHeight <= 2 ||
            _aiMessageScroller.VerticalOffset >= _aiMessageScroller.ScrollableHeight - 24;
        SetAiScrollBottomButtonVisible(!atBottom);
    }

    private void SetAiScrollBottomButtonVisible(bool visible)
    {
        if (_aiScrollBottomButton is null || _aiScrollBottomButtonVisible == visible)
            return;

        _aiScrollBottomButtonVisible = visible;
        if (visible)
            _aiScrollBottomButton.Visibility = Visibility.Visible;

        var fade = new DoubleAnimation
        {
            To = visible ? 1 : 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(150)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, _aiScrollBottomButton);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        if (!visible)
            storyboard.Completed += (_, _) =>
            {
                if (!_aiScrollBottomButtonVisible && _aiScrollBottomButton is not null)
                    _aiScrollBottomButton.Visibility = Visibility.Collapsed;
            };
        storyboard.Begin();
    }

    private void ScrollAiMessagesToBottom()
    {
        ScrollAiMessagesToBottomPass(0);
        SetAiScrollBottomButtonVisible(false);
    }

    private void ScrollAiMessagesToBottomPass(int pass)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (_aiMessageScroller is null)
                return;

            _messageList.UpdateLayout();
            _aiMessageScroller.UpdateLayout();
            _aiMessageScroller.ChangeView(null, _aiMessageScroller.ScrollableHeight, null, true);
            if (pass < 2)
                ScrollAiMessagesToBottomPass(pass + 1);
        });
    }

    private Task RunOnUiThreadAsync(Action action)
    {
        var completion = new TaskCompletionSource();
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }))
        {
            completion.SetException(new InvalidOperationException("无法切换到 UI 线程。"));
        }

        return completion.Task;
    }

    private async Task<bool> RequestAiToolApprovalAsync(AiAgentToolCall toolCall, string command)
    {
        var approved = false;
        ContentDialog? dialog = null;
        var content = new StackPanel
        {
            Spacing = 10,
            Width = 420,
            Children =
            {
                new TextBlock
                {
                    Text = "AI 请求执行 ADB 命令",
                    FontSize = 20,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = PrimaryTextBrush(),
                },
                new TextBlock
                {
                    Text = "权限模式为“请求批准”，请确认是否允许本次工具调用。",
                    Foreground = SecondaryTextBrush(),
                    TextWrapping = TextWrapping.Wrap,
                },
                new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Background = ShellBrush(),
                    Padding = new Thickness(12),
                    Child = new TextBlock
                    {
                        Text = command,
                        FontFamily = new FontFamily("Consolas"),
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = ShellTextBrush(),
                    },
                },
            },
        };
        var actions = new Grid
        {
            ColumnSpacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        var reject = SecondaryButton("拒绝");
        var allow = PrimaryButton("允许");
        reject.Click += (_, _) =>
        {
            approved = false;
            dialog?.Hide();
        };
        allow.Click += (_, _) =>
        {
            approved = true;
            dialog?.Hide();
        };
        actions.Children.Add(reject);
        Grid.SetColumn(allow, 1);
        actions.Children.Add(allow);
        content.Children.Add(actions);

        dialog = DialogChrome(string.Empty, content);
        await dialog.ShowAsync();
        return approved;
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
        if (!message.IsUser && message.ProcessingSeconds is double seconds)
        {
            body.Children.Add(new TextBlock
            {
                Text = $"处理用时 {FormatDuration(seconds)}",
                FontSize = 10,
                Foreground = MutedBrush(),
            });
        }
        if (!message.IsUser && !string.IsNullOrWhiteSpace(message.ThinkingText))
            body.Children.Add(BuildCollapsedThinkingBlock(message.ThinkingText));
        if (!string.IsNullOrWhiteSpace(message.Text))
        {
            body.Children.Add(new TextBlock
            {
                Text = message.Text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Foreground = message.IsUser ? OnPrimaryBrush() : PrimaryTextBrush(),
            });
        }
        foreach (var attachment in message.Attachments)
            body.Children.Add(AttachmentChip(attachment, false));

        panel.Children.Add(new Border
        {
            Tag = message.IsUser ? "ai-bubble-user" : "ai-bubble",
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 10, 14, 10),
            Background = message.IsUser
                ? PrimaryBrush()
                : SurfaceAltBrush(),
            Child = body,
        });
        panel.Children.Add(new TextBlock
        {
            Text = FormatMessageTime(message.CreatedAt),
            FontSize = 10,
            Foreground = MutedBrush(),
            HorizontalAlignment = message.IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = new Thickness(4, -8, 4, 0),
        });
        return panel;
    }

    private async Task ShowAddDeviceDialogAsync()
    {
        var wirelessPanel = new StackPanel { Spacing = 12 };
        var usbPanel = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed };
        var selectedMode = "wireless";
        var selectedWirelessStage = "new";
        DeviceModel? selectedUsbDevice = null;

        var mode = new StackPanel
        {
            Spacing = 8,
        };
        mode.Children.Add(new TextBlock { Text = "添加方式", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = PrimaryTextBrush() });
        var radioRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var wirelessMode = SegmentButton("无线 ADB");
        var usbMode = SegmentButton("有线 ADB");
        void SelectMode(string value)
        {
            selectedMode = value;
            var isWireless = value == "wireless";
            wirelessPanel.Visibility = isWireless ? Visibility.Visible : Visibility.Collapsed;
            usbPanel.Visibility = isWireless ? Visibility.Collapsed : Visibility.Visible;
            ApplySegmentState(wirelessMode, isWireless);
            ApplySegmentState(usbMode, !isWireless);
        }

        wirelessMode.Click += (_, _) => SelectMode("wireless");
        usbMode.Click += (_, _) => SelectMode("usb");
        radioRow.Children.Add(wirelessMode);
        radioRow.Children.Add(usbMode);
        mode.Children.Add(radioRow);

        var ip = RoundedTextBox("192.168.1.100");
        var pairPort = RoundedTextBox("端口");
        var pairCode = RoundedTextBox("123456");
        var connectIp = RoundedTextBox("192.168.1.100");
        var connectPort = RoundedTextBox("5555");
        var wirelessStatus = new TextBlock { Foreground = MutedBrush(), TextWrapping = TextWrapping.Wrap };
        var pairStagePanel = new StackPanel { Spacing = 12 };
        var connectStagePanel = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed };
        var newWireless = SegmentButton("未连接过的设备");
        var knownWireless = SegmentButton("已配对设备");
        void SelectWirelessStage(string stage)
        {
            selectedWirelessStage = stage;
            var isNew = stage == "new";
            pairStagePanel.Visibility = isNew ? Visibility.Visible : Visibility.Collapsed;
            connectStagePanel.Visibility = isNew ? Visibility.Collapsed : Visibility.Visible;
            ApplySegmentState(newWireless, isNew);
            ApplySegmentState(knownWireless, !isNew);
        }
        newWireless.Click += (_, _) => SelectWirelessStage("new");
        knownWireless.Click += (_, _) => SelectWirelessStage("known");
        var pairButton = SecondaryButton("开始配对");
        pairButton.Click += async (_, _) =>
        {
            if (!int.TryParse(pairPort.Text, out var p))
            {
                wirelessStatus.Text = "请填写有效配对端口。";
                return;
            }
            var result = await _devices.PairAsync(ip.Text.Trim(), p, pairCode.Text.Trim());
            if (result.Success)
            {
                connectIp.Text = ip.Text.Trim();
                wirelessStatus.Text = "配对成功，已切换到已配对设备连接模式。";
                SelectWirelessStage("known");
            }
            else
            {
                wirelessStatus.Text = FailureText(result);
            }
        };
        wirelessPanel.Children.Add(HintCard("适用于 Android 11 及以上设备。需在开发者选项中开启「无线调试」，然后使用配对码配对。"));
        wirelessPanel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { newWireless, knownWireless },
        });
        pairStagePanel.Children.Add(SectionTitle("未连接过的设备：先验证再连接"));
        pairStagePanel.Children.Add(BodyText("在手机无线调试页面点击「使用配对码配对设备」，输入显示的 IP、端口和配对码"));
        var pairGrid = TwoColumnGrid();
        pairGrid.Children.Add(LabeledField("IP 地址", ip, 0));
        pairGrid.Children.Add(LabeledField("端口", pairPort, 1));
        pairStagePanel.Children.Add(pairGrid);
        pairStagePanel.Children.Add(LabeledField("配对码", pairCode));
        pairStagePanel.Children.Add(pairButton);
        pairStagePanel.Children.Add(wirelessStatus);
        connectStagePanel.Children.Add(SectionTitle("已配对设备：直接连接"));
        connectStagePanel.Children.Add(BodyText("已经完成配对的设备只需要输入无线调试页面显示的 IP 和连接端口。"));
        var connectGrid = TwoColumnGrid();
        connectGrid.Children.Add(LabeledField("IP 地址", connectIp, 0));
        connectGrid.Children.Add(LabeledField("端口", connectPort, 1));
        connectStagePanel.Children.Add(connectGrid);
        wirelessPanel.Children.Add(pairStagePanel);
        wirelessPanel.Children.Add(connectStagePanel);

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
                var choose = SecondaryButton($"{device.DisplayName}  ·  {device.DeviceId}");
                choose.HorizontalAlignment = HorizontalAlignment.Stretch;
                choose.Tag = device;
                choose.Click += (_, _) =>
                {
                    selectedUsbDevice = device;
                    foreach (var child in usbList.Children.OfType<Button>())
                        ApplySegmentState(child, ReferenceEquals(child.Tag, selectedUsbDevice));
                };
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
        SelectMode("wireless");
        SelectWirelessStage("new");

        ContentDialog? dialog = null;
        var body = new StackPanel
        {
            Width = 432,
            Spacing = 14,
        };
        var dialogScroller = new ScrollViewer
        {
            Content = stack,
            MaxHeight = 456,
            Padding = new Thickness(0, 0, 12, 0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        StyleScrollViewer(dialogScroller);
        AttachWheelScrolling(dialogScroller);
        body.Children.Add(dialogScroller);
        var actions = new Grid
        {
            ColumnSpacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        var cancel = SecondaryButton("取消");
        var submit = PrimaryButton("立即添加");
        actions.Children.Add(cancel);
        Grid.SetColumn(submit, 1);
        actions.Children.Add(submit);
        body.Children.Add(actions);

        cancel.Click += (_, _) => dialog?.Hide();
        submit.Click += async (_, _) =>
        {
            if (selectedMode == "usb")
            {
                if (selectedUsbDevice is null)
                {
                    usbStatus.Text = "请先扫描并选择一台 USB ADB 设备。";
                    return;
                }
                _devices.SaveUsbDevice(selectedUsbDevice, string.Empty);
                Notify("设备已添加", selectedUsbDevice.DisplayName, InfoBarSeverity.Success);
                dialog?.Hide();
                ShowDevices();
                return;
            }

            if (!int.TryParse(connectPort.Text, out var port))
            {
                wirelessStatus.Text = "请填写有效连接端口。";
                return;
            }

            var addResult = await _devices.ConnectAndSaveAsync(connectIp.Text.Trim(), port, string.Empty);
            if (addResult.Success || addResult.Stdout.Contains("connected", StringComparison.OrdinalIgnoreCase))
            {
                Notify("设备已添加", $"{connectIp.Text}:{port}", InfoBarSeverity.Success);
                dialog?.Hide();
                ShowDevices();
            }
            else
            {
                wirelessStatus.Text = FailureText(addResult);
            }
        };
        dialog = DialogChrome(string.Empty, body);
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
        };
        StylePasswordBox(apiKey);
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

        ContentDialog? dialog = null;
        var actions = new Grid
        {
            ColumnSpacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        var cancel = SecondaryButton("取消");
        var add = PrimaryButton("添加模型");
        cancel.Click += (_, _) => dialog?.Hide();
        add.Click += (_, _) =>
        {
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
            dialog?.Hide();
        };
        actions.Children.Add(cancel);
        Grid.SetColumn(add, 1);
        actions.Children.Add(add);
        stack.Children.Add(actions);

        dialog = DialogChrome(string.Empty, stack);
        await dialog.ShowAsync();
    }

    private void ToggleAiPanel()
    {
        if (_aiPanel.Visibility == Visibility.Visible)
            HideAiPanel();
        else
            ShowAiPanel();
    }

    private void ShowAiPanel()
    {
        _aiPanel.Visibility = Visibility.Visible;
        AnimateAiPanel(32, 0, 0, 1, null);
        if (_aiButton is not null)
            ApplyNavButtonState(_aiButton, true);
    }

    private void HideAiPanel()
    {
        AnimateAiPanel(0, 32, 1, 0, () =>
        {
            _aiPanel.Visibility = Visibility.Collapsed;
            if (_aiButton is not null)
                ApplyNavButtonState(_aiButton, false);
        });
    }

    private void AnimateAiPanel(double fromX, double toX, double fromOpacity, double toOpacity, Action? completed)
    {
        if (_aiPanel.RenderTransform is not TranslateTransform)
            _aiPanel.RenderTransform = new TranslateTransform();
        var slide = new DoubleAnimation
        {
            From = fromX,
            To = toX,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var fade = new DoubleAnimation
        {
            From = fromOpacity,
            To = toOpacity,
            Duration = new Duration(TimeSpan.FromMilliseconds(160)),
        };
        Storyboard.SetTarget(slide, _aiPanel);
        Storyboard.SetTargetProperty(slide, "(UIElement.RenderTransform).(TranslateTransform.X)");
        Storyboard.SetTarget(fade, _aiPanel);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(slide);
        storyboard.Children.Add(fade);
        if (completed is not null)
            storyboard.Completed += (_, _) => completed();
        storyboard.Begin();
    }

    private ContentDialog Dialog(string title, UIElement content, string primary)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = _root.XamlRoot,
            Title = string.IsNullOrWhiteSpace(title) ? null : title,
            Content = content,
            PrimaryButtonText = primary,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = s_darkTheme ? ElementTheme.Dark : ElementTheme.Light,
            Background = SurfaceBrush(),
            BorderBrush = BorderBrush(),
            Foreground = PrimaryTextBrush(),
        };
        StyleDialog(dialog);
        return dialog;
    }

    private ContentDialog DialogChrome(string title, UIElement content)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = _root.XamlRoot,
            Title = string.IsNullOrWhiteSpace(title) ? null : title,
            Content = content,
            RequestedTheme = s_darkTheme ? ElementTheme.Dark : ElementTheme.Light,
            Background = SurfaceBrush(),
            BorderBrush = BorderBrush(),
            Foreground = PrimaryTextBrush(),
        };
        StyleDialog(dialog);
        return dialog;
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
            Margin = PageMargin(),
        };
    }

    private static Thickness PageMargin() => new(16, 6, 16, 16);

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
        var button = new Button
        {
            Content = text,
            Background = PrimaryBrush(),
            Foreground = OnPrimaryBrush(),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 8, 14, 8),
            BorderThickness = new Thickness(0),
        };
        ApplyButtonResources(button, PrimaryBrush(), OnPrimaryBrush(), PrimaryHoverBrush(), PrimaryPressedBrush(), PrimaryBrush(), new Thickness(0));
        return button;
    }

    private static Button SecondaryButton(string text)
    {
        var button = new Button
        {
            Content = text,
            Background = SurfaceBrush(),
            Foreground = PrimaryTextBrush(),
            BorderBrush = BorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 8, 14, 8),
        };
        ApplyButtonResources(button, SurfaceBrush(), PrimaryTextBrush(), HoverBrush(), SurfaceAltBrush(), BorderBrush(), new Thickness(1));
        return button;
    }

    private static Button DangerButton(string text)
    {
        var background = s_darkTheme
            ? new SolidColorBrush(ColorHelper.FromArgb(42, 248, 113, 113))
            : new SolidColorBrush(ColorHelper.FromArgb(36, 220, 38, 38));
        var hover = s_darkTheme
            ? new SolidColorBrush(ColorHelper.FromArgb(62, 248, 113, 113))
            : new SolidColorBrush(ColorHelper.FromArgb(54, 220, 38, 38));
        var pressed = s_darkTheme
            ? new SolidColorBrush(ColorHelper.FromArgb(82, 248, 113, 113))
            : new SolidColorBrush(ColorHelper.FromArgb(70, 220, 38, 38));
        var foreground = s_darkTheme
            ? new SolidColorBrush(ColorHelper.FromArgb(255, 252, 165, 165))
            : new SolidColorBrush(ColorHelper.FromArgb(255, 185, 28, 28));
        var border = s_darkTheme
            ? new SolidColorBrush(ColorHelper.FromArgb(130, 248, 113, 113))
            : new SolidColorBrush(ColorHelper.FromArgb(110, 220, 38, 38));
        var button = new Button
        {
            Content = text,
            Background = background,
            Foreground = foreground,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 8, 14, 8),
        };
        ApplyButtonResources(button, background, foreground, hover, pressed, border, new Thickness(1));
        return button;
    }

    private static Button IconSquareButton(string glyph, double size)
    {
        var button = new Button
        {
            Width = size,
            Height = size,
            MinWidth = size,
            MinHeight = size,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(size / 2.8),
            Content = new TextBlock
            {
                Text = glyph,
                FontSize = size * 0.58,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                LineHeight = size * 0.74,
            },
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ApplyButtonResources(button, TransparentBrush(), SecondaryTextBrush(), HoverBrush(), SurfaceAltBrush(), BorderLightBrush(), new Thickness(0));
        return button;
    }

    private static Button SegmentButton(string text)
    {
        var button = SecondaryButton(text);
        button.MinHeight = 34;
        button.Padding = new Thickness(12, 7, 12, 7);
        return button;
    }

    private static void ApplySegmentState(Button button, bool active)
    {
        ApplyButtonResources(
            button,
            active ? PrimaryLightBrush() : SurfaceBrush(),
            active ? PrimaryBrush() : PrimaryTextBrush(),
            active ? PrimaryLightBrush() : HoverBrush(),
            active ? PrimaryLightBrush() : SurfaceAltBrush(),
            active ? PrimaryBrush() : BorderBrush(),
            new Thickness(1));
        button.Background = active ? PrimaryLightBrush() : SurfaceBrush();
        button.Foreground = active ? PrimaryBrush() : PrimaryTextBrush();
        button.BorderBrush = active ? PrimaryBrush() : BorderBrush();
    }

    private static Button DeviceToolTabButton(string text, Symbol icon)
    {
        var button = SecondaryButton(string.Empty);
        button.Width = 60;
        button.MinWidth = 60;
        button.Height = 56;
        button.Padding = new Thickness(0);
        button.CornerRadius = new CornerRadius(14);
        button.Content = new Grid
        {
            Width = 60,
            Height = 56,
            Children =
            {
                new Border
                {
                    Tag = "selection-surface",
                    CornerRadius = new CornerRadius(14),
                    Background = TransparentBrush(),
                },
                new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Spacing = 3,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new Grid
                        {
                            Width = 24,
                            Height = 24,
                            Clip = null,
                            Children =
                            {
                                DeviceToolTabIcon(icon),
                            },
                        },
                        new TextBlock { Text = text, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center },
                    },
                },
            },
        };
        ApplyButtonResources(button, TransparentBrush(), SecondaryTextBrush(), TransparentBrush(), TransparentBrush(), TransparentBrush(), new Thickness(0));
        return button;
    }

    private static FontIcon DeviceToolTabIcon(Symbol icon)
    {
        return new FontIcon
        {
            Glyph = DeviceToolTabGlyph(icon),
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 20,
            Width = 24,
            Height = 24,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static string DeviceToolTabGlyph(Symbol icon)
    {
        return icon switch
        {
            Symbol.Favorite => "\uE734",
            Symbol.Keyboard => "\uE765",
            Symbol.AllApps => "\uE71D",
            Symbol.Folder => "\uE8B7",
            Symbol.Setting => "\uE713",
            Symbol.Refresh => "\uE72C",
            _ => "\uE700",
        };
    }

    private static void ApplyDeviceTabState(Button button, bool active)
    {
        ApplyButtonResources(
            button,
            TransparentBrush(),
            active ? OnPrimaryBrush() : SecondaryTextBrush(),
            TransparentBrush(),
            TransparentBrush(),
            TransparentBrush(),
            new Thickness(0));
        button.Background = TransparentBrush();
        button.Foreground = active ? OnPrimaryBrush() : SecondaryTextBrush();
        button.BorderBrush = TransparentBrush();
        SetTaggedBorder(button.Content as UIElement, "selection-surface", active ? PrimaryBrush() : TransparentBrush());
        if (button.Content is UIElement content)
            ApplyForeground(content, active ? OnPrimaryBrush() : SecondaryTextBrush());
    }

    private static Button SwitchButton(bool isOn)
    {
        var knob = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = OnPrimaryBrush(),
            HorizontalAlignment = isOn ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = new Thickness(3),
        };
        var track = new Grid
        {
            Width = 44,
            Height = 24,
            Children = { knob },
        };
        var button = new Button
        {
            Width = 46,
            Height = 26,
            MinWidth = 46,
            MinHeight = 26,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(13),
            BorderThickness = new Thickness(1),
            Content = track,
        };
        ApplySwitchState(button, isOn);
        return button;
    }

    private static void ApplySwitchState(Button button, bool isOn)
    {
        button.Resources["SwitchIsOn"] = isOn;
        ApplyButtonResources(
            button,
            isOn ? PrimaryBrush() : SurfaceAltBrush(),
            OnPrimaryBrush(),
            isOn ? PrimaryHoverBrush() : HoverBrush(),
            isOn ? PrimaryPressedBrush() : SurfaceBrush(),
            isOn ? PrimaryBrush() : BorderBrush(),
            new Thickness(1));
        button.Background = isOn ? PrimaryBrush() : SurfaceAltBrush();
        button.BorderBrush = isOn ? PrimaryBrush() : BorderBrush();
        if (button.Content is Grid track && track.Children.FirstOrDefault() is Border knob)
        {
            knob.HorizontalAlignment = isOn ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            knob.Background = OnPrimaryBrush();
        }
    }

    private static NumberBox StyledNumberBox(double value, double min, double max, double width)
    {
        var box = new NumberBox
        {
            Value = value,
            Minimum = min,
            Maximum = max,
            Width = width,
            CornerRadius = new CornerRadius(14),
            Background = SurfaceBrush(),
            Foreground = PrimaryTextBrush(),
            BorderBrush = BorderBrush(),
        };
        box.UseLayoutRounding = true;
        box.Resources["TextControlBackground"] = SurfaceBrush();
        box.Resources["TextControlBackgroundPointerOver"] = HoverBrush();
        box.Resources["TextControlBackgroundFocused"] = SurfaceBrush();
        box.Resources["TextControlBackgroundDisabled"] = SurfaceBrush();
        box.Resources["TextControlForeground"] = PrimaryTextBrush();
        box.Resources["TextControlForegroundPointerOver"] = PrimaryTextBrush();
        box.Resources["TextControlForegroundFocused"] = PrimaryTextBrush();
        box.Resources["TextControlPlaceholderForeground"] = MutedBrush();
        box.Resources["TextControlPlaceholderForegroundPointerOver"] = MutedBrush();
        box.Resources["TextControlPlaceholderForegroundFocused"] = MutedBrush();
        box.Resources["TextControlBorderBrush"] = BorderBrush();
        box.Resources["TextControlBorderBrushPointerOver"] = BorderLightBrush();
        box.Resources["TextControlBorderBrushFocused"] = PrimaryBrush();
        box.Resources["FocusVisualPrimaryBrush"] = TransparentBrush();
        box.Resources["FocusVisualSecondaryBrush"] = TransparentBrush();
        return box;
    }

    private static void StyleTextBox(TextBox box)
    {
        box.UseLayoutRounding = true;
        box.Background = SurfaceBrush();
        box.Foreground = PrimaryTextBrush();
        box.BorderBrush = BorderBrush();
        box.BorderThickness = new Thickness(1);
        box.CornerRadius = new CornerRadius(12);
        box.Padding = new Thickness(12, 8, 12, 8);
        box.Resources["TextControlBackground"] = box.Background;
        box.Resources["TextControlBackgroundPointerOver"] = HoverBrush();
        box.Resources["TextControlBackgroundFocused"] = SurfaceBrush();
        box.Resources["TextControlBackgroundDisabled"] = SurfaceBrush();
        box.Resources["TextControlForeground"] = box.Foreground;
        box.Resources["TextControlForegroundPointerOver"] = PrimaryTextBrush();
        box.Resources["TextControlForegroundFocused"] = PrimaryTextBrush();
        box.Resources["TextControlPlaceholderForeground"] = MutedBrush();
        box.Resources["TextControlPlaceholderForegroundPointerOver"] = MutedBrush();
        box.Resources["TextControlPlaceholderForegroundFocused"] = MutedBrush();
        box.Resources["TextControlBorderBrush"] = BorderBrush();
        box.Resources["TextControlBorderBrushPointerOver"] = BorderLightBrush();
        box.Resources["TextControlBorderBrushFocused"] = PrimaryBrush();
        box.Resources["FocusVisualPrimaryBrush"] = TransparentBrush();
        box.Resources["FocusVisualSecondaryBrush"] = TransparentBrush();
    }

    private static void StylePasswordBox(PasswordBox box)
    {
        box.UseLayoutRounding = true;
        box.Background = SurfaceBrush();
        box.Foreground = PrimaryTextBrush();
        box.BorderBrush = BorderBrush();
        box.BorderThickness = new Thickness(1);
        box.CornerRadius = new CornerRadius(12);
        box.Padding = new Thickness(12, 8, 12, 8);
        box.Resources["TextControlBackground"] = SurfaceBrush();
        box.Resources["TextControlBackgroundPointerOver"] = HoverBrush();
        box.Resources["TextControlBackgroundFocused"] = SurfaceBrush();
        box.Resources["TextControlBackgroundDisabled"] = SurfaceBrush();
        box.Resources["TextControlForeground"] = PrimaryTextBrush();
        box.Resources["TextControlForegroundPointerOver"] = PrimaryTextBrush();
        box.Resources["TextControlForegroundFocused"] = PrimaryTextBrush();
        box.Resources["TextControlBorderBrush"] = BorderBrush();
        box.Resources["TextControlBorderBrushPointerOver"] = BorderLightBrush();
        box.Resources["TextControlBorderBrushFocused"] = PrimaryBrush();
        box.Resources["FocusVisualPrimaryBrush"] = TransparentBrush();
        box.Resources["FocusVisualSecondaryBrush"] = TransparentBrush();
    }

    private static void StyleComboBox(ComboBox combo)
    {
        combo.UseLayoutRounding = true;
        combo.Foreground = PrimaryTextBrush();
        combo.Background = TransparentBrush();
        combo.BorderBrush = BorderLightBrush();
        combo.BorderThickness = new Thickness(1);
        combo.Resources["ComboBoxBackground"] = TransparentBrush();
        combo.Resources["ComboBoxBackgroundPointerOver"] = HoverBrush();
        combo.Resources["ComboBoxBackgroundPressed"] = SurfaceAltBrush();
        combo.Resources["ComboBoxBackgroundFocused"] = HoverBrush();
        combo.Resources["ComboBoxBackgroundDisabled"] = TransparentBrush();
        combo.Resources["ComboBoxForeground"] = PrimaryTextBrush();
        combo.Resources["ComboBoxForegroundPointerOver"] = PrimaryTextBrush();
        combo.Resources["ComboBoxForegroundFocused"] = PrimaryTextBrush();
        combo.Resources["ComboBoxForegroundDisabled"] = SecondaryTextBrush();
        combo.Resources["ComboBoxBorderBrush"] = BorderLightBrush();
        combo.Resources["ComboBoxBorderBrushPointerOver"] = PrimaryBrush();
        combo.Resources["ComboBoxBorderBrushPressed"] = PrimaryBrush();
        combo.Resources["ComboBoxBorderBrushFocused"] = PrimaryBrush();
        combo.Resources["ComboBoxBorderBrushDisabled"] = BorderLightBrush();
        combo.Resources["ComboBoxDropDownBackground"] = SurfaceBrush();
        combo.Resources["ComboBoxItemBackgroundPointerOver"] = HoverBrush();
        combo.Resources["ComboBoxItemBackgroundSelected"] = PrimaryLightBrush();
        combo.Resources["ComboBoxItemForegroundSelected"] = PrimaryBrush();
        combo.Resources["FocusVisualPrimaryBrush"] = TransparentBrush();
        combo.Resources["FocusVisualSecondaryBrush"] = TransparentBrush();
    }

    private static void StyleListView(ListView list)
    {
        list.UseLayoutRounding = true;
        list.Background = SurfaceBrush();
        list.BorderBrush = BorderBrush();
        list.BorderThickness = new Thickness(1);
        list.CornerRadius = new CornerRadius(12);
        list.Padding = new Thickness(6);
        list.Resources["ListViewItemBackgroundPointerOver"] = HoverBrush();
        list.Resources["ListViewItemBackgroundSelected"] = PrimaryLightBrush();
        list.Resources["ListViewItemBackgroundSelectedPointerOver"] = PrimaryLightBrush();
        list.Resources["ListViewItemBackgroundPressed"] = SurfaceAltBrush();
        list.Resources["ListViewItemForegroundSelected"] = PrimaryBrush();
        list.Resources["FocusVisualPrimaryBrush"] = TransparentBrush();
        list.Resources["FocusVisualSecondaryBrush"] = TransparentBrush();
    }

    private static void StyleScrollViewer(ScrollViewer viewer)
    {
        viewer.UseLayoutRounding = true;
        viewer.Background = TransparentBrush();
        viewer.Resources["ScrollBarTrackFill"] = TransparentBrush();
        viewer.Resources["ScrollBarTrackFillPointerOver"] = TransparentBrush();
        viewer.Resources["ScrollBarThumbFill"] = BorderLightBrush();
        viewer.Resources["ScrollBarThumbFillPointerOver"] = SecondaryTextBrush();
        viewer.Resources["ScrollBarThumbFillPressed"] = PrimaryBrush();
        viewer.Resources["ScrollBarButtonBackground"] = TransparentBrush();
        viewer.Resources["ScrollBarButtonBackgroundPointerOver"] = HoverBrush();
        viewer.Resources["ScrollBarButtonBackgroundPressed"] = SurfaceAltBrush();
        viewer.Resources["ScrollBarButtonForeground"] = SecondaryTextBrush();
        viewer.Resources["ScrollBarButtonForegroundPointerOver"] = PrimaryTextBrush();
        viewer.Resources["ScrollBarButtonForegroundPressed"] = PrimaryBrush();
        viewer.Resources["FocusVisualPrimaryBrush"] = TransparentBrush();
        viewer.Resources["FocusVisualSecondaryBrush"] = TransparentBrush();
    }

    private static void StyleDialog(ContentDialog dialog)
    {
        dialog.UseLayoutRounding = true;
        dialog.CornerRadius = new CornerRadius(20);
        dialog.Resources["ContentDialogBackground"] = SurfaceBrush();
        dialog.Resources["ContentDialogBorderBrush"] = BorderLightBrush();
        dialog.Resources["ContentDialogForeground"] = PrimaryTextBrush();
        dialog.Resources["ContentDialogTitleForeground"] = PrimaryTextBrush();
        dialog.Resources["ContentDialogContentForeground"] = PrimaryTextBrush();
        dialog.Resources["ContentDialogSeparatorBorderBrush"] = BorderLightBrush();
        dialog.Resources["ButtonBackground"] = SurfaceBrush();
        dialog.Resources["ButtonBackgroundPointerOver"] = HoverBrush();
        dialog.Resources["ButtonBackgroundPressed"] = SurfaceAltBrush();
        dialog.Resources["ButtonBackgroundFocused"] = HoverBrush();
        dialog.Resources["ButtonForeground"] = PrimaryTextBrush();
        dialog.Resources["ButtonForegroundPointerOver"] = PrimaryTextBrush();
        dialog.Resources["ButtonForegroundPressed"] = PrimaryTextBrush();
        dialog.Resources["ButtonForegroundFocused"] = PrimaryTextBrush();
        dialog.Resources["ButtonBorderBrush"] = BorderBrush();
        dialog.Resources["ButtonBorderBrushPointerOver"] = BorderLightBrush();
        dialog.Resources["ButtonBorderBrushPressed"] = BorderLightBrush();
        dialog.Resources["ButtonBorderBrushFocused"] = BorderLightBrush();
        dialog.Resources["FocusVisualPrimaryBrush"] = TransparentBrush();
        dialog.Resources["FocusVisualSecondaryBrush"] = TransparentBrush();
    }

    private static void ApplyButtonResources(Button button, Brush background, Brush foreground, Brush hover, Brush pressed, Brush border, Thickness borderThickness)
    {
        button.UseLayoutRounding = true;
        button.Background = background;
        button.Foreground = foreground;
        button.BorderBrush = border;
        button.BorderThickness = borderThickness;
        button.Resources["ButtonBackground"] = background;
        button.Resources["ButtonBackgroundPointerOver"] = hover;
        button.Resources["ButtonBackgroundPressed"] = pressed;
        button.Resources["ButtonBackgroundFocused"] = hover;
        button.Resources["ButtonBackgroundDisabled"] = background;
        button.Resources["ButtonForeground"] = foreground;
        button.Resources["ButtonForegroundPointerOver"] = foreground;
        button.Resources["ButtonForegroundPressed"] = foreground;
        button.Resources["ButtonForegroundFocused"] = foreground;
        button.Resources["ButtonForegroundDisabled"] = foreground;
        button.Resources["ButtonBorderBrush"] = border;
        button.Resources["ButtonBorderBrushPointerOver"] = border;
        button.Resources["ButtonBorderBrushPressed"] = border;
        button.Resources["ButtonBorderBrushFocused"] = border;
        button.Resources["ButtonBorderBrushDisabled"] = border;
        button.Resources["FocusVisualPrimaryBrush"] = TransparentBrush();
        button.Resources["FocusVisualSecondaryBrush"] = TransparentBrush();
    }

    private static void PolishRoundedEdges(UIElement element)
    {
        switch (element)
        {
            case Border border:
                border.UseLayoutRounding = true;
                if (HasRoundedCorners(border.CornerRadius) &&
                    HasVisibleBrush(border.Background) &&
                    HasZeroThickness(border.BorderThickness))
                {
                    border.BorderThickness = new Thickness(1);
                    border.BorderBrush = RoundedEdgeBrush();
                }
                if (border.Child is not null)
                    PolishRoundedEdges(border.Child);
                break;
            case Panel panel:
                panel.UseLayoutRounding = true;
                foreach (var child in panel.Children.OfType<UIElement>())
                    PolishRoundedEdges(child);
                break;
            case ScrollViewer viewer:
                viewer.UseLayoutRounding = true;
                if (viewer.Content is UIElement scrollContent)
                    PolishRoundedEdges(scrollContent);
                break;
            case ContentControl control:
                control.UseLayoutRounding = true;
                if (control.Content is UIElement content)
                    PolishRoundedEdges(content);
                break;
            case Control control:
                control.UseLayoutRounding = true;
                break;
            case FrameworkElement frameworkElement:
                frameworkElement.UseLayoutRounding = true;
                break;
        }
    }

    private static bool HasRoundedCorners(CornerRadius radius)
    {
        return radius.TopLeft > 0 || radius.TopRight > 0 || radius.BottomRight > 0 || radius.BottomLeft > 0;
    }

    private static bool HasZeroThickness(Thickness thickness)
    {
        return thickness.Left <= 0 && thickness.Top <= 0 && thickness.Right <= 0 && thickness.Bottom <= 0;
    }

    private static bool HasVisibleBrush(Brush? brush)
    {
        return brush switch
        {
            null => false,
            SolidColorBrush solid => solid.Color.A > 0,
            _ => true,
        };
    }

    private ScrollViewer PageScroller(UIElement content)
    {
        var viewer = new ScrollViewer
        {
            Content = content,
            VerticalScrollMode = ScrollMode.Enabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsTabStop = true,
            Padding = new Thickness(0),
        };
        StyleScrollViewer(viewer);
        _activePageScroller = viewer;
        AttachWheelScrolling(viewer);
        RefreshNativeWheelHooks();
        return viewer;
    }

    private static void AttachWheelScrolling(ScrollViewer viewer)
    {
        PointerEventHandler wheelHandler = (_, e) =>
        {
            var delta = e.GetCurrentPoint(viewer).Properties.MouseWheelDelta;
            if (delta == 0)
                return;

            ScrollPageByWheelDelta(viewer, delta);
            e.Handled = true;
        };
        viewer.AddHandler(UIElement.PointerWheelChangedEvent, wheelHandler, true);
    }

    private void OnRootPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_activePageScroller is null || _activePageScroller.Visibility != Visibility.Visible)
            return;

        var delta = e.GetCurrentPoint(_root).Properties.MouseWheelDelta;
        if (delta == 0)
            return;

        ScrollPageByWheelDelta(_activePageScroller, delta);
        e.Handled = true;
    }

    private void InstallNativeWheelHook()
    {
        _windowHandle = WindowNative.GetWindowHandle(this);
        if (_windowHandle == IntPtr.Zero)
            return;

        _wndProcDelegate = NativeWindowProc;
        EnsureWindowMinimumHeight();
        RefreshNativeWheelHooks();
    }

    private void RefreshNativeWheelHooks()
    {
        if (_windowHandle == IntPtr.Zero || _wndProcDelegate is null)
            return;

        HookWindowForMouseWheel(_windowHandle);
        EnumChildWindows(_windowHandle, (hwnd, _) =>
        {
            HookWindowForMouseWheel(hwnd);
            return true;
        }, IntPtr.Zero);
    }

    private void RestoreNativeWheelHook()
    {
        foreach (var (hwnd, previousProc) in _hookedWndProcs.ToList())
            SetWindowLongPtr(hwnd, GwlWndProc, previousProc);
        _hookedWndProcs.Clear();
        _wndProcDelegate = null;
    }

    private void HookWindowForMouseWheel(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || _hookedWndProcs.ContainsKey(hwnd) || _wndProcDelegate is null)
            return;

        // WinUI 3 and remote-control clients can deliver wheel input to late-created child HWNDs.
        // Refreshing these hooks keeps page scrolling independent from which visual child has focus.
        var newProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        var previousProc = SetWindowLongPtr(hwnd, GwlWndProc, newProc);
        if (previousProc != IntPtr.Zero)
            _hookedWndProcs[hwnd] = previousProc;
    }

    private IntPtr NativeWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (hwnd == _windowHandle && message == WmGetMinMaxInfo)
        {
            var result = _hookedWndProcs.TryGetValue(hwnd, out var previousProcForMinMax)
                ? CallWindowProc(previousProcForMinMax, hwnd, message, wParam, lParam)
                : DefWindowProc(hwnd, message, wParam, lParam);
            ApplyMinimumWindowHeight(lParam);
            return result;
        }

        if ((message == WmMouseWheel || message == WmPointerWheel) && _activePageScroller is not null)
        {
            var delta = unchecked((short)((wParam.ToInt64() >> 16) & 0xffff));
            if (delta != 0)
            {
                ScrollPageByWheelDelta(_activePageScroller, delta);
                return IntPtr.Zero;
            }
        }

        return _hookedWndProcs.TryGetValue(hwnd, out var previousProc)
            ? CallWindowProc(previousProc, hwnd, message, wParam, lParam)
            : DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void EnsureWindowMinimumHeight()
    {
        if (_windowHandle == IntPtr.Zero || !GetWindowRect(_windowHandle, out var rect))
            return;

        var minimumWidth = ScaledMinimumWindowWidth();
        var minimumHeight = ScaledMinimumWindowHeight();
        var currentWidth = rect.Right - rect.Left;
        var currentHeight = rect.Bottom - rect.Top;
        if (currentWidth >= minimumWidth && currentHeight >= minimumHeight)
            return;

        SetWindowPos(
            _windowHandle,
            IntPtr.Zero,
            0,
            0,
            Math.Max(currentWidth, minimumWidth),
            Math.Max(currentHeight, minimumHeight),
            SwpNoZOrder | SwpNoMove | SwpNoActivate);
    }

    private void ApplyMinimumWindowHeight(IntPtr lParam)
    {
        if (lParam == IntPtr.Zero)
            return;

        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        info.MinimumTrackSize.X = Math.Max(info.MinimumTrackSize.X, ScaledMinimumWindowWidth());
        info.MinimumTrackSize.Y = Math.Max(info.MinimumTrackSize.Y, ScaledMinimumWindowHeight());
        Marshal.StructureToPtr(info, lParam, false);
    }

    private int ScaledMinimumWindowWidth()
    {
        var dpi = _windowHandle == IntPtr.Zero ? 96u : GetDpiForWindow(_windowHandle);
        if (dpi == 0)
            dpi = 96;

        return (int)Math.Ceiling(MinimumWindowWidthForDeviceDetail * dpi / 96d);
    }

    private int ScaledMinimumWindowHeight()
    {
        var dpi = _windowHandle == IntPtr.Zero ? 96u : GetDpiForWindow(_windowHandle);
        if (dpi == 0)
            dpi = 96;

        return (int)Math.Ceiling(MinimumWindowHeightForDeviceTabs * dpi / 96d);
    }

    private static void ScrollPageByWheelDelta(ScrollViewer viewer, int delta)
    {
        var target = Math.Clamp(viewer.VerticalOffset - delta, 0, viewer.ScrollableHeight);
        viewer.ChangeView(null, target, null, true);
    }

    private const int GwlWndProc = -4;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint WmMouseWheel = 0x020A;
    private const uint WmPointerWheel = 0x024E;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinimumTrackSize;
        public NativePoint MaximumTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallWindowProc(IntPtr previousProc, IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumChildWindows(IntPtr parentHandle, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

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
        var box = new TextBox
        {
            PlaceholderText = placeholder,
        };
        StyleTextBox(box);
        return box;
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

    private static SolidColorBrush AppBrush() => s_darkTheme
        ? new SolidColorBrush(ColorHelper.FromArgb(255, 15, 23, 42))
        : new SolidColorBrush(ColorHelper.FromArgb(255, 241, 245, 249));
    private static SolidColorBrush GridLineBrush() => s_darkTheme
        ? new SolidColorBrush(ColorHelper.FromArgb(24, 148, 163, 184))
        : new SolidColorBrush(ColorHelper.FromArgb(80, 148, 163, 184));
    private static SolidColorBrush SurfaceBrush() => s_darkTheme
        ? new SolidColorBrush(ColorHelper.FromArgb(232, 30, 41, 59))
        : new SolidColorBrush(ColorHelper.FromArgb(238, 255, 255, 255));
    private static SolidColorBrush SurfaceAltBrush() => s_darkTheme
        ? new SolidColorBrush(ColorHelper.FromArgb(232, 51, 65, 85))
        : new SolidColorBrush(ColorHelper.FromArgb(255, 226, 232, 240));
    private static SolidColorBrush HoverBrush() => s_darkTheme
        ? new SolidColorBrush(ColorHelper.FromArgb(150, 51, 65, 85))
        : new SolidColorBrush(ColorHelper.FromArgb(230, 241, 245, 249));
    private static SolidColorBrush SelectorHoverBrush() => s_darkTheme
        ? new SolidColorBrush(ColorHelper.FromArgb(210, 71, 85, 105))
        : new SolidColorBrush(ColorHelper.FromArgb(255, 235, 241, 247));
    private static SolidColorBrush SelectorPressedBrush() => s_darkTheme
        ? new SolidColorBrush(ColorHelper.FromArgb(235, 51, 65, 85))
        : new SolidColorBrush(ColorHelper.FromArgb(255, 226, 232, 240));
    private static SolidColorBrush ShellBrush() => s_darkTheme
        ? new SolidColorBrush(ColorHelper.FromArgb(255, 2, 6, 23))
        : new SolidColorBrush(ColorHelper.FromArgb(255, 15, 23, 42));
    private static SolidColorBrush ShellTextBrush() => new(ColorHelper.FromArgb(255, 187, 247, 208));
    private static Brush NavBrush() => new AcrylicBrush
    {
        TintColor = s_darkTheme ? ColorHelper.FromArgb(255, 30, 41, 59) : Colors.White,
        TintOpacity = s_darkTheme ? 0.34 : 0.58,
        TintLuminosityOpacity = s_darkTheme ? 0.38 : 0.72,
        FallbackColor = s_darkTheme ? ColorHelper.FromArgb(218, 30, 41, 59) : ColorHelper.FromArgb(226, 255, 255, 255),
    };
    private static Brush FrostedSurfaceBrush() => new AcrylicBrush
    {
        TintColor = s_darkTheme ? ColorHelper.FromArgb(255, 30, 41, 59) : Colors.White,
        TintOpacity = s_darkTheme ? 0.62 : 0.72,
        TintLuminosityOpacity = s_darkTheme ? 0.55 : 0.85,
        FallbackColor = s_darkTheme ? ColorHelper.FromArgb(238, 30, 41, 59) : ColorHelper.FromArgb(244, 255, 255, 255),
    };
    private static SolidColorBrush PrimaryBrush() => new(ColorHelper.FromArgb(255, 34, 197, 94));
    private static SolidColorBrush PrimaryHoverBrush() => new(ColorHelper.FromArgb(255, 22, 163, 74));
    private static SolidColorBrush PrimaryPressedBrush() => new(ColorHelper.FromArgb(255, 21, 128, 61));
    private static SolidColorBrush PrimaryLightBrush() => new(ColorHelper.FromArgb(40, 34, 197, 94));
    private static SolidColorBrush OnPrimaryBrush() => new(Colors.White);
    private static SolidColorBrush PrimaryTextBrush() => s_darkTheme
        ? new SolidColorBrush(ColorHelper.FromArgb(255, 248, 250, 252))
        : new SolidColorBrush(ColorHelper.FromArgb(255, 15, 23, 42));
    private static SolidColorBrush SecondaryTextBrush() => s_darkTheme
        ? new SolidColorBrush(ColorHelper.FromArgb(255, 203, 213, 225))
        : new SolidColorBrush(ColorHelper.FromArgb(255, 71, 85, 105));
    private static SolidColorBrush MutedBrush() => s_darkTheme
        ? new SolidColorBrush(ColorHelper.FromArgb(255, 148, 163, 184))
        : new SolidColorBrush(ColorHelper.FromArgb(255, 100, 116, 139));
    private static SolidColorBrush BorderBrush() => s_darkTheme
        ? new SolidColorBrush(ColorHelper.FromArgb(255, 51, 65, 85))
        : new SolidColorBrush(ColorHelper.FromArgb(255, 203, 213, 225));
    private static SolidColorBrush BorderLightBrush() => s_darkTheme
        ? new SolidColorBrush(ColorHelper.FromArgb(102, 148, 163, 184))
        : new SolidColorBrush(ColorHelper.FromArgb(180, 203, 213, 225));
    private static SolidColorBrush RoundedEdgeBrush() => s_darkTheme
        ? new SolidColorBrush(ColorHelper.FromArgb(86, 148, 163, 184))
        : new SolidColorBrush(ColorHelper.FromArgb(132, 148, 163, 184));
    private static SolidColorBrush TransparentBrush() => new(Colors.Transparent);
}
