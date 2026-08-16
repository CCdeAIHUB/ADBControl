using ADBControl.Desktop.Models;
using ADBControl.Desktop.Services;
using ADBControl.Desktop.Services.Automation;
using ADBControl.Desktop.Controls;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Windows.Foundation;
using Windows.Storage;
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
    private readonly AutomationTaskService _automation;
    private readonly Task _automationStartTask;
    private readonly DeviceService _devices;
    private readonly CompanionQuicServer _companionQuic;
    private readonly Task _companionQuicStartTask;
    private readonly CompanionAppService _companion;
    private readonly CompanionInstallWorkflow _companionInstall;
    private readonly DeviceHardwareService _hardware;
    private readonly PackageNameResolver _packageNames = new();
    private readonly DevicePackageCatalogService _packageCatalog;
    private readonly DeviceLockService _deviceLock;
    private readonly DeviceLockStateMonitor _deviceLockMonitor;
    private readonly HardwareReportExporter _hardwareReportExporter = new();
    private readonly WindowsNotificationService _systemNotifications = new();

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
    private readonly LowLevelMouseWheelInput _lowLevelWheelInput = new();
    private readonly CancellationTokenSource _wheelFallbackCancellation = new();
    private readonly Dictionary<IntPtr, IntPtr> _hookedWndProcs = new();
    private WndProcDelegate? _wndProcDelegate;
    private int _nativeWheelDispatchDepth;
    private DeferredWheelFallback? _pendingLowLevelWheelFallback;
    private bool _lowLevelWheelFallbackScheduled;
    private GridBackground? _background;
    private Border? _navDock;
    private Border? _deviceDock;
    private Border? _aiDock;
    private Button? _aiButton;
    private Button? _deviceNavButton;
    private DispatcherTimer? _devicePreviewTimer;
    private DispatcherTimer? _deviceListPreviewTimer;
    private Image? _detailPreviewImage;
    private Grid? _detailPreviewLayer;
    private SwapChainPanel? _detailVideoSurface;
    private NativeVideoSwapChainRenderer? _detailVideoRenderer;
    private (int Width, int Height)? _detailVideoFrameSize;
    private TextBlock? _detailPreviewStatus;
    private LockedPreviewSurface? _detailLockedPreview;
    private ProjectionSession? _scrcpySession;
    private string? _activeVideoDeviceId;
    private ScrcpyVideoOptions? _activeVideoRequestedOptions;
    private ScrcpyVideoOptions? _activeVideoStreamOptions;
    private CancellationTokenSource? _videoSettingsUpdateCancellation;
    private bool _videoSettingsUpdateInProgress;
    private string? _videoSettingsUpdateDeviceId;
    private TextBlock? _videoMirrorStatus;
    private Button? _videoMirrorStartButton;
    private Button? _videoMirrorStopButton;
    private long _videoMirrorStartVersion;
    private bool _devicePreviewRefreshInProgress;
    private bool _devicePreviewFullScreen;
    private double _devicePreviewIntervalSeconds = 3;
    private readonly Dictionary<string, string> _sessionUnlockPins = new(StringComparer.Ordinal);
    private DispatcherTimer? _wirelessDiscoveryTimer;
    private bool _wirelessDiscoveryInProgress;
    private long _deviceDetailStateRefreshVersion;
    private int _deviceDetailOperationDepth;
    private readonly HashSet<string> _companionConfigurationRequests = new(StringComparer.Ordinal);
    private StackPanel? _detailConnectionBadges;
    private readonly Dictionary<string, (Image Image, TextBlock Status)> _deviceCardPreviews = new();
    private readonly Dictionary<string, Window> _hardwareMonitorWindows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _previewFrameHashes = new();
    private readonly Dictionary<string, (int Width, int Height)> _previewImageFrameSizes = new();
    private readonly Dictionary<string, long> _previewFrameSerials = new();
    private PreviewTouchState? _previewTouch;
    private readonly SemaphoreSlim _previewTouchDispatchLock = new(1, 1);
    private long _previewTouchDispatchVersion;
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
    private bool _isAiPanelResizing;
    private bool _aiScrollBottomButtonVisible;
    private bool _aiStreamFlushQueued;
    private bool _aiStreamScrollQueued;
    private double _aiPanelWidth = 400;
    private double _deviceToolPaneWidth = 400;
    private readonly object _aiStreamLock = new();
    private readonly StringBuilder _pendingAiAnswerDelta = new();
    private readonly StringBuilder _pendingAiThinkingDelta = new();
    private string _currentPage = "总览";

    private const int MinimumWindowWidthForDeviceDetail = 1180;
    private const int MinimumWindowHeightForDeviceTabs = 720;
    private const double MinAiPanelWidth = 340;
    private const double MaxAiPanelWidth = 760;
    private const double MinDeviceToolPaneWidth = 320;
    private const double MinDevicePreviewPaneWidth = 420;

    private sealed record NavButtonInfo(string Text, Symbol Symbol, bool UsesDeviceGlyph = false, bool IsTablet = false);

    private enum PreviewTouchRoute
    {
        ScreenshotAdb,
        Projection,
    }

    private sealed record PreviewTouchState(
        DeviceModel Device,
        Point StartPoint,
        DateTimeOffset StartedAt,
        uint PointerId,
        PreviewTouchRoute Route,
        ProjectionTouchPosition StartDevicePosition,
        ProjectionTouchPosition LastDevicePosition);

    private sealed record LockedPreviewSurface(
        Border Root,
        TextBlock StateText,
        TextBlock HintText,
        TextBlock PinText,
        FrameworkElement Keypad,
        FrameworkElement UnlockActions);

    private sealed record DeviceFileItem(string Name, string Path, bool IsDirectory, string SizeText, string ModifiedText, string Extension);

    private sealed record PackageListItem(string PackageName, string DisplayName, bool HasResolvedDisplayName);

    private sealed class PackageSelection
    {
        public PackageListItem? Selected { get; private set; }
        public InteractiveSurface? SelectedSurface { get; private set; }

        public void Select(PackageListItem item, InteractiveSurface surface)
        {
            if (SelectedSurface is not null)
                SelectedSurface.IsSelected = false;
            Selected = item;
            SelectedSurface = surface;
            surface.IsSelected = true;
        }

        public void Clear()
        {
            if (SelectedSurface is not null)
                SelectedSurface.IsSelected = false;
            Selected = null;
            SelectedSurface = null;
        }
    }

    private enum DeviceFilePreviewKind
    {
        Image,
        Video,
        Document,
        Other,
    }

    private sealed record AiStreamingMessageUi(
        AiChatMessage Message,
        FrameworkElement Root,
        TextBlock AnswerText,
        Border ThinkingShell,
        TextBlock ThinkingText,
        TextBlock ThinkingSummary,
        Border ThinkingBody,
        TextBlock ThinkingArrow,
        TextBlock DurationText,
        StringBuilder AnswerBuffer,
        StringBuilder ThinkingBuffer)
    {
        public bool HasAnswer { get; set; }
    }

    public MainWindow()
    {
        _settings.Load();
        _devices = new DeviceService(_settings, _adb, new DeviceConnectionLogger());
        _companionQuic = new CompanionQuicServer(_settings.Current.QuicPort);
        _companionQuic.DeviceConnectionChanged += OnCompanionDeviceConnectionChanged;
        _companionQuicStartTask = _companionQuic.StartAsync();
        _companion = new CompanionAppService(_adb, _companionQuic);
        _packageCatalog = new DevicePackageCatalogService(
            new DevicePackageCatalogGateway(_adb, _companion),
            _packageNames);
        _companionInstall = new CompanionInstallWorkflow(
            new CompanionInstallGateway(_devices, _companion),
            new CompanionInstallLogger());
        var automationDatabase = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ADBControl",
            "automation.sqlite");
        _automation = new AutomationTaskService(automationDatabase, new AdbAutomationDeviceGateway(_adb, _companion));
        _aiTools = new AiAgentToolService(_adb, _companion, _automation, _devices);
        _automation.AiExecutor = ExecuteAutomationAiAsync;
        _automation.AiOutputProduced += OnAutomationAiOutputProduced;
        _automationStartTask = _automation.StartAsync();
        _hardware = new DeviceHardwareService(_adb);
        _deviceLock = new DeviceLockService(_adb, _companionQuic);
        _deviceLockMonitor = new DeviceLockStateMonitor(_deviceLock);
        _deviceLockMonitor.StateChanged += OnDeviceLockStateChanged;

        Title = "ADBControl";
        ExtendsContentIntoTitleBar = true;
        Content = _root;
        BuildShell();
        InstallNativeWheelHook();
        _root.Loaded += async (_, _) =>
        {
            RefreshNativeWheelHooks();
            PolishRoundedEdges(_root);
            StartWirelessAutoConnect();
            try
            {
                await _companionQuicStartTask;
            }
            catch (Exception ex)
            {
                Notify("伴侣 App QUIC 服务启动失败", ex.Message, InfoBarSeverity.Error);
            }
            try
            {
                await _automationStartTask;
                if (_currentPage == "任务")
                    ShowTasks();
            }
            catch (Exception ex)
            {
                Notify("任务系统启动失败", ex.Message, InfoBarSeverity.Error);
            }
        };
        ApplyTitleBarTheme();
        Navigate("总览");
        Closed += async (_, _) =>
        {
            _wirelessDiscoveryTimer?.Stop();
            _wheelFallbackCancellation.Cancel();
            _lowLevelWheelInput.Wheel -= OnLowLevelMouseWheel;
            _lowLevelWheelInput.Dispose();
            _deviceLockMonitor.StateChanged -= OnDeviceLockStateChanged;
            await _deviceLockMonitor.DisposeAsync();
            foreach (var monitorWindow in _hardwareMonitorWindows.Values.ToList())
                monitorWindow.Close();
            _hardwareMonitorWindows.Clear();
            _companionQuic.DeviceConnectionChanged -= OnCompanionDeviceConnectionChanged;
            await StopDeviceVideoMirrorCoreAsync(restartPreview: false, reason: "window_closed");
            await _companionQuic.DisposeAsync();
            RestoreNativeWheelHook();
            _wheelFallbackCancellation.Dispose();
            _systemNotifications.Dispose();
            await _automation.DisposeAsync();
        };
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
        ExitDevicePreviewFullScreen();
        StopDeviceVideoMirror(restartPreview: false);
        StopDevicePreview();
        _deviceLockMonitor.Stop();
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
        cards.Children.Add(MetricCard("运行中任务", _automation.GetSnapshots().Count(item => item.ActiveRun is not null).ToString(), Colors.Orange, 2));
        panel.Children.Add(cards);
        _contentHost.Children.Add(panel);
    }

    private void ShowDevices(bool refreshState = true)
    {
        if (DeviceDetailRefreshPolicy.ShouldExitFullScreen(_devicePreviewFullScreen, enteringDeviceDetail: false))
            ExitDevicePreviewFullScreen();
        _deviceLockMonitor.Stop();
        _currentDetailDevice = null;
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
            var list = new WrapPanel { HorizontalSpacing = 12, VerticalSpacing = 12 };
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

    private void StartWirelessAutoConnect()
    {
        _wirelessDiscoveryTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _wirelessDiscoveryTimer.Tick -= OnWirelessDiscoveryTimerTick;
        _wirelessDiscoveryTimer.Tick += OnWirelessDiscoveryTimerTick;
        _wirelessDiscoveryTimer.Start();
        _ = RefreshWirelessConnectionsSilentlyAsync();
    }

    private async void OnWirelessDiscoveryTimerTick(object? sender, object e)
    {
        await RefreshWirelessConnectionsSilentlyAsync();
    }

    private async Task RefreshWirelessConnectionsSilentlyAsync()
    {
        if (_wirelessDiscoveryInProgress)
            return;

        _wirelessDiscoveryInProgress = true;
        try
        {
            var detailDevice = _currentDetailDevice;
            var wasDetailAdbConnected = detailDevice?.IsConnected;
            var result = await _devices.RefreshConnectivityAsync();
            if (!result.Success)
                Debug.WriteLine($"Wireless ADB discovery failed: {FailureText(result)}");
            await EnsureConnectedCompanionAppsAsync();

            if (_currentPage == "设备" && detailDevice is not null)
                await RefreshDeviceDetailStateAsync(detailDevice, connectivityAlreadyRefreshed: true, previousAdbConnected: wasDetailAdbConnected);
            else if (_currentPage == "设备" && _currentDetailDevice is null)
                ShowDevices(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Wireless ADB discovery error: {ex}");
        }
        finally
        {
            _wirelessDiscoveryInProgress = false;
        }
    }

    private async Task EnsureConnectedCompanionAppsAsync()
    {
        foreach (var device in _devices.Devices.Where(candidate => candidate.IsConnected))
        {
            if (_companionQuic.IsDeviceConnected(device.DeviceId))
            {
                device.IsCompanionConnected = true;
                continue;
            }
            await EnsureCompanionConnectionAsync(device, notifyResult: false);
        }
    }

    private async Task EnsureCompanionConnectionAsync(DeviceModel device, bool notifyResult)
    {
        if (!device.IsConnected || !_companionConfigurationRequests.Add(device.DeviceId))
            return;

        try
        {
            device.IsCompanionInstalled = await _companion.IsInstalledAsync(device);
            if (!device.IsCompanionInstalled)
            {
                device.IsCompanionConnected = false;
                return;
            }

            if (_companionQuic.IsDeviceConnected(device.DeviceId))
            {
                device.IsCompanionConnected = true;
                return;
            }

            // The transparent activity may start Android's foreground connection service
            // without bringing the companion UI in front of the current phone app.
            var configure = await _companion.ConfigureConnectionAsync(device, _settings.Current.QuicPort);
            device.IsCompanionConnected = configure.Success && await _companion.IsResponsiveAsync(device);
            if (notifyResult)
            {
                Notify(
                    device.IsCompanionConnected ? "伴侣 App 已连接" : "伴侣 App 连接配置失败",
                    device.IsCompanionConnected
                        ? $"已下发并建立 QUIC 连接，端口 {_companionQuic.Port}。"
                        : FormatCommandResult(configure),
                    device.IsCompanionConnected ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
            }
        }
        finally
        {
            _companionConfigurationRequests.Remove(device.DeviceId);
        }
    }

    private void OnCompanionDeviceConnectionChanged(string deviceId, bool connected)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var device in _devices.Devices.Where(candidate =>
                         string.Equals(candidate.DeviceId, deviceId, StringComparison.Ordinal)))
            {
                device.IsCompanionConnected = connected;
            }

            if (_currentDetailDevice is not null &&
                string.Equals(_currentDetailDevice.DeviceId, deviceId, StringComparison.Ordinal))
            {
                _currentDetailDevice.IsCompanionConnected = connected;
                UpdateDetailConnectionBadges(_currentDetailDevice);
            }
        });
    }

    private void UpdateDetailConnectionBadges(DeviceModel device)
    {
        if (_detailConnectionBadges is null || !ReferenceEquals(_currentDetailDevice, device))
            return;
        _detailConnectionBadges.Children.Clear();
        _detailConnectionBadges.Children.Add(ConnectionBadge("ADB连接", device.IsConnected));
        _detailConnectionBadges.Children.Add(ConnectionBadge("APP连接", device.IsCompanionConnected));
    }

    private void DeleteDevice(DeviceModel device)
    {
        var wasCurrentDetail = _currentDetailDevice?.DeviceId == device.DeviceId;
        var wasPinned = _pinnedDeviceNavDevice?.DeviceId == device.DeviceId;
        _devices.Remove(device);
        StopDevicePreview();
        StopDeviceListPreview();
        if (wasCurrentDetail)
        {
            _deviceLockMonitor.Stop();
            _currentDetailDevice = null;
        }
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
        meta.Children.Add(BodyText(device.ConnectionKind == "usb" ? $"设备 ID: {device.DeviceId}" : $"IP: {CurrentDeviceIp(device)}"));
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
            Background = FrostedSurfaceBrush(),
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

    private void ExitDevicePreviewFullScreen()
    {
        if (!_devicePreviewFullScreen)
            return;
        AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Default);
        _root.RowDefinitions[0].Height = new GridLength(36);
        _root.RowDefinitions[2].Height = new GridLength(88);
        _contentFrame.Margin = new Thickness(10, 4, 10, 8);
        _devicePreviewFullScreen = false;
    }
    private void ShowDeviceDetail(DeviceModel device, bool refreshState = true)
    {
        _activePageScroller = null;
        StopDeviceVideoMirror(restartPreview: false);
        StopDevicePreview();
        _deviceLockMonitor.Stop();
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
        var companionInstallStatus = new CompanionInstallStatusView(new CompanionInstallStatusPalette(
            SurfaceAltBrush(),
            BorderLightBrush(),
            PrimaryTextBrush(),
            SecondaryTextBrush(),
            PrimaryBrush(),
            new SolidColorBrush(Colors.MediumSeaGreen),
            new SolidColorBrush(Colors.Orange),
            new SolidColorBrush(Colors.OrangeRed)));
        if (_companionInstall.TryGetLatestStatus(device.DeviceId, out var latestInstallStatus) && latestInstallStatus is not null)
            companionInstallStatus.Apply(latestInstallStatus);
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
        _detailConnectionBadges = statusBadges;
        Grid.SetColumn(statusBadges, 2);
        headerRow.Children.Add(statusBadges);
        var companionInstall = SecondaryButton("安装伴侣 App");
        companionInstall.VerticalAlignment = VerticalAlignment.Center;
        companionInstall.Visibility = device.IsCompanionInstalled ? Visibility.Collapsed : Visibility.Visible;
        companionInstall.Click += async (_, _) => await InstallCompanionFromDetailAsync(device, companionInstall, companionInstallStatus);
        Grid.SetColumn(companionInstall, 3);
        headerRow.Children.Add(companionInstall);
        var connectionAction = DetailActionSurface(device.IsConnected ? "断开连接" : "连接设备");
        connectionAction.VerticalAlignment = VerticalAlignment.Center;
        connectionAction.Invoked += async (_, _) =>
        {
            if (device.IsConnected)
                await DisconnectDeviceAsync(device);
            else
                await ConnectDeviceFromDetailAsync(device, connectionAction);
        };
        Grid.SetColumn(connectionAction, 4);
        headerRow.Children.Add(connectionAction);
        var detailDelete = DetailActionSurface("删除设备", danger: true);
        detailDelete.VerticalAlignment = VerticalAlignment.Center;
        detailDelete.Invoked += (_, _) => DeleteDevice(device);
        Grid.SetColumn(detailDelete, 5);
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerRow.Children.Add(detailDelete);
        panel.Children.Add(new StackPanel
        {
            Spacing = 10,
            Children = { headerRow, companionInstallStatus.Root },
        });

        var layout = new Grid
        {
            ColumnSpacing = 16,
            RowSpacing = 16,
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var previewColumn = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        var splitColumn = new ColumnDefinition { Width = GridLength.Auto };
        var toolsColumn = new ColumnDefinition { Width = new GridLength(_deviceToolPaneWidth) };
        layout.ColumnDefinitions.Add(previewColumn);
        layout.ColumnDefinitions.Add(splitColumn);
        layout.ColumnDefinitions.Add(toolsColumn);

        var previewImage = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        var videoSurface = new SwapChainPanel
        {
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var previewStatus = new TextBlock
        {
            Text = "正在获取设备截图...",
            Foreground = SecondaryTextBrush(),
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var lockedPreview = BuildLockedPreviewOverlay(device);
        var previewLayer = new Grid
        {
            MinHeight = 0,
            Background = TransparentBrush(),
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children =
            {
                previewImage,
                videoSurface,
                previewStatus,
                lockedPreview.Root,
            },
        };
        previewLayer.SizeChanged += (_, _) =>
        {
            if (ReferenceEquals(_detailPreviewLayer, previewLayer))
                UpdateDetailVideoSurfaceBounds();
        };
        AttachPreviewTouchHandlers(previewLayer, device);
        var previewControls = BuildPreviewControls(device, previewImage, previewStatus, lockedPreview);
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
        var fullScreenButton = SecondaryButton("全屏");
        var drawerButton = SecondaryButton("控制面板");
        drawerButton.Visibility = Visibility.Collapsed;
        var previewHeading = new Grid
        {
            Margin = new Thickness(0, 0, 0, 12),
            ColumnDefinitions =
            {
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
            },
            Children =
            {
                new TextBlock
                {
                    Text = "设备预览",
                    FontSize = 18,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = PrimaryTextBrush(),
                    VerticalAlignment = VerticalAlignment.Center,
                },
                WithColumn(drawerButton, 1),
                WithColumn(fullScreenButton, 2),
            },
        };
        var previewContent = new Grid
        {
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Stretch,
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition(),
            },
            Children = { previewHeading, previewShell },
        };
        var previewCard = Card(previewContent);
        previewCard.VerticalAlignment = VerticalAlignment.Stretch;
        previewCard.MinHeight = 0;
        Grid.SetRow(previewShell, 1);
        Grid.SetColumn(previewCard, 0);
        layout.Children.Add(previewCard);

        var deviceSplitHandle = BuildDevicePaneResizeHandle(layout, toolsColumn);
        Grid.SetColumn(deviceSplitHandle, 1);
        layout.Children.Add(deviceSplitHandle);

        var collapseDrawerButton = SecondaryButton("收起控制");
        collapseDrawerButton.HorizontalAlignment = HorizontalAlignment.Right;
        collapseDrawerButton.Visibility = Visibility.Collapsed;
        var deviceToolTabs = (FrameworkElement)BuildDeviceToolTabs(device);
        Grid.SetRow(deviceToolTabs, 1);
        var toolsContent = new Grid
        {
            RowSpacing = 10,
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition(),
            },
            Children =
            {
                collapseDrawerButton,
                deviceToolTabs,
            },
        };
        var toolsCard = Card(toolsContent);
        toolsCard.VerticalAlignment = VerticalAlignment.Stretch;
        toolsCard.HorizontalAlignment = HorizontalAlignment.Stretch;
        toolsCard.MinHeight = 0;
        Grid.SetColumn(toolsCard, 2);
        Canvas.SetZIndex(toolsCard, 20);
        layout.Children.Add(toolsCard);

        void ApplyPreviewFullScreen(bool enabled)
        {
            _devicePreviewFullScreen = enabled;
            AppWindow.SetPresenter(enabled
                ? Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen
                : Microsoft.UI.Windowing.AppWindowPresenterKind.Default);
            _root.RowDefinitions[0].Height = enabled ? new GridLength(0) : new GridLength(36);
            _root.RowDefinitions[2].Height = enabled ? new GridLength(0) : new GridLength(88);
            _contentFrame.Margin = enabled ? new Thickness(0) : new Thickness(10, 4, 10, 8);
            headerRow.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
            Grid.SetColumnSpan(previewCard, enabled ? 3 : 1);
            Canvas.SetZIndex(previewCard, enabled ? 10 : 0);
            deviceSplitHandle.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
            toolsCard.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
            toolsCard.HorizontalAlignment = enabled ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
            toolsCard.Width = enabled ? _deviceToolPaneWidth : double.NaN;
            drawerButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            drawerButton.Content = "控制面板";
            collapseDrawerButton.Visibility = Visibility.Collapsed;
            fullScreenButton.Content = enabled ? "退出全屏" : "全屏";
        }

        fullScreenButton.Click += (_, _) => ApplyPreviewFullScreen(!_devicePreviewFullScreen);
        drawerButton.Click += (_, _) =>
        {
            var show = toolsCard.Visibility != Visibility.Visible;
            toolsCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            collapseDrawerButton.Visibility = DeviceDetailRefreshPolicy.ShouldShowDrawerClose(_devicePreviewFullScreen, show)
                ? Visibility.Visible
                : Visibility.Collapsed;
        };
        collapseDrawerButton.Click += (_, _) =>
        {
            toolsCard.Visibility = Visibility.Collapsed;
            collapseDrawerButton.Visibility = Visibility.Collapsed;
        };
        Grid.SetRow(layout, 1);
        panel.Children.Add(layout);
        _contentHost.Children.Add(panel);
        _detailPreviewImage = previewImage;
        _detailPreviewLayer = previewLayer;
        _detailVideoSurface = videoSurface;
        _detailPreviewStatus = previewStatus;
        _detailLockedPreview = lockedPreview;
        var companionConnected = device.IsCompanionConnected || _companionQuic.IsDeviceConnected(device.DeviceId);
        if (device.IsConnected || companionConnected)
            _deviceLockMonitor.Start(device.DeviceId);
        else
            _deviceLockMonitor.Stop();
        ApplyPreviewLockState(_deviceLockMonitor.GetCurrentState(device.DeviceId), previewImage, previewStatus, lockedPreview);
        if (device.IsConnected)
            StartDevicePreview(device, previewImage, previewStatus, lockedPreview);
        if (_devicePreviewFullScreen)
            ApplyPreviewFullScreen(enabled: true);
        if (refreshState)
            _ = RefreshDeviceDetailStateAsync(device);
    }

    private FrameworkElement BuildPreviewControls(DeviceModel device, Image previewImage, TextBlock previewStatus, LockedPreviewSurface lockedPreview)
    {
        var controls = new StackPanel
        {
            Spacing = 8,
            Width = 48,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        controls.Children.Add(PreviewControlButton("⏻", "电源", async () => await _adb.ShellAsync(device.DeviceId, "input keyevent KEYCODE_POWER"), device));
        controls.Children.Add(PreviewControlIconButton("\uE995", "音量加", async () => await _adb.ShellAsync(device.DeviceId, "input keyevent KEYCODE_VOLUME_UP"), device));
        controls.Children.Add(PreviewControlIconButton("\uE993", "音量减", async () => await _adb.ShellAsync(device.DeviceId, "input keyevent KEYCODE_VOLUME_DOWN"), device));
        controls.Children.Add(PreviewControlIconButton("\uE722", "截图", async () => await RefreshPreviewOnceAsync(device, previewImage, previewStatus, lockedPreview), device, showSuccess: false));
        controls.Children.Add(new Border { Height = 18, Background = TransparentBrush() });
        controls.Children.Add(PreviewControlButton("‹", "返回键", async () => await _adb.ShellAsync(device.DeviceId, "input keyevent KEYCODE_BACK"), device));
        controls.Children.Add(PreviewControlButton("⌂", "主页键", async () => await _adb.ShellAsync(device.DeviceId, "input keyevent KEYCODE_HOME"), device));
        controls.Children.Add(PreviewControlButton("≡", "多任务", async () => await _adb.ShellAsync(device.DeviceId, "input keyevent KEYCODE_APP_SWITCH"), device));
        return controls;
    }

    private Button PreviewControlButton(string glyph, string label, Func<Task<AdbCommandResult>> action, DeviceModel device, bool showSuccess = true)
    {
        var content = new TextBlock
        {
            Text = glyph,
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            LineHeight = 22,
        };
        return PreviewControlButton(label, content, action, device, showSuccess);
    }

    private Button PreviewControlIconButton(string iconGlyph, string label, Func<Task<AdbCommandResult>> action, DeviceModel device, bool showSuccess = true)
    {
        var content = new FontIcon
        {
            Glyph = iconGlyph,
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 17,
            Width = 24,
            Height = 24,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return PreviewControlButton(label, content, action, device, showSuccess);
    }

    private Button PreviewControlButton(string label, UIElement content, Func<Task<AdbCommandResult>> action, DeviceModel device, bool showSuccess)
    {
        var button = new Button
        {
            Width = 44,
            Height = 44,
            MinWidth = 44,
            MinHeight = 44,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(14),
            Content = content,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
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
                MarkDeviceOfflineIfUnavailable(device, result);
                if (showSuccess || !result.Success)
                    Notify(result.Success ? "操作已执行" : "操作失败", result.Success ? label : FormatCommandResult(result), result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            }
            catch (Exception ex)
            {
                MarkDeviceOfflineIfUnavailable(device, ex);
                Notify("操作失败", ex.Message, InfoBarSeverity.Error);
            }
        };
        return button;
    }

    private async Task<AdbCommandResult> RefreshPreviewOnceAsync(DeviceModel device, Image previewImage, TextBlock status, LockedPreviewSurface lockedPreview)
    {
        var serial = BeginPreviewFrameRequest("detail", device.DeviceId);
        try
        {
            if (!await EnsureDeviceReadyAsync(device, false))
            {
                ApplyPreviewLockState(DeviceLockState.Unknown, previewImage, status, lockedPreview);
                return new AdbCommandResult(1, string.Empty, "设备离线。");
            }

            var lockState = await _deviceLock.GetStateAsync(device.DeviceId);
            var projectionActive = _scrcpySession?.IsRunning == true &&
                string.Equals(_scrcpySession.DeviceId, device.DeviceId, StringComparison.Ordinal);
            if (lockState != DeviceLockState.Unlocked)
            {
                ApplyPreviewLockState(lockState, previewImage, status, lockedPreview, projectionActive);
                return new AdbCommandResult(0, "设备处于锁屏或锁屏状态未知，已暂停截图。", string.Empty);
            }

            ApplyPreviewLockState(lockState, previewImage, status, lockedPreview, projectionActive);
            status.Text = "正在刷新截图...";
            status.Visibility = Visibility.Visible;
            var png = await _adb.ScreencapPngAsync(device.DeviceId);
            await ApplyPreviewFrameAsync(previewImage, device.DeviceId, png, "detail", force: true, requestSerial: serial);
            if (IsPreviewFrameRequestCurrent("detail", device.DeviceId, serial))
                status.Visibility = Visibility.Collapsed;
            return new AdbCommandResult(0, "截图已刷新。", string.Empty);
        }
        catch (Exception ex)
        {
            if (IsPreviewFrameRequestCurrent("detail", device.DeviceId, serial))
            {
                MarkDeviceOfflineIfUnavailable(device, ex);
                status.Text = $"截图失败：{ex.Message}";
                status.Visibility = Visibility.Visible;
            }
            return new AdbCommandResult(1, string.Empty, ex.Message);
        }
    }

    private LockedPreviewSurface BuildLockedPreviewOverlay(DeviceModel device)
    {
        var state = new TextBlock
        {
            Text = "设备当前处于锁屏状态",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = PrimaryTextBrush(),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        var hint = new TextBlock
        {
            Text = string.Empty,
            FontSize = 12,
            Foreground = SecondaryTextBrush(),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
        };
        var pinText = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 16,
            Foreground = PrimaryBrush(),
            HorizontalAlignment = HorizontalAlignment.Center,
            MinHeight = 24,
        };
        var pin = new StringBuilder();
        var keypadGrid = new Grid
        {
            Width = 216,
            RowSpacing = 8,
            ColumnSpacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        for (var index = 0; index < 4; index++)
            keypadGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var index = 0; index < 3; index++)
            keypadGrid.ColumnDefinitions.Add(new ColumnDefinition());

        void UpdatePinText()
        {
            pinText.Text = pin.Length == 0 ? "" : new string('*', pin.Length);
            if (pin.Length == 0)
                _sessionUnlockPins.Remove(device.DeviceId);
            else
                _sessionUnlockPins[device.DeviceId] = pin.ToString();
        }

        Button KeypadButton(string text, int row, int column, Action action)
        {
            var button = SecondaryButton(text);
            button.Height = 42;
            button.MinWidth = 0;
            button.Padding = new Thickness(4, 0, 4, 0);
            button.Click += (_, _) => action();
            Grid.SetRow(button, row);
            Grid.SetColumn(button, column);
            keypadGrid.Children.Add(button);
            return button;
        }

        for (var index = 0; index < 9; index++)
        {
            var digit = (index + 1).ToString();
            KeypadButton(digit, index / 3, index % 3, () =>
            {
                if (pin.Length < 16)
                {
                    pin.Append(digit);
                    UpdatePinText();
                }
            });
        }
        KeypadButton("清除", 3, 0, () =>
        {
            pin.Clear();
            UpdatePinText();
        });
        KeypadButton("0", 3, 1, () =>
        {
            if (pin.Length < 16)
            {
                pin.Append('0');
                UpdatePinText();
            }
        });
        KeypadButton("确认", 3, 2, async () =>
        {
            if (pin.Length < 4)
            {
                state.Text = "请输入 4 到 16 位 PIN";
                return;
            }

            state.Text = "正在上滑并输入 PIN...";
            var result = await UnlockDeviceAsync(device, pin.ToString());
            if (result.Success)
            {
                pin.Clear();
                UpdatePinText();
                state.Text = "解锁命令已发送，正在确认设备状态...";
            }
            else
            {
                state.Text = $"解锁失败：{FailureText(result)}";
            }
        });

        var keypad = new StackPanel
        {
            Spacing = 10,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { pinText, keypadGrid },
        };
        var swipeUnlock = PrimaryButton("上滑解锁");
        swipeUnlock.Click += async (_, _) =>
        {
            state.Text = "正在唤醒并上滑...";
            var result = await UnlockDeviceAsync(device, string.Empty);
            state.Text = result.Success
                ? "上滑命令已发送，正在确认锁屏状态..."
                : $"上滑失败：{FailureText(result)}";
        };
        var keypadToggle = SecondaryButton("安全键盘");
        keypadToggle.Click += (_, _) =>
        {
            var show = keypad.Visibility != Visibility.Visible;
            keypad.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            keypadToggle.Content = show ? "收起键盘" : "安全键盘";
        };
        var unlockActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { swipeUnlock, keypadToggle },
        };
        var root = new Border
        {
            Visibility = Visibility.Collapsed,
            Background = s_darkTheme
                ? new SolidColorBrush(ColorHelper.FromArgb(238, 18, 31, 52))
                : new SolidColorBrush(ColorHelper.FromArgb(242, 248, 250, 252)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(24),
            Child = new StackPanel
            {
                Spacing = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { state, hint, unlockActions, keypad },
            },
        };
        return new LockedPreviewSurface(root, state, hint, pinText, keypad, unlockActions);
    }

    private void ApplyPreviewLockState(
        DeviceLockState lockState,
        Image previewImage,
        TextBlock status,
        LockedPreviewSurface lockedPreview,
        bool projectionActive = false)
    {
        var detailDevice = _currentDetailDevice;
        var adbConnected = detailDevice?.IsConnected == true;
        var companionConnected = detailDevice is not null &&
            (detailDevice.IsCompanionConnected || _companionQuic.IsDeviceConnected(detailDevice.DeviceId));
        var presentation = DeviceDetailRefreshPolicy.ResolvePreviewPresentation(
            adbConnected,
            companionConnected,
            lockState,
            projectionActive);
        if (presentation.Overlay == DevicePreviewOverlay.None)
        {
            previewImage.Visibility = presentation.ShowScreenshot ? Visibility.Visible : Visibility.Collapsed;
            if (projectionActive)
                status.Visibility = Visibility.Collapsed;
            lockedPreview.Root.Visibility = Visibility.Collapsed;
            lockedPreview.Keypad.Visibility = Visibility.Collapsed;
            return;
        }

        // Never retain a prior screen image while the device is locked or the Keyguard state is unknown.
        previewImage.Source = null;
        previewImage.Visibility = Visibility.Collapsed;
        status.Visibility = Visibility.Collapsed;
        lockedPreview.Root.Visibility = Visibility.Visible;
        lockedPreview.StateText.Text = presentation.Title;
        lockedPreview.HintText.Text = presentation.Hint;
        lockedPreview.UnlockActions.Visibility = presentation.ShowUnlockActions
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!presentation.ShowUnlockActions)
            lockedPreview.Keypad.Visibility = Visibility.Collapsed;
    }

    private void OnDeviceLockStateChanged(object? sender, DeviceLockStateChangedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var device = _currentDetailDevice;
            if (device is null ||
                !string.Equals(device.DeviceId, args.DeviceId, StringComparison.Ordinal) ||
                _detailPreviewImage is null ||
                _detailPreviewStatus is null ||
                _detailLockedPreview is null)
            {
                return;
            }

            var projectionActive = _scrcpySession?.IsRunning == true &&
                string.Equals(_scrcpySession.DeviceId, args.DeviceId, StringComparison.Ordinal);
            if (projectionActive)
            {
                _scrcpySession?.WriteDiagnostic(
                    "device.lock_state.changed",
                    $"previous={args.PreviousState}; current={args.State}; error={args.ErrorCode ?? "none"}");
            }
            ApplyPreviewLockState(
                args.State,
                _detailPreviewImage,
                _detailPreviewStatus,
                _detailLockedPreview,
                projectionActive);
        });
    }

    private async Task<AdbCommandResult> UnlockDeviceAsync(DeviceModel device, string pin)
    {
        if (!await EnsureDeviceReadyAsync(device))
            return new AdbCommandResult(1, string.Empty, "设备离线。");

        var result = await _deviceLock.UnlockAsync(device.DeviceId, pin);
        if (result.Success)
        {
            _sessionUnlockPins.Remove(device.DeviceId);
            Notify(
                "解锁命令已发送",
                string.IsNullOrWhiteSpace(pin) ? "设备已唤醒并执行上滑。" : "设备已上滑并输入本次会话 PIN。",
                InfoBarSeverity.Informational);
            await Task.Delay(650);
            if (_detailPreviewImage is not null && _detailPreviewStatus is not null && _detailLockedPreview is not null)
            {
                var state = await _deviceLock.GetStateAsync(device.DeviceId);
                var projectionActive = _scrcpySession?.IsRunning == true &&
                    string.Equals(_scrcpySession.DeviceId, device.DeviceId, StringComparison.Ordinal);
                ApplyPreviewLockState(
                    state,
                    _detailPreviewImage,
                    _detailPreviewStatus,
                    _detailLockedPreview,
                    projectionActive);
            }
        }
        else
        {
            Notify("解锁失败", FormatCommandResult(result), InfoBarSeverity.Warning);
        }

        return result;
    }

    private static UIElement ConnectionBadge(string label, bool connected)
    {
        var accent = connected ? Colors.MediumSeaGreen : Colors.Orange;
        var brush = new SolidColorBrush(accent);
        return new Border
        {
            UseLayoutRounding = true,
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

    private static InteractiveSurface DetailActionSurface(string text, bool danger = false)
    {
        var foreground = danger
            ? (s_darkTheme ? new SolidColorBrush(ColorHelper.FromArgb(255, 252, 165, 165)) : new SolidColorBrush(ColorHelper.FromArgb(255, 185, 28, 28)))
            : PrimaryTextBrush();
        var background = danger
            ? (s_darkTheme ? new SolidColorBrush(ColorHelper.FromArgb(42, 248, 113, 113)) : new SolidColorBrush(ColorHelper.FromArgb(36, 220, 38, 38)))
            : SurfaceBrush();
        var hover = danger
            ? (s_darkTheme ? new SolidColorBrush(ColorHelper.FromArgb(62, 248, 113, 113)) : new SolidColorBrush(ColorHelper.FromArgb(54, 220, 38, 38)))
            : HoverBrush();
        var pressed = danger
            ? (s_darkTheme ? new SolidColorBrush(ColorHelper.FromArgb(82, 248, 113, 113)) : new SolidColorBrush(ColorHelper.FromArgb(70, 220, 38, 38)))
            : SurfaceAltBrush();
        var border = danger
            ? (s_darkTheme ? new SolidColorBrush(ColorHelper.FromArgb(130, 248, 113, 113)) : new SolidColorBrush(ColorHelper.FromArgb(110, 220, 38, 38)))
            : BorderBrush();
        var textBlock = new TextBlock
        {
            Text = text,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var surface = new InteractiveSurface(
            textBlock,
            new InteractiveSurfacePalette(background, hover, pressed, PrimaryLightBrush(), border, danger ? border : PrimaryBrush(), foreground, danger ? foreground : PrimaryBrush()),
            new CornerRadius(14),
            new Thickness(14, 8, 14, 8));
        surface.SetAutomationName(text);
        return surface;
    }

    private async Task DisconnectDeviceAsync(DeviceModel device)
    {
        if (string.IsNullOrWhiteSpace(device.DeviceId))
            return;

        var result = await _devices.DisconnectDeviceAsync(device);
        device.IsConnected = false;
        StopDevicePreview();
        Notify(result.Success ? "已断开连接" : "断开连接失败", FormatCommandResult(result), result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        ShowDeviceDetail(device);
    }

    private async Task ConnectDeviceFromDetailAsync(DeviceModel device, InteractiveSurface surface)
    {
        surface.IsInteractive = false;
        try
        {
            var result = await _devices.ConnectSavedWirelessDeviceAsync(device);
            if (result.Success && device.IsConnected)
            {
                Notify("设备已连接", FormatCommandResult(result), InfoBarSeverity.Success);
                ShowDeviceDetail(device, refreshState: false);
            }
            else
            {
                device.IsConnected = false;
                Notify("连接设备失败", FormatCommandResult(result), InfoBarSeverity.Error);
                ShowDeviceDetail(device, refreshState: false);
                await ShowWirelessReconnectDialogAsync(device);
            }
        }
        catch (Exception ex)
        {
            device.IsConnected = false;
            Notify("连接设备失败", ex.Message, InfoBarSeverity.Error);
            ShowDeviceDetail(device, refreshState: false);
        }
        finally
        {
            surface.IsInteractive = true;
        }
    }

    private async Task ShowWirelessReconnectDialogAsync(DeviceModel device)
    {
        var ip = RoundedTextBox("设备 IP 地址");
        ip.Text = device.IpAddress;
        var port = RoundedTextBox("连接端口");
        port.Text = device.Port > 0 ? device.Port.ToString() : "5555";
        var content = new StackPanel
        {
            Width = 420,
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "设备重启后无线调试端口可能变化，请填写开发者选项中当前显示的 IP 与端口。",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = SecondaryTextBrush(),
                },
                LabeledField("IP 地址", ip),
                LabeledField("连接端口", port),
            },
        };
        var dialog = Dialog("重新连接无线 ADB", content, "连接");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        if (!int.TryParse(port.Text, out var parsedPort))
        {
            Notify("连接设备失败", "连接端口必须是 1 到 65535 之间的数字。", InfoBarSeverity.Error);
            return;
        }

        var result = await _devices.ConnectSavedWirelessDeviceAtEndpointAsync(device, ip.Text, parsedPort);
        Notify(result.Success && device.IsConnected ? "设备已连接" : "连接设备失败", FormatCommandResult(result),
            result.Success && device.IsConnected ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        ShowDeviceDetail(device, refreshState: false);
    }

    private Border BuildDevicePaneResizeHandle(Grid layout, ColumnDefinition toolsColumn)
    {
        var handle = BuildResizeHandle("拖动调整设备预览和操作栏宽度");
        var dragging = false;
        var startX = 0d;
        var startWidth = 0d;

        handle.PointerPressed += (_, e) =>
        {
            dragging = true;
            startX = e.GetCurrentPoint(_root).Position.X;
            startWidth = _deviceToolPaneWidth;
            handle.CapturePointer(e.Pointer);
        };
        handle.PointerMoved += (_, e) =>
        {
            if (!dragging)
                return;

            var currentX = e.GetCurrentPoint(_root).Position.X;
            var maxWidth = Math.Max(MinDeviceToolPaneWidth, layout.ActualWidth - MinDevicePreviewPaneWidth);
            _deviceToolPaneWidth = Math.Clamp(startWidth + startX - currentX, MinDeviceToolPaneWidth, maxWidth);
            toolsColumn.Width = new GridLength(_deviceToolPaneWidth);
        };
        handle.PointerReleased += (_, e) =>
        {
            dragging = false;
            handle.ReleasePointerCapture(e.Pointer);
        };
        handle.PointerCanceled += (_, e) =>
        {
            dragging = false;
            handle.ReleasePointerCapture(e.Pointer);
        };
        handle.PointerCaptureLost += (_, _) => dragging = false;
        return handle;
    }

    private Border BuildAiPanelResizeHandle()
    {
        var handle = BuildResizeHandle("拖动调整 AI Agent 宽度");
        var dragging = false;
        var startX = 0d;
        var startWidth = 0d;
        var pendingWidth = 0d;
        var lastWidthCommit = 0L;

        handle.PointerPressed += (_, e) =>
        {
            dragging = true;
            _isAiPanelResizing = true;
            startX = e.GetCurrentPoint(_root).Position.X;
            startWidth = _aiPanelWidth;
            pendingWidth = startWidth;
            lastWidthCommit = 0;
            FreezeAiPanelTextLayout();
            handle.CapturePointer(e.Pointer);
        };
        handle.PointerMoved += (_, e) =>
        {
            if (!dragging)
                return;

            var currentX = e.GetCurrentPoint(_root).Position.X;
            pendingWidth = startWidth + startX - currentX;
            var now = Environment.TickCount64;
            if (now - lastWidthCommit < 16)
                return;
            lastWidthCommit = now;
            SetAiPanelWidth(pendingWidth);
        };
        handle.PointerReleased += (_, e) =>
        {
            dragging = false;
            SetAiPanelWidth(pendingWidth);
            CompleteAiPanelResize();
            handle.ReleasePointerCapture(e.Pointer);
        };
        handle.PointerCanceled += (_, e) =>
        {
            dragging = false;
            SetAiPanelWidth(pendingWidth);
            CompleteAiPanelResize();
            handle.ReleasePointerCapture(e.Pointer);
        };
        handle.PointerCaptureLost += (_, _) =>
        {
            dragging = false;
            CompleteAiPanelResize();
        };
        return handle;
    }

    private void SetAiPanelWidth(double width)
    {
        var maxWidth = _root.ActualWidth > 0
            ? Math.Min(MaxAiPanelWidth, Math.Max(MinAiPanelWidth, _root.ActualWidth - 48))
            : MaxAiPanelWidth;
        var newWidth = Math.Clamp(width, MinAiPanelWidth, maxWidth);
        if (Math.Abs(_aiPanelWidth - newWidth) < 0.5)
            return;

        _aiPanelWidth = newWidth;
        _aiPanel.Width = _aiPanelWidth;
    }

    private void CompleteAiPanelResize()
    {
        if (!_isAiPanelResizing)
            return;

        _isAiPanelResizing = false;
        _aiMessageScroller?.ClearValue(FrameworkElement.WidthProperty);
        if (_aiMessageScroller is not null)
            _aiMessageScroller.HorizontalAlignment = HorizontalAlignment.Stretch;
        _messageList.ClearValue(FrameworkElement.WidthProperty);
        _aiInput.ClearValue(FrameworkElement.WidthProperty);

        // Reflow only after the drag completes so frequent pointer events do not block the UI thread.
        RefreshAiMessageWidths();
        UpdateAiInputHeight();
    }

    private void FreezeAiPanelTextLayout()
    {
        var messageWidth = Math.Max(220, _aiPanelWidth - 32);
        if (_aiMessageScroller is not null)
        {
            _aiMessageScroller.Width = _aiPanelWidth;
            _aiMessageScroller.HorizontalAlignment = HorizontalAlignment.Left;
        }
        _messageList.Width = messageWidth;
        _aiInput.Width = Math.Max(180, _aiPanelWidth - 48);
    }

    private double AiBubbleMaxWidth()
    {
        return Math.Max(220, _aiPanelWidth - 72);
    }

    private void RefreshAiMessageWidths()
    {
        foreach (var element in _messageList.Children.OfType<FrameworkElement>())
            element.MaxWidth = AiBubbleMaxWidth();
    }

    private Border BuildResizeHandle(string tooltip)
    {
        var indicator = new Border
        {
            Width = 3,
            Height = 44,
            CornerRadius = new CornerRadius(2),
            Background = BorderLightBrush(),
            Opacity = 0.72,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var handle = new Border
        {
            Width = 10,
            MinWidth = 10,
            Background = TransparentBrush(),
            Child = indicator,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        ToolTipService.SetToolTip(handle, tooltip);
        handle.PointerEntered += (_, _) =>
        {
            indicator.Background = PrimaryBrush();
            SetCursor(LoadCursor(IntPtr.Zero, new IntPtr(IdcSizeWe)));
        };
        handle.PointerExited += (_, _) =>
        {
            indicator.Background = BorderLightBrush();
        };
        handle.PointerMoved += (_, _) => SetCursor(LoadCursor(IntPtr.Zero, new IntPtr(IdcSizeWe)));
        return handle;
    }

    private async Task RefreshDeviceDetailStateAsync(
        DeviceModel device,
        bool connectivityAlreadyRefreshed = false,
        bool? previousAdbConnected = null)
    {
        var refreshVersion = Interlocked.Increment(ref _deviceDetailStateRefreshVersion);
        if (Volatile.Read(ref _deviceDetailOperationDepth) > 0)
            return;

        var wasAdbConnected = previousAdbConnected ?? device.IsConnected;
        var wasCompanionInstalled = device.IsCompanionInstalled;
        var wasCompanionConnected = device.IsCompanionConnected;

        try
        {
            if (!connectivityAlreadyRefreshed)
            {
                var connectivity = await _devices.RefreshConnectivityAsync();
                if (!connectivity.Success)
                    Debug.WriteLine($"Device detail connectivity refresh failed: {FailureText(connectivity)}");
            }

            if (!device.IsConnected)
            {
                device.IsCompanionConnected = _companionQuic.IsDeviceConnected(device.DeviceId);
            }
            else
            {
                device.IsCompanionInstalled = await _companion.IsInstalledAsync(device);
                if (!device.IsCompanionInstalled)
                {
                    _companionConfigurationRequests.Remove(device.DeviceId);
                    device.IsCompanionConnected = false;
                }
                else
                {
                    await EnsureCompanionConnectionAsync(device, notifyResult: true);
                    device.IsCompanionConnected = _companionQuic.IsDeviceConnected(device.DeviceId);
                }
            }
        }
        catch (Exception ex)
        {
            device.IsCompanionConnected = false;
            Notify("设备详情状态刷新失败", ex.Message, InfoBarSeverity.Warning);
        }

        // Detail headers are built from the model once. Rebuild only when this refresh has
        // changed a displayed state, otherwise a periodic mDNS poll would reset the preview.
        var adbStateChanged = wasAdbConnected != device.IsConnected;
        var companionStateChanged = wasCompanionInstalled != device.IsCompanionInstalled ||
            wasCompanionConnected != device.IsCompanionConnected;
        var videoActive = _scrcpySession?.IsRunning == true &&
            string.Equals(_scrcpySession.DeviceId, device.DeviceId, StringComparison.Ordinal);
        if (refreshVersion == _deviceDetailStateRefreshVersion &&
            ReferenceEquals(_currentDetailDevice, device) &&
            DeviceDetailRefreshPolicy.ShouldRebuild(
                videoActive,
                adbStateChanged,
                companionStateChanged,
                detailOperationActive: Volatile.Read(ref _deviceDetailOperationDepth) > 0))
        {
            ShowDeviceDetail(device, refreshState: false);
        }
    }

    private async Task InstallCompanionFromDetailAsync(
        DeviceModel device,
        Button installButton,
        CompanionInstallStatusView installStatus)
    {
        var originalContent = installButton.Content;
        Interlocked.Increment(ref _deviceDetailOperationDepth);
        Interlocked.Increment(ref _deviceDetailStateRefreshVersion);
        installButton.IsEnabled = false;
        installButton.Content = "安装进行中";
        Notify("正在安装伴侣 App", "安装进度已显示在当前设备详情页，请保持手机解锁。", InfoBarSeverity.Informational);

        try
        {
            var result = await _companionInstall.RunAsync(
                device,
                _settings.Current.QuicPort,
                status =>
                {
                    if (_currentDetailDevice?.DeviceId == device.DeviceId)
                        installStatus.Apply(status);
                });
            installButton.Visibility = result.PackageInstalled ? Visibility.Collapsed : Visibility.Visible;
            UpdateDetailConnectionBadges(device);
            var message = result.Error is null
                ? $"安装和连接均已完成。trace {result.TraceId[..12]}"
                : $"{result.Error.Message} {result.Error.Suggestion} 错误码：{result.Error.ErrorCode}；trace：{result.TraceId[..12]}";
            Notify(
                result.Success
                    ? result.CompanionConnected ? "伴侣 App 安装成功" : "伴侣 App 已安装，连接待完成"
                    : "伴侣 App 安装失败",
                message,
                result.Success
                    ? result.CompanionConnected ? InfoBarSeverity.Success : InfoBarSeverity.Warning
                    : InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Companion install UI binding failed: {ex}");
            Notify(
                "伴侣 App 安装流程异常",
                $"安装流程未能完成，请查看 {CompanionInstallLogger.LogPath} 后重试。",
                InfoBarSeverity.Error);
        }
        finally
        {
            Interlocked.Decrement(ref _deviceDetailOperationDepth);
            Interlocked.Increment(ref _deviceDetailStateRefreshVersion);
            installButton.IsEnabled = true;
            installButton.Content = originalContent;
        }
    }

    private void AttachPreviewTouchHandlers(Grid previewLayer, DeviceModel device)
    {
        previewLayer.PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(previewLayer);
            if (!point.Properties.IsLeftButtonPressed ||
                !TryMapPreviewPoint(previewLayer, device.DeviceId, point.Position, out var mapped))
                return;

            var route = IsScrcpyTouchActive(device)
                ? PreviewTouchRoute.Projection
                : PreviewTouchRoute.ScreenshotAdb;
            _previewTouch = new PreviewTouchState(
                device,
                point.Position,
                DateTimeOffset.Now,
                point.PointerId,
                route,
                mapped,
                mapped);
            previewLayer.CapturePointer(e.Pointer);
            e.Handled = true;
            if (route == PreviewTouchRoute.Projection)
                _ = SendProjectionTouchAsync(device, mapped, point.PointerId, action: 0);
        };

        previewLayer.PointerMoved += (_, e) =>
        {
            if (_previewTouch is not { Route: PreviewTouchRoute.Projection } touch ||
                touch.PointerId != e.Pointer.PointerId ||
                !IsScrcpyTouchActive(device))
                return;
            var point = e.GetCurrentPoint(previewLayer);
            if (!point.Properties.IsLeftButtonPressed ||
                !TryMapPreviewPoint(previewLayer, device.DeviceId, point.Position, out var mapped) ||
                mapped.Space != touch.StartDevicePosition.Space)
                return;
            _previewTouch = touch with { LastDevicePosition = mapped };
            _ = SendProjectionTouchAsync(device, mapped, point.PointerId, action: 2);
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
            var endPoint = e.GetCurrentPoint(previewLayer).Position;
            if (touch.Route == PreviewTouchRoute.Projection)
            {
                ProjectionTouchPosition? candidate = TryMapPreviewPoint(
                    previewLayer,
                    device.DeviceId,
                    endPoint,
                    out var mapped)
                    ? mapped
                    : null;
                var releasePosition = PreviewCoordinateMapper.ResolveGesturePosition(
                    touch.LastDevicePosition,
                    candidate);
                await SendProjectionTouchAsync(
                    device,
                    releasePosition,
                    e.Pointer.PointerId,
                    action: 1,
                    isTapGesture: PreviewInteractionPolicy.IsTapGesture(
                        touch.StartPoint.X,
                        touch.StartPoint.Y,
                        endPoint.X,
                        endPoint.Y));
            }
            else
            {
                await SendPreviewTouchAsync(previewLayer, touch, endPoint);
            }
        };

        previewLayer.PointerCanceled += (_, e) =>
        {
            var touch = _previewTouch;
            if (touch?.PointerId != e.Pointer.PointerId)
                return;
            _previewTouch = null;
            previewLayer.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
            if (touch.Route == PreviewTouchRoute.Projection)
                _ = CancelProjectionTouchAsync(touch.Device, touch.LastDevicePosition, touch.PointerId);
        };

        previewLayer.PointerCaptureLost += (_, _) =>
        {
            var touch = _previewTouch;
            _previewTouch = null;
            if (touch?.Route == PreviewTouchRoute.Projection)
                _ = CancelProjectionTouchAsync(touch.Device, touch.LastDevicePosition, touch.PointerId);
        };
    }

    private bool IsScrcpyTouchActive(DeviceModel device)
        => _scrcpySession?.IsRunning == true &&
            string.Equals(_scrcpySession.DeviceId, device.DeviceId, StringComparison.Ordinal);

    private async Task SendProjectionTouchAsync(
        DeviceModel device,
        ProjectionTouchPosition position,
        uint pointerId,
        int action,
        bool isTapGesture = false)
    {
        var session = _scrcpySession;
        if (session?.IsRunning != true || !string.Equals(session.DeviceId, device.DeviceId, StringComparison.Ordinal))
            return;
        try
        {
            await session.SendTouchAsync(action, position, pointerId, isTapGesture);
        }
        catch (Exception ex)
        {
            Notify("scrcpy 触控失败", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task CancelProjectionTouchAsync(
        DeviceModel device,
        ProjectionTouchPosition position,
        uint pointerId)
    {
        var session = _scrcpySession;
        if (session?.IsRunning != true || !string.Equals(session.DeviceId, device.DeviceId, StringComparison.Ordinal))
            return;
        try
        {
            await session.CancelTouchAsync(position, pointerId);
        }
        catch (Exception ex)
        {
            Notify("scrcpy 触控取消失败", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task SendPreviewTouchAsync(FrameworkElement previewLayer, PreviewTouchState touch, Point endPoint)
    {
        var start = touch.StartDevicePosition;
        if (!TryMapPreviewPoint(previewLayer, start.Space, endPoint, out var end))
            return;

        var dispatchVersion = Interlocked.Increment(ref _previewTouchDispatchVersion);
        await _previewTouchDispatchLock.WaitAsync();
        try
        {
            // One device gesture may still be executing for up to 250 ms. If several newer
            // gestures arrived while waiting, skip this stale one instead of replaying a backlog.
            if (!PreviewInteractionPolicy.IsLatestPendingGesture(
                    dispatchVersion,
                    Volatile.Read(ref _previewTouchDispatchVersion)))
            {
                return;
            }

            if (PreviewInteractionPolicy.RequiresConnectivityProbe(touch.Device.IsConnected) &&
                !await EnsureDeviceReadyAsync(touch.Device))
            {
                return;
            }

            var elapsed = Math.Max(1, (int)(DateTimeOffset.Now - touch.StartedAt).TotalMilliseconds);
            var result = PreviewInteractionPolicy.IsTapGesture(
                touch.StartPoint.X,
                touch.StartPoint.Y,
                endPoint.X,
                endPoint.Y)
                ? elapsed >= 500
                    ? await _adb.SwipeAsync(touch.Device.DeviceId, start.X, start.Y, start.X, start.Y, Math.Max(650, elapsed))
                    : await _adb.TapAsync(touch.Device.DeviceId, start.X, start.Y)
                : await _adb.SwipeAsync(
                    touch.Device.DeviceId,
                    start.X,
                    start.Y,
                    end.X,
                    end.Y,
                    PreviewInteractionPolicy.CalculateSwipeDurationMs(elapsed));
            if (!result.Success)
            {
                MarkDeviceOfflineIfUnavailable(touch.Device, result);
                Notify("触控失败", FormatCommandResult(result), InfoBarSeverity.Error);
            }
        }
        finally
        {
            _previewTouchDispatchLock.Release();
        }
    }

    private bool TryMapPreviewPoint(
        FrameworkElement previewLayer,
        string deviceId,
        Point point,
        out ProjectionTouchPosition devicePoint)
    {
        devicePoint = default;
        var screenshotSpace = _previewImageFrameSizes.TryGetValue($"detail:{deviceId}", out var screenshot)
            ? new PreviewCoordinateSpace(screenshot.Width, screenshot.Height)
            : default;
        var session = _scrcpySession;
        var projectionActive = session?.IsRunning == true &&
            string.Equals(session.DeviceId, deviceId, StringComparison.Ordinal);
        var projectionFrame = projectionActive ? session!.FrameSize : default;
        var coordinateSpace = PreviewCoordinateMapper.ResolveActiveSpace(
            screenshotSpace,
            new PreviewCoordinateSpace(projectionFrame.Width, projectionFrame.Height),
            projectionActive);
        return TryMapPreviewPoint(previewLayer, coordinateSpace, point, out devicePoint);
    }

    private static bool TryMapPreviewPoint(
        FrameworkElement previewLayer,
        PreviewCoordinateSpace coordinateSpace,
        Point point,
        out ProjectionTouchPosition devicePoint)
    {
        return PreviewCoordinateMapper.TryMapUniform(
            previewLayer.ActualWidth,
            previewLayer.ActualHeight,
            coordinateSpace,
            point.X,
            point.Y,
            out devicePoint);
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
            Padding = new Thickness(0, 0, 12, 0),
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
        FrameworkElement? fillViewportContent = null;

        var items = new (string Title, Symbol Icon, Func<UIElement> Build, bool RequiresAdb)[]
        {
            ("控制", Symbol.Favorite, () => BuildDeviceControls(device), true),
            ("终端", Symbol.Keyboard, () => BuildAdbTerminal(device), true),
            ("软件", Symbol.AllApps, () => BuildPackageManager(device), true),
            ("文件", Symbol.Folder, () => BuildFileManager(device), true),
            ("硬件", Symbol.Setting, () => BuildHardwareInfo(device), true),
            ("重启", Symbol.Refresh, () => BuildRebootActions(device), true),
        };

        void Select(int index)
        {
            // Read the live model state on every click. The detail page is built before the
            // asynchronous connectivity refresh, so capturing IsConnected here leaves tabs
            // permanently disabled even after the header changes to "ADB connected".
            if (!DeviceDetailRefreshPolicy.CanUseAdbTool(items[index].RequiresAdb, device.IsConnected))
            {
                contentScroller.Content = BuildDisconnectedNotice();
                contentScroller.ChangeView(null, 0, null, true);
                fillViewportContent = null;
                contentScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                for (var i = 0; i < buttons.Count; i++)
                    ApplyDeviceTabState(buttons[i], i == index);
                return;
            }

            var content = items[index].Build();
            contentScroller.Content = content;
            contentScroller.ChangeView(null, 0, null, true);
            fillViewportContent = items[index].Title == "终端" ? content as FrameworkElement : null;
            contentScroller.VerticalScrollBarVisibility = fillViewportContent is null ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            if (fillViewportContent is not null)
                fillViewportContent.Height = Math.Max(240, contentScroller.ActualHeight - 2);
            for (var i = 0; i < buttons.Count; i++)
                ApplyDeviceTabState(buttons[i], i == index);
        }

        contentScroller.SizeChanged += (_, e) =>
        {
            if (fillViewportContent is not null)
                fillViewportContent.Height = Math.Max(240, e.NewSize.Height - 2);
        };

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

    /// <summary>
    /// 构建设备未连接时的提示面板，替代被禁用的功能内容。
    /// </summary>
    private static UIElement BuildDisconnectedNotice()
    {
        return new StackPanel
        {
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(24, 40, 24, 24),
            Children =
            {
                new FontIcon
                {
                    Glyph = "\uEA18",
                    FontFamily = new FontFamily("Segoe Fluent Icons"),
                    FontSize = 40,
                    Foreground = MutedBrush(),
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Text = "ADB 未连接",
                    FontSize = 16,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = PrimaryTextBrush(),
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Text = "设备当前未通过 ADB 连接，请先连接设备后再使用此功能。",
                    FontSize = 12,
                    Foreground = MutedBrush(),
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
            },
        };
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
        // The dock entry represents the already-open device. Rebuilding the same page resets
        // preview and tool state, so only navigate when it points at a different device.
        if (_pinnedDeviceNavDevice is not null &&
            !string.Equals(_currentDetailDevice?.DeviceId, _pinnedDeviceNavDevice.DeviceId, StringComparison.Ordinal))
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

    private UIElement BuildDeviceControls(DeviceModel device)
    {
        var stack = ToolStack();
        var projectionAvailable = device.IsConnected || _companionQuic.IsDeviceConnected(device.DeviceId);
        var mirrorStatus = (TextBlock)BodyText(_scrcpySession?.IsRunning == true ? "投屏：运行中" : "投屏：已停止");
        _videoMirrorStatus = mirrorStatus;
        var startMirror = PrimaryButton("开启投屏");
        var stopMirror = SecondaryButton("停止投屏");
        _videoMirrorStartButton = startMirror;
        _videoMirrorStopButton = stopMirror;
        UpdateVideoMirrorControlState(device);
        var sizes = new[] { (1920, 1080), (1280, 720), (854, 480) };
        var rates = new[] { 500_000, 1_000_000, 2_000_000, 4_000_000, 8_000_000, 12_000_000, 20_000_000 };
        var frames = new[] { 30, 45, 60 };
        var activeSelection = string.Equals(_activeVideoDeviceId, device.DeviceId, StringComparison.Ordinal)
            ? _activeVideoRequestedOptions
            : null;
        var selectedResolution = activeSelection is null
            ? 1
            : Array.FindIndex(sizes, size => size.Item1 == activeSelection.Width && size.Item2 == activeSelection.Height);
        var selectedBitrate = activeSelection is null ? 4 : Array.IndexOf(rates, activeSelection.BitRate);
        var selectedFrameRate = activeSelection is null ? 2 : Array.IndexOf(frames, activeSelection.FrameRate);
        // 使用自绘 DropdownSelector 替代原生 ComboBox，保持与整体圆角自绘 UI 风格一致。
        var resolution = StyleDropdown(new DropdownSelector
        {
            MinWidth = 170,
            ItemsSource = new[] { "1920 × 1080", "1280 × 720", "854 × 480" },
            SelectedIndex = selectedResolution >= 0 ? selectedResolution : 1,
        });
        var bitrate = StyleDropdown(new DropdownSelector
        {
            MinWidth = 130,
            ItemsSource = new[] { "0.5 Mbps", "1 Mbps", "2 Mbps", "4 Mbps", "8 Mbps", "12 Mbps", "20 Mbps" },
            SelectedIndex = selectedBitrate >= 0 ? selectedBitrate : 4,
        });
        var frameRate = StyleDropdown(new DropdownSelector
        {
            MinWidth = 110,
            ItemsSource = new[] { "30 FPS", "45 FPS", "60 FPS" },
            SelectedIndex = selectedFrameRate >= 0 ? selectedFrameRate : 2,
        });

        ScrcpyVideoOptions SelectedVideoOptions()
        {
            var size = sizes[Math.Clamp(resolution.SelectedIndex, 0, sizes.Length - 1)];
            return new ScrcpyVideoOptions(
                size.Item1,
                size.Item2,
                rates[Math.Clamp(bitrate.SelectedIndex, 0, rates.Length - 1)],
                frames[Math.Clamp(frameRate.SelectedIndex, 0, frames.Length - 1)]);
        }

        startMirror.Click += async (_, _) =>
        {
            startMirror.IsEnabled = false;
            stopMirror.IsEnabled = true;
            CancelPendingVideoSettingsUpdate();
            await StartDeviceVideoMirrorAsync(device, SelectedVideoOptions());
        };
        stopMirror.Click += (_, _) => StopDeviceVideoMirror(restartPreview: true, reason: "user_stop");

        void VideoSettingChanged(object sender, SelectionChangedEventArgs args)
        {
            if (!string.Equals(_activeVideoDeviceId, device.DeviceId, StringComparison.Ordinal) ||
                (_scrcpySession?.IsRunning != true && !_videoSettingsUpdateInProgress))
                return;
            QueueDeviceVideoSettingsUpdate(device, SelectedVideoOptions());
        }

        resolution.SelectionChanged += VideoSettingChanged;
        bitrate.SelectionChanged += VideoSettingChanged;
        frameRate.SelectionChanged += VideoSettingChanged;

        var pin = new PasswordBox
        {
            PlaceholderText = "可选：设备设置 PIN 时填写",
            MaxLength = 16,
            PasswordChar = "*",
        };
        StylePasswordBox(pin);
        pin.PasswordChanged += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(pin.Password))
                _sessionUnlockPins.Remove(device.DeviceId);
            else
                _sessionUnlockPins[device.DeviceId] = pin.Password;
        };
        var autoUnlock = PrimaryButton("自动解锁");
        autoUnlock.Click += async (_, _) =>
        {
            var currentPin = string.IsNullOrWhiteSpace(pin.Password) && _sessionUnlockPins.TryGetValue(device.DeviceId, out var sessionPin)
                ? sessionPin
                : pin.Password;
            var result = await UnlockDeviceAsync(device, currentPin);
            if (result.Success)
                pin.Password = string.Empty;
        };

        var mirrorSettings = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "投屏", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = PrimaryTextBrush() },
                mirrorStatus,
                new WrapPanel { HorizontalSpacing = 8, VerticalSpacing = 8, Children = { startMirror, stopMirror } },
                new TextBlock { Text = "分辨率 / 码率 / 帧率", FontSize = 12, Foreground = MutedBrush() },
                new WrapPanel { HorizontalSpacing = 8, VerticalSpacing = 8, Children = { resolution, bitrate, frameRate } },
            },
        };
        var unlockSettings = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "锁屏解锁", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = PrimaryTextBrush() },
                new TextBlock { Text = "无论是否设置 PIN，都会先点亮屏幕并上滑；未设置 PIN 时留空。", FontSize = 12, Foreground = MutedBrush(), TextWrapping = TextWrapping.Wrap },
                pin,
                autoUnlock,
            },
        };
        stack.Children.Add(mirrorSettings);
        stack.Children.Add(unlockSettings);
        stack.Children.Add(new TextBlock { Text = "常用控制", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = PrimaryTextBrush() });
        var controlActions = ActionGrid(
            DeviceActionButton(device, "刷新预览", async () => _detailPreviewImage is not null && _detailPreviewStatus is not null && _detailLockedPreview is not null
                ? await RefreshPreviewOnceAsync(device, _detailPreviewImage, _detailPreviewStatus, _detailLockedPreview)
                : new AdbCommandResult(1, string.Empty, "预览未初始化。")),
            DeviceActionButton(device, "返回", async () => await _adb.ShellAsync(device.DeviceId, "input keyevent KEYCODE_BACK")),
            DeviceActionButton(device, "主页", async () => await _adb.ShellAsync(device.DeviceId, "input keyevent KEYCODE_HOME")),
            DeviceActionButton(device, "任务视图", async () => await _adb.ShellAsync(device.DeviceId, "input keyevent KEYCODE_APP_SWITCH")),
            DeviceActionButton(device, "点亮屏幕", async () => await _adb.ShellAsync(device.DeviceId, "input keyevent KEYCODE_WAKEUP")),
            DeviceActionButton(device, "锁屏", async () => await _adb.ShellAsync(device.DeviceId, "input keyevent KEYCODE_SLEEP")));
        // ADB 未连接时禁用所有常用控制按钮
        if (!device.IsConnected)
        {
            foreach (var child in controlActions.Children)
                if (child is Button btn)
                    btn.IsEnabled = false;
        }
        stack.Children.Add(controlActions);
        return stack;
    }

    private UIElement BuildAdbTerminal(DeviceModel device)
    {
        const string prompt = "$ adb shell ";
        var commandExecuting = false;
        var promptStart = prompt.Length;
        var historyIndex = 0;
        var history = new List<string>();
        var terminal = new TextBox
        {
            Text = prompt,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MinHeight = 360,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 13,
            Foreground = ShellTextBrush(),
            Background = ShellBrush(),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(14),
        };
        StyleShellTerminal(terminal);
        ScrollViewer.SetHorizontalScrollBarVisibility(terminal, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(terminal, ScrollBarVisibility.Auto);
        terminal.SelectionStart = terminal.Text.Length;

        void ReplaceCurrentCommand(string command)
        {
            terminal.Text = terminal.Text[..promptStart] + command;
            terminal.SelectionStart = terminal.Text.Length;
        }

        async Task ExecuteAsync()
        {
            if (commandExecuting)
                return;
            var command = terminal.Text[promptStart..].Trim();
            if (string.IsNullOrWhiteSpace(command))
                return;

            commandExecuting = true;
            terminal.IsReadOnly = true;
            history.Remove(command);
            history.Add(command);
            historyIndex = history.Count;
            terminal.Text = terminal.Text[..promptStart] + command + Environment.NewLine + "正在执行...";
            terminal.SelectionStart = terminal.Text.Length;
            try
            {
                string response;
                if (!await EnsureDeviceReadyAsync(device))
                {
                    response = "设备离线。";
                }
                else
                {
                    var result = await _adb.ShellAsync(device.DeviceId, command);
                    response = FormatCommandResult(result).TrimEnd();
                    MarkDeviceOfflineIfUnavailable(device, result);
                }

                var executingMarker = terminal.Text.LastIndexOf("正在执行...", StringComparison.Ordinal);
                if (executingMarker >= 0)
                    terminal.Text = terminal.Text[..executingMarker] + response;
            }
            catch (Exception ex)
            {
                MarkDeviceOfflineIfUnavailable(device, ex);
                var executingMarker = terminal.Text.LastIndexOf("正在执行...", StringComparison.Ordinal);
                if (executingMarker >= 0)
                    terminal.Text = terminal.Text[..executingMarker] + ex.Message;
            }
            finally
            {
                terminal.Text = terminal.Text.TrimEnd() + Environment.NewLine + prompt;
                promptStart = terminal.Text.Length;
                terminal.IsReadOnly = false;
                commandExecuting = false;
                terminal.SelectionStart = terminal.Text.Length;
                terminal.Focus(FocusState.Programmatic);
            }
        }

        KeyEventHandler terminalKeyHandler = async (_, e) =>
        {
            if (commandExecuting)
                return;
            if (e.Key is VirtualKey.Enter or VirtualKey.Accept)
            {
                e.Handled = true;
                await ExecuteAsync();
                return;
            }
            if (e.Key == VirtualKey.Up && history.Count > 0)
            {
                e.Handled = true;
                historyIndex = Math.Max(0, historyIndex - 1);
                ReplaceCurrentCommand(history[historyIndex]);
                return;
            }
            if (e.Key == VirtualKey.Down && history.Count > 0)
            {
                e.Handled = true;
                historyIndex = Math.Min(history.Count, historyIndex + 1);
                ReplaceCurrentCommand(historyIndex == history.Count ? string.Empty : history[historyIndex]);
                return;
            }
            if (terminal.SelectionStart < promptStart)
                terminal.SelectionStart = terminal.Text.Length;
        };
        // TextBox handles Enter internally when AcceptsReturn is enabled. Receive handled
        // routed events as well so the terminal can execute instead of silently adding a line.
        terminal.AddHandler(UIElement.KeyDownEvent, terminalKeyHandler, true);
        terminal.GettingFocus += (_, args) =>
        {
            if (terminal.SelectionStart < promptStart)
                terminal.SelectionStart = terminal.Text.Length;
        };

        var stack = ToolStack();
        stack.Children.Add(BodyText("像 Windows 命令终端一样直接输入命令并按 Enter；使用上、下方向键浏览历史命令。"));
        stack.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(14),
            BorderBrush = BorderBrush(),
            BorderThickness = new Thickness(1),
            Background = ShellBrush(),
            Child = terminal,
        });
        _ = DispatcherQueue.TryEnqueue(() => terminal.Focus(FocusState.Programmatic));
        return stack;
    }
    private static Brush TerminalTokenBrush(TerminalTokenKind kind)
    {
        return kind switch
        {
            TerminalTokenKind.Command => PrimaryBrush(),
            TerminalTokenKind.Option => s_darkTheme ? new SolidColorBrush(ColorHelper.FromArgb(255, 125, 211, 252)) : new SolidColorBrush(ColorHelper.FromArgb(255, 3, 105, 161)),
            TerminalTokenKind.String => s_darkTheme ? new SolidColorBrush(ColorHelper.FromArgb(255, 253, 186, 116)) : new SolidColorBrush(ColorHelper.FromArgb(255, 180, 83, 9)),
            TerminalTokenKind.Operator => MutedBrush(),
            _ => ShellTextBrush(),
        };
    }

    private static Brush TerminalErrorBrush()
        => s_darkTheme ? new SolidColorBrush(ColorHelper.FromArgb(255, 252, 165, 165)) : new SolidColorBrush(ColorHelper.FromArgb(255, 185, 28, 28));

    private static string FormatPreviewInterval(double seconds)
        => seconds < 1 ? $"{seconds:0.#} 秒" : $"{seconds:0} 秒";

    private UIElement BuildPackageManager(DeviceModel device)
    {
        var packageRows = new StackPanel { Spacing = 4 };
        var selection = new PackageSelection();
        var packages = new ScrollViewer
        {
            Content = packageRows,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 12, 0),
            MinHeight = 300,
            MaxHeight = 460,
        };
        StyleScrollViewer(packages);
        AttachWheelScrolling(packages);
        var loading = ContentLoadingOverlay("正在读取软件包...");
        var packageSurface = new Grid
        {
            MinHeight = 300,
            MaxHeight = 460,
            Children =
            {
                new Border
                {
                    UseLayoutRounding = true,
                    CornerRadius = new CornerRadius(12),
                    Background = SurfaceBrush(),
                    BorderBrush = BorderBrush(),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(6, 6, 0, 6),
                    Child = packages,
                },
                loading,
            },
        };
        var status = BodyText("正在读取已安装软件包...");
        var refresh = PrimaryButton("刷新软件包");
        refresh.Click += async (_, _) => await LoadPackagesAsync(device, packageRows, selection, status, loading);
        var install = SecondaryButton("安装 APK");
        install.Click += async (_, _) => await InstallApkAsync(device);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { refresh, install } };
        var actions = ActionGrid(
            DevicePackageButton(device, "运行", selection, packageName => _adb.ShellAsync(device.DeviceId, $"monkey -p {packageName} 1")),
            DevicePackageButton(device, "强制停止", selection, packageName => _adb.ShellAsync(device.DeviceId, $"am force-stop {packageName}")),
            DevicePackageButton(device, "禁用", selection, packageName => _adb.ShellAsync(device.DeviceId, $"pm disable-user {packageName}")),
            DevicePackageButton(device, "启用", selection, packageName => _adb.ShellAsync(device.DeviceId, $"pm enable {packageName}")),
            DevicePackageButton(device, "提取 APK", selection, packageName => PullPackageApkAsync(device, packageName)),
            DevicePackageButton(device, "清除数据", selection, packageName => _adb.ShellAsync(device.DeviceId, $"pm clear {packageName}")));
        var detail = SecondaryButton("查看软件信息");
        detail.Click += async (_, _) =>
        {
            var packageName = SelectedPackageName(selection);
            if (packageName is null)
            {
                Notify("请选择软件包", "先在列表中选择一个软件包。", InfoBarSeverity.Warning);
                return;
            }

            if (!await EnsureDeviceReadyAsync(device))
                return;
            var result = await GetPackageDetailsAsync(device, packageName);
            if (!result.Success)
                Notify("读取软件信息失败", FormatCommandResult(result), InfoBarSeverity.Error);
            await ShowTextDialogAsync($"软件信息 - {packageName}", FormatCommandResult(result));
        };

        var stack = ToolStack();
        stack.Children.Add(row);
        stack.Children.Add(packageSurface);
        stack.Children.Add(status);
        stack.Children.Add(actions);
        stack.Children.Add(detail);
        _ = LoadPackagesAsync(device, packageRows, selection, status, loading);
        return stack;
    }

    private UIElement BuildFileManager(DeviceModel device)
    {
        var path = RoundedTextBox("/");
        path.MinWidth = 0;
        path.Width = 148;
        var fileRows = new StackPanel { Spacing = 4 };
        var files = new ScrollViewer
        {
            Content = fileRows,
            MinHeight = 320,
            MaxHeight = 460,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(6, 6, 10, 6),
        };
        StyleScrollViewer(files);
        AttachWheelScrolling(files);
        DeviceFileItem? selectedFile = null;
        InteractiveSurface? selectedFileSurface = null;
        var status = BodyText(string.Empty);
        status.Visibility = Visibility.Collapsed;
        var loading = ContentLoadingOverlay("正在读取目录...");
        var fileSurface = new Grid
        {
            MinHeight = 320,
            MaxHeight = 460,
            Children = { files, loading },
        };
        var previewMode = false;

        var open = PrimaryButton("打开");
        var up = SecondaryButton("上一级");
        var send = SecondaryButton("发送文件");
        var delete = SecondaryButton("删除");
        var listMode = SecondaryButton("列表");
        var previewModeButton = SecondaryButton("预览");

        void ApplyModeState()
        {
            ApplySegmentState(listMode, !previewMode);
            ApplySegmentState(previewModeButton, previewMode);
        }

        async Task LoadDirectoryAsync(string requestedPath)
        {
            var targetPath = NormalizeDevicePath(requestedPath);
            path.Text = targetPath;
            status.Visibility = Visibility.Collapsed;
            loading.Visibility = Visibility.Visible;
            fileRows.Children.Clear();
            selectedFile = null;
            selectedFileSurface = null;

            if (!await EnsureDeviceReadyAsync(device))
            {
                status.Text = $"设备离线：{device.DeviceId}";
                status.Visibility = Visibility.Visible;
                loading.Visibility = Visibility.Collapsed;
                return;
            }

            AdbCommandResult result;
            try
            {
                result = await _adb.ShellAsync(device.DeviceId, $"ls -la -p {EscapeShellToken(targetPath)}");
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
                status.Visibility = Visibility.Visible;
                loading.Visibility = Visibility.Collapsed;
                return;
            }
            if (!result.Success)
            {
                MarkDeviceOfflineIfUnavailable(device, result);
                status.Text = FormatCommandResult(result);
                status.Visibility = Visibility.Visible;
                loading.Visibility = Visibility.Collapsed;
                return;
            }

            var entries = ParseDeviceFileList(targetPath, result.Stdout)
                .OrderByDescending(item => item.IsDirectory)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var entry in entries)
            {
                var row = BuildDeviceFileListItem(entry, previewMode);
                row.Invoked += (_, _) =>
                {
                    if (selectedFileSurface is not null)
                        selectedFileSurface.IsSelected = false;
                    selectedFile = entry;
                    selectedFileSurface = row;
                    row.IsSelected = true;
                };
                row.DoubleTapped += async (_, _) =>
                {
                    if (entry.IsDirectory)
                        await LoadDirectoryAsync(entry.Path);
                    else
                        await ShowDeviceFilePreviewAsync(device, entry);
                };
                fileRows.Children.Add(row);
            }

            if (entries.Count == 0)
            {
                status.Text = "当前目录为空。";
                status.Visibility = Visibility.Visible;
            }
            loading.Visibility = Visibility.Collapsed;
        }

        open.Click += async (_, _) => await LoadDirectoryAsync(path.Text);
        up.Click += async (_, _) => await LoadDirectoryAsync(ParentDevicePath(path.Text));
        listMode.Click += async (_, _) =>
        {
            previewMode = false;
            ApplyModeState();
            await LoadDirectoryAsync(path.Text);
        };
        previewModeButton.Click += async (_, _) =>
        {
            previewMode = true;
            ApplyModeState();
            await LoadDirectoryAsync(path.Text);
        };
        send.Click += async (_, _) =>
        {
            await PushFileAsync(device, NormalizeDevicePath(path.Text));
            await LoadDirectoryAsync(path.Text);
        };
        delete.Click += async (_, _) =>
        {
            var selected = selectedFile;
            if (selected is null)
            {
                Notify("请选择文件", "先在文件列表中选择要删除的文件或文件夹。", InfoBarSeverity.Warning);
                return;
            }

            if (!await EnsureDeviceReadyAsync(device))
                return;
            var result = await _adb.ShellAsync(device.DeviceId, $"rm -rf {EscapeShellToken(selected.Path)}");
            MarkDeviceOfflineIfUnavailable(device, result);
            Notify(result.Success ? "删除命令已执行" : "删除失败", FormatCommandResult(result), result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            if (result.Success)
                await LoadDirectoryAsync(path.Text);
        };
        ApplyModeState();

        path.Margin = new Thickness(0, 0, 8, 8);
        // 间距统一由 WrapPanel 的 HorizontalSpacing 管理，不再单独设置 Margin。
        var pathRow = new WrapPanel { HorizontalSpacing = 8, VerticalSpacing = 8, Children = { path, open, up } };
        var actionRow = new WrapPanel { HorizontalSpacing = 8, VerticalSpacing = 8, Children = { send, delete } };
        var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { listMode, previewModeButton } };
        actionRow.Children.Add(modeRow);

        var stack = ToolStack();
        stack.Children.Add(pathRow);
        stack.Children.Add(actionRow);
        stack.Children.Add(fileSurface);
        stack.Children.Add(status);
        _ = LoadDirectoryAsync(path.Text);
        return stack;
    }

    private static Border ContentLoadingOverlay(string message)
    {
        return new Border
        {
            Visibility = Visibility.Collapsed,
            Background = s_darkTheme
                ? new SolidColorBrush(ColorHelper.FromArgb(156, 18, 31, 52))
                : new SolidColorBrush(ColorHelper.FromArgb(156, 248, 250, 252)),
            CornerRadius = new CornerRadius(12),
            BorderBrush = BorderLightBrush(),
            BorderThickness = new Thickness(1),
            Child = new CyberLoader(message, PrimaryBrush(), PrimaryTextBrush(), SurfaceAltBrush(), BorderLightBrush()),
        };
    }

    private async Task ShowDeviceFilePreviewAsync(DeviceModel device, DeviceFileItem item)
    {
        var previewKind = GetDeviceFilePreviewKind(item);
        var extension = string.IsNullOrWhiteSpace(item.Extension) ? ".bin" : "." + item.Extension;
        var directory = Path.Combine(Path.GetTempPath(), "ADBControl", "file-preview", SanitizeFileName(device.DeviceId));
        Directory.CreateDirectory(directory);
        var localPath = Path.Combine(directory, SanitizeFileName(item.Name));
        if (!Path.HasExtension(localPath))
            localPath += extension;

        Notify("正在准备预览", item.Name, InfoBarSeverity.Informational);
        var result = await _adb.PullAsync(device.DeviceId, item.Path, localPath);
        if (!result.Success)
        {
            Notify("预览失败", FormatCommandResult(result), InfoBarSeverity.Error);
            return;
        }

        if (previewKind == DeviceFilePreviewKind.Image)
        {
            var image = new Image
            {
                MaxHeight = 520,
                Stretch = Stretch.Uniform,
                Source = new BitmapImage(new Uri(localPath)),
            };
            await ShowFilePreviewDialogAsync(item, image, localPath, "图片预览");
            return;
        }

        var message = previewKind switch
        {
            DeviceFilePreviewKind.Video => "视频已准备完成，将使用系统默认播放器打开。",
            DeviceFilePreviewKind.Document => "文档已准备完成，将使用系统默认查看器打开。",
            _ => "该文件类型没有内置预览，将使用系统默认程序打开。",
        };
        await ShowFilePreviewDialogAsync(item, BodyText(message), localPath, "文件预览");
    }

    private async Task ShowFilePreviewDialogAsync(DeviceFileItem item, UIElement preview, string localPath, string title)
    {
        FrostedDialog? dialog = null;
        var open = PrimaryButton("使用默认程序打开");
        var close = SecondaryButton("关闭");
        open.Click += async (_, _) =>
        {
            var file = await StorageFile.GetFileFromPathAsync(localPath);
            var opened = await Launcher.LaunchFileAsync(file);
            Notify(opened ? "已打开预览文件" : "无法打开预览文件", localPath, opened ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        };
        close.Click += (_, _) => dialog?.Hide();
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { close, open },
        };
        var content = new StackPanel
        {
            Width = 640,
            Spacing = 12,
            Children =
            {
                SectionTitle(item.Name),
                preview,
                actions,
            },
        };
        dialog = DialogChrome(title, content);
        await dialog.ShowAsync();
    }

    private static DeviceFilePreviewKind GetDeviceFilePreviewKind(DeviceFileItem item)
    {
        return item.Extension switch
        {
            "png" or "jpg" or "jpeg" or "webp" or "gif" or "bmp" => DeviceFilePreviewKind.Image,
            "mp4" or "mkv" or "mov" or "avi" or "webm" => DeviceFilePreviewKind.Video,
            "pdf" or "txt" or "md" or "json" or "xml" or "doc" or "docx" or "xls" or "xlsx" or "ppt" or "pptx" => DeviceFilePreviewKind.Document,
            _ => DeviceFilePreviewKind.Other,
        };
    }

    private static InteractiveSurface BuildDeviceFileListItem(DeviceFileItem item, bool previewMode)
    {
        var icon = new FontIcon
        {
            Glyph = DeviceFileGlyph(item),
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = previewMode ? 28 : 20,
            Width = previewMode ? 42 : 28,
            Height = previewMode ? 42 : 28,
            Foreground = item.IsDirectory ? PrimaryBrush() : SecondaryTextBrush(),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var name = new TextBlock
        {
            Text = item.Name,
            FontSize = previewMode ? 14 : 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = PrimaryTextBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var detail = new TextBlock
        {
            Text = item.IsDirectory ? "文件夹" : $"{FileTypeLabel(item)}  ·  {item.SizeText}",
            FontSize = 11,
            Foreground = SecondaryTextBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        UIElement content;
        if (previewMode)
        {
            content = new Border
            {
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10),
                Child = new Grid
                {
                    ColumnSpacing = 12,
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = GridLength.Auto },
                        new ColumnDefinition(),
                    },
                    Children =
                    {
                        new Border
                        {
                            Width = 48,
                            Height = 48,
                            CornerRadius = new CornerRadius(12),
                            Background = SurfaceAltBrush(),
                            BorderBrush = BorderLightBrush(),
                            BorderThickness = new Thickness(1),
                            Child = icon,
                        },
                        WithColumn(new StackPanel
                        {
                            Spacing = 4,
                            VerticalAlignment = VerticalAlignment.Center,
                            Children =
                            {
                                name,
                                detail,
                                new TextBlock
                                {
                                    Text = item.ModifiedText,
                                    FontSize = 11,
                                    Foreground = MutedBrush(),
                                    TextTrimming = TextTrimming.CharacterEllipsis,
                                },
                            },
                        }, 1),
                    },
                },
            };
        }
        else
        {
            var row = new Grid
            {
                ColumnSpacing = 10,
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition(),
                    new ColumnDefinition { Width = new GridLength(74) },
                    new ColumnDefinition { Width = new GridLength(72) },
                },
            };
            row.Children.Add(icon);
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var type = new TextBlock { Text = item.IsDirectory ? "文件夹" : FileTypeLabel(item), FontSize = 11, Foreground = SecondaryTextBrush(), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(type, 2);
            row.Children.Add(type);
            var size = new TextBlock { Text = item.IsDirectory ? string.Empty : item.SizeText, FontSize = 11, Foreground = MutedBrush(), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(size, 3);
            row.Children.Add(size);
            content = new Border
            {
                Padding = new Thickness(8, 7, 8, 7),
                Child = row,
            };
        }

        var surface = new InteractiveSurface(
            content,
            new InteractiveSurfacePalette(
                TransparentBrush(),
                HoverBrush(),
                SurfaceAltBrush(),
                PrimaryLightBrush(),
                TransparentBrush(),
                PrimaryBrush(),
                PrimaryTextBrush(),
                PrimaryTextBrush()),
            new CornerRadius(12),
            new Thickness(0),
            preserveContentForeground: true)
        {
            Tag = item,
            Margin = new Thickness(0, 0, 0, 4),
        };
        surface.SetAutomationName($"{(item.IsDirectory ? "文件夹" : "文件")} {item.Name}");
        return surface;
    }

    private static IReadOnlyList<DeviceFileItem> ParseDeviceFileList(string directory, string stdout)
    {
        var items = new List<DeviceFileItem>();
        foreach (var line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var item = TryParseDeviceFileLine(directory, line);
            if (item is not null)
                items.Add(item);
        }

        return items;
    }

    private static DeviceFileItem? TryParseDeviceFileLine(string directory, string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("total ", StringComparison.OrdinalIgnoreCase))
            return null;

        var fields = line.Split(new[] { ' ', '\t' }, 9, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 6 || fields[0].Length == 0)
            return null;

        var rawName = fields.Length >= 9 ? fields[8] : fields[^1];
        if (string.IsNullOrWhiteSpace(rawName) || rawName is "." or "..")
            return null;

        var linkIndex = rawName.IndexOf(" -> ", StringComparison.Ordinal);
        if (linkIndex >= 0)
            rawName = rawName[..linkIndex];
        var isDirectory = fields[0][0] == 'd' || rawName.EndsWith("/", StringComparison.Ordinal);
        var name = rawName.TrimEnd('/');
        var sizeText = fields.Length > 4 && long.TryParse(fields[4], out var size) ? FormatBytes(size) : string.Empty;
        var modified = fields.Length >= 8 ? $"{fields[5]} {fields[6]} {fields[7]}" : string.Empty;
        var extension = isDirectory ? string.Empty : Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        return new DeviceFileItem(name, CombineDevicePath(directory, name), isDirectory, sizeText, modified, extension);
    }

    private static string DeviceFileGlyph(DeviceFileItem item)
    {
        if (item.IsDirectory)
            return "\uE8B7";

        return item.Extension switch
        {
            "png" or "jpg" or "jpeg" or "webp" or "gif" or "bmp" => "\uEB9F",
            "mp4" or "mkv" or "mov" or "avi" or "webm" => "\uE714",
            "mp3" or "wav" or "flac" or "aac" or "m4a" => "\uE8D6",
            "apk" or "apks" or "xapk" => "\uE71D",
            "zip" or "rar" or "7z" or "tar" or "gz" => "\uF012",
            "txt" or "md" or "json" or "xml" or "log" => "\uE8A5",
            "pdf" => "\uEA90",
            _ => "\uE8A5",
        };
    }

    private static string FileTypeLabel(DeviceFileItem item)
    {
        if (item.IsDirectory)
            return "文件夹";
        return string.IsNullOrWhiteSpace(item.Extension) ? "文件" : item.Extension.ToUpperInvariant();
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }

    private static string NormalizeDevicePath(string value)
    {
        var path = string.IsNullOrWhiteSpace(value) ? "/" : value.Trim().Replace('\\', '/');
        if (!path.StartsWith("/", StringComparison.Ordinal))
            path = "/" + path;
        while (path.Length > 1 && path.EndsWith("/", StringComparison.Ordinal))
            path = path[..^1];
        return path;
    }

    private static string ParentDevicePath(string value)
    {
        var path = NormalizeDevicePath(value);
        if (path == "/")
            return "/";
        var index = path.LastIndexOf('/');
        return index <= 0 ? "/" : path[..index];
    }

    private static string CombineDevicePath(string directory, string name)
    {
        var root = NormalizeDevicePath(directory);
        return root == "/" ? $"/{name}" : $"{root}/{name}";
    }

    private UIElement BuildHardwareInfo(DeviceModel device)
    {
        var content = new StackPanel { Spacing = 10 };
        var status = (TextBlock)BodyText(string.Empty);
        status.Visibility = Visibility.Collapsed;
        var loading = ContentLoadingOverlay("正在扫描硬件...");
        var dashboard = new Grid
        {
            MinHeight = 420,
            Children = { content, loading },
        };
        var refresh = PrimaryButton("刷新硬件信息");
        var details = SecondaryButton("详情");
        var monitor = SecondaryButton("硬件监控");
        DeviceHardwareSnapshot? snapshot = null;

        // 硬件面板的动态刷新定时器：每 5 秒自动采集一次 CPU 频率、内存、电池、温度等
        // 实时变化的指标，避免用户手动点击刷新才能看到最新数据。
        var hardwareRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        var hardwareRefreshInProgress = false;

        async Task LoadAsync()
        {
            content.Children.Clear();
            status.Visibility = Visibility.Collapsed;
            loading.Visibility = Visibility.Visible;
            if (!await EnsureDeviceReadyAsync(device))
            {
                status.Text = "设备离线，无法读取硬件信息。";
                status.Visibility = Visibility.Visible;
                loading.Visibility = Visibility.Collapsed;
                return;
            }
            try
            {
                snapshot = await _hardware.CollectAsync(device.DeviceId);
                RenderHardwareDashboard(content, snapshot);
            }
            catch (Exception ex)
            {
                status.Text = $"硬件信息读取失败：{ex.Message}";
                status.Visibility = Visibility.Visible;
            }
            loading.Visibility = Visibility.Collapsed;
        }

        // 静默刷新：不显示 loading 遮罩，只更新已有面板内容，避免每 5 秒闪烁。
        async Task SilentRefreshAsync()
        {
            if (hardwareRefreshInProgress || loading.Visibility == Visibility.Visible)
                return;
            hardwareRefreshInProgress = true;
            try
            {
                if (!await EnsureDeviceReadyAsync(device, false))
                    return;
                snapshot = await _hardware.CollectAsync(device.DeviceId);
                content.Children.Clear();
                RenderHardwareDashboard(content, snapshot);
            }
            catch
            {
                // 静默刷新失败时不打断用户，下次定时器触发时会重试。
            }
            finally
            {
                hardwareRefreshInProgress = false;
            }
        }

        hardwareRefreshTimer.Tick += async (_, _) => await SilentRefreshAsync();

        refresh.Click += async (_, _) => await LoadAsync();
        details.Click += async (_, _) => await ShowHardwareDetailsAsync(device, snapshot);
        monitor.Click += async (_, _) => await ShowHardwareMonitorAsync(device);

        var stack = ToolStack();
        stack.Children.Add(new WrapPanel { HorizontalSpacing = 8, VerticalSpacing = 8, Children = { refresh, details, monitor } });
        stack.Children.Add(status);
        stack.Children.Add(dashboard);

        // 面板卸载时停止定时器，避免后台持续采集已离开页面的设备数据。
        stack.Unloaded += (_, _) => hardwareRefreshTimer.Stop();
        _ = LoadAsync().ContinueWith(_ => hardwareRefreshTimer.Start(), TaskScheduler.FromCurrentSynchronizationContext());
        return stack;
    }

    private static void RenderHardwareDashboard(StackPanel content, DeviceHardwareSnapshot snapshot)
    {
        content.Children.Add(HardwareSection("设备", "\uE8CC", null,
            ("品牌", snapshot.Value("brand").Value),
            ("型号", snapshot.Value("model").Value),
            ("设备代号", snapshot.Value("device").Value)));
        content.Children.Add(HardwareSection("系统", "\uE770", null,
            ("Android", snapshot.Value("android").Value),
            ("SDK", snapshot.Value("sdk").Value),
            ("CPU ABI", snapshot.Value("abi").Value),
            ("屏幕刷新率", snapshot.RefreshRateHz is double refresh ? $"{refresh:0.##} Hz" : "当前连接无法获取")));

        var cpuMetrics = new List<(string Name, string Value)>
        {
            ("处理器型号", snapshot.Value("cpu_model").Value),
            ("核心数", snapshot.Value("cpu_cores").Value),
            ("当前负载", snapshot.Value("load").Value),
            ("CPU 总体占用率", snapshot.CpuFrequencyUsagePercent is double cpuUsage
                ? $"{cpuUsage:0.#}%（按当前/最高频率估算）"
                : "当前连接无法获取"),
            ("GPU 占用率", snapshot.Gpu?.EffectiveUsagePercent is double gpuUsage
                ? $"{gpuUsage:0.#}%"
                : "当前连接无法获取"),
            ("GPU 显存占用", snapshot.Gpu?.MemoryBytes is long gpuMemory
                ? $"{gpuMemory / 1024d / 1024d:0.##} MB"
                : "当前连接无法获取"),
        };
        cpuMetrics.AddRange(snapshot.CpuFrequencies.Select(frequency => (
            $"CPU {frequency.CoreIndex}",
            frequency.FrequencyUsagePercent is double usage
                ? $"{frequency.DisplayValue} / {frequency.MaximumDisplayValue}（{usage:0.#}%）"
                : frequency.DisplayValue)));
        content.Children.Add(HardwareSection("CPU 状态", "\uE950", null, cpuMetrics.ToArray()));

        content.Children.Add(HardwareSection("内存", "\uE950", BuildMemoryUsagePie(snapshot.ExtendedMemoryUsagePercent ?? snapshot.MemoryUsagePercent),
            ("物理内存", FormatMemory(snapshot.TotalMemoryKb)),
            ("物理可用内存", FormatMemory(snapshot.AvailableMemoryKb)),
            ("当前内存占用", snapshot.UsedMemoryKb is long used && snapshot.MemoryUsagePercent is double usage
                ? $"{FormatMemory(used)} ({usage:0.#}%)"
                : "当前连接无法获取"),
            ("虚拟内存扩展", snapshot.SwapTotalKb is > 0
                ? $"{FormatMemory(snapshot.SwapTotalKb)}（可用 {FormatMemory(snapshot.SwapFreeKb)}）"
                : "未启用或当前连接无法获取"),
            ("扩展后总容量", snapshot.ExtendedTotalMemoryKb is long extended
                ? FormatMemory(extended)
                : "当前连接无法获取")));

        content.Children.Add(HardwareSection("电池与温度", "\uEBAA", BuildHardwareTemperaturePanel(snapshot.Temperatures),
            ("电量", FormatPercent(snapshot.Value("battery_level").Value)),
            ("电池温度", FormatBatteryTemperature(snapshot.Value("battery_temp").Value)),
            ("充电状态", FormatBatteryStatus(snapshot.Value("battery_status").Value, string.Empty))));
    }

    private static UIElement BuildHardwareTemperaturePanel(IReadOnlyList<HardwareTemperature> temperatures)
    {
        var groups = HardwareTemperaturePresentation.Group(temperatures);
        if (groups.Count == 0)
        {
            return new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = SurfaceAltBrush(),
                BorderBrush = BorderLightBrush(),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 8, 10, 8),
                Child = new TextBlock
                {
                    Text = "当前连接无法获取温度传感器数据",
                    FontSize = 12,
                    Foreground = MutedBrush(),
                },
            };
        }

        var root = new StackPanel { Spacing = 10 };
        foreach (var group in groups)
        {
            var cards = new WrapPanel { HorizontalSpacing = 8, VerticalSpacing = 8 };
            foreach (var temperature in group.Items)
            {
                cards.Children.Add(new Border
                {
                    MinWidth = 118,
                    CornerRadius = new CornerRadius(8),
                    Background = SurfaceAltBrush(),
                    BorderBrush = BorderLightBrush(),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(10, 8, 10, 8),
                    Child = new StackPanel
                    {
                        Spacing = 3,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = HardwareTemperaturePresentation.FormatSensorName(temperature.Name),
                                FontSize = 11,
                                Foreground = MutedBrush(),
                                TextTrimming = TextTrimming.CharacterEllipsis,
                            },
                            new TextBlock
                            {
                                Text = temperature.DisplayValue,
                                FontSize = 15,
                                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                                Foreground = PrimaryBrush(),
                            },
                        },
                    },
                });
            }

            root.Children.Add(new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock
                    {
                        Text = group.Title,
                        FontSize = 11,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = SecondaryTextBrush(),
                    },
                    cards,
                },
            });
        }
        return root;
    }

    private static FrameworkElement HardwareSection(string title, string iconGlyph, UIElement? visual, params (string Name, string Value)[] values)
    {
        var metrics = new StackPanel { Spacing = 8 };
        foreach (var value in values)
        {
            metrics.Children.Add(new Border
            {
                UseLayoutRounding = true,
                CornerRadius = new CornerRadius(8),
                Background = SurfaceAltBrush(),
                BorderBrush = BorderLightBrush(),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 8, 10, 8),
                Child = new Grid
                {
                    ColumnSpacing = 8,
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = GridLength.Auto },
                        new ColumnDefinition(),
                    },
                    Children =
                    {
                        new FontIcon
                        {
                            Glyph = HardwareMetricGlyph(value.Name),
                            FontFamily = new FontFamily("Segoe Fluent Icons"),
                            FontSize = 15,
                            Width = 18,
                            Height = 18,
                            Foreground = PrimaryBrush(),
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                        WithColumn(new StackPanel
                        {
                            Spacing = 2,
                            Children =
                            {
                                new TextBlock { Text = value.Name, FontSize = 11, Foreground = MutedBrush() },
                                new TextBlock { Text = value.Value, FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = PrimaryTextBrush() },
                            },
                        }, 1),
                    },
                },
            });
        }

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new FontIcon
                {
                    Glyph = iconGlyph,
                    FontFamily = new FontFamily("Segoe Fluent Icons"),
                    FontSize = 18,
                    Width = 20,
                    Height = 20,
                    Foreground = PrimaryBrush(),
                    VerticalAlignment = VerticalAlignment.Center,
                },
                new TextBlock { Text = title, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = PrimaryBrush(), VerticalAlignment = VerticalAlignment.Center },
            },
        };
        var sectionContent = new StackPanel { Spacing = 10, Children = { header } };
        if (visual is not null)
            sectionContent.Children.Add(visual);
        sectionContent.Children.Add(metrics);
        return new Border
        {
            UseLayoutRounding = true,
            MinWidth = 260,
            CornerRadius = new CornerRadius(10),
            Background = SurfaceBrush(),
            BorderBrush = BorderLightBrush(),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            Child = sectionContent,
        };
    }

    private static string HardwareMetricGlyph(string name)
    {
        return name.Contains("电", StringComparison.Ordinal) ? "\uEBAA" :
            name.Contains("CPU", StringComparison.Ordinal) || name.Contains("处理器", StringComparison.Ordinal) ? "\uE950" :
            name.Contains("内存", StringComparison.Ordinal) ? "\uE950" :
            name.Contains("温度", StringComparison.Ordinal) ? "\uE9CA" :
            name.Contains("刷新", StringComparison.Ordinal) ? "\uE7F4" :
            "\uE8CC";
    }

    private static string FormatMemory(long? value)
        => value is long kilobytes ? $"{kilobytes / 1024d / 1024d:0.##} GB" : "当前连接无法获取";

    private static string FormatPercent(string value)
        => int.TryParse(value, out var percent) ? $"{percent}%" : value;

    private static FrameworkElement BuildMemoryUsagePie(double? usagePercent)
    {
        const double size = 76;
        const double radius = 36;
        const double center = size / 2;
        var canvas = new Canvas { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center };
        canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = size,
            Height = size,
            Fill = BorderLightBrush(),
        });
        if (usagePercent is double percent && percent > 0)
        {
            var ratio = Math.Clamp(percent / 100d, 0, 1);
            var angle = ratio * Math.PI * 2;
            var end = new Point(center + radius * Math.Sin(angle), center - radius * Math.Cos(angle));
            var figure = new PathFigure { StartPoint = new Point(center, center), IsClosed = true };
            figure.Segments.Add(new LineSegment { Point = new Point(center, center - radius) });
            figure.Segments.Add(new ArcSegment
            {
                Point = end,
                Size = new Size(radius, radius),
                IsLargeArc = ratio > 0.5,
                SweepDirection = SweepDirection.Clockwise,
            });
            figure.Segments.Add(new LineSegment { Point = new Point(center, center) });
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = geometry, Fill = PrimaryBrush() });
        }
        var label = new TextBlock
        {
            Text = usagePercent is double percentage ? $"{percentage:0}%" : "--",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = OnPrimaryBrush(),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return new Grid
        {
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { canvas, label },
        };
    }

    private async Task ShowHardwareDetailsAsync(DeviceModel device, DeviceHardwareSnapshot? knownSnapshot)
    {
        var snapshot = knownSnapshot;
        var readError = string.Empty;
        if (snapshot is null)
        {
            try
            {
                if (await EnsureDeviceReadyAsync(device, false))
                    snapshot = await _hardware.CollectAsync(device.DeviceId);
                else
                    readError = "当前 ADB 连接不可用。";
            }
            catch (Exception ex)
            {
                readError = ex.Message;
            }
        }
        snapshot ??= DeviceHardwareService.ParseSnapshot(string.Empty);

        var rows = new StackPanel { Spacing = 8 };
        var orderedKeys = new[]
        {
            "brand", "model", "device", "android", "sdk", "abi", "cpu_model", "cpu_cores", "load",
            "gpu_usage", "gpu_memory_bytes", "mem_total_kb", "mem_available_kb", "swap_total_kb", "swap_free_kb", "zram_disk_bytes",
            "battery_level", "battery_status", "battery_temp", "refresh_rate",
        };
        foreach (var key in orderedKeys)
            rows.Children.Add(BuildHardwareDetailRow(snapshot.Value(key), device.IsCompanionConnected));

        foreach (var frequency in snapshot.CpuFrequencies)
        {
            rows.Children.Add(BuildHardwareDetailRow(
                new HardwareValue(
                    $"cpu_{frequency.CoreIndex}",
                    $"CPU {frequency.CoreIndex} 当前/最高频率",
                    frequency.FrequencyUsagePercent is double usage
                        ? $"{frequency.DisplayValue} / {frequency.MaximumDisplayValue}（{usage:0.#}%）"
                        : $"{frequency.DisplayValue} / {frequency.MaximumDisplayValue}",
                    "ADB",
                    true),
                device.IsCompanionConnected));
        }

        if (snapshot.CpuFrequencies.Count == 0)
        {
            rows.Children.Add(BuildHardwareDetailRow(
                new HardwareValue("cpu_frequency", "CPU 各核心当前频率", "当前连接无法获取", "当前 ADB 连接", false),
                device.IsCompanionConnected));
        }

        foreach (var temperature in snapshot.Temperatures)
        {
            rows.Children.Add(BuildHardwareDetailRow(
                new HardwareValue($"temperature_{temperature.Name}", $"温度：{temperature.Name}", temperature.DisplayValue, "ADB", true),
                device.IsCompanionConnected));
        }

        if (snapshot.Temperatures.Count == 0)
        {
            rows.Children.Add(BuildHardwareDetailRow(
                new HardwareValue("temperatures", "硬件温度传感器", "当前连接无法获取", "当前 ADB 连接", false),
                device.IsCompanionConnected));
        }

        var error = string.IsNullOrWhiteSpace(readError)
            ? null
            : new TextBlock { Text = $"读取说明：{readError}", Foreground = MutedBrush(), TextWrapping = TextWrapping.Wrap };
        var scroll = new ScrollViewer
        {
            Height = 500,
            Content = rows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 10, 0),
        };
        StyleScrollViewer(scroll);
        FrostedDialog? dialog = null;
        var close = SecondaryButton("关闭");
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Click += (_, _) => dialog?.Hide();
        var content = new StackPanel
        {
            Width = 640,
            MaxWidth = 640,
            Spacing = 12,
        };
        if (error is not null)
            content.Children.Add(error);
        content.Children.Add(scroll);
        content.Children.Add(close);
        dialog = DialogChrome("硬件详情", content);
        await dialog.ShowAsync();
    }

    private static FrameworkElement BuildHardwareDetailRow(HardwareValue value, bool companionConnected)
    {
        var source = value.IsAvailable
            ? $"来源：{value.Source}"
            : companionConnected
                ? "当前连接无法获取（ADB 未返回，Companion 暂未提供该字段）"
                : "当前 ADB 连接无法获取";
        return new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = SurfaceAltBrush(),
            BorderBrush = BorderLightBrush(),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
            Child = new Grid
            {
                ColumnSpacing = 10,
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition(),
                },
                Children =
                {
                    new FontIcon
                    {
                        Glyph = HardwareMetricGlyph(value.Label),
                        FontFamily = new FontFamily("Segoe Fluent Icons"),
                        FontSize = 16,
                        Width = 20,
                        Height = 20,
                        Foreground = value.IsAvailable ? PrimaryBrush() : MutedBrush(),
                        VerticalAlignment = VerticalAlignment.Top,
                    },
                    WithColumn(new StackPanel
                    {
                        Spacing = 2,
                        Children =
                        {
                            new TextBlock { Text = value.Label, FontSize = 12, Foreground = MutedBrush() },
                            new TextBlock { Text = value.Value, FontSize = 14, TextWrapping = TextWrapping.Wrap, Foreground = PrimaryTextBrush() },
                            new TextBlock { Text = source, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = SecondaryTextBrush() },
                        },
                    }, 1),
                },
            },
        };
    }

    private sealed class HardwareMetricRuntime
    {
        public required string Id { get; init; }
        public required TextBlock BigValue { get; init; }
        public required TextBlock Detail { get; init; }
        public required MiniSparkline Sparkline { get; init; }
        public required PerformanceChart Chart { get; init; }
        public required HardwareTimeRangeSelector RangeSelector { get; init; }
        public required Border Card { get; init; }
        public required StackPanel ChartContainer { get; init; }
        public required Button DeleteButton { get; init; }
        public required SolidColorBrush AccentBrush { get; init; }
        public List<double> RecordedValues { get; } = new();
        public List<DateTimeOffset> RecordedTimes { get; } = new();
    }

    private sealed record HardwareMetricInfo(string Title, string Unit);
    private sealed record HardwareMetricReading(double? Value, string Detail);

    private Task ShowHardwareMonitorAsync(DeviceModel device)
    {
        if (_hardwareMonitorWindows.TryGetValue(device.DeviceId, out var existingWindow))
        {
            existingWindow.Activate();
            return Task.CompletedTask;
        }

        var monitorKey = device.DeviceId;
        var monitorWindow = new Window
        {
            Title = $"硬件监控 - {device.DisplayName}",
        };
        monitorWindow.ExtendsContentIntoTitleBar = true;
        _hardwareMonitorWindows[monitorKey] = monitorWindow;
        var monitorLifetime = new CancellationTokenSource();
        var samples = new List<HardwareMonitorSample>();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        var recordStatsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        var sampling = false;
        var recording = false;
        DeviceHardwareSnapshot? latestSnapshot = null;
        var metrics = new Dictionary<string, HardwareMetricRuntime>(StringComparer.OrdinalIgnoreCase);
        var metricsPanel = new StackPanel { Spacing = 8 };
        var chartsPanel = new StackPanel { Spacing = 10 };
        var addMetric = SecondaryButton("新增监控项");

        var status = new TextBlock
        {
            Text = "正在读取设备硬件信息...",
            FontSize = 12,
            Foreground = MutedBrush(),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var recordDuration = new TextBlock
        {
            Text = "00:00",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = PrimaryBrush(),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        var start = PrimaryButton("开始记录");
        var stop = SecondaryButton("结束记录");
        var export = SecondaryButton("导出");
        export.IsEnabled = false;
        var logToggle = SecondaryButton("日志");
        var logText = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 11,
            Background = TransparentBrush(),
            BorderThickness = new Thickness(0),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(logText, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(logText, ScrollBarVisibility.Auto);
        StyleTextBox(logText);
        var logLines = new Queue<string>();

        var recordStartTime = DateTimeOffset.MinValue;
        var recordEndTime = DateTimeOffset.MinValue;
        var failedSamples = 0;

        var summaryState = new TextBlock
        {
            Text = "尚未开始记录",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = PrimaryTextBrush(),
        };
        var summaryDuration = new TextBlock { Text = "时长 00:00.000", FontSize = 11, Foreground = SecondaryTextBrush() };
        var summarySamples = new TextBlock { Text = "采样 0 · 失败 0", FontSize = 11, Foreground = SecondaryTextBrush() };
        var summaryRange = new TextBlock { Text = "时间范围 --", FontSize = 10, Foreground = MutedBrush(), TextWrapping = TextWrapping.Wrap };
        var summaryCard = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = SurfaceAltBrush(),
            BorderBrush = PrimaryLightBrush(),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            Child = new StackPanel
            {
                Spacing = 7,
                Children =
                {
                    new Grid
                    {
                        ColumnDefinitions =
                        {
                            new ColumnDefinition(),
                            new ColumnDefinition { Width = GridLength.Auto },
                        },
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "记录统计",
                                FontSize = 12,
                                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                                Foreground = PrimaryBrush(),
                            },
                            WithColumn(new SymbolIcon(Symbol.Clock)
                            {
                                Foreground = PrimaryBrush(),
                                Width = 14,
                                Height = 14,
                            }, 1),
                        },
                    },
                    summaryState,
                    summaryDuration,
                    summarySamples,
                    summaryRange,
                },
            },
        };
        metricsPanel.Children.Add(summaryCard);

        void AddMonitorLog(string level, string message)
        {
            var entry = HardwareMonitorLogger.FormatEntry(device.DeviceId, level, message);
            logLines.Enqueue(entry);
            while (logLines.Count > 400)
                logLines.Dequeue();
            logText.Text = string.Join(Environment.NewLine, logLines);
            logText.Select(logText.Text.Length, 0);
            _ = HardwareMonitorLogger.AppendAsync(entry);
        }

        void UpdateRecordSummary()
        {
            if (recordStartTime == DateTimeOffset.MinValue)
                return;
            var end = recording ? DateTimeOffset.Now : recordEndTime;
            var elapsed = end > recordStartTime ? end - recordStartTime : TimeSpan.Zero;
            summaryState.Text = recording ? "正在记录" : "记录已结束";
            summaryState.Foreground = recording ? PrimaryBrush() : PrimaryTextBrush();
            summaryDuration.Text = $"时长 {(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}.{elapsed.Milliseconds:000}";
            summarySamples.Text = $"采样 {samples.Count} · 失败 {failedSamples}";
            summaryRange.Text = $"时间范围 {recordStartTime:HH:mm:ss.fff} → {end:HH:mm:ss.fff}";
        }

        void SaveMetricConfiguration()
        {
            _settings.Current.HardwareMonitorMetrics = metrics.Keys.ToList();
            _settings.Save();
        }

        void ShowMetricColorPicker(Button anchor, string id, SolidColorBrush accent, Action refreshAccent)
        {
            var flyout = SelectorFlyout(250, 320, out var panel);
            panel.Children.Add(new TextBlock
            {
                Text = "监控项颜色",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = PrimaryTextBrush(),
                Margin = new Thickness(6, 4, 6, 2),
            });
            var swatches = new WrapPanel { HorizontalSpacing = 8, VerticalSpacing = 8 };
            foreach (var color in HardwareMonitorPalette())
            {
                var swatch = new Button
                {
                    Width = 34,
                    Height = 34,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(17),
                    Background = TransparentBrush(),
                    BorderBrush = accent.Color == color ? PrimaryTextBrush() : BorderLightBrush(),
                    BorderThickness = new Thickness(accent.Color == color ? 2 : 1),
                    Content = new Microsoft.UI.Xaml.Shapes.Ellipse
                    {
                        Width = 20,
                        Height = 20,
                        Fill = new SolidColorBrush(color),
                    },
                };
                ToolTipService.SetToolTip(swatch, $"#{color.R:X2}{color.G:X2}{color.B:X2}");
                ApplyButtonResources(swatch, TransparentBrush(), PrimaryTextBrush(), HoverBrush(), SurfaceAltBrush(), swatch.BorderBrush, swatch.BorderThickness);
                swatch.Click += (_, _) =>
                {
                    accent.Color = color;
                    _settings.Current.HardwareMonitorMetricColors[id] = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
                    _settings.Save();
                    refreshAccent();
                    flyout.Hide();
                };
                swatches.Children.Add(swatch);
            }
            panel.Children.Add(swatches);
            flyout.ShowAt(anchor);
        }

        void RemoveMetric(string id)
        {
            if (!metrics.Remove(id, out var runtime))
                return;
            metricsPanel.Children.Remove(runtime.Card);
            chartsPanel.Children.Remove(runtime.ChartContainer);
            SaveMetricConfiguration();
            AddMonitorLog("info", $"metric.remove id={id}");
        }

        void AddMetric(string id, bool save)
        {
            if (metrics.ContainsKey(id))
                return;
            var info = GetHardwareMetricInfo(id);
            _settings.Current.HardwareMonitorMetricColors ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _settings.Current.HardwareMonitorMetricColors.TryGetValue(id, out var configuredColor);
            var accent = HardwareMonitorBrush(metrics.Count, configuredColor);
            var bigValue = MakeMetricBigValue(accent);
            var detail = MakeMetricDetail();
            var sparkline = new MiniSparkline(accent);
            var chart = new PerformanceChart(info.Title, $" {info.Unit}", accent, BorderLightBrush(), SecondaryTextBrush(), SurfaceAltBrush());
            var selector = new HardwareTimeRangeSelector(accent, BorderLightBrush())
            {
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 4, 0, 0),
            };
            Button? deleteButton = null;
            HardwareMetricRuntime? runtime = null;
            var card = BuildMetricCard(
                info.Title,
                info.Unit,
                bigValue,
                sparkline,
                detail,
                accent,
                anchor => ShowMetricColorPicker(anchor, id, accent, () =>
                {
                    runtime?.Sparkline.RefreshAccent();
                    runtime?.Chart.RefreshAccent();
                    runtime?.RangeSelector.RefreshAccent();
                }),
                () => RemoveMetric(id),
                out deleteButton);
            var chartContainer = new StackPanel { Spacing = 2, Children = { chart, selector } };
            runtime = new HardwareMetricRuntime
            {
                Id = id,
                BigValue = bigValue,
                Detail = detail,
                Sparkline = sparkline,
                Chart = chart,
                RangeSelector = selector,
                Card = card,
                ChartContainer = chartContainer,
                DeleteButton = deleteButton!,
                AccentBrush = accent,
            };
            selector.RangeChanged += (_, range) =>
            {
                if (runtime.RecordedValues.Count == 0)
                    return;
                var count = range.EndIndex - range.StartIndex + 1;
                runtime.Chart.SetSamples(
                    runtime.RecordedValues.Skip(range.StartIndex).Take(count).ToList(),
                    runtime.RecordedTimes.Skip(range.StartIndex).Take(count).ToList());
            };
            metrics[id] = runtime;
            if (metricsPanel.Children.Contains(addMetric))
                metricsPanel.Children.Insert(metricsPanel.Children.Count - 1, card);
            else
                metricsPanel.Children.Add(card);
            chartsPanel.Children.Add(chartContainer);
            if (latestSnapshot is not null)
                UpdateMetric(runtime, latestSnapshot, false);
            if (save)
            {
                SaveMetricConfiguration();
                AddMonitorLog("info", $"metric.add id={id}");
            }
        }

        void UpdateSnapshot(DeviceHardwareSnapshot snapshot)
        {
            latestSnapshot = snapshot;
            foreach (var runtime in metrics.Values)
                UpdateMetric(runtime, snapshot, recording);
        }

        async Task CaptureAsync()
        {
            if (sampling || monitorLifetime.IsCancellationRequested)
                return;

            sampling = true;
            var captureWatch = Stopwatch.StartNew();
            try
            {
                var snapshot = await _hardware.CollectAsync(device.DeviceId, monitorLifetime.Token);
                UpdateSnapshot(snapshot);
                if (recording)
                {
                    samples.Add(snapshot.ToMonitorSample());
                    status.Text = $"正在记录 · {samples.Count} 个采样点";
                    var elapsed = DateTimeOffset.Now - recordStartTime;
                    recordDuration.Text = $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
                    UpdateRecordSummary();
                }
                captureWatch.Stop();
                AddMonitorLog(
                    "sample",
                    $"capture.ok elapsed_ms={captureWatch.ElapsedMilliseconds} recording={recording} samples={samples.Count} fps={snapshot.AppFps?.ToString("0.##") ?? "n/a"}");
            }
            catch (OperationCanceledException) when (monitorLifetime.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                captureWatch.Stop();
                if (recording)
                {
                    failedSamples++;
                    UpdateRecordSummary();
                }
                status.Text = $"采样失败：{ex.Message}";
                AddMonitorLog("error", $"capture.failed elapsed_ms={captureWatch.ElapsedMilliseconds} error={ex.Message}");
            }
            finally
            {
                sampling = false;
            }
        }

        void SetExportState()
        {
            export.IsEnabled = !recording && samples.Count > 0;
        }

        start.Click += async (_, _) =>
        {
            if (recording)
                return;
            recording = true;
            recordStartTime = DateTimeOffset.Now;
            recordEndTime = DateTimeOffset.MinValue;
            failedSamples = 0;
            samples.Clear();
            recordDuration.Text = "00:00";
            foreach (var runtime in metrics.Values)
            {
                runtime.RecordedValues.Clear();
                runtime.RecordedTimes.Clear();
                runtime.Chart.Clear();
                runtime.Sparkline.Clear();
                runtime.RangeSelector.Visibility = Visibility.Collapsed;
                runtime.DeleteButton.IsEnabled = false;
            }
            addMetric.IsEnabled = false;
            start.Visibility = Visibility.Collapsed;
            stop.Visibility = Visibility.Visible;
            SetExportState();
            timer.Start();
            recordStatsTimer.Start();
            UpdateRecordSummary();
            AddMonitorLog("info", $"record.start metrics={string.Join(",", metrics.Keys)}");
            await CaptureAsync();
        };
        stop.Click += (_, _) =>
        {
            recording = false;
            recordEndTime = DateTimeOffset.Now;
            timer.Stop();
            recordStatsTimer.Stop();
            start.Visibility = Visibility.Visible;
            stop.Visibility = Visibility.Collapsed;
            status.Text = samples.Count == 0 ? "未获取到可保存的采样" : $"记录已结束 · 共 {samples.Count} 个采样点";
            foreach (var runtime in metrics.Values)
            {
                runtime.DeleteButton.IsEnabled = true;
                if (runtime.RecordedValues.Count > 1)
                {
                    runtime.RangeSelector.SetSamples(runtime.RecordedValues, runtime.RecordedTimes);
                    runtime.RangeSelector.ResetRange();
                    runtime.RangeSelector.Visibility = Visibility.Visible;
                }
            }
            addMetric.IsEnabled = true;
            SetExportState();
            UpdateRecordSummary();
            AddMonitorLog("info", $"record.stop samples={samples.Count} failed={failedSamples} duration_ms={(recordEndTime - recordStartTime).TotalMilliseconds:0}");
        };
        timer.Tick += async (_, _) => await CaptureAsync();
        recordStatsTimer.Tick += (_, _) =>
        {
            if (!recording)
                return;
            var elapsed = DateTimeOffset.Now - recordStartTime;
            recordDuration.Text = $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
            UpdateRecordSummary();
        };
        export.Click += (_, _) =>
        {
            var flyout = SelectorFlyout(220, 280, out var panel);
            AddSelectorOption(panel, flyout, Symbol.Save, "Excel 工作簿", ".xlsx，便于继续分析", false, false,
                async () =>
                {
                    AddMonitorLog("info", "export.request format=excel");
                    await ExportHardwareReportAsync(HardwareReportFormat.Excel, samples, monitorWindow);
                });
            AddSelectorOption(panel, flyout, Symbol.Globe, "HTML 网页", ".html，可独立打开查看", false, false,
                async () =>
                {
                    AddMonitorLog("info", "export.request format=html");
                    await ExportHardwareReportAsync(HardwareReportFormat.Html, samples, monitorWindow);
                });
            AddSelectorOption(panel, flyout, Symbol.Library, "SQLite 数据库", ".sqlite，保留结构化采样", false, false,
                async () =>
                {
                    AddMonitorLog("info", "export.request format=sqlite");
                    await ExportHardwareReportAsync(HardwareReportFormat.Sqlite, samples, monitorWindow);
                });
            flyout.ShowAt(export);
        };
        stop.Visibility = Visibility.Collapsed;

        var configured = _settings.Current.HardwareMonitorMetrics;
        if (configured is null || configured.Count == 0)
            configured = ["cpu.usage", "memory.physical", "temperature.max", "display.refresh"];
        foreach (var id in configured.Distinct(StringComparer.OrdinalIgnoreCase))
            AddMetric(id, false);

        addMetric.HorizontalAlignment = HorizontalAlignment.Stretch;
        addMetric.Click += (_, _) =>
        {
            var flyout = SelectorFlyout(340, 440, out var panel);
            var search = new TextBox
            {
                PlaceholderText = "搜索 CPU、GPU、温度、频率、FPS...",
                Margin = new Thickness(4, 4, 4, 6),
            };
            StyleTextBox(search);
            var optionsPanel = new StackPanel { Spacing = 4 };
            var optionsScroller = new ScrollViewer
            {
                Content = optionsPanel,
                MaxHeight = 360,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            StyleScrollViewer(optionsScroller);
            panel.Children.Add(search);
            panel.Children.Add(optionsScroller);

            void RenderOptions()
            {
                optionsPanel.Children.Clear();
                var query = search.Text.Trim();
                var available = GetAvailableHardwareMetricIds(latestSnapshot)
                    .Where(id => !metrics.ContainsKey(id))
                    .Select(id => (Id: id, Info: GetHardwareMetricInfo(id)))
                    .Where(item => HardwareMonitorMetricSearch.Matches(query, item.Id, item.Info.Title, item.Info.Unit))
                    .ToList();
                foreach (var item in available)
                {
                    var reading = latestSnapshot is null ? null : ReadHardwareMetric(item.Id, latestSnapshot);
                    AddSelectorOption(
                        optionsPanel,
                        flyout,
                        Symbol.Add,
                        item.Info.Title,
                        reading?.Value is null ? "当前连接暂时无法获取" : $"{reading.Value:0.##} {item.Info.Unit}",
                        false,
                        false,
                        () => AddMetric(item.Id, true));
                }
                if (available.Count == 0)
                {
                    optionsPanel.Children.Add(new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(query) ? "所有可用项目均已添加" : "没有匹配的监控项",
                        Foreground = MutedBrush(),
                        Padding = new Thickness(8),
                    });
                }
            }
            search.TextChanged += (_, _) => RenderOptions();
            RenderOptions();
            flyout.ShowAt(addMetric);
        };
        metricsPanel.Children.Add(addMetric);

        var metricsScroller = new ScrollViewer
        {
            Content = metricsPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 4, 0),
        };
        StyleScrollViewer(metricsScroller);
        AttachWheelScrolling(metricsScroller);

        var chartsScroller = new ScrollViewer
        {
            Content = chartsPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(4, 0, 8, 0),
        };
        StyleScrollViewer(chartsScroller);
        AttachWheelScrolling(chartsScroller);

        var logPanel = new Border
        {
            Height = 170,
            Visibility = Visibility.Collapsed,
            Background = SurfaceAltBrush(),
            BorderBrush = BorderLightBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Child = new Grid
            {
                RowSpacing = 6,
                RowDefinitions =
                {
                    new RowDefinition { Height = GridLength.Auto },
                    new RowDefinition(),
                },
                Children =
                {
                    new TextBlock
                    {
                        Text = "硬件监控日志 · hardware-monitor.log",
                        FontSize = 11,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = PrimaryTextBrush(),
                    },
                    WithRow(logText, 1),
                },
            },
        };
        logToggle.Click += (_, _) =>
        {
            logPanel.Visibility = logPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            logToggle.Content = logPanel.Visibility == Visibility.Visible ? "收起日志" : "日志";
        };

        // === 顶部工具栏 ===
        var toolbar = new Grid
        {
            ColumnSpacing = 12,
            ColumnDefinitions =
            {
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
            },
            Margin = new Thickness(0, 0, 0, 4),
            Children =
            {
                WithColumn(new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children = { status, recordDuration },
                }, 0),
            },
        };
        Grid.SetColumn(logToggle, 1);
        toolbar.Children.Add(logToggle);
        Grid.SetColumn(export, 2);
        toolbar.Children.Add(export);
        Grid.SetColumn(stop, 3);
        toolbar.Children.Add(stop);
        Grid.SetColumn(start, 4);
        toolbar.Children.Add(start);

        // === 主布局：左右分栏 ===
        var metricsColumn = new ColumnDefinition { Width = new GridLength(260) };
        var mainLayout = new Grid
        {
            ColumnSpacing = 12,
            ColumnDefinitions =
            {
                metricsColumn,
                new ColumnDefinition(),
            },
            Children =
            {
                WithColumn(metricsScroller, 0),
                WithColumn(chartsScroller, 1),
            },
        };
        mainLayout.SizeChanged += (_, args) =>
        {
            var width = args.NewSize.Width;
            var target = Math.Clamp(width * 0.31, 220, 300);
            if (Math.Abs(metricsColumn.Width.Value - target) >= 1)
                metricsColumn.Width = new GridLength(target);
        };

        var content = new Grid
        {
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            RowSpacing = 10,
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition(),
                new RowDefinition { Height = GridLength.Auto },
            },
            Children =
            {
                WithRow(toolbar, 0),
                WithRow(mainLayout, 1),
                WithRow(logPanel, 2),
            },
        };

        var monitorTitleBar = new Grid
        {
            Height = 40,
            Padding = new Thickness(14, 0, 138, 0),
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition(),
            },
            Children =
            {
                WithColumn(new Border
                {
                    Width = 24,
                    Height = 24,
                    CornerRadius = new CornerRadius(6),
                    Background = PrimaryLightBrush(),
                    Child = new SymbolIcon(Symbol.Setting)
                    {
                        Foreground = PrimaryBrush(),
                        Width = 14,
                        Height = 14,
                    },
                }, 0),
                WithColumn(new TextBlock
                {
                    Text = "硬件监控",
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = PrimaryTextBrush(),
                    VerticalAlignment = VerticalAlignment.Center,
                }, 1),
                WithColumn(new TextBlock
                {
                    Text = device.DisplayName,
                    FontSize = 11,
                    Foreground = MutedBrush(),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                }, 2),
            },
        };
        var windowRoot = new Grid
        {
            Background = AppBrush(),
            RequestedTheme = s_darkTheme ? ElementTheme.Dark : ElementTheme.Light,
            RowDefinitions =
            {
                new RowDefinition { Height = new GridLength(40) },
                new RowDefinition(),
            },
        };
        var monitorBackground = new GridBackground
        {
            Fill = AppBrush(),
            GridLineBrush = GridLineBrush(),
            GridSize = 20,
        };
        Grid.SetRowSpan(monitorBackground, 2);
        windowRoot.Children.Add(monitorBackground);
        Grid.SetRow(monitorTitleBar, 0);
        windowRoot.Children.Add(monitorTitleBar);
        var monitorBody = new Border
        {
            Margin = new Thickness(18, 8, 18, 18),
            Background = ShellBrush(),
            BorderBrush = BorderLightBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Child = content,
        };
        Grid.SetRow(monitorBody, 1);
        windowRoot.Children.Add(monitorBody);
        monitorWindow.Content = windowRoot;
        monitorWindow.SetTitleBar(monitorTitleBar);
        ApplyWindowTitleBarTheme(monitorWindow);
        monitorWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(1040, 640));
        var minimumSizeHook = WindowMinimumSize.Attach(monitorWindow, 760, 520);
        monitorWindow.Closed += (_, _) =>
        {
            timer.Stop();
            recordStatsTimer.Stop();
            AddMonitorLog("info", "window.closed");
            monitorLifetime.Cancel();
            monitorLifetime.Dispose();
            minimumSizeHook.Dispose();
            _hardwareMonitorWindows.Remove(monitorKey);
        };
        monitorWindow.Activate();
        AddMonitorLog("info", "window.opened min_size=760x520");
        _ = CaptureAsync();
        return Task.CompletedTask;
    }

    private static IReadOnlyList<Windows.UI.Color> HardwareMonitorPalette()
    {
        return
        [
            ColorHelper.FromArgb(255, 34, 197, 94),
            ColorHelper.FromArgb(255, 56, 189, 248),
            ColorHelper.FromArgb(255, 251, 146, 60),
            ColorHelper.FromArgb(255, 168, 85, 247),
            ColorHelper.FromArgb(255, 244, 63, 94),
            ColorHelper.FromArgb(255, 45, 212, 191),
            ColorHelper.FromArgb(255, 250, 204, 21),
            ColorHelper.FromArgb(255, 99, 102, 241),
        ];
    }

    private static SolidColorBrush HardwareMonitorBrush(int index, string? configuredColor)
    {
        if (!string.IsNullOrWhiteSpace(configuredColor))
        {
            var hex = configuredColor.Trim().TrimStart('#');
            if (hex.Length == 6 &&
                byte.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out var red) &&
                byte.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out var green) &&
                byte.TryParse(hex[4..6], System.Globalization.NumberStyles.HexNumber, null, out var blue))
            {
                return new SolidColorBrush(ColorHelper.FromArgb(255, red, green, blue));
            }
        }
        var colors = HardwareMonitorPalette();
        return new SolidColorBrush(colors[index % colors.Count]);
    }

    private static HardwareMetricInfo GetHardwareMetricInfo(string id)
    {
        if (id.StartsWith("cpu.core.", StringComparison.OrdinalIgnoreCase))
        {
            var parts = id.Split('.');
            var core = parts.Length > 2 ? parts[2] : "?";
            var usage = id.EndsWith(".usage", StringComparison.OrdinalIgnoreCase);
            return new HardwareMetricInfo($"CPU {core} {(usage ? "占用估算" : "频率")}", usage ? "%" : "GHz");
        }
        if (id.Equals("temperature.cpu", StringComparison.OrdinalIgnoreCase))
            return new HardwareMetricInfo("CPU 温度", "°C");
        if (id.Equals("temperature.gpu", StringComparison.OrdinalIgnoreCase))
            return new HardwareMetricInfo("GPU 温度", "°C");
        if (id.StartsWith("temperature.", StringComparison.OrdinalIgnoreCase) && id != "temperature.max")
            return new HardwareMetricInfo($"{id["temperature.".Length..]} 温度", "°C");
        return id.ToLowerInvariant() switch
        {
            "cpu.usage" => new HardwareMetricInfo("CPU 总体占用估算", "%"),
            "cpu.averagefrequency" => new HardwareMetricInfo("CPU 平均核心频率", "GHz"),
            "memory.physical" => new HardwareMetricInfo("物理内存占用", "%"),
            "memory.extended" => new HardwareMetricInfo("扩展后内存占用", "%"),
            "gpu.usage" => new HardwareMetricInfo("GPU 占用率", "%"),
            "gpu.frequency" => new HardwareMetricInfo("GPU 频率", "MHz"),
            "gpu.memory" => new HardwareMetricInfo("GPU 显存占用", "MB"),
            "temperature.max" => new HardwareMetricInfo("最高硬件温度", "°C"),
            "display.refresh" => new HardwareMetricInfo("屏幕刷新率", "Hz"),
            "display.appfps" => new HardwareMetricInfo("当前应用 FPS", "FPS"),
            _ => new HardwareMetricInfo(id, string.Empty),
        };
    }

    private static IReadOnlyList<string> GetAvailableHardwareMetricIds(DeviceHardwareSnapshot? snapshot)
    {
        var ids = new List<string>
        {
            "cpu.usage",
            "cpu.averageFrequency",
            "memory.physical",
            "memory.extended",
            "gpu.usage",
            "gpu.frequency",
            "gpu.memory",
            "temperature.max",
            "temperature.cpu",
            "temperature.gpu",
            "display.refresh",
            "display.appfps",
        };
        if (snapshot is not null)
        {
            foreach (var core in snapshot.CpuFrequencies)
            {
                ids.Add($"cpu.core.{core.CoreIndex}.frequency");
                ids.Add($"cpu.core.{core.CoreIndex}.usage");
            }
            ids.AddRange(snapshot.Temperatures.Select(temperature => $"temperature.{temperature.Name}"));
        }
        return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static HardwareMetricReading ReadHardwareMetric(string id, DeviceHardwareSnapshot snapshot)
    {
        if (id.StartsWith("cpu.core.", StringComparison.OrdinalIgnoreCase))
        {
            var parts = id.Split('.');
            if (parts.Length >= 4 && int.TryParse(parts[2], out var index))
            {
                var core = snapshot.CpuFrequencies.FirstOrDefault(item => item.CoreIndex == index);
                if (core is not null)
                {
                    if (parts[3].Equals("usage", StringComparison.OrdinalIgnoreCase))
                        return new HardwareMetricReading(core.FrequencyUsagePercent, $"{core.DisplayValue} / {core.MaximumDisplayValue}");
                    return new HardwareMetricReading(core.Kilohertz / 1_000_000d, $"最高 {core.MaximumDisplayValue}");
                }
            }
            return new HardwareMetricReading(null, "当前连接无法获取");
        }
        if (id.Equals("temperature.cpu", StringComparison.OrdinalIgnoreCase))
        {
            return snapshot.CpuTemperatures.Count == 0
                ? new HardwareMetricReading(null, "当前连接无法获取 CPU 温度")
                : new HardwareMetricReading(
                    snapshot.CpuTemperatures.Max(item => item.Celsius),
                    string.Join("\n", snapshot.CpuTemperatures.Select(item => $"{item.Name}: {item.DisplayValue}")));
        }
        if (id.Equals("temperature.gpu", StringComparison.OrdinalIgnoreCase))
        {
            return snapshot.GpuTemperatures.Count == 0
                ? new HardwareMetricReading(null, "当前连接无法获取 GPU 温度")
                : new HardwareMetricReading(
                    snapshot.GpuTemperatures.Max(item => item.Celsius),
                    string.Join("\n", snapshot.GpuTemperatures.Select(item => $"{item.Name}: {item.DisplayValue}")));
        }
        if (id.StartsWith("temperature.", StringComparison.OrdinalIgnoreCase) && id != "temperature.max")
        {
            var name = id["temperature.".Length..];
            var sensor = snapshot.Temperatures.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            return sensor is null
                ? new HardwareMetricReading(null, "当前连接无法获取")
                : new HardwareMetricReading(sensor.Celsius, sensor.DisplayValue);
        }
        return id.ToLowerInvariant() switch
        {
            "cpu.usage" => new HardwareMetricReading(
                snapshot.CpuFrequencyUsagePercent,
                snapshot.CpuFrequencyUsagePercent is null ? "缺少最高频率数据" : "按各核心当前频率 / 最高频率加权估算"),
            "cpu.averagefrequency" => new HardwareMetricReading(
                snapshot.CpuFrequencies.Count > 0 ? snapshot.CpuFrequencies.Average(item => item.Kilohertz) / 1_000_000d : null,
                snapshot.CpuFrequencies.Count > 0
                    ? string.Join("\n", snapshot.CpuFrequencies.Select(core => $"核心 {core.CoreIndex}: {core.DisplayValue}"))
                    : "当前连接无法获取"),
            "memory.physical" => new HardwareMetricReading(
                snapshot.MemoryUsagePercent,
                snapshot.UsedMemoryKb is long used ? $"{FormatMemory(used)} / {FormatMemory(snapshot.TotalMemoryKb)}" : "当前连接无法获取"),
            "memory.extended" => new HardwareMetricReading(
                snapshot.ExtendedMemoryUsagePercent,
                snapshot.SwapTotalKb is > 0
                    ? $"物理 {FormatMemory(snapshot.TotalMemoryKb)} + 虚拟 {FormatMemory(snapshot.SwapTotalKb)}"
                    : "设备未启用虚拟内存扩展"),
            "gpu.usage" => new HardwareMetricReading(
                snapshot.Gpu?.EffectiveUsagePercent,
                snapshot.Gpu?.EffectiveUsagePercent is not null
                    ? snapshot.Gpu.Source
                    : snapshot.Gpu?.UnavailableReason ?? "设备未暴露可读取的 GPU 占用率"),
            "gpu.frequency" => new HardwareMetricReading(
                snapshot.Gpu?.CurrentFrequencyHz is long frequency ? frequency / 1_000_000d : null,
                snapshot.Gpu?.CurrentFrequencyHz is null
                    ? snapshot.Gpu?.UnavailableReason ?? "设备未暴露可读取的 GPU 频率"
                    : snapshot.Gpu?.MaximumFrequencyHz is long maximum
                        ? $"最高 {maximum / 1_000_000d:0.##} MHz · {snapshot.Gpu?.Source ?? "ADB sysfs"}"
                        : snapshot.Gpu?.Source ?? "ADB sysfs"),
            "gpu.memory" => new HardwareMetricReading(
                snapshot.Gpu?.MemoryBytes is long memory ? memory / 1024d / 1024d : null,
                snapshot.Gpu?.MemoryBytes is long bytes ? $"{bytes:N0} Bytes（GPU Service）" : "当前连接无法获取"),
            "temperature.max" => new HardwareMetricReading(
                snapshot.Temperatures.Count > 0 ? snapshot.Temperatures.Max(item => item.Celsius) : null,
                snapshot.Temperatures.Count > 0
                    ? string.Join("\n", snapshot.Temperatures.Select(item => $"{item.Name}: {item.DisplayValue}"))
                    : "当前连接无法获取"),
            "display.refresh" => new HardwareMetricReading(
                snapshot.RefreshRateHz,
                snapshot.RefreshRateHz is double rate ? $"内置屏幕活动模式 {rate:0.##} Hz" : "当前连接无法获取"),
            "display.appfps" => new HardwareMetricReading(
                snapshot.AppFps,
                snapshot.AppFps is null
                    ? "未获取到前台应用的 SurfaceFlinger 图层"
                    : "前台应用 SurfaceFlinger 实际呈现帧率"),
            _ => new HardwareMetricReading(null, "当前连接无法获取"),
        };
    }

    private static void UpdateMetric(HardwareMetricRuntime runtime, DeviceHardwareSnapshot snapshot, bool recording)
    {
        var reading = ReadHardwareMetric(runtime.Id, snapshot);
        runtime.BigValue.Text = reading.Value is double value ? $"{value:0.##}" : "--";
        runtime.Detail.Text = reading.Detail;
        runtime.Sparkline.AddValue(reading.Value);
        if (!recording || reading.Value is not double recorded)
            return;
        runtime.RecordedValues.Add(recorded);
        runtime.RecordedTimes.Add(snapshot.CapturedAt);
        runtime.Chart.AddValue(recorded, snapshot.CapturedAt);
    }

    /// <summary>
    /// 创建实时指标卡片中的大号数值 TextBlock。
    /// </summary>
    private static TextBlock MakeMetricBigValue(Brush color)
    {
        return new TextBlock
        {
            Text = "--",
            FontSize = 28,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = color,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>
    /// 创建实时指标卡片中的详细信息 TextBlock。
    /// </summary>
    private static TextBlock MakeMetricDetail()
    {
        return new TextBlock
        {
            Text = "等待采样",
            FontSize = 11,
            Foreground = MutedBrush(),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 4,
        };
    }

    /// <summary>
    /// 构建 PerfDog 风格的实时指标卡片：标题 + 大数字 + sparkline + 详细信息。
    /// </summary>
    private static Border BuildMetricCard(
        string title,
        string unit,
        TextBlock bigValue,
        MiniSparkline sparkline,
        TextBlock detail,
        SolidColorBrush accent,
        Action<Button> chooseColor,
        Action remove,
        out Button deleteButton)
    {
        var header = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
            },
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = accent,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
        var unitText = new TextBlock
        {
            Text = unit,
            FontSize = 11,
            Foreground = MutedBrush(),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(unitText, 1);
        header.Children.Add(unitText);
        var colorButton = new Button
        {
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(6, -4, 0, -4),
            Content = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 14,
                Height = 14,
                Fill = accent,
            },
        };
        ToolTipService.SetToolTip(colorButton, "设置监控项颜色");
        ApplyButtonResources(colorButton, TransparentBrush(), SecondaryTextBrush(), HoverBrush(), SurfaceAltBrush(), TransparentBrush(), new Thickness(0));
        colorButton.Click += (_, _) => chooseColor(colorButton);
        Grid.SetColumn(colorButton, 2);
        header.Children.Add(colorButton);
        deleteButton = new Button
        {
            Content = new SymbolIcon(Symbol.Delete),
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(6, -4, -6, -4),
        };
        ToolTipService.SetToolTip(deleteButton, "删除监控项");
        ApplyButtonResources(deleteButton, TransparentBrush(), SecondaryTextBrush(), HoverBrush(), SurfaceAltBrush(), TransparentBrush(), new Thickness(0));
        deleteButton.Click += (_, _) => remove();
        Grid.SetColumn(deleteButton, 3);
        header.Children.Add(deleteButton);

        var valueRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Bottom,
            Children =
            {
                bigValue,
                new TextBlock { Text = unit, FontSize = 13, Foreground = MutedBrush(), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 4) },
            },
        };

        var sparklineContainer = new Border
        {
            Height = 32,
            Margin = new Thickness(0, 6, 0, 6),
            Child = sparkline,
        };

        var card = new StackPanel
        {
            Spacing = 4,
            Children = { header, valueRow, sparklineContainer, detail },
        };

        return new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = SurfaceBrush(),
            BorderBrush = BorderLightBrush(),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10, 12, 10),
            Child = card,
        };
    }

    private async Task ExportHardwareReportAsync(
        HardwareReportFormat format,
        IReadOnlyList<HardwareMonitorSample> samples,
        Window? ownerWindow = null)
    {
        if (samples.Count == 0)
        {
            Notify("无法导出", "请先开始并结束硬件监控记录。", InfoBarSeverity.Warning);
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"ADBControl-Hardware-{DateTime.Now:yyyyMMdd-HHmmss}",
        };
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(ownerWindow ?? this));
        var label = format switch
        {
            HardwareReportFormat.Excel => "Excel 工作簿",
            HardwareReportFormat.Html => "HTML 网页",
            HardwareReportFormat.Sqlite => "SQLite 数据库",
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        var extension = format switch
        {
            HardwareReportFormat.Excel => ".xlsx",
            HardwareReportFormat.Html => ".html",
            HardwareReportFormat.Sqlite => ".sqlite",
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        picker.FileTypeChoices.Add(label, new List<string> { extension });
        var file = await picker.PickSaveFileAsync();
        if (file is null)
            return;

        try
        {
            await _hardwareReportExporter.ExportAsync(format, samples, file.Path);
            Notify("硬件记录已导出", file.Path, InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Notify("硬件记录导出失败", ex.Message, InfoBarSeverity.Error);
        }
    }

    private static IReadOnlyDictionary<string, string> ParseKeyValueOutput(string output)
    {
        return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
            .GroupBy(parts => parts[0].Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last()[1].Trim(), StringComparer.OrdinalIgnoreCase);
    }

    private static string ValueOr(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : "未获取到";

    private static string Suffix(string value, string suffix)
        => value == "未获取到" || value.EndsWith(suffix, StringComparison.Ordinal) ? value : value + suffix;

    private static string FormatBatteryTemperature(string value)
    {
        return int.TryParse(value, out var raw)
            ? $"{raw / 10d:0.#} °C"
            : value;
    }

    private static string FormatBatteryStatus(string status, string acPowered)
    {
        var state = status switch
        {
            "2" => "充电中",
            "3" => "已充满",
            "4" => "未充电",
            "5" => "未充电",
            _ => "未知",
        };
        return acPowered.Equals("true", StringComparison.OrdinalIgnoreCase) ? $"{state} · 外接电源" : state;
    }

    private static void AddHardwareCard(Grid grid, FrameworkElement card, int column, int row, int columnSpan = 1)
    {
        Grid.SetColumn(card, column);
        Grid.SetRow(card, row);
        Grid.SetColumnSpan(card, columnSpan);
        grid.Children.Add(card);
    }

    private static FrameworkElement HardwareSection(string title, params (string Name, string Value)[] values)
    {
        var grid = new Grid { ColumnSpacing = 10, RowSpacing = 10 };
        for (var index = 0; index < values.Length; index++)
        {
            if (index % 2 == 0)
                grid.ColumnDefinitions.Add(new ColumnDefinition());
            if (index / 2 >= grid.RowDefinitions.Count)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var metric = new Border
            {
                UseLayoutRounding = true,
                CornerRadius = new CornerRadius(10),
                Background = SurfaceAltBrush(),
                BorderBrush = BorderLightBrush(),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 10, 12, 10),
                Child = new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock { Text = values[index].Name, FontSize = 11, Foreground = MutedBrush() },
                        new TextBlock { Text = values[index].Value, FontSize = 14, TextWrapping = TextWrapping.Wrap, Foreground = PrimaryTextBrush() },
                    },
                },
            };
            Grid.SetColumn(metric, index % 2);
            Grid.SetRow(metric, index / 2);
            grid.Children.Add(metric);
        }

        return new Border
        {
            UseLayoutRounding = true,
            CornerRadius = new CornerRadius(12),
            Background = SurfaceBrush(),
            BorderBrush = BorderLightBrush(),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = PrimaryBrush() },
                    grid,
                },
            },
        };
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

    private static T WithColumn<T>(T element, int column) where T : FrameworkElement
    {
        Grid.SetColumn(element, column);
        return element;
    }

    private static T WithRow<T>(T element, int row) where T : FrameworkElement
    {
        Grid.SetRow(element, row);
        return element;
    }

    private static WrapPanel ActionGrid(params UIElement[] actions)
    {
        // 统一设置水平间距 8、垂直间距 8，替代各按钮自行设置 Margin 的方式，
        // 消除有的按钮有间距、有的没有间距的问题。
        var panel = new WrapPanel { HorizontalSpacing = 8, VerticalSpacing = 8 };
        foreach (var action in actions)
            panel.Children.Add(action);
        return panel;
    }

    private Button DeviceActionButton(DeviceModel device, string text, Func<Task<AdbCommandResult>> action)
    {
        var button = SecondaryButton(text);
        // 间距由 ActionGrid 的 WrapPanel.HorizontalSpacing 统一管理，不再设置 Margin。
        button.Click += async (_, _) =>
        {
            if (!await EnsureDeviceReadyAsync(device))
                return;
            try
            {
                var result = await action();
                MarkDeviceOfflineIfUnavailable(device, result);
                Notify(result.Success ? "操作已执行" : "操作失败", FormatCommandResult(result), result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            }
            catch (Exception ex)
            {
                MarkDeviceOfflineIfUnavailable(device, ex);
                Notify("操作失败", ex.Message, InfoBarSeverity.Error);
            }
        };
        return button;
    }

    private Button DeviceActionButton(DeviceModel device, string text, Func<Task<byte[]>> action, string successMessage)
    {
        var button = SecondaryButton(text);
        // 间距由 ActionGrid 的 WrapPanel.HorizontalSpacing 统一管理，不再设置 Margin。
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
                MarkDeviceOfflineIfUnavailable(device, ex);
                Notify("操作失败", ex.Message, InfoBarSeverity.Error);
            }
        };
        return button;
    }

    private static ListViewItem BuildPackageListItem(PackageListItem item)
    {
        var icon = new FontIcon
        {
            Glyph = "\uE71D",
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 20,
            Width = 30,
            Height = 30,
            Foreground = item.HasResolvedDisplayName ? PrimaryBrush() : SecondaryTextBrush(),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var contentGrid = new Grid
        {
            ColumnSpacing = 10,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition(),
            },
        };
        contentGrid.Children.Add(icon);
        var text = new StackPanel { Spacing = 3 };
        text.Children.Add(new TextBlock
        {
            Text = item.PackageName,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = PrimaryTextBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = item.DisplayName,
            FontSize = 11,
            Foreground = item.HasResolvedDisplayName ? SecondaryTextBrush() : MutedBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(text, 1);
        contentGrid.Children.Add(text);

        return new ListViewItem
        {
            Tag = item,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 4),
            Content = new Border
            {
                Padding = new Thickness(8, 8, 8, 8),
                Child = contentGrid,
            },
        };
    }

    private static InteractiveSurface BuildPackageSurfaceItem(
        PackageListItem item,
        PackageSelection selection,
        BitmapImage? iconSource)
    {
        FrameworkElement icon = iconSource is not null
            ? new Border
            {
                Width = 38,
                Height = 38,
                CornerRadius = new CornerRadius(11),
                Background = SurfaceAltBrush(),
                Padding = new Thickness(3),
                Child = new Image
                {
                    Source = iconSource,
                    Stretch = Stretch.Uniform,
                },
            }
            : new Border
            {
                Width = 38,
                Height = 38,
                CornerRadius = new CornerRadius(11),
                Background = SurfaceAltBrush(),
                Child = new FontIcon
                {
                    Glyph = "\uE71D",
                    FontFamily = new FontFamily("Segoe Fluent Icons"),
                    FontSize = 20,
                    Foreground = SecondaryTextBrush(),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
        var labels = new StackPanel
        {
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = item.DisplayName,
                    FontSize = 13,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = item.HasResolvedDisplayName ? PrimaryTextBrush() : MutedBrush(),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                new TextBlock
                {
                    Text = item.PackageName,
                    FontSize = 11,
                    Foreground = SecondaryTextBrush(),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            },
        };
        var content = new Grid
        {
            ColumnSpacing = 10,
            Children =
            {
                icon,
                WithColumn(labels, 1),
            },
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition(),
            },
        };
        var palette = new InteractiveSurfacePalette(
            SurfaceBrush(),
            HoverBrush(),
            SurfaceAltBrush(),
            PrimaryLightBrush(),
            TransparentBrush(),
            PrimaryBrush(),
            PrimaryTextBrush(),
            PrimaryBrush());
        var surface = new InteractiveSurface(content, palette, new CornerRadius(10), new Thickness(10, 8, 10, 8))
        {
            Margin = new Thickness(0, 0, 0, 4),
        };
        surface.SetAutomationName($"软件包 {item.PackageName} {item.DisplayName}");
        surface.Invoked += (_, _) => selection.Select(item, surface);
        return surface;
    }

    private static async Task<(BitmapImage? Image, string? ErrorCode)> DecodePackageIconAsync(byte[]? png)
    {
        if (png is null)
            return (null, null);

        try
        {
            var bitmap = new BitmapImage();
            using var stream = new MemoryStream(png);
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            return (bitmap, null);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or COMException)
        {
            // A corrupt APK icon must not discard the other package metadata from the same page.
            return (null, "PACKAGE_ICON_DECODE_FAILED");
        }
    }

    private static string? SelectedPackageName(ListView packages)
    {
        return packages.SelectedItem switch
        {
            ListViewItem { Tag: PackageListItem item } => item.PackageName,
            PackageListItem item => item.PackageName,
            string value when !string.IsNullOrWhiteSpace(value) => value,
            _ => null,
        };
    }

    private static string? SelectedPackageName(PackageSelection selection)
    {
        return selection.Selected?.PackageName;
    }

    private Button DevicePackageButton(DeviceModel device, string text, PackageSelection selection, Func<string, Task<AdbCommandResult>> action)
    {
        var button = SecondaryButton(text);
        button.Margin = new Thickness(0, 0, 8, 8);
        button.Click += async (_, _) =>
        {
            var packageName = SelectedPackageName(selection);
            if (packageName is null)
            {
                Notify("请选择软件包", "先在列表中选择一个软件包。", InfoBarSeverity.Warning);
                return;
            }

            if (!await EnsureDeviceReadyAsync(device))
                return;
            var result = await action(packageName);
            MarkDeviceOfflineIfUnavailable(device, result);
            Notify(result.Success ? $"{text}已完成" : $"{text}失败", FormatCommandResult(result), result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        };
        return button;
    }

    private static void MarkDeviceOfflineIfUnavailable(DeviceModel device, AdbCommandResult result)
    {
        if (AdbConnectionEvaluator.IsDeviceUnavailable(result))
            device.IsConnected = false;
    }

    private static void MarkDeviceOfflineIfUnavailable(DeviceModel device, Exception exception)
    {
        if (AdbConnectionEvaluator.IsDeviceUnavailable(exception))
            device.IsConnected = false;
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

    private async Task<AdbCommandResult> GetPackageDetailsAsync(DeviceModel device, string packageName)
    {
        var path = await _adb.ShellAsync(device.DeviceId, $"pm path {EscapeShellToken(packageName)}");
        var details = await _adb.ShellAsync(device.DeviceId, $"dumpsys package {EscapeShellToken(packageName)}");
        if (!details.Success || string.IsNullOrWhiteSpace(details.Stdout))
            return details.Success
                ? new AdbCommandResult(1, string.Empty, "设备没有返回该软件包的详细信息。")
                : details;

        var output = new StringBuilder();
        output.AppendLine($"软件包：{packageName}");
        if (path.Success && !string.IsNullOrWhiteSpace(path.Stdout))
        {
            output.AppendLine();
            output.AppendLine("APK 路径：");
            output.AppendLine(path.Stdout.Trim());
        }
        output.AppendLine();
        output.AppendLine(details.Stdout.Trim());
        return new AdbCommandResult(0, output.ToString(), details.Stderr);
    }

    private async Task LoadPackagesAsync(DeviceModel device, StackPanel packageRows, PackageSelection selection, TextBlock status, FrameworkElement loading)
    {
        status.Text = "正在读取软件包...";
        status.Visibility = Visibility.Visible;
        loading.Visibility = Visibility.Visible;
        packageRows.Children.Clear();
        selection.Clear();
        if (!await EnsureDeviceReadyAsync(device))
        {
            status.Text = $"设备离线：{device.DeviceId}";
            loading.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            var catalog = await _packageCatalog.LoadAsync(
                device,
                progress => status.Text = progress.Message);
            if (!catalog.Success)
            {
                var error = catalog.FatalError!;
                if (error.ErrorCode == "PACKAGE_LIST_DEVICE_UNAVAILABLE")
                    device.IsConnected = false;
                status.Text = $"{error.Message} {error.Suggestion}";
                Notify("读取软件包失败", status.Text, InfoBarSeverity.Error);
                return;
            }

            var decodeFailures = 0;
            foreach (var package in catalog.Packages)
            {
                var decodedIcon = await DecodePackageIconAsync(package.IconPng);
                if (decodedIcon.ErrorCode is not null)
                    decodeFailures++;
                var item = new PackageListItem(
                    package.PackageName,
                    package.DisplayName,
                    package.HasResolvedDisplayName);
                packageRows.Children.Add(BuildPackageSurfaceItem(item, selection, decodedIcon.Image));
            }

            if (catalog.Packages.Count == 0)
            {
                status.Text = "未读取到软件包。";
                return;
            }

            var displayedIcons = Math.Max(0, catalog.IconCount - decodeFailures);
            status.Text =
                $"已读取 {catalog.Packages.Count} 个软件包；App 名称 {catalog.ResolvedDisplayNameCount}/{catalog.Packages.Count}，" +
                $"真实图标 {displayedIcons}/{catalog.Packages.Count}。";
            var warnings = catalog.Issues
                .Select(issue => issue.Message)
                .Distinct(StringComparer.Ordinal)
                .Take(2)
                .ToList();
            if (decodeFailures > 0)
                warnings.Add($"有 {decodeFailures} 个图标解码失败，已使用占位图标。");
            if (warnings.Count > 0)
                status.Text += $" {string.Join(' ', warnings)}";
        }
        catch (Exception ex)
        {
            MarkDeviceOfflineIfUnavailable(device, ex);
            Trace.TraceError($"[PACKAGE_CATALOG_UNEXPECTED] {ex}");
            status.Text = "读取软件包失败：桌面端无法处理应用元数据。请刷新重试。";
            Notify("读取软件包失败", status.Text, InfoBarSeverity.Error);
        }
        finally
        {
            loading.Visibility = Visibility.Collapsed;
        }
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
        MarkDeviceOfflineIfUnavailable(device, result);
        Notify(result.Success ? "APK 已安装" : "安装失败", FormatCommandResult(result), result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private async Task<AdbCommandResult> PullPackageApkAsync(DeviceModel device, string packageName)
    {
        if (!await EnsureDeviceReadyAsync(device))
            return new AdbCommandResult(1, string.Empty, $"设备离线：{device.DeviceId}");

        var pathResult = await _adb.ShellAsync(device.DeviceId, $"pm path {packageName}");
        if (!pathResult.Success)
        {
            MarkDeviceOfflineIfUnavailable(device, pathResult);
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
        MarkDeviceOfflineIfUnavailable(device, result);
        Notify(result.Success ? "文件已发送" : "发送失败", FormatCommandResult(result), result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private void StartDevicePreview(DeviceModel device, Image previewImage, TextBlock status, LockedPreviewSurface lockedPreview)
    {
        async void Tick()
        {
            if (_devicePreviewRefreshInProgress)
                return;

            _devicePreviewRefreshInProgress = true;
            var serial = BeginPreviewFrameRequest("detail", device.DeviceId);
            try
            {
                if (!await EnsureDeviceReadyAsync(device, false))
                {
                    if (IsPreviewFrameRequestCurrent("detail", device.DeviceId, serial))
                        ApplyPreviewLockState(DeviceLockState.Unknown, previewImage, status, lockedPreview);
                    return;
                }

                var lockState = _deviceLockMonitor.GetCurrentState(device.DeviceId);
                if (lockState != DeviceLockState.Unlocked)
                {
                    if (IsPreviewFrameRequestCurrent("detail", device.DeviceId, serial))
                        ApplyPreviewLockState(lockState, previewImage, status, lockedPreview);
                    return;
                }

                ApplyPreviewLockState(lockState, previewImage, status, lockedPreview);
                var png = await _adb.ScreencapPngAsync(device.DeviceId);
                await ApplyPreviewFrameAsync(previewImage, device.DeviceId, png, "detail", requestSerial: serial);
                if (IsPreviewFrameRequestCurrent("detail", device.DeviceId, serial))
                    status.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                if (IsPreviewFrameRequestCurrent("detail", device.DeviceId, serial))
                {
                    MarkDeviceOfflineIfUnavailable(device, ex);
                    status.Visibility = Visibility.Visible;
                    status.Text = $"截图失败：{ex.Message}";
                }
            }
            finally
            {
                _devicePreviewRefreshInProgress = false;
            }
        }

        StopDevicePreview();
        _devicePreviewTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_devicePreviewIntervalSeconds) };
        _devicePreviewTimer.Tick += (_, _) => Tick();
        _devicePreviewTimer.Start();
        Tick();
    }

    private void StopDevicePreview()
    {
        _devicePreviewTimer?.Stop();
        _devicePreviewTimer = null;
        InvalidatePreviewFrameRequests("detail");
        _previewFrameHashes.Keys
            .Where(key => key.StartsWith("detail:", StringComparison.Ordinal))
            .ToList()
            .ForEach(key => _previewFrameHashes.Remove(key));
        _previewImageFrameSizes.Keys
            .Where(key => key.StartsWith("detail:", StringComparison.Ordinal))
            .ToList()
            .ForEach(key => _previewImageFrameSizes.Remove(key));
    }

    private void QueueDeviceVideoSettingsUpdate(DeviceModel device, ScrcpyVideoOptions options)
    {
        if (_activeVideoRequestedOptions == options ||
            !string.Equals(_activeVideoDeviceId, device.DeviceId, StringComparison.Ordinal))
            return;

        CancelPendingVideoSettingsUpdate();
        var cancellation = new CancellationTokenSource();
        _videoSettingsUpdateCancellation = cancellation;
        _ = ApplyDeviceVideoSettingsAfterDelayAsync(device, options, cancellation.Token);
    }

    private void CancelPendingVideoSettingsUpdate()
    {
        var cancellation = _videoSettingsUpdateCancellation;
        _videoSettingsUpdateCancellation = null;
        if (cancellation is null)
            return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private async Task ApplyDeviceVideoSettingsAfterDelayAsync(
        DeviceModel device,
        ScrcpyVideoOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(300, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return;
            _videoSettingsUpdateInProgress = true;
            _videoSettingsUpdateDeviceId = device.DeviceId;
            UpdateVideoMirrorControlState(device);
            if (_videoMirrorStatus is not null)
                _videoMirrorStatus.Text = "投屏：正在应用新参数...";
            await StartDeviceVideoMirrorAsync(device, options, isReconfiguration: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Notify("投屏参数更新失败", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _videoSettingsUpdateInProgress = false;
            _videoSettingsUpdateDeviceId = null;
            UpdateVideoMirrorControlState(device);
        }
    }

    private async Task StartDeviceVideoMirrorAsync(
        DeviceModel device,
        ScrcpyVideoOptions options,
        bool isReconfiguration = false)
    {
        CancelPendingVideoSettingsUpdate();
        await StopDeviceVideoMirrorCoreAsync(restartPreview: false, reason: isReconfiguration ? "settings_reconfiguration" : "new_start");

        var startVersion = ++_videoMirrorStartVersion;
        _activeVideoDeviceId = device.DeviceId;
        _activeVideoRequestedOptions = options;
        UpdateVideoMirrorControlState(device);

        var adbAvailable = device.IsConnected && await EnsureDeviceReadyAsync(device);
        var companionAvailable = _companionQuic.IsDeviceConnected(device.DeviceId);
        if (!adbAvailable && !companionAvailable)
        {
            await StopDeviceVideoMirrorCoreAsync(restartPreview: true, reason: "projection_transport_unavailable");
            Notify("无法开启投屏", "设备既没有可用的 ADB 连接，也没有伴侣 App QUIC 连接。", InfoBarSeverity.Error);
            return;
        }

        if (_videoMirrorStatus is not null)
            _videoMirrorStatus.Text = adbAvailable
                ? "投屏：正在从伴侣 APK 启动 scrcpy server..."
                : "投屏：正在请求手机授权...";
        if (_detailPreviewStatus is not null)
        {
            _detailPreviewStatus.Text = adbAvailable
                ? "正在连接 scrcpy 视频与控制通道..."
                : "等待手机确认投屏授权...";
            _detailPreviewStatus.Visibility = Visibility.Visible;
        }

        StopDevicePreview();
        var scrcpy = new ProjectionSession(_adb, _companionQuic);
        _scrcpySession = scrcpy;
        scrcpy.BackendChanged += backend => DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(_scrcpySession, scrcpy) || backend == ProjectionBackend.None)
                return;
            if (_videoMirrorStatus is not null)
                _videoMirrorStatus.Text = backend == ProjectionBackend.AdbScrcpy
                    ? "投屏：正在从伴侣 APK 启动 scrcpy server..."
                    : "投屏：正在请求手机授权...";
            if (_detailPreviewStatus is not null)
            {
                _detailPreviewStatus.Text = backend == ProjectionBackend.AdbScrcpy
                    ? "正在连接 scrcpy 视频与控制通道..."
                    : "等待手机确认投屏授权...";
                _detailPreviewStatus.Visibility = Visibility.Visible;
            }
        });
        scrcpy.FrameSizeChanged += size => DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(_scrcpySession, scrcpy))
                return;
            _detailVideoFrameSize = (size.Width, size.Height);
            UpdateDetailVideoSurfaceBounds();
            if (_activeVideoStreamOptions is { } active && _videoMirrorStatus is not null)
                _videoMirrorStatus.Text = $"{scrcpy.BackendLabel}：{size.Width}×{size.Height} · {active.FrameRate} FPS · {active.BitRate / 1_000_000d:0.#} Mbps";
        });
        scrcpy.Faulted += message => DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(_scrcpySession, scrcpy))
                return;
            StopDeviceVideoMirror(restartPreview: true, reason: "scrcpy_stream_fault");
            Notify("投屏已停止", message, InfoBarSeverity.Error);
        });

        var result = await scrcpy.StartAsync(device.DeviceId, options, adbAvailable);
        if (startVersion != _videoMirrorStartVersion || !ReferenceEquals(_scrcpySession, scrcpy))
        {
            await scrcpy.DisposeAsync();
            return;
        }
        if (!result.Success || _detailVideoSurface is null)
        {
            await StopDeviceVideoMirrorCoreAsync(restartPreview: true, reason: "scrcpy_start_failed");
            Notify("开启投屏失败", FormatCommandResult(result), InfoBarSeverity.Error);
            return;
        }

        _detailVideoFrameSize = (scrcpy.FrameSize.Width, scrcpy.FrameSize.Height);
        _activeVideoStreamOptions = options;
        UpdateDetailVideoSurfaceBounds();

        if (_videoMirrorStatus is not null)
            _videoMirrorStatus.Text = $"{scrcpy.BackendLabel}：正在初始化 FFmpeg 低延迟解码...";
        _detailVideoSurface.Visibility = Visibility.Visible;
        _detailVideoSurface.Opacity = 0.01;

        NativeVideoSwapChainRenderer renderer;
        try
        {
            renderer = new NativeVideoSwapChainRenderer(_detailVideoSurface);
            scrcpy.WriteDiagnostic(
                "renderer.ready",
                $"adapter={renderer.AdapterName}; fallback_failures={renderer.InitializationFailures.Count}");
        }
        catch (Exception ex)
        {
            scrcpy.WriteDiagnostic("renderer.initialization_failed", ex.ToString());
            await StopDeviceVideoMirrorCoreAsync(restartPreview: true, reason: "renderer_initialization_failed");
            Notify("视频渲染器初始化失败", ex.Message, InfoBarSeverity.Error);
            return;
        }

        var nativeRenderer = renderer;
        renderer.Faulted += message => DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(_detailVideoRenderer, nativeRenderer))
                return;
            scrcpy.WriteDiagnostic("renderer.fault", message);
            StopDeviceVideoMirror(restartPreview: true, reason: "native_renderer_fault");
            Notify("视频渲染失败", message, InfoBarSeverity.Error);
        });
        _detailVideoRenderer = renderer;
        try
        {
            scrcpy.StartDecoding(
                () => nativeRenderer.TargetPixelSize,
                frame =>
                {
                    if (ReferenceEquals(_detailVideoRenderer, nativeRenderer))
                        nativeRenderer.RenderBgraFrame(frame);
                });
        }
        catch (Exception ex)
        {
            scrcpy.WriteDiagnostic("decoder.start_failed", ex.ToString());
            await StopDeviceVideoMirrorCoreAsync(restartPreview: true, reason: "decoder_initialization_failed");
            Notify("投屏解码器初始化失败", ex.Message, InfoBarSeverity.Error);
            return;
        }

        var firstFrameReady = await renderer.WaitForFirstPresentedFrameAsync(TimeSpan.FromSeconds(8));
        if (!firstFrameReady || startVersion != _videoMirrorStartVersion)
        {
            if (!ReferenceEquals(_scrcpySession, scrcpy))
                return;
            await StopDeviceVideoMirrorCoreAsync(restartPreview: true, reason: "first_frame_timeout");
            Notify("投屏画面未就绪", "8 秒内未完成视频首帧解码。", InfoBarSeverity.Error);
            return;
        }

        _detailVideoSurface.Opacity = 1;
        if (_detailPreviewImage is not null && _detailPreviewStatus is not null && _detailLockedPreview is not null)
        {
            var lockState = _deviceLockMonitor.GetCurrentState(device.DeviceId);
            ApplyPreviewLockState(
                lockState,
                _detailPreviewImage,
                _detailPreviewStatus,
                _detailLockedPreview,
                projectionActive: true);
        }

        var actualSize = scrcpy.FrameSize;
        if (_videoMirrorStatus is not null)
            _videoMirrorStatus.Text = $"{scrcpy.BackendLabel}：{actualSize.Width}×{actualSize.Height} · {options.FrameRate} FPS · {options.BitRate / 1_000_000d:0.#} Mbps";

        Notify(
            isReconfiguration ? "投屏参数已更新" : "投屏已开启",
            $"{scrcpy.BackendLabel} · {actualSize.Width}×{actualSize.Height} · {options.BitRate / 1_000_000d:0.#} Mbps",
            InfoBarSeverity.Success);
        UpdateVideoMirrorControlState(device);
    }

    private void StopDeviceVideoMirror(bool restartPreview, string reason = "navigation_or_ui_reset")
        => _ = StopDeviceVideoMirrorCoreAsync(restartPreview, reason);

    private async Task StopDeviceVideoMirrorCoreAsync(bool restartPreview, string reason)
    {
        CancelPendingVideoSettingsUpdate();
        if (!string.Equals(reason, "settings_reconfiguration", StringComparison.Ordinal))
            _videoSettingsUpdateDeviceId = null;
        _videoMirrorStartVersion++;
        var scrcpy = _scrcpySession;
        _scrcpySession = null;
        var renderer = _detailVideoRenderer;
        _detailVideoRenderer = null;
        if (scrcpy is not null)
        {
            scrcpy.WriteDiagnostic(
                "renderer.stopping",
                renderer is null
                    ? $"reason={reason}; renderer=not_initialized"
                    : $"reason={reason}; presented={renderer.PresentedFrameCount}; dropped={renderer.DroppedFrameCount}");
        }
        if (scrcpy is not null)
            await scrcpy.DisposeAsync();

        renderer?.Dispose();
        if (_detailVideoSurface is not null)
        {
            _detailVideoSurface.Opacity = 1;
            _detailVideoSurface.Visibility = Visibility.Collapsed;
            _detailVideoSurface.Width = double.NaN;
            _detailVideoSurface.Height = double.NaN;
        }

        _detailVideoFrameSize = null;
        _activeVideoDeviceId = null;
        _activeVideoRequestedOptions = null;
        _activeVideoStreamOptions = null;
        if (_detailPreviewImage is not null)
            _detailPreviewImage.Visibility = Visibility.Visible;
        if (_videoMirrorStatus is not null)
            _videoMirrorStatus.Text = "投屏：已停止";
        UpdateVideoMirrorControlState(_currentDetailDevice);

        if (restartPreview && _currentDetailDevice is not null && _detailPreviewImage is not null && _detailPreviewStatus is not null && _detailLockedPreview is not null)
            StartDevicePreview(_currentDetailDevice, _detailPreviewImage, _detailPreviewStatus, _detailLockedPreview);
    }

    private void UpdateVideoMirrorControlState(DeviceModel? device)
    {
        if (_videoMirrorStartButton is null || _videoMirrorStopButton is null || device is null)
            return;

        var available = device.IsConnected || _companionQuic.IsDeviceConnected(device.DeviceId);
        var ownsActiveSession =
            string.Equals(_activeVideoDeviceId, device.DeviceId, StringComparison.Ordinal) &&
            (_scrcpySession is not null || _activeVideoRequestedOptions is not null);
        var isReconfiguring =
            _videoSettingsUpdateInProgress &&
            string.Equals(_videoSettingsUpdateDeviceId, device.DeviceId, StringComparison.Ordinal);
        var state = ProjectionControlStateEvaluator.Resolve(available, ownsActiveSession, isReconfiguring);
        _videoMirrorStartButton.IsEnabled = state.StartEnabled;
        _videoMirrorStopButton.IsEnabled = state.StopEnabled;
    }

    private void UpdateDetailVideoSurfaceBounds()
    {
        if (_detailPreviewLayer is null || _detailVideoSurface is null || _detailVideoFrameSize is not { } frameSize)
            return;
        if (_detailPreviewLayer.ActualWidth <= 0 || _detailPreviewLayer.ActualHeight <= 0)
            return;

        var bounds = PreviewContentLayout.FitUniform(
            _detailPreviewLayer.ActualWidth,
            _detailPreviewLayer.ActualHeight,
            frameSize.Width,
            frameSize.Height);
        _detailVideoSurface.Width = bounds.Width;
        _detailVideoSurface.Height = bounds.Height;
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
        InvalidatePreviewFrameRequests("card");
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

        var serial = BeginPreviewFrameRequest("card", device.DeviceId);
        try
        {
            target.Status.Text = "刷新中...";
            target.Status.Visibility = Visibility.Visible;
            if (!await EnsureDeviceReadyAsync(device, false))
            {
                if (IsPreviewFrameRequestCurrent("card", device.DeviceId, serial))
                    target.Status.Text = $"设备离线：{device.DeviceId}";
                return;
            }
            var png = await _adb.ScreencapPngAsync(device.DeviceId);
            await ApplyPreviewFrameAsync(target.Image, device.DeviceId, png, "card", requestSerial: serial);
            if (IsPreviewFrameRequestCurrent("card", device.DeviceId, serial))
                target.Status.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            if (IsPreviewFrameRequestCurrent("card", device.DeviceId, serial))
            {
                MarkDeviceOfflineIfUnavailable(device, ex);
                target.Status.Visibility = Visibility.Visible;
                target.Status.Text = $"截图失败：{ex.Message}";
            }
        }
    }

    private long BeginPreviewFrameRequest(string scope, string deviceId)
    {
        var key = $"{scope}:{deviceId}";
        var serial = _previewFrameSerials.TryGetValue(key, out var current) ? current + 1 : 1;
        _previewFrameSerials[key] = serial;
        return serial;
    }

    private void InvalidatePreviewFrameRequests(string scope)
    {
        foreach (var key in _previewFrameSerials.Keys.Where(key => key.StartsWith($"{scope}:", StringComparison.Ordinal)).ToList())
            _previewFrameSerials[key]++;
    }

    private bool IsPreviewFrameRequestCurrent(string scope, string deviceId, long serial)
    {
        return _previewFrameSerials.TryGetValue($"{scope}:{deviceId}", out var current) && current == serial;
    }

    private async Task<bool> ApplyPreviewFrameAsync(Image image, string deviceId, byte[] png, string scope, bool force = false, long? requestSerial = null)
    {
        var key = $"{scope}:{deviceId}";
        if (requestSerial is long serial && !IsPreviewFrameRequestCurrent(scope, deviceId, serial))
            return false;

        var hasFrameSize = TryReadPngSize(png, out var width, out var height);
        var hash = Convert.ToHexString(SHA256.HashData(png));
        if (!force && _previewFrameHashes.TryGetValue(key, out var previousHash) && previousHash == hash)
            return false;

        _previewFrameHashes[key] = hash;
        var bitmap = new BitmapImage();
        using var stream = new MemoryStream(png);
        await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
        if (requestSerial is long serialAfterDecode && !IsPreviewFrameRequestCurrent(scope, deviceId, serialAfterDecode))
            return false;

        if (hasFrameSize)
            _previewImageFrameSizes[key] = (width, height);
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
        var textContent = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            Foreground = PrimaryTextBrush(),
            Padding = new Thickness(12),
        };
        var textSurface = new Border
        {
            UseLayoutRounding = true,
            Height = 330,
            MaxWidth = 560,
            Background = SurfaceAltBrush(),
            BorderBrush = BorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Child = new ScrollViewer
            {
                Content = textContent,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(0, 0, 10, 0),
            },
        };
        StyleScrollViewer((ScrollViewer)textSurface.Child);
        FrostedDialog? dialog = null;
        var close = SecondaryButton("关闭");
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Click += (_, _) => dialog?.Hide();
        var content = new StackPanel
        {
            Width = 560,
            MaxWidth = 560,
            Spacing = 12,
            Children = { textSurface, close },
        };
        dialog = DialogChrome(title, content);
        await dialog.ShowAsync();
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

    private static string EscapeShellToken(string value)
    {
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
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
        _contentHost.Children.Add(new AutomationTaskPage(
            _automation,
            () => _devices.Devices.ToList(),
            OpenAiForTaskCreation,
            (title, message, isError) => Notify(
                title,
                string.IsNullOrWhiteSpace(message) ? title : message,
                isError ? InfoBarSeverity.Error : InfoBarSeverity.Success)));
    }

    private void OpenAiForTaskCreation()
    {
        _aiInput.Text = "请创建一个自动化任务：";
        if (_aiPanel.Visibility != Visibility.Visible)
            ToggleAiPanel();
        _ = DispatcherQueue.TryEnqueue(() => _aiInput.Focus(FocusState.Programmatic));
    }

    private async Task<string> ExecuteAutomationAiAsync(AutomationAiRequest request, CancellationToken cancellationToken)
    {
        var model = !string.IsNullOrWhiteSpace(request.ModelId)
            ? _settings.Current.AiModels.FirstOrDefault(item => string.Equals(item.ModelId, request.ModelId, StringComparison.Ordinal))
            : _selectedAiModel ?? _settings.Current.AiModels.FirstOrDefault();
        if (model is null)
        {
            throw new AutomationExecutionException(
                "ACTION_AI_MODEL_MISSING",
                "没有配置可供任务调用的 AI 模型。",
                "automation.ai",
                recoverable: true,
                suggestion: "在设置中添加 AI 模型后重新运行任务。");
        }

        var device = string.IsNullOrWhiteSpace(request.DeviceId)
            ? null
            : _devices.Devices.FirstOrDefault(item => string.Equals(item.DeviceId, request.DeviceId, StringComparison.Ordinal))
                ?? new DeviceModel { DeviceId = request.DeviceId, DisplayName = request.DeviceId, IsConnected = true };
        var response = await _ai.SendAsync(
            new AiAgentRequest
            {
                Model = model,
                Messages =
                [
                    new AiConversationMessage
                    {
                        Role = "user",
                        Text = $"[自动任务：{request.TaskName}]\n{request.Prompt}",
                    },
                ],
                PermissionMode = request.AllowDeviceTools || request.AllowTaskMutation ? "完全访问" : "替我审批",
                CurrentDeviceId = device?.DeviceId,
                CurrentDeviceName = device?.DisplayName,
                KnownDevices = _devices.GetDevices(),
                AllowInteractiveChoices = false,
            },
            async toolCall =>
            {
                var taskTool = toolCall.Name.StartsWith("task_", StringComparison.Ordinal);
                var readOnlyTaskTool = toolCall.Name is "task_list" or "task_get";
                if (taskTool && !readOnlyTaskTool && !request.AllowTaskMutation)
                {
                    return new AiAgentToolResult
                    {
                        ToolCallId = toolCall.Id,
                        Name = toolCall.Name,
                        Success = false,
                        Content = "当前任务没有 allowTaskMutation 权限。",
                    };
                }
                if (!taskTool && !request.AllowDeviceTools)
                {
                    return new AiAgentToolResult
                    {
                        ToolCallId = toolCall.Id,
                        Name = toolCall.Name,
                        Success = false,
                        Content = "当前任务没有 allowAiDeviceTools 权限。",
                    };
                }
                return await _aiTools.ExecuteAsync(
                    toolCall,
                    "完全访问",
                    device,
                    (_, _) => Task.FromResult(false),
                    cancellationToken);
            },
            cancellationToken);
        return response.Text.Trim();
    }

    private void OnAutomationAiOutputProduced(object? sender, AutomationAiOutput output)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            var prompt = new AiChatMessage
            {
                Role = "user",
                Text = $"[自动任务：{output.TaskName}]\n{output.Prompt}",
                IsUser = true,
            };
            var answer = new AiChatMessage
            {
                Role = "assistant",
                Text = output.Response,
                IsUser = false,
            };
            _aiConversation.Add(new AiConversationMessage { Role = "user", Text = prompt.Text });
            _aiConversation.Add(new AiConversationMessage { Role = "assistant", Text = answer.Text });
            AddAiVisibleMessage(prompt);
            AddAiVisibleMessage(answer);
            Notify("自动任务已调用 AI", output.TaskName, InfoBarSeverity.Success);
        });
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
        => ApplyWindowTitleBarTheme(this);

    private static void ApplyWindowTitleBarTheme(Window window)
    {
        var titleBar = window.AppWindow.TitleBar;
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
        _aiPanel.Width = _aiPanelWidth;
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
            var migratedLegacyErrors = false;
            foreach (var message in _settings.Current.AiMessages)
            {
                migratedLegacyErrors |= NormalizeLegacyAiError(message);
                _messageList.Children.Add(MessageBubble(message));
            }
            if (migratedLegacyErrors)
                SaveAiConversation();
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

        var resizableRoot = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition(),
            },
        };
        var resizeHandle = BuildAiPanelResizeHandle();
        resizableRoot.Children.Add(resizeHandle);
        Grid.SetColumn(root, 1);
        resizableRoot.Children.Add(root);
        _aiPanel.Child = resizableRoot;
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
        if (_isAiPanelResizing)
            return;

        UpdateAiInputHeight();
    }

    private void UpdateAiInputHeight()
    {
        var lineCount = EstimateAiInputVisualLineCount();
        var targetHeight = Math.Clamp(24 + lineCount * 22, 44, 180);
        if (Math.Abs(_aiInput.Height - targetHeight) < 0.5)
            return;

        _aiInput.Height = targetHeight;
    }

    private int EstimateAiInputVisualLineCount()
    {
        var text = string.IsNullOrEmpty(_aiInput.Text) ? string.Empty : _aiInput.Text.Replace("\r", string.Empty, StringComparison.Ordinal);
        var availableWidth = _aiInput.ActualWidth > 0 ? _aiInput.ActualWidth : AiBubbleMaxWidth();
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
        var requestMessages = _aiConversation.ToList();
        requestMessages.AddRange(await BuildAiDeviceObservationMessagesAsync("发送本轮请求前的当前设备截图。", _aiCancellation.Token));
        var stopwatch = Stopwatch.StartNew();
        var streamingMessage = AddAiStreamingMessage();
        try
        {
            var response = await _ai.SendStreamingAsync(
                new AiAgentRequest
                {
                    Model = _selectedAiModel,
                    Messages = requestMessages,
                    PermissionMode = permission,
                    CurrentDeviceId = _currentDetailDevice?.DeviceId,
                    CurrentDeviceName = _currentDetailDevice?.DisplayName,
                    KnownDevices = _devices.GetDevices(),
                    AllowInteractiveChoices = true,
                },
                delta =>
                {
                    QueueAiStreamDelta(streamingMessage, delta);
                    return Task.CompletedTask;
                },
                async toolCall =>
                {
                    var toolResult = await ExecuteAiToolAsync(toolCall, permission, _aiCancellation.Token);
                    if (toolResult.Success && string.Equals(toolCall.Name, "ask_user_choice", StringComparison.Ordinal))
                    {
                        FlushAiStreamDeltas(streamingMessage);
                        MaterializeAiStreamMessage(streamingMessage);
                        if (string.IsNullOrWhiteSpace(streamingMessage.Message.Text) &&
                            string.IsNullOrWhiteSpace(streamingMessage.Message.ThinkingText))
                        {
                            _messageList.Children.Remove(streamingMessage.Root);
                        }
                        else
                        {
                            streamingMessage.Message.ProcessingSeconds = stopwatch.Elapsed.TotalSeconds;
                            streamingMessage.DurationText.Text = "已完成该阶段，正在根据选择继续...";
                            CompleteStreamingThinking(streamingMessage);
                            _settings.Current.AiMessages.Add(CloneVisibleAiMessage(streamingMessage.Message));
                            SaveAiConversation();
                        }
                        streamingMessage = AddAiStreamingMessage();
                    }
                    return toolResult;
                },
                _aiCancellation.Token,
                afterToolContextProvider: async (toolCalls, token) =>
                    AiToolCallsMayChangeScreen(toolCalls)
                        ? await BuildAiDeviceObservationMessagesAsync("工具执行后的当前设备截图。请使用这张最新截图判断页面变化后的可见控件。", token)
                        : Array.Empty<AiConversationMessage>());

            FlushAiStreamDeltas(streamingMessage);
            MaterializeAiStreamMessage(streamingMessage);
            stopwatch.Stop();
            _aiConversation.AddRange(response.NewMessages);
            var text = string.IsNullOrWhiteSpace(streamingMessage.Message.Text)
                ? response.Text.Trim()
                : streamingMessage.Message.Text;
            if (string.IsNullOrWhiteSpace(text))
                text = "操作已完成。";
            var thinking = string.IsNullOrWhiteSpace(streamingMessage.Message.ThinkingText)
                ? response.ThinkingText.Trim()
                : streamingMessage.Message.ThinkingText;
            streamingMessage.Message.Text = text;
            streamingMessage.Message.ThinkingText = thinking;
            streamingMessage.Message.ProcessingSeconds = stopwatch.Elapsed.TotalSeconds;
            streamingMessage.AnswerText.Text = text;
            streamingMessage.DurationText.Text = $"处理用时 {FormatDuration(stopwatch.Elapsed.TotalSeconds)}";
            CompleteStreamingThinking(streamingMessage);
            _settings.Current.AiMessages.Add(CloneVisibleAiMessage(streamingMessage.Message));
            SaveAiConversation();
            ScrollAiMessagesToBottom();
            foreach (var warning in response.Warnings)
            {
                AddAiVisibleMessage(new AiChatMessage
                {
                    Kind = AiChatMessageKinds.Warning,
                    Role = "System",
                    Text = warning.Message,
                    IsUser = false,
                });
            }
            if (response.Warnings.Count > 0)
                Notify("AI 已完成（有兼容提示）", response.Warnings[0].Message, InfoBarSeverity.Warning);
            else
                Notify("AI 思考完成", $"处理用时 {FormatDuration(stopwatch.Elapsed.TotalSeconds)}", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            FlushAiStreamDeltas(streamingMessage);
            MaterializeAiStreamMessage(streamingMessage);
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
            var failure = AiRequestFailureClassifier.FromException(ex, Guid.NewGuid().ToString("N"));
            _messageList.Children.Remove(streamingMessage.Root);
            AddAiVisibleMessage(new AiChatMessage
            {
                Kind = AiChatMessageKinds.Error,
                Role = "AI",
                Text = failure.Summary,
                IsUser = false,
                ProcessingSeconds = stopwatch.Elapsed.TotalSeconds,
                Error = failure.ToChatError(),
            });
            Notify(failure.Title, failure.Summary, InfoBarSeverity.Error);
            _systemNotifications.Show(failure.Title, $"{failure.Summary} 追踪号：{failure.TraceId}");
        }
        finally
        {
            SetAiSending(false);
            _aiCancellation?.Dispose();
            _aiCancellation = null;
        }
    }

    private async Task<AiAgentToolResult> ExecuteAiToolAsync(
        AiAgentToolCall toolCall,
        string permission,
        CancellationToken cancellationToken)
    {
        if (string.Equals(toolCall.Name, "ask_user_choice", StringComparison.Ordinal))
            return await RequestAiChoiceAsync(toolCall, cancellationToken);
        return await _aiTools.ExecuteAsync(
            toolCall,
            permission,
            _currentDetailDevice,
            RequestAiToolApprovalAsync,
            cancellationToken);
    }

    private async Task<AiAgentToolResult> RequestAiChoiceAsync(
        AiAgentToolCall toolCall,
        CancellationToken cancellationToken)
    {
        if (!AiChoiceRequestParser.TryParse(toolCall, out var request, out var errorCode, out var errorMessage) || request is null)
        {
            return new AiAgentToolResult
            {
                ToolCallId = toolCall.Id,
                Name = toolCall.Name,
                Success = false,
                Content = $"{errorCode}: {errorMessage}",
            };
        }

        var completion = new TaskCompletionSource<AiAgentToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var message = new AiChatMessage
        {
            Kind = AiChatMessageKinds.Choice,
            Role = "AI",
            IsUser = false,
            Choice = request,
            CreatedAt = DateTimeOffset.Now,
        };
        await RunOnUiThreadAsync(() =>
        {
            var bubble = BuildAiChoiceBubble(message, _ =>
            {
                var result = new AiAgentToolResult
                {
                    ToolCallId = toolCall.Id,
                    Name = toolCall.Name,
                    Success = true,
                    Content = AiChoiceRequestParser.BuildResultJson(request),
                };
                if (!completion.TrySetResult(result))
                    return;
                _settings.Current.AiMessages.Add(CloneVisibleAiMessage(message));
                SaveAiConversation();
            });
            _messageList.Children.Add(bubble);
            ScrollAiMessagesToBottom();
        });

        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        return await completion.Task;
    }
    private async Task<IReadOnlyList<AiConversationMessage>> BuildAiDeviceObservationMessagesAsync(string reason, CancellationToken cancellationToken)
    {
        var device = _currentDetailDevice;
        if (device is null || string.IsNullOrWhiteSpace(device.DeviceId))
            return Array.Empty<AiConversationMessage>();

        try
        {
            if (!await EnsureDeviceReadyAsync(device, false))
            {
                return
                [
                    new AiConversationMessage
                    {
                        Role = "user",
                        Text = $"[系统设备观察]{Environment.NewLine}{reason}{Environment.NewLine}当前设备不在线，无法附加屏幕截图。",
                    },
                ];
            }

            var png = await _adb.ScreencapPngAsync(device.DeviceId);
            var directory = Path.Combine(Path.GetTempPath(), "ADBControl", "ai-screen-observations");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.png");
            await File.WriteAllBytesAsync(path, png, cancellationToken);
            var sizeText = TryReadPngSize(png, out var width, out var height)
                ? $"截图原始尺寸：{width}x{height} 像素。坐标原点在左上角，x 向右、y 向下；所有 adb_tap/adb_swipe 坐标必须使用这个原始像素坐标系。"
                : "未能读取截图尺寸；执行触控前必须先通过工具重新校验当前屏幕尺寸。";

            return
            [
                new AiConversationMessage
                {
                    Role = "user",
                    Text = $"[系统设备观察]{Environment.NewLine}{reason}{Environment.NewLine}已附加当前设备截图。{sizeText}{Environment.NewLine}若 adb_ui_dump 节点为空，请以这张最新截图为当前屏幕事实，基于可见控件和设备坐标继续判断。",
                    Attachments =
                    [
                        new AiAttachment
                        {
                            Name = "当前设备截图.png",
                            Path = path,
                            IsImage = true,
                        },
                    ],
                },
            ];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return
            [
                new AiConversationMessage
                {
                    Role = "user",
                    Text = $"[系统设备观察]{Environment.NewLine}{reason}{Environment.NewLine}当前设备截图采集失败：{ex.Message}",
                },
            ];
        }
    }

    private static bool AiToolCallsMayChangeScreen(IReadOnlyList<AiAgentToolCall> toolCalls)
    {
        return toolCalls.Any(toolCall =>
            string.Equals(toolCall.Name, "adb_tap", StringComparison.Ordinal) ||
            string.Equals(toolCall.Name, "adb_swipe", StringComparison.Ordinal) ||
            string.Equals(toolCall.Name, "adb_shell", StringComparison.Ordinal) &&
                IsScreenChangingAdbCommand(ReadAiToolArgument(toolCall.ArgumentsJson, "command")) ||
            string.Equals(toolCall.Name, "companion_call", StringComparison.Ordinal) &&
                IsScreenChangingCompanionOperation(ReadAiToolArgument(toolCall.ArgumentsJson, "operation")));
    }

    private static bool IsScreenChangingAdbCommand(string command)
    {
        var normalized = command.Trim().ToLowerInvariant();
        if (normalized.Length == 0)
            return false;

        var changingPrefixes = new[]
        {
            "input ", "am start", "monkey ", "cmd statusbar", "wm dismiss-keyguard",
            "settings put", "svc power", "reboot", "am force-stop", "pm clear",
        };
        return changingPrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static bool IsScreenChangingCompanionOperation(string operation)
    {
        return string.Equals(operation, "input.text", StringComparison.Ordinal) ||
            string.Equals(operation, "input.key", StringComparison.Ordinal) ||
            operation.StartsWith("accessibility.global.", StringComparison.Ordinal) ||
            operation.StartsWith("accessibility.touch.", StringComparison.Ordinal);
    }

    private static string ReadAiToolArgument(string argumentsJson, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            return document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
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
            IsTextSelectionEnabled = true,
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
            IsTextSelectionEnabled = true,
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
            MaxWidth = AiBubbleMaxWidth(),
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
        return new AiStreamingMessageUi(
            message,
            panel,
            textBlock,
            thinkingShell,
            thinkingText,
            thinkingSummary,
            thinkingBody,
            thinkingArrow,
            durationText,
            new StringBuilder(),
            new StringBuilder());
    }

    private void ResetAiStreamBuffers()
    {
        lock (_aiStreamLock)
        {
            _pendingAiAnswerDelta.Clear();
            _pendingAiThinkingDelta.Clear();
            _aiStreamFlushQueued = false;
        }
        _aiStreamScrollQueued = false;
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

        _ = Task.Delay(AiStreamRenderPolicy.FlushDelay, _aiCancellation?.Token ?? CancellationToken.None)
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
            streamingMessage.ThinkingBuffer.Append(thinkingDelta);
            var preview = AiStreamRenderPolicy.BuildThinkingPreview(streamingMessage.ThinkingBuffer);
            streamingMessage.ThinkingText.Text = preview;
            streamingMessage.ThinkingSummary.Text = $"思考中 · {BuildThinkingSummary(preview)}";
            streamingMessage.ThinkingShell.Visibility = Visibility.Visible;
            streamingMessage.ThinkingBody.Visibility = Visibility.Visible;
            streamingMessage.ThinkingArrow.Text = "⌃";
        }

        if (answerDelta.Length > 0)
        {
            streamingMessage.AnswerBuffer.Append(answerDelta);
            streamingMessage.AnswerText.Text = streamingMessage.AnswerBuffer.ToString();
            if (!streamingMessage.HasAnswer && streamingMessage.ThinkingBuffer.Length > 0)
            {
                streamingMessage.HasAnswer = true;
                CollapseStreamingThinking(streamingMessage);
            }
        }

        if (thinkingDelta.Length > 0 || answerDelta.Length > 0)
            QueueAiStreamingScrollToBottom();
    }

    private static void MaterializeAiStreamMessage(AiStreamingMessageUi streamingMessage)
    {
        streamingMessage.Message.Text = streamingMessage.AnswerBuffer.ToString();
        streamingMessage.Message.ThinkingText = streamingMessage.ThinkingBuffer.ToString();
    }

    private void QueueAiStreamingScrollToBottom()
    {
        if (_aiStreamScrollQueued)
            return;
        _aiStreamScrollQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _aiStreamScrollQueued = false;
            _aiMessageScroller?.ChangeView(null, _aiMessageScroller.ScrollableHeight, null, true);
        }))
        {
            _aiStreamScrollQueued = false;
        }
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
        streamingMessage.ThinkingText.Text = streamingMessage.Message.ThinkingText;
        streamingMessage.ThinkingSummary.Text = $"思考完成 · {BuildThinkingSummary(streamingMessage.Message.ThinkingText)}";
        CollapseStreamingThinking(streamingMessage);
    }

    private static UIElement BuildCollapsedThinkingBlock(string thinkingText)
    {
        var content = new TextBlock
        {
            Text = thinkingText,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
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
            if (!string.Equals(message.Kind, AiChatMessageKinds.Message, StringComparison.Ordinal))
                continue;
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
            Kind = message.Kind,
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
            Error = message.Error is null ? null : new AiChatErrorDetails
            {
                ErrorCode = message.Error.ErrorCode,
                Category = message.Error.Category,
                Title = message.Error.Title,
                Summary = message.Error.Summary,
                Detail = message.Error.Detail,
                Suggestion = message.Error.Suggestion,
                TraceId = message.Error.TraceId,
                HttpStatusCode = message.Error.HttpStatusCode,
                Recoverable = message.Error.Recoverable,
            },
            Choice = message.Choice is null ? null : new AiChoiceRequest
            {
                ToolCallId = message.Choice.ToolCallId,
                Question = message.Choice.Question,
                SelectionMode = message.Choice.SelectionMode,
                Options = message.Choice.Options.ToList(),
                SelectedOptions = message.Choice.SelectedOptions.ToList(),
                IsSubmitted = message.Choice.IsSubmitted,
            },
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

            _aiMessageScroller.ChangeView(null, _aiMessageScroller.ScrollableHeight, null, true);
            if (pass < 1)
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
        FrostedDialog? dialog = null;
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
        if (string.Equals(message.Kind, AiChatMessageKinds.Error, StringComparison.Ordinal))
            return BuildAiErrorBubble(message);
        if (string.Equals(message.Kind, AiChatMessageKinds.Choice, StringComparison.Ordinal))
            return BuildAiChoiceBubble(message, onSubmit: null);
        if (string.Equals(message.Kind, AiChatMessageKinds.Warning, StringComparison.Ordinal))
            return BuildAiWarningBubble(message);

        var panel = new StackPanel
        {
            HorizontalAlignment = message.IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = AiBubbleMaxWidth(),
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
                IsTextSelectionEnabled = true,
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

    private static bool NormalizeLegacyAiError(AiChatMessage message)
    {
        if (message.IsUser ||
            !string.Equals(message.Kind, AiChatMessageKinds.Message, StringComparison.Ordinal) ||
            !message.Text.StartsWith("AI 请求失败", StringComparison.Ordinal))
        {
            return false;
        }

        var traceId = Guid.NewGuid().ToString("N");
        var httpIndex = message.Text.IndexOf("HTTP ", StringComparison.OrdinalIgnoreCase);
        AiChatErrorDetails details;
        if (httpIndex >= 0)
        {
            var statusText = new string(message.Text
                .Skip(httpIndex + 5)
                .TakeWhile(char.IsDigit)
                .ToArray());
            var bodyIndex = message.Text.IndexOf('{', httpIndex);
            var body = bodyIndex >= 0 ? message.Text[bodyIndex..] : string.Empty;
            details = int.TryParse(statusText, out var statusCode)
                ? AiRequestFailureClassifier.FromHttp(statusCode, "历史请求", body, traceId).ToChatError()
                : LegacyErrorDetails(message.Text, traceId);
        }
        else
        {
            details = LegacyErrorDetails(message.Text, traceId);
        }

        message.Kind = AiChatMessageKinds.Error;
        message.Error = details;
        message.Text = details.Summary;
        return true;
    }

    private static AiChatErrorDetails LegacyErrorDetails(string detail, string traceId) => new()
    {
        ErrorCode = "AI_LEGACY_REQUEST_FAILED",
        Category = AiRequestFailureCategory.Unknown.ToString(),
        Title = "历史 AI 请求失败",
        Summary = "此前的 AI 请求未能完成。",
        Detail = detail,
        Suggestion = "请使用当前版本重新发送；展开详情可查看旧版错误信息。",
        TraceId = traceId,
        Recoverable = true,
    };
    private UIElement BuildAiErrorBubble(AiChatMessage message)
    {
        var error = message.Error ?? new AiChatErrorDetails
        {
            ErrorCode = "AI_REQUEST_FAILED",
            Title = "AI 请求失败",
            Summary = message.Text,
            Detail = message.Text,
            Suggestion = "请稍后重试。",
        };
        var foreground = s_darkTheme
            ? new SolidColorBrush(ColorHelper.FromArgb(255, 252, 165, 165))
            : new SolidColorBrush(ColorHelper.FromArgb(255, 185, 28, 28));
        var background = s_darkTheme
            ? new SolidColorBrush(ColorHelper.FromArgb(42, 248, 113, 113))
            : new SolidColorBrush(ColorHelper.FromArgb(30, 220, 38, 38));
        var border = s_darkTheme
            ? new SolidColorBrush(ColorHelper.FromArgb(120, 248, 113, 113))
            : new SolidColorBrush(ColorHelper.FromArgb(100, 220, 38, 38));

        var fullDetail = $"错误码：{error.ErrorCode}\n追踪号：{error.TraceId}\n{error.Detail}";
        var detailText = new TextBlock
        {
            Text = fullDetail,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 11,
            Foreground = SecondaryTextBrush(),
        };
        var detailScroller = new ScrollViewer
        {
            Content = detailText,
            MaxHeight = 220,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        AttachWheelScrolling(detailScroller);
        var detailShell = new Border
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 6, 0, 0),
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(10),
            Background = SurfaceBrush(),
            Child = detailScroller,
        };
        var detailsButton = SecondaryButton("详情");
        detailsButton.Padding = new Thickness(10, 5, 10, 5);
        ToolTipService.SetToolTip(detailsButton, fullDetail);
        var expanded = false;
        detailsButton.Click += (_, _) =>
        {
            expanded = !expanded;
            detailShell.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            detailsButton.Content = expanded ? "收起" : "详情";
        };

        var title = new TextBlock
        {
            Text = error.Title,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = foreground,
        };
        var header = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto },
            },
            Children = { title, detailsButton },
        };
        Grid.SetColumn(detailsButton, 1);
        var body = new StackPanel
        {
            Spacing = 7,
            Children =
            {
                header,
                new TextBlock
                {
                    Text = error.Summary,
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                    Foreground = PrimaryTextBrush(),
                },
                new TextBlock
                {
                    Text = error.Suggestion,
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Foreground = SecondaryTextBrush(),
                },
                detailShell,
            },
        };
        return BuildAiSpecialBubble("AI · 请求错误", body, background, border, foreground, message.CreatedAt);
    }

    private UIElement BuildAiWarningBubble(AiChatMessage message)
    {
        var foreground = s_darkTheme
            ? new SolidColorBrush(ColorHelper.FromArgb(255, 253, 230, 138))
            : new SolidColorBrush(ColorHelper.FromArgb(255, 161, 98, 7));
        var background = s_darkTheme
            ? new SolidColorBrush(ColorHelper.FromArgb(35, 250, 204, 21))
            : new SolidColorBrush(ColorHelper.FromArgb(30, 202, 138, 4));
        var border = s_darkTheme
            ? new SolidColorBrush(ColorHelper.FromArgb(105, 250, 204, 21))
            : new SolidColorBrush(ColorHelper.FromArgb(90, 202, 138, 4));
        var content = new TextBlock
        {
            Text = message.Text,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = PrimaryTextBrush(),
        };
        return BuildAiSpecialBubble("系统 · 兼容提示", content, background, border, foreground, message.CreatedAt);
    }

    private UIElement BuildAiChoiceBubble(AiChatMessage message, Action<IReadOnlyList<string>>? onSubmit)
    {
        var request = message.Choice ?? new AiChoiceRequest
        {
            Question = "该选择请求已损坏。",
            Options = ["关闭"],
            IsSubmitted = true,
        };
        var selected = new HashSet<string>(request.SelectedOptions, StringComparer.Ordinal);
        var optionButtons = new List<(string Option, Button Button)>();
        var optionsPanel = new StackPanel { Spacing = 7 };
        var status = new TextBlock
        {
            FontSize = 11,
            Foreground = SecondaryTextBrush(),
            TextWrapping = TextWrapping.Wrap,
        };
        var submit = PrimaryButton("确认并继续");
        submit.HorizontalAlignment = HorizontalAlignment.Left;

        void RefreshChoiceState()
        {
            foreach (var (option, button) in optionButtons)
            {
                ApplySegmentState(button, selected.Contains(option));
                button.IsEnabled = onSubmit is not null && !request.IsSubmitted;
            }
            submit.IsEnabled = onSubmit is not null && !request.IsSubmitted && selected.Count > 0;
            submit.Visibility = onSubmit is not null && !request.IsSubmitted ? Visibility.Visible : Visibility.Collapsed;
            status.Text = request.IsSubmitted
                ? $"已选择：{string.Join("、", request.SelectedOptions)}"
                : onSubmit is null
                    ? "本次选择未完成。"
                    : request.SelectionMode == AiChoiceSelectionMode.Single ? "请选择一项。" : "可选择多项，然后确认。";
        }

        foreach (var option in request.Options)
        {
            var button = SecondaryButton(option);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.Content = new TextBlock { Text = option, TextWrapping = TextWrapping.Wrap };
            button.Click += (_, _) =>
            {
                if (request.SelectionMode == AiChoiceSelectionMode.Single)
                {
                    selected.Clear();
                    selected.Add(option);
                }
                else if (!selected.Add(option))
                {
                    selected.Remove(option);
                }
                RefreshChoiceState();
            };
            optionButtons.Add((option, button));
            optionsPanel.Children.Add(button);
        }
        submit.Click += (_, _) =>
        {
            if (selected.Count == 0)
                return;
            request.SelectedOptions = request.Options.Where(selected.Contains).ToList();
            request.IsSubmitted = true;
            RefreshChoiceState();
            onSubmit?.Invoke(request.SelectedOptions);
        };
        RefreshChoiceState();

        var body = new StackPanel
        {
            Spacing = 9,
            Children =
            {
                new TextBlock
                {
                    Text = request.Question,
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = PrimaryTextBrush(),
                },
                optionsPanel,
                status,
                submit,
            },
        };
        return BuildAiSpecialBubble("AI · 需要选择", body, SurfaceAltBrush(), BorderLightBrush(), PrimaryBrush(), message.CreatedAt);
    }

    private UIElement BuildAiSpecialBubble(
        string label,
        UIElement content,
        Brush background,
        Brush border,
        Brush accent,
        DateTimeOffset createdAt)
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = AiBubbleMaxWidth(),
            Spacing = 4,
            Margin = new Thickness(0, 0, 0, 16),
        };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = accent,
        });
        panel.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 11, 14, 11),
            Background = background,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            Child = content,
        });
        panel.Children.Add(new TextBlock
        {
            Text = FormatMessageTime(createdAt),
            FontSize = 10,
            Foreground = MutedBrush(),
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

        FrostedDialog? dialog = null;
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
            if (addResult.Success)
            {
                Notify("设备已添加", $"{connectIp.Text}:{port}", InfoBarSeverity.Success);
                dialog?.Hide();
                ShowDevices();
            }
            else
            {
                wirelessStatus.Text = FailureText(addResult);
                Notify("无线 ADB 连接失败", FailureText(addResult), InfoBarSeverity.Error);
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

        FrostedDialog? dialog = null;
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
        var wasAtTop = _aiMessageScroller is not null && _aiMessageScroller.VerticalOffset <= 2;
        _aiPanel.Visibility = Visibility.Visible;
        AnimateAiPanel(32, 0, 0, 1, null);
        if (_aiButton is not null)
            ApplyNavButtonState(_aiButton, true);
        if (wasAtTop)
            ScrollAiMessagesToBottom();
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

    private FrostedDialog Dialog(string title, UIElement content, string primary)
        => new(_root.XamlRoot, title, content, DialogPalette(), primary, "取消");

    private FrostedDialog DialogChrome(string title, UIElement content)
        => new(_root.XamlRoot, title, content, DialogPalette());

    private static FrostedDialogPalette DialogPalette()
        => new(
            s_darkTheme ? ColorHelper.FromArgb(255, 20, 30, 46) : ColorHelper.FromArgb(255, 244, 248, 252),
            s_darkTheme ? ColorHelper.FromArgb(255, 19, 29, 44) : ColorHelper.FromArgb(255, 247, 250, 253),
            BorderLightBrush(),
            PrimaryTextBrush(),
            SecondaryTextBrush(),
            SurfaceAltBrush(),
            HoverBrush(),
            PrimaryBrush(),
            OnPrimaryBrush());

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
        var address = device.ConnectionKind == "usb"
            ? device.DeviceId
            : DeviceService.TryParseAdbEndpoint(device.DeviceId, out var host, out var port)
                ? $"{host}:{port}"
                : $"{device.IpAddress}:{device.Port}";
        return string.IsNullOrWhiteSpace(device.Note) ? $"{kind} · {address}" : $"{kind} · {device.Note}";
    }

    private static string CurrentDeviceIp(DeviceModel device)
        => device.IsConnected && DeviceService.TryParseAdbEndpoint(device.DeviceId, out var host, out _)
            ? host
            : device.IpAddress;

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
            Symbol.Favorite => "\uE745",   // 控制 → 游戏手柄，语义更贴近"控制"
            Symbol.Keyboard => "\uE756",   // 终端 → 命令窗口图标
            Symbol.AllApps => "\uE71D",    // 软件 → 所有应用
            Symbol.Folder => "\uE8B7",     // 文件 → 文件夹
            Symbol.Setting => "\uE950",    // 硬件 → 芯片/CPU 图标
            Symbol.Refresh => "\uE72C",    // 重启 → 刷新/重启
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

    private static void StyleShellTerminal(TextBox terminal)
    {
        // A terminal is a stable work surface, not a form field: hover and focus must not
        // introduce the standard TextBox surface or border while a command is being typed.
        terminal.UseLayoutRounding = true;
        terminal.Resources["TextControlBackground"] = ShellBrush();
        terminal.Resources["TextControlBackgroundPointerOver"] = ShellBrush();
        terminal.Resources["TextControlBackgroundFocused"] = ShellBrush();
        terminal.Resources["TextControlBackgroundDisabled"] = ShellBrush();
        terminal.Resources["TextControlForeground"] = ShellTextBrush();
        terminal.Resources["TextControlForegroundPointerOver"] = ShellTextBrush();
        terminal.Resources["TextControlForegroundFocused"] = ShellTextBrush();
        terminal.Resources["TextControlBorderBrush"] = TransparentBrush();
        terminal.Resources["TextControlBorderBrushPointerOver"] = TransparentBrush();
        terminal.Resources["TextControlBorderBrushFocused"] = TransparentBrush();
        terminal.Resources["FocusVisualPrimaryBrush"] = TransparentBrush();
        terminal.Resources["FocusVisualSecondaryBrush"] = TransparentBrush();
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

    /// <summary>
    /// 为自绘 DropdownSelector 注入主题画刷，保持与 MainWindow 主题系统一致。
    /// </summary>
    private static DropdownSelector StyleDropdown(DropdownSelector dropdown)
    {
        dropdown.ApplyTheme(
            foreground: PrimaryTextBrush(),
            background: SurfaceBrush(),
            border: BorderLightBrush(),
            hover: HoverBrush(),
            accent: PrimaryBrush(),
            accentText: OnPrimaryBrush(),
            popupBackground: SurfaceBrush());
        return dropdown;
    }

    private static void StyleListView(ListView list)
    {
        list.UseLayoutRounding = true;
        list.Background = SurfaceBrush();
        list.BorderBrush = BorderBrush();
        list.BorderThickness = new Thickness(1);
        list.CornerRadius = new CornerRadius(12);
        // Keep the scrollbar outside the list's content rhythm. This avoids long names or
        // metadata being painted underneath the thumb in every device-detail category.
        list.Padding = new Thickness(6, 6, 16, 6);
        list.Resources["ListViewItemBackgroundPointerOver"] = HoverBrush();
        list.Resources["ListViewItemBackgroundSelected"] = PrimaryLightBrush();
        list.Resources["ListViewItemBackgroundSelectedPointerOver"] = PrimaryLightBrush();
        list.Resources["ListViewItemSelectedBackground"] = PrimaryLightBrush();
        list.Resources["ListViewItemSelectedPointerOverBackground"] = PrimaryLightBrush();
        list.Resources["ListViewItemSelectedPressedBackground"] = SurfaceAltBrush();
        list.Resources["ListViewItemSelectionIndicatorBrush"] = PrimaryBrush();
        list.Resources["SystemControlHighlightListAccentLowBrush"] = PrimaryLightBrush();
        list.Resources["SystemControlHighlightListAccentHighBrush"] = PrimaryBrush();
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
            var offsetBefore = viewer.VerticalOffset;
            var handled = delta != 0 && TryScrollPageByWheelDelta(viewer, delta);
            MouseWheelDiagnostics.Write(
                "xaml-scrollviewer",
                delta,
                DescribeWheelTarget(viewer),
                offsetBefore,
                viewer.VerticalOffset,
                handled);
            if (!handled)
                return;
            e.Handled = true;
        };
        viewer.AddHandler(UIElement.PointerWheelChangedEvent, wheelHandler, true);
    }

    private void OnRootPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_root);
        var delta = point.Properties.MouseWheelDelta;
        MouseWheelDiagnostics.Write(
            "xaml-root",
            delta,
            e.OriginalSource?.GetType().Name ?? "none",
            null,
            null,
            e.Handled);
        if (e.Handled || delta == 0)
            return;

        var target = FindWheelScrollTarget(e.OriginalSource as DependencyObject, delta)
            ?? FindWheelScrollTarget(point.Position, delta);
        e.Handled = target is not null && TryScrollPageByWheelDelta(target, delta);
    }

    private void InstallNativeWheelHook()
    {
        _windowHandle = WindowNative.GetWindowHandle(this);
        if (_windowHandle == IntPtr.Zero)
            return;

        _wndProcDelegate = NativeWindowProc;
        _lowLevelWheelInput.Wheel += OnLowLevelMouseWheel;
        _lowLevelWheelInput.Start();
        EnsureWindowMinimumHeight();
        RefreshNativeWheelHooks();
    }

    private void OnLowLevelMouseWheel(object? sender, LowLevelMouseWheelEventArgs e)
    {
        var screenPoint = new NativePoint { X = e.ScreenX, Y = e.ScreenY };
        if (DispatcherQueue.HasThreadAccess)
        {
            ScheduleLowLevelWheelFallback(screenPoint, e.Delta);
            return;
        }

        if (!DispatcherQueue.TryEnqueue(() => ScheduleLowLevelWheelFallback(screenPoint, e.Delta)))
        {
            MouseWheelDiagnostics.Write(
                "low-level-queue-rejected",
                e.Delta,
                "none",
                null,
                null,
                false,
                "MOUSE_WHEEL_DISPATCH_REJECTED");
        }
    }

    private void ScheduleLowLevelWheelFallback(NativePoint screenPoint, int delta)
    {
        if (delta == 0 || _wheelFallbackCancellation.IsCancellationRequested)
            return;

        if (_pendingLowLevelWheelFallback is null)
        {
            var target = FindNativeWheelTarget(screenPoint, delta);
            _pendingLowLevelWheelFallback = new DeferredWheelFallback(
                screenPoint,
                delta,
                target,
                target?.VerticalOffset);
        }
        else
        {
            var pending = _pendingLowLevelWheelFallback;
            _pendingLowLevelWheelFallback = pending with
            {
                ScreenPoint = screenPoint,
                Delta = pending.Delta + delta,
            };
        }

        MouseWheelDiagnostics.Write(
            "low-level-captured",
            delta,
            DescribeWheelTarget(_pendingLowLevelWheelFallback.InitialTarget),
            _pendingLowLevelWheelFallback.OffsetBeforeDefaultHandling,
            null,
            true);
        if (_lowLevelWheelFallbackScheduled)
            return;

        _lowLevelWheelFallbackScheduled = true;
        _ = ApplyLowLevelWheelFallbackAfterDelayAsync(_wheelFallbackCancellation.Token);
    }

    private async Task ApplyLowLevelWheelFallbackAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(MouseWheelScrollPolicy.LowLevelFallbackDelay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!DispatcherQueue.TryEnqueue(ApplyPendingLowLevelWheelFallback))
        {
            _lowLevelWheelFallbackScheduled = false;
            MouseWheelDiagnostics.Write(
                "low-level-fallback-queue-rejected",
                0,
                "none",
                null,
                null,
                false,
                "MOUSE_WHEEL_DISPATCH_REJECTED");
        }
    }

    private void ApplyPendingLowLevelWheelFallback()
    {
        var fallback = _pendingLowLevelWheelFallback;
        _pendingLowLevelWheelFallback = null;
        _lowLevelWheelFallbackScheduled = false;
        if (fallback is null || fallback.Delta == 0)
            return;

        var offsetAfterDefaultHandling = fallback.InitialTarget?.VerticalOffset;
        if (!MouseWheelScrollPolicy.ShouldScheduleDeferredFallback(
                isOutermostNativeDispatch: true,
                fallback.OffsetBeforeDefaultHandling,
                offsetAfterDefaultHandling))
        {
            MouseWheelDiagnostics.Write(
                "low-level-default-handled",
                fallback.Delta,
                DescribeWheelTarget(fallback.InitialTarget),
                fallback.OffsetBeforeDefaultHandling,
                offsetAfterDefaultHandling,
                true);
            return;
        }

        var handled = TryRouteNativeWheel(
            fallback.ScreenPoint,
            fallback.Delta,
            out var target,
            out var offsetBefore,
            out var requestedOffset);
        MouseWheelDiagnostics.Write(
            "low-level-deferred-fallback",
            fallback.Delta,
            DescribeWheelTarget(target),
            offsetBefore,
            requestedOffset,
            handled);
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
        var installedProc = _wndProcDelegate is null
            ? IntPtr.Zero
            : Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        foreach (var (hwnd, previousProc) in _hookedWndProcs.ToList())
        {
            if (installedProc != IntPtr.Zero && GetWindowLongPtr(hwnd, GwlWndProc) == installedProc)
                SetWindowLongPtr(hwnd, GwlWndProc, previousProc);
        }
        _hookedWndProcs.Clear();
        _wndProcDelegate = null;
    }

    private void HookWindowForMouseWheel(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || _wndProcDelegate is null)
            return;

        // WinUI 3 and remote-control clients can deliver wheel input to late-created child HWNDs.
        // Read the live WndProc because WinUI can replace it after an earlier hook was installed.
        var newProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        var currentProc = GetWindowLongPtr(hwnd, GwlWndProc);
        if (!MouseWheelScrollPolicy.ShouldInstallNativeHook(hwnd, currentProc, newProc))
            return;

        var previousProc = SetWindowLongPtr(hwnd, GwlWndProc, newProc);
        if (previousProc != IntPtr.Zero)
        {
            _hookedWndProcs[hwnd] = previousProc;
            MouseWheelDiagnostics.Write(
                "native-hook-installed",
                0,
                hwnd == _windowHandle ? "window" : "child-window",
                null,
                null,
                true);
            return;
        }

        MouseWheelDiagnostics.Write(
            "native-hook-install-failed",
            0,
            hwnd == _windowHandle ? "window" : "child-window",
            null,
            null,
            false,
            "MOUSE_WHEEL_NATIVE_HOOK_FAILED",
            $"Win32Error: {Marshal.GetLastWin32Error()}");
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

        // XAML pointer routing does not receive wheel input from every native child HWND.
        // Always observe native messages; the deferred offset checks below prevent duplicate scrolling.
        var isWheelMessage = message is (WmMouseWheel or WmPointerWheel);
        DeferredWheelFallback? fallback = null;
        if (isWheelMessage)
        {
            MouseWheelDiagnostics.Write(
                "native-message-received",
                MouseWheelScrollPolicy.DecodeDelta(wParam),
                hwnd == _windowHandle ? "window" : "child-window",
                null,
                null,
                true);
            _nativeWheelDispatchDepth++;
        }

        try
        {
            if (isWheelMessage && _nativeWheelDispatchDepth == 1)
                fallback = CaptureDeferredWheelFallback(wParam, lParam);

            // Preserve WinUI and child HWND handling before applying the app-level fallback.
            return ForwardWindowMessage(hwnd, message, wParam, lParam);
        }
        finally
        {
            if (isWheelMessage)
            {
                _nativeWheelDispatchDepth--;
                if (fallback is not null)
                    ScheduleDeferredWheelFallback(fallback);
            }
        }
    }

    private IntPtr ForwardWindowMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
        => _hookedWndProcs.TryGetValue(hwnd, out var previousProc)
            ? CallWindowProc(previousProc, hwnd, message, wParam, lParam)
            : DefWindowProc(hwnd, message, wParam, lParam);

    private DeferredWheelFallback? CaptureDeferredWheelFallback(IntPtr wParam, IntPtr lParam)
    {
        var delta = MouseWheelScrollPolicy.DecodeDelta(wParam);
        if (delta == 0)
            return null;

        var screenPoint = GetCursorPos(out var cursorPoint)
            ? cursorPoint
            : DecodeNativePoint(lParam);
        var target = DispatcherQueue.HasThreadAccess
            ? FindNativeWheelTarget(screenPoint, delta)
            : null;
        return new DeferredWheelFallback(screenPoint, delta, target, target?.VerticalOffset);
    }

    private void ScheduleDeferredWheelFallback(DeferredWheelFallback fallback)
    {
        var offsetAfterDefaultHandling = fallback.InitialTarget?.VerticalOffset;
        if (!MouseWheelScrollPolicy.ShouldScheduleDeferredFallback(
                isOutermostNativeDispatch: true,
                fallback.OffsetBeforeDefaultHandling,
                offsetAfterDefaultHandling))
        {
            MouseWheelDiagnostics.Write(
                "default-handled",
                fallback.Delta,
                DescribeWheelTarget(fallback.InitialTarget),
                fallback.OffsetBeforeDefaultHandling,
                offsetAfterDefaultHandling,
                true);
            return;
        }

        if (!DispatcherQueue.TryEnqueue(() => ApplyDeferredWheelFallback(fallback)))
        {
            MouseWheelDiagnostics.Write(
                "fallback-queue-rejected",
                fallback.Delta,
                DescribeWheelTarget(fallback.InitialTarget),
                fallback.OffsetBeforeDefaultHandling,
                offsetAfterDefaultHandling,
                false);
        }
    }

    private void ApplyDeferredWheelFallback(DeferredWheelFallback fallback)
    {
        var offsetBeforeFallback = fallback.InitialTarget?.VerticalOffset;
        if (!MouseWheelScrollPolicy.ShouldScheduleDeferredFallback(
                isOutermostNativeDispatch: true,
                fallback.OffsetBeforeDefaultHandling,
                offsetBeforeFallback))
        {
            MouseWheelDiagnostics.Write(
                "default-handled-async",
                fallback.Delta,
                DescribeWheelTarget(fallback.InitialTarget),
                fallback.OffsetBeforeDefaultHandling,
                offsetBeforeFallback,
                true);
            return;
        }

        var handled = TryRouteNativeWheel(
            fallback.ScreenPoint,
            fallback.Delta,
            out var target,
            out var offsetBefore,
            out var requestedOffset);
        MouseWheelDiagnostics.Write(
            "deferred-fallback",
            fallback.Delta,
            DescribeWheelTarget(target),
            offsetBefore,
            requestedOffset,
            handled);
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

    private static bool TryScrollPageByWheelDelta(ScrollViewer viewer, int delta)
        => TryScrollPageByWheelDelta(viewer, delta, out _);

    private static bool TryScrollPageByWheelDelta(ScrollViewer viewer, int delta, out double target)
    {
        if (!MouseWheelScrollPolicy.TryCalculateTarget(
                viewer.VerticalOffset,
                viewer.ScrollableHeight,
                delta,
                out target))
            return false;
        return viewer.ChangeView(null, target, null, true);
    }

    private bool TryRouteNativeWheel(
        NativePoint screenPoint,
        int delta,
        out ScrollViewer? target,
        out double? offsetBefore,
        out double? requestedOffset)
    {
        target = FindNativeWheelTarget(screenPoint, delta);
        offsetBefore = target?.VerticalOffset;
        requestedOffset = null;
        if (target is null)
            return false;

        var handled = TryScrollPageByWheelDelta(target, delta, out var calculatedOffset);
        requestedOffset = calculatedOffset;
        return handled;
    }

    private ScrollViewer? FindNativeWheelTarget(NativePoint screenPoint, int delta)
    {
        var clientPoint = screenPoint;
        if (!ScreenToClient(_windowHandle, ref clientPoint))
            return null;
        var dpi = Math.Max(96u, GetDpiForWindow(_windowHandle));
        return FindWheelScrollTarget(
            new Point(clientPoint.X * 96d / dpi, clientPoint.Y * 96d / dpi),
            delta);
    }

    private ScrollViewer? FindWheelScrollTarget(Point point, int delta)
    {
        var topmost = VisualTreeHelper.FindElementsInHostCoordinates(point, _root).FirstOrDefault();
        return FindWheelScrollTarget(topmost, delta);
    }

    private ScrollViewer? FindWheelScrollTarget(DependencyObject? source, int delta)
    {
        var visited = new HashSet<ScrollViewer>();
        var current = source;
        while (current is not null)
        {
            if (current is ScrollViewer viewer &&
                viewer.Visibility == Visibility.Visible &&
                visited.Add(viewer) &&
                MouseWheelScrollPolicy.TryCalculateTarget(
                    viewer.VerticalOffset,
                    viewer.ScrollableHeight,
                    delta,
                    out _))
            {
                return viewer;
            }
            current = VisualTreeHelper.GetParent(current);
        }

        return _activePageScroller is not null &&
            _activePageScroller.Visibility == Visibility.Visible &&
            visited.Add(_activePageScroller) &&
            MouseWheelScrollPolicy.TryCalculateTarget(
                _activePageScroller.VerticalOffset,
                _activePageScroller.ScrollableHeight,
                delta,
                out _)
            ? _activePageScroller
            : null;
    }

    private static string DescribeWheelTarget(ScrollViewer? viewer)
    {
        if (viewer is null)
            return "none";
        if (!string.IsNullOrWhiteSpace(viewer.Name))
            return viewer.Name;
        return viewer.Content?.GetType().Name ?? nameof(ScrollViewer);
    }

    private static NativePoint DecodeNativePoint(IntPtr lParam)
        => new()
        {
            X = unchecked((short)((long)lParam & 0xffff)),
            Y = unchecked((short)(((long)lParam >> 16) & 0xffff)),
        };

    private sealed record DeferredWheelFallback(
        NativePoint ScreenPoint,
        int Delta,
        ScrollViewer? InitialTarget,
        double? OffsetBeforeDefaultHandling);

    private const int GwlWndProc = -4;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint WmMouseWheel = 0x020A;
    private const uint WmPointerWheel = 0x024E;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int IdcSizeWe = 32644;

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

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallWindowProc(IntPtr previousProc, IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumChildWindows(IntPtr parentHandle, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ScreenToClient(IntPtr hwnd, ref NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr cursorName);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

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
            UseLayoutRounding = true,
            CornerRadius = new CornerRadius(10),
            Background = PrimaryLightBrush(),
            BorderBrush = TransparentBrush(),
            BorderThickness = new Thickness(1),
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
