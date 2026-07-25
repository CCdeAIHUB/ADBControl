using ADBControl.Desktop.Controls;
using ADBControl.Desktop.Models;
using ADBControl.Desktop.Services.Automation;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ADBControl.Desktop.Views;

public sealed class AutomationTaskPage : UserControl
{
    private readonly AutomationTaskService _service;
    private readonly Func<IReadOnlyList<DeviceModel>> _devices;
    private readonly Action _openAi;
    private readonly Action<string, string, bool> _notify;
    private readonly StackPanel _taskList = new() { Spacing = 10 };
    private readonly TextBlock _totalValue = MetricValue();
    private readonly TextBlock _runningValue = MetricValue();
    private readonly TextBlock _enabledValue = MetricValue();
    private readonly TextBox _search = new();
    private readonly Dictionary<string, Button> _filterButtons = new(StringComparer.Ordinal);
    private string _filter = "all";
    private bool _subscribed;

    public AutomationTaskPage(
        AutomationTaskService service,
        Func<IReadOnlyList<DeviceModel>> devices,
        Action openAi,
        Action<string, string, bool> notify)
    {
        _service = service;
        _devices = devices;
        _openAi = openAi;
        _notify = notify;
        Content = BuildPage();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += (_, _) => Refresh();
    }

    private UIElement BuildPage()
    {
        var panel = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(16, 6, 16, 20),
        };
        panel.Children.Add(BuildHeader());
        panel.Children.Add(BuildMetrics());
        panel.Children.Add(BuildToolbar());
        panel.Children.Add(_taskList);
        return new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    private UIElement BuildHeader()
    {
        var grid = new Grid
        {
            ColumnSpacing = 12,
            ColumnDefinitions =
            {
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        grid.Children.Add(new StackPanel
        {
            Spacing = 3,
            Children =
            {
                new TextBlock
                {
                    Text = "任务",
                    FontSize = 22,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = TextBrush(),
                },
                new TextBlock
                {
                    Text = "自动化与运行队列",
                    FontSize = 13,
                    Foreground = MutedBrush(),
                },
            },
        });

        var ai = TextButton("\uE8BD", "AI 创建", primary: false);
        ai.Click += (_, _) => _openAi();
        Grid.SetColumn(ai, 1);
        grid.Children.Add(ai);

        var add = TextButton("\uE710", "新建任务", primary: true);
        add.Click += async (_, _) => await ShowEditorAsync(null);
        Grid.SetColumn(add, 2);
        grid.Children.Add(add);
        return grid;
    }

    private UIElement BuildMetrics()
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        AddMetric(grid, "全部任务", _totalValue, Colors.DeepSkyBlue, 0);
        AddMetric(grid, "运行中", _runningValue, Colors.MediumSeaGreen, 1);
        AddMetric(grid, "已启用", _enabledValue, Colors.Goldenrod, 2);
        return grid;
    }

    private UIElement BuildToolbar()
    {
        var grid = new Grid
        {
            ColumnSpacing = 10,
            ColumnDefinitions =
            {
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        StyleTextBox(_search, "搜索任务、设备或触发器");
        _search.MaxWidth = 360;
        _search.HorizontalAlignment = HorizontalAlignment.Left;
        _search.TextChanged += (_, _) => RefreshList();
        grid.Children.Add(_search);

        var filters = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var item in new[]
        {
            (Key: "all", Text: "全部"),
            (Key: "running", Text: "运行中"),
            (Key: "paused", Text: "已暂停"),
            (Key: "failed", Text: "失败"),
        })
        {
            var button = SegmentButton(item.Text, item.Key == _filter);
            button.Click += (_, _) =>
            {
                _filter = item.Key;
                ApplyFilterStates();
                RefreshList();
            };
            _filterButtons[item.Key] = button;
            filters.Children.Add(button);
        }
        Grid.SetColumn(filters, 1);
        grid.Children.Add(filters);
        return grid;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            _service.Changed += OnServiceChanged;
            _subscribed = true;
        }
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed)
        {
            _service.Changed -= OnServiceChanged;
            _subscribed = false;
        }
    }

    private void OnServiceChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Refresh);

    private void Refresh()
    {
        var snapshots = _service.GetSnapshots();
        _totalValue.Text = snapshots.Count.ToString();
        _runningValue.Text = snapshots.Count(item => item.ActiveRun?.Status is AutomationRunStatus.Running or AutomationRunStatus.Queued).ToString();
        _enabledValue.Text = snapshots.Count(item => item.Definition.Enabled).ToString();
        ApplyFilterStates();
        RefreshList();
    }

    private void RefreshList()
    {
        if (_taskList is null)
            return;
        var query = _search.Text?.Trim() ?? string.Empty;
        var snapshots = _service.GetSnapshots().Where(snapshot => MatchesFilter(snapshot) && MatchesSearch(snapshot, query)).ToList();
        _taskList.Children.Clear();
        if (!_service.IsInitialized)
        {
            _taskList.Children.Add(EmptyState("正在读取任务...", "\uE895"));
            return;
        }
        if (snapshots.Count == 0)
        {
            _taskList.Children.Add(EmptyState(_service.GetSnapshots().Count == 0 ? "暂无任务" : "没有符合条件的任务", "\uE8FD"));
            return;
        }
        foreach (var snapshot in snapshots)
            _taskList.Children.Add(BuildTaskCard(snapshot));
    }

    private UIElement BuildTaskCard(AutomationTaskSnapshot snapshot)
    {
        var definition = snapshot.Definition;
        var run = snapshot.ActiveRun ?? snapshot.LastRun;
        var status = StatusPresentation(definition, run);
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
            Width = 9,
            Height = 9,
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(status.Color),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var title = new StackPanel { Spacing = 2 };
        title.Children.Add(new TextBlock
        {
            Text = definition.Name,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TextBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        title.Children.Add(new TextBlock
        {
            Text = status.Text,
            FontSize = 12,
            Foreground = new SolidColorBrush(status.Color),
        });
        Grid.SetColumn(title, 1);
        head.Children.Add(title);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var enabled = IconButton("\uE7E8", definition.Enabled ? "停用任务" : "启用任务", definition.Enabled ? PrimaryBrush() : MutedBrush());
        enabled.Click += async (_, _) => await ExecuteUiActionAsync(
            () => _service.SetEnabledAsync(definition.Id, !definition.Enabled),
            definition.Enabled ? "任务已停用" : "任务已启用");
        actions.Children.Add(enabled);

        var play = IconButton("\uE768", "立即运行", PrimaryBrush());
        play.Click += async (_, _) => await ExecuteUiActionAsync(async () => { await _service.RunNowAsync(definition.Id); }, "任务已加入运行队列");
        actions.Children.Add(play);
        if (snapshot.ActiveRun is { } active)
        {
            if (active.Status == AutomationRunStatus.Paused)
            {
                var resume = IconButton("\uE768", "继续", new SolidColorBrush(Colors.DeepSkyBlue));
                resume.Click += async (_, _) => await ExecuteUiActionAsync(async () => { await _service.ResumeAsync(active.Id); }, "任务已继续");
                actions.Children.Add(resume);
            }
            else
            {
                var pause = IconButton("\uE769", "暂停", new SolidColorBrush(Colors.Goldenrod));
                pause.Click += async (_, _) => await ExecuteUiActionAsync(async () => { await _service.PauseAsync(active.Id); }, "任务已暂停");
                actions.Children.Add(pause);
            }
            var stop = IconButton("\uE71A", "停止", DangerBrush());
            stop.Click += async (_, _) => await ExecuteUiActionAsync(async () => { await _service.StopAsync(active.Id); }, "任务已停止");
            actions.Children.Add(stop);
        }
        var edit = IconButton("\uE70F", "编辑", TextBrush());
        edit.Click += async (_, _) => await ShowEditorAsync(definition);
        actions.Children.Add(edit);
        var delete = IconButton("\uE74D", "删除", DangerBrush());
        delete.Click += async (_, _) => await ConfirmDeleteAsync(definition);
        actions.Children.Add(delete);
        Grid.SetColumn(actions, 2);
        head.Children.Add(actions);
        root.Children.Add(head);

        root.Children.Add(BuildMetadata(snapshot));
        if (run is not null)
            root.Children.Add(BuildProgress(run));
        if (run?.Status == AutomationRunStatus.Failed && !string.IsNullOrWhiteSpace(run.ErrorMessage))
        {
            root.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 7, 10, 7),
                Background = new SolidColorBrush(Color.FromArgb(IsDark ? (byte)42 : (byte)24, 239, 68, 68)),
                Child = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(run.ErrorCode) ? run.ErrorMessage : $"{run.ErrorCode}: {run.ErrorMessage}",
                    Foreground = DangerBrush(),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                },
            });
        }
        return Card(root);
    }

    private UIElement BuildMetadata(AutomationTaskSnapshot snapshot)
    {
        var grid = new Grid { ColumnSpacing = 18 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        AddMetadata(grid, "设备", DeviceName(snapshot.Definition.DeviceId), 0);
        AddMetadata(grid, "触发", TriggerSummary(snapshot.Definition), 1);
        AddMetadata(grid, "下次执行", snapshot.NextRunAt is null ? "-" : snapshot.NextRunAt.Value.ToLocalTime().ToString("MM-dd HH:mm:ss"), 2);
        return grid;
    }

    private UIElement BuildProgress(AutomationRunRecord run)
    {
        var stack = new StackPanel { Spacing = 6 };
        var label = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } },
        };
        label.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(run.CurrentStep) ? "等待执行" : run.CurrentStep,
            Foreground = MutedBrush(),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var percent = new TextBlock
        {
            Text = $"{Math.Clamp(run.Progress, 0, 1):P0}  {run.CompletedSteps}/{run.TotalSteps}",
            Foreground = MutedBrush(),
            FontSize = 12,
        };
        Grid.SetColumn(percent, 1);
        label.Children.Add(percent);
        stack.Children.Add(label);

        var fill = new Border
        {
            Height = 5,
            CornerRadius = new CornerRadius(3),
            Background = run.Status == AutomationRunStatus.Failed ? DangerBrush() : PrimaryBrush(),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var track = new Grid
        {
            Height = 5,
            Background = AltBrush(),
            Children = { fill },
        };
        track.SizeChanged += (_, args) => fill.Width = Math.Max(0, args.NewSize.Width * Math.Clamp(run.Progress, 0, 1));
        stack.Children.Add(new Border { CornerRadius = new CornerRadius(3), Child = track });
        return stack;
    }

    private async Task ShowEditorAsync(AutomationTaskDefinition? existing)
    {
        var json = existing is null ? AutomationTaskSerializer.CreateTemplate(_devices().FirstOrDefault()?.DeviceId) : AutomationTaskSerializer.Serialize(existing);
        while (true)
        {
            var editor = new TextBox
            {
                Text = json,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 12,
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
            StyleTextBox(editor, string.Empty);
            editor.MinWidth = 760;
            editor.MinHeight = 440;
            editor.MaxHeight = 560;
            var content = new StackPanel { Spacing = 10 };
            if (existing is null)
                content.Children.Add(BuildTemplateSelector(editor));
            content.Children.Add(editor);
            var result = await Dialog(existing is null ? "新建任务" : "编辑任务", content, "保存", "取消").ShowAsync();
            json = editor.Text;
            if (result != ContentDialogResult.Primary)
                return;
            try
            {
                var definition = AutomationTaskSerializer.Deserialize(json);
                if (existing is not null)
                {
                    definition.Id = existing.Id;
                    definition.CreatedAt = existing.CreatedAt;
                }
                await _service.CreateOrUpdateAsync(definition);
                _notify(existing is null ? "任务已创建" : "任务已更新", definition.Name, false);
                return;
            }
            catch (Exception ex)
            {
                _notify("任务定义无效", ex.Message, true);
            }
        }
    }

    private UIElement BuildTemplateSelector(TextBox editor)
    {
        var names = new[] { "空白", "每天 17:00", "每周三 11:00", "每小时", "打开 App", "打开网页", "出现通知", "开始充电" };
        var selector = new DropdownSelector
        {
            ItemsSource = names,
            SelectedIndex = 0,
            MinWidth = 180,
        };
        selector.ApplyTheme(TextBrush(), SurfaceBrush(), CardBorderBrush(), AltBrush(), PrimaryBrush(), new SolidColorBrush(Colors.White), SurfaceBrush());
        selector.SelectionChanged += (_, _) => editor.Text = CreateTemplate(selector.SelectedIndex);
        return new Grid
        {
            ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() },
            ColumnSpacing = 10,
            Children =
            {
                new TextBlock { Text = "模板", Foreground = MutedBrush(), VerticalAlignment = VerticalAlignment.Center },
                WithColumn(selector, 1),
            },
        };
    }

    private string CreateTemplate(int index)
    {
        var deviceId = _devices().FirstOrDefault()?.DeviceId ?? string.Empty;
        var definition = AutomationTaskSerializer.Deserialize(AutomationTaskSerializer.CreateTemplate(deviceId));
        switch (index)
        {
            case 1:
                definition.Name = "每日任务";
                definition.Triggers = [new AutomationTriggerDefinition { Type = "daily", At = "17:00", TimeZoneId = TimeZoneInfo.Local.Id }];
                break;
            case 2:
                definition.Name = "每周任务";
                definition.Triggers = [new AutomationTriggerDefinition { Type = "weekly", Days = [DayOfWeek.Wednesday], At = "11:00", TimeZoneId = TimeZoneInfo.Local.Id }];
                break;
            case 3:
                definition.Name = "每小时任务";
                definition.Triggers = [new AutomationTriggerDefinition { Type = "interval", IntervalSeconds = 3600, CatchUp = true }];
                break;
            case 4:
                definition.Name = "打开 App 时";
                definition.Permissions.AllowAdb = true;
                definition.Triggers = [ConditionTrigger("app.foreground", "com.example.app")];
                break;
            case 5:
                definition.Name = "打开网页时";
                definition.Permissions.AllowAdb = true;
                definition.Triggers = [ConditionTrigger("webpage.open", "example.com")];
                break;
            case 6:
                definition.Name = "出现通知时";
                definition.Permissions.AllowAdb = true;
                definition.Triggers = [ConditionTrigger("notification.present", "通知文本")];
                break;
            case 7:
                definition.Name = "开始充电时";
                definition.Permissions.AllowAdb = true;
                definition.Triggers = [ConditionTrigger("battery.charging", "true")];
                break;
        }
        return AutomationTaskSerializer.Serialize(definition);
    }

    private static AutomationTriggerDefinition ConditionTrigger(string type, string value)
    {
        return new AutomationTriggerDefinition
        {
            Type = "condition",
            PollIntervalSeconds = 2,
            EdgeOnly = true,
            CooldownSeconds = 60,
            Conditions =
            [
                new AutomationConditionDefinition
                {
                    Type = type,
                    Operator = type is "webpage.open" or "notification.present" ? "contains" : "equals",
                    Parameters = AutomationParameterReader.Create(("value", value)),
                },
            ],
        };
    }

    private async Task ConfirmDeleteAsync(AutomationTaskDefinition definition)
    {
        var content = new TextBlock
        {
            Text = $"删除“{definition.Name}”及其运行历史？",
            Foreground = TextBrush(),
            TextWrapping = TextWrapping.Wrap,
            MinWidth = 360,
        };
        if (await Dialog("删除任务", content, "删除", "取消").ShowAsync() != ContentDialogResult.Primary)
            return;
        await ExecuteUiActionAsync(() => _service.DeleteAsync(definition.Id), "任务已删除");
    }

    private async Task ExecuteUiActionAsync(Func<Task> action, string success)
    {
        try
        {
            await action();
            _notify(success, string.Empty, false);
        }
        catch (Exception ex)
        {
            _notify("任务操作失败", ex.Message, true);
        }
    }

    private FrostedDialog Dialog(string title, UIElement content, string? primary = null, string? cancel = null)
    {
        return new FrostedDialog(
            XamlRoot,
            title,
            content,
            new FrostedDialogPalette(
                IsDark ? Color.FromArgb(255, 30, 41, 59) : Colors.White,
                IsDark ? Color.FromArgb(255, 30, 41, 59) : Colors.White,
                CardBorderBrush(),
                TextBrush(),
                MutedBrush(),
                SurfaceBrush(),
                AltBrush(),
                PrimaryBrush(),
                new SolidColorBrush(Colors.White)),
            primary,
            cancel);
    }

    private bool MatchesFilter(AutomationTaskSnapshot snapshot) => _filter switch
    {
        "running" => snapshot.ActiveRun?.Status is AutomationRunStatus.Running or AutomationRunStatus.Queued,
        "paused" => snapshot.ActiveRun?.Status == AutomationRunStatus.Paused,
        "failed" => snapshot.LastRun?.Status == AutomationRunStatus.Failed,
        _ => true,
    };

    private static bool MatchesSearch(AutomationTaskSnapshot snapshot, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;
        return snapshot.Definition.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            snapshot.Definition.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            snapshot.Definition.DeviceId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            snapshot.Definition.Triggers.Any(trigger => trigger.Type.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private void ApplyFilterStates()
    {
        foreach (var pair in _filterButtons)
            StyleSegment(pair.Value, pair.Key == _filter);
    }

    private string DeviceName(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return "本机 / 无设备";
        return _devices().FirstOrDefault(device => device.DeviceId == deviceId)?.DisplayName ?? deviceId;
    }

    private static string TriggerSummary(AutomationTaskDefinition definition)
    {
        return string.Join(" · ", definition.Triggers.Select(trigger => trigger.Type.ToLowerInvariant() switch
        {
            "manual" => "手动",
            "once" => trigger.RunAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "单次",
            "daily" => $"每天 {trigger.At}",
            "weekly" => $"每周 {string.Join('/', trigger.Days.Select(DayLabel))} {trigger.At}",
            "interval" => IntervalLabel(trigger.IntervalSeconds),
            "cron" => $"Cron {trigger.Cron}",
            "condition" => $"条件 {string.Join("/", trigger.Conditions.Select(condition => condition.Type))}",
            _ => trigger.Type,
        }));
    }

    private static string DayLabel(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "一", DayOfWeek.Tuesday => "二", DayOfWeek.Wednesday => "三",
        DayOfWeek.Thursday => "四", DayOfWeek.Friday => "五", DayOfWeek.Saturday => "六", _ => "日",
    };

    private static string IntervalLabel(int seconds)
    {
        if (seconds % 86400 == 0) return $"每 {seconds / 86400} 天";
        if (seconds % 3600 == 0) return $"每 {seconds / 3600} 小时";
        if (seconds % 60 == 0) return $"每 {seconds / 60} 分钟";
        return $"每 {seconds} 秒";
    }

    private static (string Text, Color Color) StatusPresentation(AutomationTaskDefinition definition, AutomationRunRecord? run)
    {
        if (!definition.Enabled && run is null)
            return ("已停用", Colors.Gray);
        return run?.Status switch
        {
            AutomationRunStatus.Queued => ("等待运行", Colors.DeepSkyBlue),
            AutomationRunStatus.Running => ("正在运行", Colors.MediumSeaGreen),
            AutomationRunStatus.Paused => ("已暂停", Colors.Goldenrod),
            AutomationRunStatus.Succeeded => ("最近运行成功", Colors.MediumSeaGreen),
            AutomationRunStatus.Failed => ("最近运行失败", Colors.IndianRed),
            AutomationRunStatus.Stopped => ("最近运行已停止", Colors.Gray),
            AutomationRunStatus.Skipped => ("最近运行已跳过", Colors.DarkOrange),
            _ when !definition.Enabled => ("已停用", Colors.Gray),
            _ => ("等待触发", Colors.DeepSkyBlue),
        };
    }

    private static void AddMetric(Grid grid, string title, TextBlock value, Color color, int column)
    {
        value.Foreground = new SolidColorBrush(color);
        var card = Card(new StackPanel
        {
            Spacing = 5,
            Children =
            {
                new TextBlock { Text = title, Foreground = MutedBrush(), FontSize = 12 },
                value,
            },
        });
        Grid.SetColumn(card, column);
        grid.Children.Add(card);
    }

    private static TextBlock MetricValue() => new()
    {
        Text = "0",
        FontSize = 25,
        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
    };

    private static void AddMetadata(Grid grid, string label, string value, int column)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = label, Foreground = MutedBrush(), FontSize = 11 });
        panel.Children.Add(new TextBlock
        {
            Text = value,
            Foreground = SecondaryTextBrush(),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(panel, column);
        grid.Children.Add(panel);
    }

    private static Border Card(UIElement content) => new()
    {
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(16),
        Background = SurfaceBrush(),
        BorderBrush = CardBorderBrush(),
        BorderThickness = new Thickness(1),
        Child = content,
    };

    private static UIElement EmptyState(string text, string glyph) => new Border
    {
        MinHeight = 180,
        CornerRadius = new CornerRadius(8),
        BorderBrush = CardBorderBrush(),
        BorderThickness = new Thickness(1),
        Background = SurfaceBrush(),
        Child = new StackPanel
        {
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new FontIcon { Glyph = glyph, FontSize = 28, Foreground = MutedBrush() },
                new TextBlock { Text = text, Foreground = MutedBrush(), FontSize = 13 },
            },
        },
    };

    private static Button TextButton(string glyph, string text, bool primary)
    {
        var button = new Button
        {
            MinHeight = 38,
            Padding = new Thickness(13, 8, 13, 8),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 7,
                Children =
                {
                    new FontIcon { Glyph = glyph, FontSize = 13 },
                    new TextBlock { Text = text, FontSize = 13 },
                },
            },
        };
        StyleButton(button, primary ? PrimaryBrush() : SurfaceBrush(), primary ? new SolidColorBrush(Colors.White) : TextBrush(), primary ? PrimaryBrush() : CardBorderBrush());
        return button;
    }

    private static Button IconButton(string glyph, string tooltip, Brush foreground)
    {
        var button = new Button
        {
            Width = 34,
            Height = 34,
            MinWidth = 34,
            MinHeight = 34,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(0),
            Content = new FontIcon { Glyph = glyph, FontSize = 13 },
        };
        StyleButton(button, new SolidColorBrush(Colors.Transparent), foreground, new SolidColorBrush(Colors.Transparent));
        ToolTipService.SetToolTip(button, tooltip);
        AutomationProperties.SetName(button, tooltip);
        return button;
    }

    private static Button SegmentButton(string text, bool active)
    {
        var button = new Button
        {
            Content = text,
            MinHeight = 34,
            Padding = new Thickness(11, 6, 11, 6),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
        };
        StyleSegment(button, active);
        return button;
    }

    private static void StyleSegment(Button button, bool active) =>
        StyleButton(button, active ? PrimaryBrush() : SurfaceBrush(), active ? new SolidColorBrush(Colors.White) : SecondaryTextBrush(), active ? PrimaryBrush() : CardBorderBrush());

    private static void StyleButton(Button button, Brush background, Brush foreground, Brush border)
    {
        button.Background = background;
        button.Foreground = foreground;
        button.BorderBrush = border;
        button.Resources["ButtonBackgroundPointerOver"] = AltBrush();
        button.Resources["ButtonBackgroundPressed"] = HoverBrush();
        button.Resources["ButtonBorderBrushPointerOver"] = PrimaryBrush();
        button.Resources["ButtonBorderBrushPressed"] = PrimaryBrush();
    }

    private static void StyleTextBox(TextBox box, string placeholder)
    {
        box.PlaceholderText = placeholder;
        box.MinHeight = 38;
        box.Padding = new Thickness(11, 7, 11, 7);
        box.CornerRadius = new CornerRadius(8);
        box.Background = SurfaceBrush();
        box.Foreground = TextBrush();
        box.BorderBrush = CardBorderBrush();
        box.BorderThickness = new Thickness(1);
        box.PlaceholderForeground = MutedBrush();
        box.Resources["TextControlBackground"] = SurfaceBrush();
        box.Resources["TextControlBackgroundPointerOver"] = SurfaceBrush();
        box.Resources["TextControlBackgroundFocused"] = SurfaceBrush();
        box.Resources["TextControlBorderBrush"] = CardBorderBrush();
        box.Resources["TextControlBorderBrushPointerOver"] = PrimaryBrush();
        box.Resources["TextControlBorderBrushFocused"] = PrimaryBrush();
        box.Resources["TextControlForeground"] = TextBrush();
        box.Resources["TextControlPlaceholderForeground"] = MutedBrush();
    }

    private static T WithColumn<T>(T element, int column) where T : FrameworkElement
    {
        Grid.SetColumn(element, column);
        return element;
    }

    private static bool IsDark => Application.Current.RequestedTheme != ApplicationTheme.Light;
    private static SolidColorBrush TextBrush() => IsDark ? new(Color.FromArgb(255, 248, 250, 252)) : new(Color.FromArgb(255, 15, 23, 42));
    private static SolidColorBrush SecondaryTextBrush() => IsDark ? new(Color.FromArgb(255, 203, 213, 225)) : new(Color.FromArgb(255, 71, 85, 105));
    private static SolidColorBrush MutedBrush() => IsDark ? new(Color.FromArgb(255, 148, 163, 184)) : new(Color.FromArgb(255, 100, 116, 139));
    private static SolidColorBrush SurfaceBrush() => IsDark ? new(Color.FromArgb(232, 30, 41, 59)) : new(Color.FromArgb(238, 255, 255, 255));
    private static SolidColorBrush AltBrush() => IsDark ? new(Color.FromArgb(232, 51, 65, 85)) : new(Color.FromArgb(255, 226, 232, 240));
    private static SolidColorBrush HoverBrush() => IsDark ? new(Color.FromArgb(255, 71, 85, 105)) : new(Color.FromArgb(255, 203, 213, 225));
    private static SolidColorBrush CardBorderBrush() => IsDark ? new(Color.FromArgb(255, 51, 65, 85)) : new(Color.FromArgb(255, 203, 213, 225));
    private static SolidColorBrush PrimaryBrush() => new(Color.FromArgb(255, 34, 197, 94));
    private static SolidColorBrush DangerBrush() => IsDark ? new(Color.FromArgb(255, 252, 165, 165)) : new(Color.FromArgb(255, 185, 28, 28));
}
