using ADBControl.Desktop.Services;
using ADBControl.Desktop.Models;
using Microsoft.Data.Sqlite;
using System.IO.Compression;

await AutomationTests.RunAsync();

var services = AdbMdnsServiceParser.Parse("""
List of discovered mdns services
adb-DY5TEUGQ5PDE6TBY-crWn4l (2)  _adb-tls-connect._tcp  192.168.3.121:41119
adb-DY5TEUGQ5PDE6TBY-crWn4l      _adb-tls-connect._tcp  192.168.3.121:32807
studio-123456                    _adb-tls-pairing._tcp  192.168.3.121:37123
""");

Assert(services.Count == 3, "应解析三条 mDNS 服务记录。");
Assert(services[0].Endpoint == "192.168.3.121:41119", "应保留 TLS 连接端点。");
Assert(services[0].AdbSerial == "adb-DY5TEUGQ5PDE6TBY-crWn4l._adb-tls-connect._tcp", "应生成 adb devices 使用的 mDNS serial。");
Assert(services[0].StableDeviceId == "adb-DY5TEUGQ5PDE6TBY", "应去除瞬态 mDNS 后缀。");
Assert(services[0].IsConnectService, "TLS connect 服务应可用于自动连接。");
Assert(!services[2].IsConnectService, "配对服务不能用于自动连接。");

Console.WriteLine("AdbMdnsServiceParser tests passed.");

// 场景：两台无线设备并存时，旧配置中的错误 IP 不能覆盖当前 ADB 在线端点。
var pollutedDevice = new DeviceModel
{
    DeviceId = "192.168.3.121:45891",
    DisplayName = "22041211AC",
    ConnectionKind = "wireless",
    IpAddress = "192.168.3.168",
    Port = 45891,
    MdnsServiceId = "adb-DY5TEUGQ5PDE6TBY",
};
var onlineDevice = new DeviceModel
{
    DeviceId = "192.168.3.121:45891",
    DisplayName = "22041211AC",
    Brand = "rubens",
    Model = "22041211AC",
    IsConnected = true,
};
var otherPhoneServices = AdbMdnsServiceParser.Parse("""
List of discovered mdns services
adb-16a424c7-X9sGA6  _adb-tls-connect._tcp  192.168.3.168:44163
""");
DeviceService.ApplyOnlineDevice(pollutedDevice, onlineDevice, otherPhoneServices);
Assert(pollutedDevice.IpAddress == "192.168.3.121" && pollutedDevice.Port == 45891, "在线 ADB 端点必须修复被另一台设备污染的保存 IP。");
Assert(pollutedDevice.MdnsServiceId == "adb-DY5TEUGQ5PDE6TBY", "另一台设备的 mDNS 服务不能覆盖当前设备绑定。");
Assert(DeviceService.TryParseAdbEndpoint("[fd00::121]:5555", out var ipv6Host, out var ipv6Port) && ipv6Host == "[fd00::121]" && ipv6Port == 5555, "无线端点解析必须支持带方括号的 IPv6。");
Console.WriteLine("Multi-device endpoint isolation tests passed.");

// 场景：硬件采集必须优先返回 SoC 型号、每核频率和内存占用所需字段。
var snapshot = DeviceHardwareService.ParseSnapshot("""
brand=Google
model=Pixel 8
device=shiba
android=15
sdk=35
abi=arm64-v8a
cpu_model=Tensor G3
cpu_cores=8
cpu_freqs=cpu0:1200000,cpu1:1800000,cpu2:2500000,
cpu_max_freqs=cpu0:2400000,cpu1:2400000,cpu2:3000000,
mem_total_kb=8192000
mem_available_kb=3072000
swap_total_kb=4194304
swap_free_kb=3145728
gpu_usage=63
gpu_cur_freq=800000000
gpu_max_freq=1000000000
gpu_memory_bytes=646717440
battery_level=87
battery_status=2
battery_temp=315
refresh_rate=120
temperatures=soc:42500,battery:31500,
""");
Assert(snapshot.Value("cpu_model").Value == "Tensor G3", "SoC 型号必须优先显示设备属性返回值。");
Assert(snapshot.CpuFrequencies.Count == 3 && snapshot.CpuFrequencies[2].Kilohertz == 2500000, "必须解析每个 CPU 核心频率。");
Assert(snapshot.CpuFrequencyUsagePercent is > 70 and < 80, "必须按每核当前频率和最高频率计算 CPU 总体占用估算。");
Assert(snapshot.UsedMemoryKb == 5120000, "必须计算当前内存已用量。");
Assert(snapshot.MemoryUsagePercent is > 60 and < 70, "必须计算内存使用百分比。");
Assert(snapshot.SwapTotalKb == 4194304 && snapshot.ExtendedTotalMemoryKb == 12386304, "必须计入 Swap/ZRAM 虚拟内存扩展。");
Assert(snapshot.Gpu?.EffectiveUsagePercent == 63, "必须解析可读取的 GPU 占用率。");
Assert(snapshot.Gpu?.MemoryBytes == 646717440, "必须解析 GPU Service 返回的显存占用。");
var kilohertzGpu = DeviceHardwareService.ParseSnapshot("gpu_cur_freq=800000\ngpu_max_freq=1000000\ngpu_access=available");
Assert(kilohertzGpu.Gpu?.CurrentFrequencyHz == 800000000, "以 kHz 暴露的 GPU 频率必须统一换算为 Hz。");
var protectedGpu = DeviceHardwareService.ParseSnapshot("gpu_access=permission_denied\ngpu_memory_bytes=1024");
Assert(protectedGpu.Gpu is { EffectiveUsagePercent: null } protectedGpuTelemetry &&
    protectedGpuTelemetry.UnavailableReason.Contains("系统限制", StringComparison.Ordinal),
    "GPU 节点被 SELinux 拒绝时必须返回准确原因，不能误报连接异常。");
var fpsSnapshot = DeviceHardwareService.ParseSnapshot("app_package=com.example.game", appFps: 119.8);
Assert(fpsSnapshot.AppFps == 119.8 && fpsSnapshot.Value("app_fps").IsAvailable, "必须保存当前前台应用实际 FPS。");
Assert(snapshot.Temperatures.Count == 2 && snapshot.Temperatures[0].Celsius == 42.5, "必须将毫摄氏温度转换为摄氏温度。");
var thermalFallback = DeviceHardwareService.ParseSnapshot("thermal_service=CPU:41.952,GPU:41.871,");
Assert(thermalFallback.Temperatures.Count == 2 && thermalFallback.Temperatures[0].Celsius == 41.952, "sysfs 不可读时必须解析 thermalservice 温度。");
var android16Thermals = DeviceHardwareService.ParseSnapshot("thermal_service=GPU0:50.0,GPU1:48.8,CPU0:59.3,CPU1:58.1,battery:40.0,");
Assert(android16Thermals.GpuTemperatures.Count == 2 && android16Thermals.GpuTemperatures.Max(item => item.Celsius) == 50.0, "Android 16 多 GPU 传感器必须可聚合监控。");
Assert(android16Thermals.CpuTemperatures.Count == 2, "Android 16 每核 CPU 温度必须可识别。");
Assert(HardwareMonitorMetricSearch.Matches("GPU", "temperature.gpu", "GPU 温度", "°C"), "监控项搜索必须支持英文标识和标题。");
Assert(HardwareMonitorMetricSearch.Matches("温度", "temperature.gpu", "GPU 温度", "°C"), "监控项搜索必须支持中文标题。");
Assert(!HardwareMonitorMetricSearch.Matches("内存", "temperature.gpu", "GPU 温度", "°C"), "监控项搜索必须排除不匹配项目。");
var colorSettings = new AppSettings();
colorSettings.HardwareMonitorMetricColors["temperature.gpu"] = "#38BDF8";
var colorSettingsJson = System.Text.Json.JsonSerializer.Serialize(colorSettings);
var restoredColorSettings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(colorSettingsJson);
Assert(restoredColorSettings?.HardwareMonitorMetricColors["temperature.gpu"] == "#38BDF8", "监控卡颜色设置必须持久化。");
var hardwareLogEntry = HardwareMonitorLogger.FormatEntry(
    "192.168.3.168:44163",
    "sample",
    "capture.ok elapsed_ms=1800",
    DateTimeOffset.Parse("2026-07-24T06:30:00+08:00"));
Assert(
    hardwareLogEntry.Contains("[SAMPLE]", StringComparison.Ordinal) &&
    hardwareLogEntry.Contains("device=192.168.3.168:44163", StringComparison.Ordinal) &&
    hardwareLogEntry.Contains("elapsed_ms=1800", StringComparison.Ordinal),
    "硬件监控日志必须包含时间、级别、设备与采集详情。");
var hardwareLogMarker = $"test.marker={Guid.NewGuid():N}";
await HardwareMonitorLogger.AppendAsync(HardwareMonitorLogger.FormatEntry("test-device", "test", hardwareLogMarker));
Assert(
    File.Exists(HardwareMonitorLogger.LogPath) &&
    (await File.ReadAllTextAsync(HardwareMonitorLogger.LogPath)).Contains(hardwareLogMarker, StringComparison.Ordinal),
    "硬件监控日志必须真实写入本地 hardware-monitor.log。");

// 场景：锁屏无法确定时不能误认为已解锁，明确的 Keyguard 状态必须可识别。
Assert(DeviceLockService.ParseState("mKeyguardShowing=true") == DeviceLockState.Locked, "Keyguard=true 时必须识别为锁屏。");
Assert(DeviceLockService.ParseState("isStatusBarKeyguard=false") == DeviceLockState.Unlocked, "Keyguard=false 时必须识别为已解锁。");
Assert(DeviceLockService.ParseState("screenState=SCREEN_STATE_OFF isStatusBarKeyguard=false") == DeviceLockState.Locked, "屏幕关闭时不能因 Keyguard=false 被误判为已解锁。");
Assert(DeviceLockService.ParseState("Window state unavailable") == DeviceLockState.Unknown, "没有 Keyguard 字段时必须保持未知状态。");
Assert(DeviceLockService.TryParseScreenSize("Physical size: 1080x2400", out var width, out var height) && width == 1080 && height == 2400, "必须解析 wm size 输出。");

// 场景：无 PIN 设备仍必须执行唤醒和上滑，不能在上滑前因空 PIN 直接失败。
var swipeOnlyPlan = DeviceLockService.BuildUnlockCommands(1080, 2400, string.Empty);
Assert(swipeOnlyPlan.Count == 2, "无 PIN 解锁只能包含唤醒和上滑命令。");
Assert(swipeOnlyPlan[0].Contains("KEYCODE_WAKEUP", StringComparison.Ordinal), "无 PIN 解锁必须先唤醒设备。");
Assert(swipeOnlyPlan[1].Contains("input touchscreen swipe", StringComparison.Ordinal), "无 PIN 解锁必须执行触摸屏上滑。");
var pinPlan = DeviceLockService.BuildUnlockCommands(1080, 2400, "1234");
Assert(pinPlan.Count == 4 && pinPlan[2] == "input text 1234", "有 PIN 时必须在上滑后输入 PIN。");
Assert(
    DeviceLockService.IsInputInjectionDenied(new AdbCommandResult(
        255,
        string.Empty,
        "java.lang.SecurityException: Injecting input events requires the INJECT_EVENTS permission")),
    "MIUI/HyperOS 拒绝 ADB 输入注入时必须切换到伴侣 App 备用通道。");

// 场景：adb connect 的退出码为 0 不能单独证明连接成功，必须同时通过在线设备列表验证。
Assert(!AdbConnectionEvaluator.IsConnectCommandAccepted(new AdbCommandResult(0, "failed to connect to 192.168.3.2:5555", string.Empty)), "failed to connect 不能被识别为成功。");
Assert(AdbConnectionEvaluator.IsConnectCommandAccepted(new AdbCommandResult(0, "connected to 192.168.3.2:5555", string.Empty)), "connected to 应通过命令结果校验。");
Assert(AdbConnectionEvaluator.IsEndpointOnline("List of devices attached\n192.168.3.2:5555 device product:test", "192.168.3.2:5555"), "设备列表中的 device 状态才算在线。");
Assert(!AdbConnectionEvaluator.IsEndpointOnline("List of devices attached\n192.168.3.2:5555 offline", "192.168.3.2:5555"), "offline 设备不能被标记为在线。");
Assert(AdbConnectionEvaluator.IsEndpointOnline("List of devices attached\nadb-DY5-crWn._adb-tls-connect._tcp device product:test", "adb-DY5-crWn._adb-tls-connect._tcp"), "mDNS serial 的 device 状态必须被识别为在线。");

// 场景：新投屏只接受 scrcpy 参数，并在仓库内保留可构建的上游服务端源码。
Assert(ScrcpyVideoOptions.TryCreate(1280, 720, 500_000, 60, out var scrcpyOptions, out _), "scrcpy 投屏必须支持 0.5 Mbps。");
Assert(scrcpyOptions!.MaxSize == 1280, "scrcpy max_size 必须使用所选分辨率的长边。");
Assert(!ScrcpyVideoOptions.TryCreate(1280, 720, 499_999, 60, out _, out _), "低于 0.5 Mbps 的 scrcpy 码率必须被拒绝。");
Assert(!ScrcpyVideoOptions.TryCreate(1280, 720, 8_000_000, 121, out _, out _), "超过 120 FPS 的 scrcpy 帧率必须被拒绝。");
var projectionRunningControls = ProjectionControlStateEvaluator.Resolve(true, ownsActiveSession: true, isReconfiguring: false);
Assert(!projectionRunningControls.StartEnabled && projectionRunningControls.StopEnabled, "投屏运行中必须禁用开启并允许停止。");
var projectionReconfiguringControls = ProjectionControlStateEvaluator.Resolve(true, ownsActiveSession: false, isReconfiguring: true);
Assert(!projectionReconfiguringControls.StartEnabled && projectionReconfiguringControls.StopEnabled, "投屏参数重启期间必须保持停止按钮可用。");
var projectionStoppedControls = ProjectionControlStateEvaluator.Resolve(true, ownsActiveSession: false, isReconfiguring: false);
Assert(projectionStoppedControls.StartEnabled && !projectionStoppedControls.StopEnabled, "投屏完全停止后必须允许开启并禁用停止。");
Assert(ScrcpyIntegration.UpstreamVersion == "4.0", "桌面端与引入的 scrcpy 服务端版本必须固定一致。");
var scrcpySourceRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "third_party", "scrcpy"));
Assert(File.Exists(Path.Combine(scrcpySourceRoot, "LICENSE")), "仓库必须包含 scrcpy Apache-2.0 许可证。");
Assert(File.Exists(Path.Combine(scrcpySourceRoot, "server", "src", "main", "java", "com", "genymobile", "scrcpy", "Server.java")), "仓库必须直接包含 scrcpy 服务端源码。");
Console.WriteLine("scrcpy source integration and option tests passed.");

// 场景：监控记录必须导出为可打开的 Excel、HTML 和真实 SQLite 数据库。
var reportDirectory = Path.Combine(Path.GetTempPath(), "ADBControl", "hardware-report-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(reportDirectory);
var samples = new[]
{
    new HardwareMonitorSample(
        DateTimeOffset.Parse("2026-07-12T10:00:00+08:00"),
        new[] { new CpuCoreFrequency(0, 1200000, 2400000), new CpuCoreFrequency(1, 1800000, 2400000) },
        8192000,
        3072000,
        new[] { new HardwareTemperature("soc", 42.5) },
        120,
        4194304,
        3145728,
        new GpuTelemetry(63, 800000000, 1000000000, "test", 646717440),
        119.8),
};
var exporter = new HardwareReportExporter();
var xlsxPath = Path.Combine(reportDirectory, "hardware.xlsx");
var htmlPath = Path.Combine(reportDirectory, "hardware.html");
var sqlitePath = Path.Combine(reportDirectory, "hardware.sqlite");
await exporter.ExportAsync(HardwareReportFormat.Excel, samples, xlsxPath);
await exporter.ExportAsync(HardwareReportFormat.Html, samples, htmlPath);
await exporter.ExportAsync(HardwareReportFormat.Sqlite, samples, sqlitePath);
using (var archive = ZipFile.OpenRead(xlsxPath))
    Assert(archive.GetEntry("xl/worksheets/sheet1.xml") is not null, "Excel 导出必须包含工作表。");
Assert((await File.ReadAllTextAsync(htmlPath)).Contains("CPU 各核心频率", StringComparison.Ordinal), "HTML 导出必须包含监控表头。");
Assert((await File.ReadAllTextAsync(htmlPath)).Contains("119.8 FPS", StringComparison.Ordinal), "HTML 导出必须包含当前应用 FPS。");
using (var connection = new SqliteConnection($"Data Source={sqlitePath}"))
{
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM hardware_samples";
    Assert(Convert.ToInt32(command.ExecuteScalar()) == 1, "SQLite 导出必须保存监控样本。");
    command.CommandText = "SELECT swap_total_kb FROM hardware_samples LIMIT 1";
    Assert(Convert.ToInt64(command.ExecuteScalar()) == 4194304, "SQLite 导出必须保存虚拟内存字段。");
    command.CommandText = "SELECT app_fps FROM hardware_samples LIMIT 1";
    Assert(Math.Abs(Convert.ToDouble(command.ExecuteScalar()) - 119.8) < 0.01, "SQLite 导出必须保存当前应用 FPS。");
}
SqliteConnection.ClearAllPools();
Directory.Delete(reportDirectory, recursive: true);
Console.WriteLine("Hardware monitoring parser and exporter tests passed.");

// 场景：不同 Android dumpsys 格式和 Companion 快照都必须解析出 App 名称。
var adbLabels = PackageLabelParser.ParseAdbPackageLabels("""
Packages:
  Package [com.example.alpha] (a1b2c3):
    application-label:Alpha App
  Package [com.example.beta] (d4e5f6):
    application-label:en-US=Beta App
""");
Assert(adbLabels["com.example.alpha"] == "Alpha App", "必须解析普通 application-label 字段。");
Assert(adbLabels["com.example.beta"] == "Beta App", "必须解析带 locale 的 application-label 字段。");
var companionLabels = PackageLabelParser.ParseCompanionPackageLabels("""
Broadcast completed: result=0
data={"requestId":"1","result":{"apps":[{"packageName":"com.example.gamma","label":"Gamma App"}]}}
""");
Assert(companionLabels["com.example.gamma"] == "Gamma App", "必须解析 Companion 命令快照中的应用名称。");
Console.WriteLine("Package label parser tests passed.");

// 场景：终端必须标出命令、选项、字符串与错误输出，不能把所有内容显示成同一种颜色。
var terminalTokens = TerminalSyntaxHighlighter.Tokenize("dumpsys package --checkin 'com.example.app' | grep label");
Assert(terminalTokens[0].Kind == TerminalTokenKind.Command, "终端必须高亮首个 Shell 命令。");
Assert(terminalTokens.Any(token => token.Kind == TerminalTokenKind.Option), "终端必须高亮命令选项。");
Assert(terminalTokens.Any(token => token.Kind == TerminalTokenKind.String), "终端必须高亮字符串参数。");
Assert(terminalTokens.Any(token => token.Kind == TerminalTokenKind.Operator), "终端必须高亮管道操作符。");
Assert(TerminalSyntaxHighlighter.IsErrorLine("Error: permission denied"), "终端必须识别错误输出行。");
Console.WriteLine("Terminal syntax highlighter tests passed.");

if (Environment.GetEnvironmentVariable("ADB_CONTROL_HARDWARE_INTEGRATION") == "1")
{
    var deviceId = Environment.GetEnvironmentVariable("ADB_CONTROL_DEVICE_ID")
        ?? throw new InvalidOperationException("ADB_CONTROL_DEVICE_ID is required for hardware integration.");
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var hardwareSnapshot = await new DeviceHardwareService(new AdbService()).CollectAsync(deviceId);
    stopwatch.Stop();
    Console.WriteLine($"Hardware integration measurement: {stopwatch.ElapsedMilliseconds} ms; FPS={hardwareSnapshot.AppFps?.ToString("0.##") ?? "n/a"}.");
    Assert(hardwareSnapshot.GpuTemperatures.Count > 0, "目标设备必须通过客户端采集链路返回 GPU 温度。");
    Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(5), "单次硬件采样不应阻塞超过 5 秒。");
    Console.WriteLine($"Hardware integration passed: {hardwareSnapshot.GpuTemperatures.Count} GPU sensors in {stopwatch.ElapsedMilliseconds} ms.");
}

if (Environment.GetEnvironmentVariable("ADB_CONTROL_INTEGRATION") == "1")
{
    var settings = new SettingsService();
    settings.Load();
    var adb = new AdbService();
    var devices = new DeviceService(settings, adb);
    var device = devices.Devices.SingleOrDefault(saved => saved.ConnectionKind == "wireless")
        ?? throw new InvalidOperationException("需要至少一台已保存的无线 ADB 设备来执行集成测试。");
    var refresh = await devices.RefreshConnectivityAsync();
    Assert(refresh.Success, "设备在线时刷新连接状态必须成功。");
    Assert(device.IsConnected, "adb devices -l 已列出设备时，保存设备必须标记为在线。");
    var result = await devices.ConnectSavedWirelessDeviceAsync(device);

    Assert(result.Success || result.Stdout.Contains("connected to", StringComparison.OrdinalIgnoreCase), "保存的无线设备应通过 mDNS 候选成功连接。");
    Assert(device.IsConnected, "连接成功后设备状态必须为在线。");
    Assert(!string.IsNullOrWhiteSpace(device.MdnsServiceId), "mDNS 成功连接后必须保存稳定设备标识。");
    Console.WriteLine($"Wireless ADB integration test passed: {device.DeviceId} ({device.MdnsServiceId}).");
}

if (Environment.GetEnvironmentVariable("ADB_CONTROL_QUIC_INTEGRATION") == "1")
{
    const int quicPort = 15038;
    var deviceId = Environment.GetEnvironmentVariable("ADB_CONTROL_DEVICE_ID")
        ?? throw new InvalidOperationException("ADB_CONTROL_DEVICE_ID is required for QUIC integration.");
    await using var quicServer = new CompanionQuicServer(quicPort);
    await quicServer.StartAsync();
    var adb = new AdbService();
    var companion = new CompanionAppService(adb, quicServer);
    var device = new DeviceModel { DeviceId = deviceId, DisplayName = deviceId, IsConnected = true };
    var passiveReconnect = Environment.GetEnvironmentVariable("ADB_CONTROL_QUIC_PASSIVE_RECONNECT_INTEGRATION") == "1";
    if (passiveReconnect)
    {
        Console.WriteLine("Waiting for the companion service to reconnect from its saved QUIC configuration...");
    }
    else if (Environment.GetEnvironmentVariable("ADB_CONTROL_QUIC_RECONNECT_INTEGRATION") == "1")
    {
        var open = await companion.OpenAsync(device);
        Assert(open.Success, $"打开伴侣 App 必须成功：{open.Stderr}");
    }
    else
    {
        var configure = await companion.ConfigureConnectionAsync(device, quicPort);
        Assert(configure.Success, $"向真机下发 QUIC 配置必须成功：{configure.Stderr}");
    }
    var session = await quicServer.WaitForDeviceAsync(deviceId, TimeSpan.FromSeconds(20));
    Assert(session.IsConnected, "伴侣 App 必须完成 hello/helloAck 并进入 QUIC READY 状态。");
    var status = await session.SendCommandAsync(
        "android.accessibility.control",
        "accessibility.status",
        new Dictionary<string, object?>(),
        TimeSpan.FromSeconds(5));
    Assert(
        status.GetProperty("payload").GetProperty("ok").GetBoolean(),
        "QUIC commandRequest 必须收到伴侣 App commandResponse。");
    var connectionMode = passiveReconnect
        ? "passive saved-config reconnect"
        : Environment.GetEnvironmentVariable("ADB_CONTROL_QUIC_RECONNECT_INTEGRATION") == "1"
            ? "app-open saved-config reconnect"
            : "initial configure";
    Console.WriteLine($"Companion QUIC control integration passed ({connectionMode}): {deviceId}.");

    var stopProjection = await session.SendCommandAsync(
        "android.screen.projection",
        "projection.stop",
        new Dictionary<string, object?>(),
        TimeSpan.FromSeconds(5));
    Assert(
        stopProjection.GetProperty("payload").GetProperty("ok").GetBoolean(),
        "projection.stop 必须返回成功响应。");
    var statusAfterProjectionStop = await session.SendCommandAsync(
        "android.accessibility.control",
        "accessibility.status",
        new Dictionary<string, object?>(),
        TimeSpan.FromSeconds(5));
    Assert(
        statusAfterProjectionStop.GetProperty("payload").GetProperty("ok").GetBoolean() && session.IsConnected,
        "停止投屏后伴侣 App QUIC 控制连接必须继续可用。");
    Console.WriteLine("Projection stop keeps the companion QUIC control session alive.");

    if (Environment.GetEnvironmentVariable("ADB_CONTROL_PROJECTION_INTEGRATION") == "1")
    {
        await using var projection = new CompanionProjectionSession(quicServer);
        Console.WriteLine("Projection consent requested on device; waiting for the first H.264 packet...");
        var start = await projection.StartAsync(
            deviceId,
            new ScrcpyVideoOptions(720, 1280, 2_000_000, 30));
        if (Environment.GetEnvironmentVariable("ADB_CONTROL_EXPECT_PROJECTION_DENIED") == "1")
        {
            Assert(!start.Success, "锁屏状态下系统取消授权时，伴侣 App 投屏不得假装启动成功。");
            Assert(
                start.Stderr.Contains("授权", StringComparison.Ordinal) ||
                start.Stderr.Contains("录制", StringComparison.Ordinal) ||
                start.Stderr.Contains("锁屏", StringComparison.Ordinal) ||
                start.Stderr.Contains("解锁", StringComparison.Ordinal),
                $"投屏拒绝必须返回明确授权错误，实际为：{start.Stderr}");
            Console.WriteLine($"Companion projection denial propagation passed: {start.Stderr}");
            return;
        }
        Assert(start.Success, $"伴侣 App 投屏必须启动成功：{start.Stderr}");
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (projection.ReceivedFrames == 0 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(50);
        Assert(projection.ReceivedFrames > 0, "伴侣 App QUIC 视频流必须收到至少一个 H.264 画面包。");
        Console.WriteLine($"Companion projection integration passed: {projection.FrameSize.Width}x{projection.FrameSize.Height}, received {projection.ReceivedFrames} frame(s).");
    }
}

if (Environment.GetEnvironmentVariable("ADB_CONTROL_SCRCPY_INTEGRATION") == "1")
{
    var deviceId = Environment.GetEnvironmentVariable("ADB_CONTROL_DEVICE_ID")
        ?? throw new InvalidOperationException("ADB_CONTROL_DEVICE_ID is required for scrcpy integration.");
    var adb = new AdbService();
    await using var companionServer = new CompanionQuicServer(15039);
    await using var projection = new ProjectionSession(adb, companionServer);
    var start = await projection.StartAsync(
        deviceId,
        new ScrcpyVideoOptions(1280, 720, 2_000_000, 30),
        adbAvailable: true);
    Assert(start.Success, $"APK 内置 scrcpy 投屏必须启动成功：{start.Stderr}");
    Assert(projection.Backend == ProjectionBackend.AdbScrcpy, "ADB 可用时必须选择 scrcpy 后端。");
    Assert(projection.FrameSize.Width > 0 && projection.FrameSize.Height > 0, "scrcpy 必须返回真实画面尺寸。");
    var sourceSize = projection.FrameSize;

    var firstFrame = new TaskCompletionSource<ScrcpyDecodedFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
    projection.StartDecoding(
        () => (288, 640),
        frame => firstFrame.TrySetResult(frame));
    var decoded = await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(15));
    Assert(decoded.Width == 288 && decoded.Height == 640, "FFmpeg 必须按桌面渲染目标尺寸输出 BGRA 帧。");
    Assert(decoded.Bgra.Length == decoded.Width * decoded.Height * 4, "解码帧必须包含完整 BGRA 像素数据。");
    await projection.StopAsync();
    Assert(projection.Backend == ProjectionBackend.None, "停止投屏后编排器必须释放后端状态。");
    Console.WriteLine($"Projection orchestrator + bundled scrcpy + FFmpeg decode passed: {sourceSize.Width}x{sourceSize.Height} -> {decoded.Width}x{decoded.Height} BGRA.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
