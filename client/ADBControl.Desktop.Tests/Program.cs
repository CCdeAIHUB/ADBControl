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

// Scenario: reconnecting one offline device must never delay a foreground operation on another known-online device.
await VerifyForegroundConnectivityIsolationAsync();
await VerifyMdnsBoundEndpointPolicyAsync();
Assert(DeviceConnectivityPolicy.CanUseCachedOnlineState(true), "已知在线设备必须直接放行，不能重复执行连接探测。");
Assert(!DeviceConnectivityPolicy.CanUseCachedOnlineState(false), "未知或离线设备必须经过目标探测后才能执行操作。");
Assert(!DeviceConnectivityPolicy.ShouldUseSavedEndpointFallback(hasStableMdnsBinding: true, explicitConnection: false), "mDNS 绑定设备的后台连接不能回退过期端口。");
Assert(DeviceConnectivityPolicy.ShouldUseSavedEndpointFallback(hasStableMdnsBinding: true, explicitConnection: true), "用户显式连接必须保留保存端点回退能力。");

// 场景：详情页先以离线状态创建、随后刷新为在线时，ADB 工具必须立即可用，不能保留旧的禁用快照。
Assert(!DeviceDetailRefreshPolicy.CanUseAdbTool(requiresAdb: true, adbConnected: false), "ADB 离线时不能执行依赖 ADB 的工具。");
Assert(DeviceDetailRefreshPolicy.CanUseAdbTool(requiresAdb: true, adbConnected: true), "ADB 状态刷新为在线后必须允许使用工具。");
Assert(DeviceDetailRefreshPolicy.CanUseAdbTool(requiresAdb: false, adbConnected: false), "不依赖 ADB 的工具不应被 ADB 状态禁用。");
Assert(DeviceDetailRefreshPolicy.ShouldShowDrawerClose(fullScreen: true, panelVisible: true), "全屏控制板展开后必须在面板内部显示关闭入口。");
Assert(!DeviceDetailRefreshPolicy.ShouldShowDrawerClose(fullScreen: false, panelVisible: true), "普通详情布局不能显示全屏抽屉关闭入口。");
Assert(!DeviceDetailRefreshPolicy.ShouldShowDrawerClose(fullScreen: true, panelVisible: false), "控制板收起后不应保留抽屉关闭入口。");
// 场景：伴侣 App 安装等详情长操作期间，连接状态的瞬时变化不能重建或退出当前详情界面。
Assert(!DeviceDetailRefreshPolicy.ShouldRebuild(videoActive: false, adbStateChanged: true, companionStateChanged: true, detailOperationActive: true), "详情长操作期间必须抑制自动重建。");
Assert(DeviceDetailRefreshPolicy.ShouldRebuild(videoActive: false, adbStateChanged: true, companionStateChanged: false, detailOperationActive: false), "详情空闲时仍需响应真实 ADB 状态变化。");
// 场景：离开设备详情时，无论通过哪种入口，都必须退出设备预览全屏样式。
Assert(DeviceDetailRefreshPolicy.ShouldExitFullScreen(fullScreen: true, enteringDeviceDetail: false), "离开设备详情必须退出全屏。");
Assert(!DeviceDetailRefreshPolicy.ShouldExitFullScreen(fullScreen: true, enteringDeviceDetail: true), "同一详情重建时必须保持全屏。");
// 场景：安装伴侣 App 必须从立即可见的检查阶段开始，并按顺序进入安装、配置和成功终态。
var successfulInstallGateway = new FakeCompanionInstallGateway();
var successfulInstallProgress = new List<CompanionInstallStatus>();
var successfulInstall = await new CompanionInstallWorkflow(successfulInstallGateway, new NullCompanionInstallLogger()).RunAsync(
    new DeviceModel { DeviceId = "test-device", DisplayName = "Test", IsConnected = true },
    15038,
    successfulInstallProgress.Add);
Assert(successfulInstall.Success && successfulInstall.PackageInstalled && successfulInstall.CompanionConnected, "完整安装流程必须返回成功并更新连接结果。");
Assert(successfulInstallProgress.Select(item => item.Stage).SequenceEqual(new[]
{
    CompanionInstallStage.CheckingDevice,
    CompanionInstallStage.InstallingPackage,
    CompanionInstallStage.ConfiguringConnection,
    CompanionInstallStage.WaitingForConnection,
    CompanionInstallStage.Succeeded,
}), "安装进度必须按受控状态机顺序发布，不能跳过用户可见阶段。");
Assert(successfulInstallProgress.All(item => !string.IsNullOrWhiteSpace(item.TraceId)), "每个安装阶段必须携带 traceId。");
Assert(successfulInstallProgress.Single(item => item.Stage == CompanionInstallStage.InstallingPackage).Message.Contains("手机", StringComparison.Ordinal), "安装阶段必须提示用户留意手机端确认。");

// 场景：Android 拒绝 ADB 安装时必须失败并给出稳定错误码，不能只显示原始 adb 文本或沉默返回。
var restrictedGateway = new FakeCompanionInstallGateway
{
    InstallResult = new AdbCommandResult(1, string.Empty, "Failure [INSTALL_FAILED_USER_RESTRICTED: Install canceled by user]"),
};
var restrictedProgress = new List<CompanionInstallStatus>();
var restrictedInstall = await new CompanionInstallWorkflow(restrictedGateway, new NullCompanionInstallLogger()).RunAsync(
    new DeviceModel { DeviceId = "restricted-device", DisplayName = "Restricted", IsConnected = true },
    15038,
    restrictedProgress.Add);
Assert(!restrictedInstall.Success && restrictedInstall.Error?.ErrorCode == "COMPANION_INSTALL_USER_RESTRICTED", "设备拒绝安装必须返回专用错误码。");
Assert(restrictedInstall.Error is { Recoverable: true } && restrictedInstall.Error.Suggestion.Contains("安装", StringComparison.Ordinal), "设备策略拒绝必须提供可执行的恢复建议。");
Assert(restrictedProgress[^1].Stage == CompanionInstallStage.Failed, "安装失败必须发布可持久显示的失败终态。");
var installLogLine = CompanionInstallLogger.FormatEntry(
    restrictedProgress[^1],
    "restricted-device",
    DateTimeOffset.Parse("2026-08-12T22:47:59+08:00"));
Assert(installLogLine.Contains("COMPANION_INSTALL_USER_RESTRICTED", StringComparison.Ordinal) &&
    installLogLine.Contains(restrictedInstall.TraceId, StringComparison.Ordinal) &&
    installLogLine.Contains("elapsedMs", StringComparison.Ordinal),
    "安装日志必须包含错误码、traceId 和阶段耗时。");
Assert(!installLogLine.Contains("restricted-device", StringComparison.Ordinal), "安装日志不能写入原始设备 ID。");
// 场景：APK 已安装后的配置或连接探测异常，也必须进入失败终态，不能因非法状态转换导致 UI 事件崩溃。
var connectionExceptionGateway = new FakeCompanionInstallGateway
{
    ResponsiveException = new IOException("simulated connection probe failure"),
};
var connectionExceptionProgress = new List<CompanionInstallStatus>();
var connectionExceptionResult = await new CompanionInstallWorkflow(connectionExceptionGateway, new NullCompanionInstallLogger()).RunAsync(
    new DeviceModel { DeviceId = "exception-device", DisplayName = "Exception", IsConnected = true },
    15038,
    connectionExceptionProgress.Add);
Assert(!connectionExceptionResult.Success && connectionExceptionResult.Error?.ErrorCode == "COMPANION_INSTALL_UNEXPECTED", "连接探测异常必须转换为结构化安装失败。");
Assert(connectionExceptionProgress[^1].Stage == CompanionInstallStage.Failed, "连接探测异常必须发布失败终态。");
// 场景：APK 已安装但连接配置失败时，安装结果仍须保留成功事实，且以可恢复警告结束，不能回滚卸载。
var configurationWarningGateway = new FakeCompanionInstallGateway
{
    ConfigureResult = new AdbCommandResult(1, string.Empty, "configuration failed"),
};
var configurationWarningProgress = new List<CompanionInstallStatus>();
var configurationWarningResult = await new CompanionInstallWorkflow(configurationWarningGateway, new NullCompanionInstallLogger()).RunAsync(
    new DeviceModel { DeviceId = "warning-device", DisplayName = "Warning", IsConnected = true },
    15038,
    configurationWarningProgress.Add);
Assert(configurationWarningResult.Success && configurationWarningResult.PackageInstalled && !configurationWarningResult.CompanionConnected, "连接配置失败不能抹掉 APK 已安装事实。");
Assert(configurationWarningProgress[^1].Stage == CompanionInstallStage.CompletedWithWarning &&
    configurationWarningResult.Error?.ErrorCode == "COMPANION_CONFIGURE_FAILED", "连接配置失败必须进入可恢复警告终态。");

// 场景：常见 ADB 安装失败必须有稳定分类，避免不同设备再次退化成原始命令文本。
Assert(CompanionInstallFailureClassifier.FromInstallResult(
    new AdbCommandResult(1, string.Empty, "INSTALL_FAILED_UPDATE_INCOMPATIBLE"), "trace").ErrorCode == "COMPANION_INSTALL_SIGNATURE_MISMATCH", "签名冲突必须使用专用错误码。");
Assert(CompanionInstallFailureClassifier.FromInstallResult(
    new AdbCommandResult(-1, string.Empty, "adb 命令执行超时（180 秒）"), "trace").ErrorCode == "COMPANION_INSTALL_TIMEOUT", "安装超时必须使用专用错误码。");
Assert(CompanionInstallFailureClassifier.FromInstallResult(
    new AdbCommandResult(1, string.Empty, "error: device offline"), "trace").ErrorCode == "COMPANION_INSTALL_DEVICE_UNAVAILABLE", "安装中断线必须使用专用错误码。");
await new CompanionInstallLogger().WriteAsync(restrictedProgress[^1], "restricted-device");
var persistedInstallLog = await File.ReadAllTextAsync(CompanionInstallLogger.LogPath);
Assert(persistedInstallLog.Contains(restrictedInstall.TraceId, StringComparison.Ordinal), "安装状态必须真实写入 companion-install.log。");
Assert(!persistedInstallLog.Contains("restricted-device", StringComparison.Ordinal), "落盘安装日志不能包含原始设备 ID。");

// 场景：Windows 多行命令进入 Android shell 前必须统一为 LF，厂商 shell 不能收到 do\r 等非法 token。
var normalizedShellCommand = AdbService.NormalizeShellCommand("echo first\r\nfor value in one two; do\r\n  echo $value\rdone\r\n");
Assert(
    normalizedShellCommand == "echo first\nfor value in one two; do\n  echo $value\ndone\n" &&
    !normalizedShellCommand.Contains('\r'),
    "ADB shell 边界必须同时归一化 CRLF 和孤立 CR，不能把 Windows 换行发送给 Android。");

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
var groupedTemperatures = HardwareTemperaturePresentation.Group(android16Thermals.Temperatures);
Assert(
    groupedTemperatures.Select(group => group.Title).SequenceEqual(new[] { "处理器", "图形处理器", "电池" }),
    "硬件温度必须按处理器、图形处理器和电池分组，不能拼成一段长文本。");
Assert(
    groupedTemperatures.SelectMany(group => group.Items).Count() == android16Thermals.Temperatures.Count,
    "温度分组不能丢失厂商暴露的有效传感器。");
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
// 场景：HyperOS/MIUI 使用 KeyguardServiceDelegate 和数字 deviceLocked，亮屏锁屏时仍必须识别。
var hyperOsLockedState = """
KeyguardServiceDelegate
  showing=true
  showingAndNotOccluded=true
  screenState=SCREEN_STATE_ON
  interactiveState=INTERACTIVE_STATE_AWAKE
  KeyguardStateMonitor
    mIsShowing=true
User "机主" (id=0) (current): trusted=0, deviceLocked=1
""";
Assert(DeviceLockService.ParseState(hyperOsLockedState) == DeviceLockState.Locked, "HyperOS 亮屏锁屏字段必须识别为锁屏。");
// 场景：ColorOS/AOSP 风格的 delegate 与当前用户 trust 都明确为 false 时必须识别为已解锁。
var colorOsUnlockedState = """
KeyguardServiceDelegate state:
  showing=false
  showingAndNotOccluded=false
  screenState=SCREEN_STATE_ON
  interactiveState=INTERACTIVE_STATE_AWAKE
User "Owner" (id=0) (current): trusted=1, deviceLocked=0
""";
Assert(DeviceLockService.ParseState(colorOsUnlockedState) == DeviceLockState.Unlocked, "ColorOS/AOSP 已解锁字段必须识别为已解锁。");
// 场景：工作资料用户仍锁定时，必须以标记为 current 的前台用户为准，不能产生跨用户误报。
var multiUserUnlockedState = """
User "Work" (id=10): trusted=0, deviceLocked=1
User "Owner" (id=0) (current): trusted=1, deviceLocked=0
""";
Assert(DeviceLockService.ParseState(multiUserUnlockedState) == DeviceLockState.Unlocked, "多用户设备必须以 current 用户锁定状态为准。");
Assert(DeviceLockService.ParseState("mWakefulness=Dozing") == DeviceLockState.Locked, "息屏显示或 Doze 状态必须识别为锁屏。");
using (var companionLockedResponse = System.Text.Json.JsonDocument.Parse("""
{"payload":{"ok":true,"result":{"state":"locked","isInteractive":true,"isKeyguardLocked":true,"isDeviceLocked":true}}}
"""))
{
    Assert(
        DeviceLockService.TryParseCompanionState(companionLockedResponse.RootElement, out var companionLockState) &&
        companionLockState == DeviceLockState.Locked,
        "Companion KeyguardManager 锁屏响应必须优先识别为锁屏。");
}
using (var companionUnlockedResponse = System.Text.Json.JsonDocument.Parse("""
{"payload":{"ok":true,"result":{"state":"unlocked","isInteractive":true,"isKeyguardLocked":false,"isDeviceLocked":false}}}
"""))
{
    Assert(
        DeviceLockService.TryParseCompanionState(companionUnlockedResponse.RootElement, out var companionLockState) &&
        companionLockState == DeviceLockState.Unlocked,
        "Companion KeyguardManager 已解锁响应必须识别为已解锁。");
}
// 场景：详情页进入实时投屏后，独立状态监控仍必须观察到后续 Unlocked -> Locked 迁移。
var lockSequenceSource = new SequenceDeviceLockStateSource(DeviceLockState.Unlocked, DeviceLockState.Locked);
await using (var lockMonitor = new DeviceLockStateMonitor(lockSequenceSource, TimeSpan.FromMilliseconds(10)))
{
    var observedStates = new System.Collections.Concurrent.ConcurrentQueue<DeviceLockState>();
    var lockedObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    lockMonitor.StateChanged += (_, args) =>
    {
        observedStates.Enqueue(args.State);
        if (args.State == DeviceLockState.Locked)
            lockedObserved.TrySetResult();
    };
    lockMonitor.Start("projection-device");
    await lockedObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
    Assert(observedStates.Contains(DeviceLockState.Unlocked) && observedStates.Contains(DeviceLockState.Locked), "状态监控必须持续发布运行中的锁屏迁移。");
    Assert(lockMonitor.GetCurrentState("projection-device") == DeviceLockState.Locked, "状态监控必须保留当前设备的最新锁屏状态。");
    lockMonitor.Stop();
    Assert(!lockMonitor.IsRunning, "离开设备详情页后状态监控必须停止。");
}
var lockedProjectionPresentation = DeviceDetailRefreshPolicy.ResolvePreviewLockPresentation(DeviceLockState.Locked, projectionActive: true);
Assert(lockedProjectionPresentation.ShowLockOverlay && !lockedProjectionPresentation.ShowScreenshot, "实时投屏期间检测到锁屏时必须显示解锁覆盖层并隐藏旧截图。");
var unlockedProjectionPresentation = DeviceDetailRefreshPolicy.ResolvePreviewLockPresentation(DeviceLockState.Unlocked, projectionActive: true);
Assert(!unlockedProjectionPresentation.ShowLockOverlay && !unlockedProjectionPresentation.ShowScreenshot, "实时投屏解锁后必须隐藏覆盖层且不能让旧截图盖住视频。");
var unlockedScreenshotPresentation = DeviceDetailRefreshPolicy.ResolvePreviewLockPresentation(DeviceLockState.Unlocked, projectionActive: false);
Assert(!unlockedScreenshotPresentation.ShowLockOverlay && unlockedScreenshotPresentation.ShowScreenshot, "非投屏状态解锁后必须恢复截图预览。");
var disconnectedPreviewPresentation = DeviceDetailRefreshPolicy.ResolvePreviewPresentation(
    adbConnected: false,
    companionConnected: false,
    DeviceLockState.Unknown,
    projectionActive: false);
Assert(
    disconnectedPreviewPresentation.Overlay == DevicePreviewOverlay.ConnectionRequired &&
    !disconnectedPreviewPresentation.ShowScreenshot &&
    !disconnectedPreviewPresentation.ShowUnlockActions &&
    disconnectedPreviewPresentation.Hint.Contains("无线调试", StringComparison.Ordinal),
    "ADB 与伴侣均未连接时必须优先提示设备未连接和无线调试，不能显示锁屏状态未知。");
var companionProjectionLockedPresentation = DeviceDetailRefreshPolicy.ResolvePreviewPresentation(
    adbConnected: false,
    companionConnected: true,
    DeviceLockState.Locked,
    projectionActive: true);
Assert(
    companionProjectionLockedPresentation.Overlay == DevicePreviewOverlay.Locked &&
    companionProjectionLockedPresentation.ShowUnlockActions,
    "ADB 离线但伴侣投屏可用时仍必须显示真实锁屏状态和解锁动作。");
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

// Scenario: operation or permission errors must not corrupt a still-valid transport state.
Assert(!AdbConnectionEvaluator.IsDeviceUnavailable(new AdbCommandResult(1, string.Empty, "java.lang.SecurityException: Permission denial")), "权限失败不能被误判为设备离线。");
Assert(!AdbConnectionEvaluator.IsDeviceUnavailable(new AdbCommandResult(1, string.Empty, "Unknown package: com.example.missing")), "业务命令失败不能被误判为设备离线。");
Assert(!AdbConnectionEvaluator.IsDeviceUnavailable(new AdbCommandResult(127, string.Empty, "/system/bin/sh: device_config: not found")), "设备内命令缺失不能被误判为 ADB 设备离线。");
Assert(AdbConnectionEvaluator.IsDeviceUnavailable(new AdbCommandResult(1, string.Empty, "error: device offline")), "ADB transport offline 必须标记设备离线。");
Assert(AdbConnectionEvaluator.IsDeviceUnavailable(new AdbCommandResult(1, string.Empty, "adb: device 'serial' not found")), "ADB 找不到目标序列号时必须标记设备离线。");
var connectionLog = DeviceConnectionLogger.FormatEntry(
    new DeviceConnectionLogEntry("trace-test", "raw-device-id", "foreground-target-probe", 123, 7, "offline", "DEVICE_CONNECTION_ENDPOINT_UNAVAILABLE"),
    new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.Zero));
Assert(connectionLog.Contains("trace-test", StringComparison.Ordinal) && connectionLog.Contains("\"elapsedMs\":123", StringComparison.Ordinal), "连接诊断必须包含 traceId 与阶段耗时。");
Assert(!connectionLog.Contains("raw-device-id", StringComparison.Ordinal), "连接诊断不能记录原始设备 ID。");

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

// 场景：平板截图尺寸大于活动视频尺寸时，触控必须使用视频坐标，不能被在途截图覆盖。
var tabletScreenshotSpace = new PreviewCoordinateSpace(1600, 2560);
var tabletVideoSpace = new PreviewCoordinateSpace(800, 1280);
var activeTabletSpace = PreviewCoordinateMapper.ResolveActiveSpace(tabletScreenshotSpace, tabletVideoSpace, projectionActive: true);
Assert(activeTabletSpace == tabletVideoSpace, "活动投屏必须使用投屏帧尺寸，不能使用平板原生截图尺寸。");
Assert(
    !PreviewCoordinateMapper.ResolveActiveSpace(tabletScreenshotSpace, default, projectionActive: true).IsValid,
    "投屏已激活但帧尺寸尚未就绪时必须拒绝触控，不能回退到旧截图坐标。");
Assert(
    PreviewCoordinateMapper.ResolveActiveSpace(tabletScreenshotSpace, tabletVideoSpace, projectionActive: false) == tabletScreenshotSpace,
    "截图预览模式必须继续使用截图原始尺寸。");
Assert(
    PreviewCoordinateMapper.TryMapUniform(1000, 1000, activeTabletSpace, 500, 500, out var tabletCenter) &&
    tabletCenter.X == 400 && tabletCenter.Y == 640,
    "平板预览中心必须映射到 800x1280 视频帧中心。");
Assert(!PreviewCoordinateMapper.TryMapUniform(1000, 1000, activeTabletSpace, 100, 500, out _), "点击 Uniform 左侧黑边不能注入设备。");
Assert(!PreviewCoordinateMapper.TryMapUniform(double.NaN, 1000, activeTabletSpace, 500, 500, out _), "非有限预览尺寸必须被拒绝。");
Assert(
    PreviewCoordinateMapper.TryMapUniform(1000, 1000, activeTabletSpace, 812.49, 999.99, out var tabletEdge) &&
    tabletEdge.X == 799 && tabletEdge.Y == 1279,
    "视频内容右下边缘必须稳定映射到最后一个有效像素。");
Assert(PreviewInteractionPolicy.IsTapGesture(100, 100, 104, 103), "点击判定必须使用桌面坐标，不能因平板像素倍率把轻微抖动变成滑动。");
Assert(!PreviewInteractionPolicy.IsTapGesture(100, 100, 107, 100), "超过桌面点击容差的移动必须识别为滑动。");
var companionCenter = PreviewCoordinateMapper.ScaleToSpace(tabletCenter, tabletScreenshotSpace);
Assert(companionCenter.X == 800 && companionCenter.Y == 1280, "伴侣投屏必须把编码帧中心缩放到真实平板屏幕中心。");
var companionEdge = PreviewCoordinateMapper.ScaleToSpace(tabletEdge, tabletScreenshotSpace);
Assert(companionEdge.X == 1599 && companionEdge.Y == 2559, "伴侣端坐标换算必须保持两个坐标空间的边缘像素一致。");
var touchPacket = ScrcpyControlMessageEncoder.EncodeTouch(0, tabletEdge, pointerId: 1);
Assert(
    System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(touchPacket.AsSpan(10, 4)) == 799 &&
    System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(touchPacket.AsSpan(18, 2)) == 800,
    "scrcpy 控制消息必须携带生成坐标时的视频宽高。");
var releaseTouchPacket = ScrcpyControlMessageEncoder.EncodeTouch(1, tabletCenter, pointerId: 1);
Assert(
    releaseTouchPacket[1] == 1 &&
    System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(releaseTouchPacket.AsSpan(22, 2)) == 0,
    "预览失去指针捕获时必须发送零压力 ACTION_UP，不能留下未闭合触控。");
var rotatedTabletPoint = new ProjectionTouchPosition(640, 400, new PreviewCoordinateSpace(1280, 800));
Assert(
    PreviewCoordinateMapper.ResolveGesturePosition(tabletCenter, rotatedTabletPoint) == tabletCenter,
    "手势期间坐标空间变化时必须保留最后一个同空间坐标，不能把一次手势跨旋转发送。");
var rejectedOutOfRangeTouch = false;
try
{
    ScrcpyControlMessageEncoder.EncodeTouch(0, new ProjectionTouchPosition(1301, 2208, tabletVideoSpace), pointerId: 1);
}
catch (ArgumentOutOfRangeException)
{
    rejectedOutOfRangeTouch = true;
}
Assert(rejectedOutOfRangeTouch, "scrcpy 越界坐标必须被拒绝，不能静默钳制到屏幕边缘。");
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
// 场景：部分 OEM 的 am broadcast 会给未转义 JSON 再包一层双引号，仍必须读取设备本地 App 名称。
var oemQuotedCompanionLabels = PackageLabelParser.ParseCompanionPackageLabels("""
Broadcast completed: result=0, data="{"requestId":"2","ok":true,"result":{"apps":[{"packageName":"com.example.oem","label":"OEM App"}]}}"
""");
Assert(
    oemQuotedCompanionLabels.TryGetValue("com.example.oem", out var oemAppLabel) && oemAppLabel == "OEM App",
    "OEM 广播输出额外包裹双引号时不能丢弃本地 App 名称并错误依赖联网查询。");
// 场景：伴侣端分页元数据必须携带设备本地标签和真实 PNG 图标，同时兼容旧版无图标响应。
const string onePixelPngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
var metadataPayload = """
Broadcast completed: result=0, data="{"requestId":"3","ok":true,"result":{"apps":[{"packageName":"com.example.icon","label":"Icon App","enabled":true,"system":false,"sourceDir":"/data/app/icon/base.apk","iconPngBase64":"__ICON_PNG__"},{"packageName":"com.example.invalid","label":"Invalid Icon","enabled":true,"system":false,"iconPngBase64":"bm90LWEtcG5n"}],"count":2,"total":375,"offset":64,"includeSystem":true,"limit":64}}"
""".Replace("__ICON_PNG__", onePixelPngBase64, StringComparison.Ordinal);
Assert(
    PackageLabelParser.TryParseCompanionPackagePage(metadataPayload, out var metadataPage, out var metadataError),
    $"有效应用元数据页必须可解析：{metadataError}");
var iconMetadata = metadataPage.Apps.Single(app => app.PackageName == "com.example.icon");
Assert(
    iconMetadata.IconPng is not null && iconMetadata.IconPng.SequenceEqual(Convert.FromBase64String(onePixelPngBase64)),
    "软件列表必须接收 APK 对应的真实 PNG 图标字节。");
Assert(
    metadataPage.InvalidIconCount == 1 &&
    metadataPage.Apps.Single(app => app.PackageName == "com.example.invalid").IconPng is null,
    "单个损坏图标只能被隔离，不能丢弃同页 App 名称或伪装成有效图标。");
Assert(metadataPage.NextOffset == 66 && metadataPage.HasMore, "分页结果必须根据实际返回数量继续读取，不能假定固定页长。");
Assert(
    PackageLabelParser.TryParseCompanionPackagePage(
        """data={"result":{"apps":[{"packageName":"com.example.legacy","label":"Legacy App"}],"count":1,"total":1,"offset":0,"limit":64}}""",
        out var legacyMetadataPage,
        out _) &&
    legacyMetadataPage.Apps.Single().IconPng is null &&
    !legacyMetadataPage.HasMore,
    "新版桌面端必须兼容旧伴侣版本不含图标字段的响应。");
Console.WriteLine("Package label parser tests passed.");

// 场景：ADB 已确认的包名必须按受控页长交给伴侣端显式查询，不能依赖 OEM 的全量枚举行为。
var catalogPackageNames = Enumerable.Range(0, 66).Select(index => $"com.example.app{index:000}").ToArray();
var packageCatalogGateway = new FakeDevicePackageCatalogGateway(catalogPackageNames, onePixelPngBase64);
var packageCatalogResult = await new DevicePackageCatalogService(
    packageCatalogGateway,
    new EmptyPackageNameLookup()).LoadAsync(
        new DeviceModel { DeviceId = "catalog-device", DisplayName = "Catalog", IsConnected = true });
Assert(packageCatalogResult.Success, $"应用目录必须成功：{packageCatalogResult.FatalError?.Message}");
Assert(
    packageCatalogGateway.MetadataRequests.Count == 2 &&
    packageCatalogGateway.MetadataRequests.All(request => request.Count <= 64),
    "应用元数据查询必须按最多 64 个包分页，避免 Android 广播 Binder 超限。");
Assert(
    packageCatalogGateway.MetadataRequests.SelectMany(request => request).SequenceEqual(catalogPackageNames),
    "每个 ADB 包名必须按原顺序恰好显式查询一次，不能遗漏、重复或改用 OEM 全量枚举。");
Assert(
    packageCatalogResult.Packages.Count == 66 &&
    packageCatalogResult.Packages.All(package => package.HasResolvedDisplayName) &&
    packageCatalogResult.Packages.All(package => package.IconPng is not null),
    "目录服务必须合并每页的设备本地 App 名称和真实图标。");
// 场景：无线 ADB 页读取发生明确 transport 中断时必须只重试一次，不能留下半份应用目录。
var transientPackageGateway = new FakeDevicePackageCatalogGateway(catalogPackageNames, onePixelPngBase64)
{
    TransientMetadataFailuresRemaining = 1,
};
var recoveredPackageCatalog = await new DevicePackageCatalogService(
    transientPackageGateway,
    new EmptyPackageNameLookup()).LoadAsync(
        new DeviceModel { DeviceId = "transient-catalog-device", DisplayName = "Transient", IsConnected = true });
Assert(
    recoveredPackageCatalog.Packages.All(package => package.HasResolvedDisplayName && package.IconPng is not null) &&
    transientPackageGateway.MetadataAttemptCount == 3,
    "明确的 device offline 只能重试失败页一次，并在恢复后继续剩余分页。");

Assert(PackageNameResolver.ParseStoreTitle("<html><head><meta itemprop=\"name\" content=\"示例应用\"></head></html>") == "示例应用", "联网标签解析必须读取商店页面的应用名称元数据。");
Assert(PackageNameResolver.ParseStoreTitle("<title>Example App - Google Play 上的应用</title>") == "Example App", "联网标签解析必须清理 Google Play 标题后缀。");
// 场景：OEM 广播早于 goAsync 快照落盘返回时，只允许轮询尚未出现的结果文件。
Assert(
    CompanionAppService.ShouldRetryCommandResultSnapshot(
        new AdbCommandResult(1, string.Empty, "cat: result.json: No such file or directory"),
        attempt: 0,
        maxAttempts: 8),
    "结果文件尚未原子落盘时必须短时轮询。");
Assert(
    CompanionAppService.ShouldRetryCommandResultSnapshot(
        new AdbCommandResult(0, string.Empty, string.Empty),
        attempt: 1,
        maxAttempts: 8),
    "cat 成功但快照仍为空时必须短时轮询。");
Assert(
    !CompanionAppService.ShouldRetryCommandResultSnapshot(
        new AdbCommandResult(1, string.Empty, "Permission denied"),
        attempt: 0,
        maxAttempts: 8) &&
    !CompanionAppService.ShouldRetryCommandResultSnapshot(
        new AdbCommandResult(1, string.Empty, "No such file or directory"),
        attempt: 7,
        maxAttempts: 8),
    "权限错误或达到上限后禁止继续轮询，不能掩盖真实失败。");

Assert(DeviceHardwareService.IsTransientHardwareFailure(new AdbCommandResult(1, string.Empty, "error: device offline")), "设备离线应识别为可重试的硬件采集错误。");
Assert(!DeviceHardwareService.IsTransientHardwareFailure(new AdbCommandResult(1, string.Empty, "syntax error: unexpected token")), "采集命令语法错误不能通过重试掩盖。");

// 场景：终端必须标出命令、选项、字符串与错误输出，不能把所有内容显示成同一种颜色。
var terminalTokens = TerminalSyntaxHighlighter.Tokenize("dumpsys package --checkin 'com.example.app' | grep label");
Assert(terminalTokens[0].Kind == TerminalTokenKind.Command, "终端必须高亮首个 Shell 命令。");
Assert(terminalTokens.Any(token => token.Kind == TerminalTokenKind.Option), "终端必须高亮命令选项。");
Assert(terminalTokens.Any(token => token.Kind == TerminalTokenKind.String), "终端必须高亮字符串参数。");
Assert(terminalTokens.Any(token => token.Kind == TerminalTokenKind.Operator), "终端必须高亮管道操作符。");
Assert(TerminalSyntaxHighlighter.IsErrorLine("Error: permission denied"), "终端必须识别错误输出行。");
Console.WriteLine("Terminal syntax highlighter tests passed.");

// 场景：AI 在未打开详情页时必须掌握全局设备清单，只有具体设备操作才要求选择目标。
var knownAiDevices = new[]
{
    new DeviceModel { DeviceId = "phone-1", DisplayName = "主力手机", Model = "Phone", IsConnected = true },
    new DeviceModel { DeviceId = "tablet-2", DisplayName = "客厅平板", Model = "Tablet", IsConnected = false },
};
var globalDeviceContext = AiConversationPolicy.BuildDeviceContext(currentDevice: null, knownDevices: knownAiDevices);
Assert(
    globalDeviceContext.Contains("主力手机", StringComparison.Ordinal) &&
    globalDeviceContext.Contains("客厅平板", StringComparison.Ordinal) &&
    globalDeviceContext.Contains("ADB 已连接", StringComparison.Ordinal) &&
    globalDeviceContext.Contains("ADB 未连接", StringComparison.Ordinal) &&
    !globalDeviceContext.Contains("需要进入设备详情页", StringComparison.Ordinal),
    "无详情页时 AI 上下文仍必须列出全部已知设备及连接状态。");
var interactionRules = AiConversationPolicy.BuildInteractionRules(allowInteractiveChoices: true);
Assert(
    interactionRules.Contains("单选", StringComparison.Ordinal) &&
    interactionRules.Contains("多选", StringComparison.Ordinal) &&
    interactionRules.Contains("新一轮对话", StringComparison.Ordinal),
    "AI 必须明确只在单选或多选时使用内联提问，文字回答应开启新一轮对话。");

var inventory = new FakeAiDeviceInventory(knownAiDevices);
var explicitTarget = AiDeviceTargetResolver.Resolve(inventory, currentDevice: null, requestedDeviceId: "tablet-2");
Assert(explicitTarget.Success && explicitTarget.Device?.DisplayName == "客厅平板", "具体操作必须能通过 deviceId 选择非当前详情设备。");
var selectionRequired = AiDeviceTargetResolver.Resolve(inventory, currentDevice: null, requestedDeviceId: null);
Assert(!selectionRequired.Success && selectionRequired.ErrorCode == "DEVICE_SELECTION_REQUIRED", "无当前设备且未指定目标时必须返回稳定的待选择错误码。");
var aiAdb = new AdbService();
var deviceListTools = new AiAgentToolService(aiAdb, new CompanionAppService(aiAdb), automation: null, inventory: inventory);
var deviceListResult = await deviceListTools.ExecuteAsync(
    new AiAgentToolCall { Id = "device-list", Name = "device_list", ArgumentsJson = "{}" },
    "请求批准",
    currentDevice: null,
    (_, _) => Task.FromResult(false));
Assert(
    deviceListResult.Success &&
    deviceListResult.Content.Contains("主力手机", StringComparison.Ordinal) &&
    deviceListResult.Content.Contains("客厅平板", StringComparison.Ordinal),
    "device_list 必须是不依赖当前详情页的全局只读工具。");

// 场景：内联 AI 提问只接受 2-8 个选项的单选或多选，不能借此等待任意文字输入。
var singleChoiceCall = new AiAgentToolCall
{
    Id = "choice-single",
    Name = "ask_user_choice",
    ArgumentsJson = """{"question":"选择设备","selectionMode":"single","options":["主力手机","客厅平板"]}""",
};
Assert(
    AiChoiceRequestParser.TryParse(singleChoiceCall, out var singleChoice, out _, out _) &&
    singleChoice is { SelectionMode: AiChoiceSelectionMode.Single } &&
    singleChoice.Options.Count == 2,
    "有效单选提问必须被解析为可渲染请求。");
var invalidTextChoiceCall = new AiAgentToolCall
{
    Id = "choice-text",
    Name = "ask_user_choice",
    ArgumentsJson = """{"question":"请输入名称","selectionMode":"text","options":["任意"]}""",
};
Assert(
    !AiChoiceRequestParser.TryParse(invalidTextChoiceCall, out _, out var invalidChoiceCode, out _) &&
    invalidChoiceCode == "AI_CHOICE_MODE_INVALID",
    "内联提问不得接受自由文字模式。");

// 场景：DeepSeek 文本端点不得收到 image_url；未知兼容端点返回对应 400 时可识别并受控重试。
var deepSeekProfile = AiProviderCompatibility.Resolve(new AiModelSettings
{
    ApiUrl = "https://api.deepseek.com",
    ModelId = "deepseek-v4-flash",
});
Assert(!deepSeekProfile.SupportsImageContent, "已确认只接受 text 的 DeepSeek 模型必须禁用图像内容块。");
var providerMessages = new[]
{
    new AiConversationMessage
    {
        Role = "user",
        Text = "检查当前画面",
        Attachments = [new AiAttachment { Name = "screen.png", Path = "ignored", IsImage = true }],
    },
};
var textOnlyMessages = AiProviderCompatibility.PrepareMessages(providerMessages, deepSeekProfile, out var omittedImageCount);
Assert(
    omittedImageCount == 1 &&
    textOnlyMessages[0].Attachments.Count == 0 &&
    textOnlyMessages[0].Text.Contains("图像", StringComparison.Ordinal),
    "文本模型请求必须移除图像附件并显式留下兼容性说明。");
const string unsupportedImageBody = """{"error":{"message":"messages[10]: unknown variant image_url, expected text","type":"invalid_request_error","code":"invalid_request_error"}}""";
Assert(AiProviderCompatibility.IsImageContentUnsupported(400, unsupportedImageBody), "供应商 image_url 反序列化 400 必须识别为图像能力不兼容。");

// 场景：HTTP/网络失败必须映射为稳定分类，并保留可展开诊断而不是把原始正文塞进普通消息。
var imageFailure = AiRequestFailureClassifier.FromHttp(400, "Bad Request", unsupportedImageBody, "trace-image");
Assert(
    imageFailure.ErrorCode == "AI_REQUEST_IMAGE_UNSUPPORTED" &&
    imageFailure.Category == AiRequestFailureCategory.Compatibility &&
    imageFailure.Detail.Contains("image_url", StringComparison.Ordinal),
    "图像不兼容 400 必须返回兼容性分类和完整可诊断详情。");
Assert(
    AiRequestFailureClassifier.FromHttp(401, "Unauthorized", "{}", "trace-auth").ErrorCode == "AI_AUTHENTICATION_FAILED",
    "401 必须分类为鉴权失败。");
Assert(
    AiRequestFailureClassifier.FromHttp(429, "Too Many Requests", "{}", "trace-rate") is { ErrorCode: "AI_RATE_LIMITED", Recoverable: true },
    "429 必须分类为可恢复的限流错误。");
Assert(
    AiRequestFailureClassifier.FromHttp(503, "Unavailable", "{}", "trace-provider").ErrorCode == "AI_PROVIDER_UNAVAILABLE",
    "5xx 必须分类为供应商服务不可用。");
Assert(
    AiRequestFailureClassifier.FromException(new HttpRequestException("network down"), "trace-network").ErrorCode == "AI_NETWORK_FAILED",
    "网络异常必须分类为网络连接失败。");
Assert(
    AiRequestFailureClassifier.FromException(new TaskCanceledException("timeout"), "trace-timeout", timedOut: true).ErrorCode == "AI_REQUEST_TIMEOUT",
    "请求超时必须与用户取消区分并返回稳定错误码。");
var aiLogLine = AiRequestLogger.FormatEntry(
    new AiRequestLogEntry("trace-image", "api.deepseek.com", "deepseek-v4-flash", "response", 400, imageFailure.ErrorCode, "invalid_request_error", "invalid_request_error", "unknown image_url", 321),
    DateTimeOffset.Parse("2026-08-15T19:31:22+08:00"));
Assert(
    aiLogLine.Contains("trace-image", StringComparison.Ordinal) &&
    aiLogLine.Contains("AI_REQUEST_IMAGE_UNSUPPORTED", StringComparison.Ordinal) &&
    aiLogLine.Contains("\"elapsedMs\":321", StringComparison.Ordinal) &&
    !aiLogLine.Contains("apiKey", StringComparison.OrdinalIgnoreCase) &&
    !aiLogLine.Contains("prompt", StringComparison.OrdinalIgnoreCase),
    "AI 诊断日志必须包含 traceId、错误码和耗时，且结构上不得记录密钥或对话正文。");

var persistedAiCardsJson = System.Text.Json.JsonSerializer.Serialize(new[]
{
    new AiChatMessage
    {
        Kind = AiChatMessageKinds.Error,
        Role = "AI",
        Error = imageFailure.ToChatError(),
    },
    new AiChatMessage
    {
        Kind = AiChatMessageKinds.Choice,
        Role = "AI",
        Choice = new AiChoiceRequest
        {
            ToolCallId = "choice-persisted",
            Question = "选择设备",
            SelectionMode = AiChoiceSelectionMode.Multiple,
            Options = ["主力手机", "客厅平板"],
            SelectedOptions = ["客厅平板"],
            IsSubmitted = true,
        },
    },
});
var persistedAiCards = System.Text.Json.JsonSerializer.Deserialize<List<AiChatMessage>>(persistedAiCardsJson);
Assert(
    persistedAiCards is { Count: 2 } &&
    persistedAiCards[0].Error?.TraceId == "trace-image" &&
    persistedAiCards[1].Choice is { IsSubmitted: true, SelectionMode: AiChoiceSelectionMode.Multiple } &&
    persistedAiCards[1].Choice!.SelectedOptions.SequenceEqual(new[] { "客厅平板" }),
    "AI 错误卡和已提交选择卡必须可完整持久化并在重启后恢复。");
// 场景：Windows 原生滚轮消息必须正确解码方向、滚动内容，并在边界交给父级容器。
var positiveWheelWParam = new IntPtr(((long)unchecked((ushort)(short)120)) << 16);
var negativeWheelWParam = new IntPtr(((long)unchecked((ushort)(short)-120)) << 16);
Assert(MouseWheelScrollPolicy.DecodeDelta(positiveWheelWParam) == 120, "滚轮向上必须解码为正 delta。");
Assert(MouseWheelScrollPolicy.DecodeDelta(negativeWheelWParam) == -120, "滚轮向下必须解码为负 delta。");
Assert(
    MouseWheelScrollPolicy.TryCalculateTarget(verticalOffset: 64, scrollableHeight: 500, delta: 120, out var upwardTarget) && upwardTarget == 0,
    "滚轮向上必须减小垂直偏移。");
Assert(
    MouseWheelScrollPolicy.TryCalculateTarget(verticalOffset: 0, scrollableHeight: 500, delta: -120, out var downwardTarget) && downwardTarget == 64,
    "滚轮向下必须增大垂直偏移。");
Assert(
    !MouseWheelScrollPolicy.TryCalculateTarget(verticalOffset: 0, scrollableHeight: 500, delta: 120, out _),
    "内层滚动容器已到边界时必须返回未处理，让父级继续滚动。");
Assert(
    !MouseWheelScrollPolicy.ShouldScheduleDeferredFallback(
        isOutermostNativeDispatch: true,
        offsetBeforeDefaultHandling: 64,
        offsetAfterDefaultHandling: 128),
    "系统默认处理已经改变偏移时不得再次兜底滚动。");
Assert(
    MouseWheelScrollPolicy.ShouldScheduleDeferredFallback(
        isOutermostNativeDispatch: true,
        offsetBeforeDefaultHandling: 64,
        offsetAfterDefaultHandling: 64),
    "系统默认处理未改变偏移时必须异步兜底滚动。");
Assert(
    MouseWheelScrollPolicy.ShouldScheduleDeferredFallback(
        isOutermostNativeDispatch: true,
        offsetBeforeDefaultHandling: null,
        offsetAfterDefaultHandling: null),
    "消息到达时尚未命中滚动容器也必须在事件分发后重试。");
Assert(
    !MouseWheelScrollPolicy.ShouldScheduleDeferredFallback(
        isOutermostNativeDispatch: false,
        offsetBeforeDefaultHandling: 64,
        offsetAfterDefaultHandling: 64),
    "同一滚轮消息经过嵌套子窗口钩子时只能由最外层调度一次兜底。");
// 场景：WinUI 替换窗口过程后，刷新必须根据真实 WndProc 重装，而不是相信陈旧字典状态。
Assert(
    MouseWheelScrollPolicy.ShouldInstallNativeHook((nint)1, (nint)2, (nint)3),
    "当前 WndProc 与目标代理不同时必须重新安装原生滚轮钩子。");
Assert(
    !MouseWheelScrollPolicy.ShouldInstallNativeHook((nint)1, (nint)3, (nint)3),
    "当前 WndProc 已是目标代理时不得重复安装原生滚轮钩子。");
Assert(
    !MouseWheelScrollPolicy.ShouldInstallNativeHook((nint)1, nint.Zero, (nint)3),
    "无法读取当前 WndProc 时不得盲目覆盖窗口过程。");
// 场景：窗口过程必须先保留 WinUI 默认处理，再异步兜底，不能重新引入同步吞消息实现。
var mainWindowSourcePath = Path.GetFullPath(Path.Combine(
    Directory.GetCurrentDirectory(),
    "client",
    "ADBControl.Desktop",
    "Views",
    "MainWindow.cs"));
var mainWindowSource = (await File.ReadAllTextAsync(mainWindowSourcePath)).Replace("\r\n", "\n", StringComparison.Ordinal);
Assert(
    mainWindowSource.Contains("return ForwardWindowMessage(hwnd, message, wParam, lParam);", StringComparison.Ordinal),
    "原生滚轮窗口过程必须调用原窗口过程，保留 WinUI 和子 HWND 默认处理。");
Assert(
    mainWindowSource.Contains("var isWheelMessage = message is (WmMouseWheel or WmPointerWheel);", StringComparison.Ordinal) &&
    mainWindowSource.Contains("\"native-message-received\"", StringComparison.Ordinal),
    "原生滚轮消息必须始终进入可观测的去重兜底流程。");
Assert(
    mainWindowSource.Contains("var currentProc = GetWindowLongPtr(hwnd, GwlWndProc);", StringComparison.Ordinal) &&
    !mainWindowSource.Contains("_hookedWndProcs.ContainsKey(hwnd)", StringComparison.Ordinal),
    "刷新原生滚轮钩子必须核对当前 WndProc，不能依赖可能陈旧的句柄字典。");
Assert(
    !mainWindowSource.Contains("if (TryRouteNativeWheel(screenPoint, delta))\n                        return IntPtr.Zero;", StringComparison.Ordinal),
    "原生滚轮窗口过程不得同步滚动后返回 0 吞掉消息。");
Assert(
    mainWindowSource.Contains("DispatcherQueue.TryEnqueue(() => ApplyDeferredWheelFallback(fallback))", StringComparison.Ordinal) &&
    mainWindowSource.Contains("FindElementsInHostCoordinates(point, _root).FirstOrDefault()", StringComparison.Ordinal),
    "滚轮兜底必须延迟派发，并且只从最上层视觉命中目标开始路由。");
Assert(
    mainWindowSource.Contains("_root.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnRootPointerWheelChanged), true)", StringComparison.Ordinal) &&
    mainWindowSource.Contains("viewer.AddHandler(UIElement.PointerWheelChangedEvent, wheelHandler, true)", StringComparison.Ordinal) &&
    !mainWindowSource.Contains("InputPointerSource.GetForIsland", StringComparison.Ordinal),
    "桌面窗口必须使用已验证的 XAML handled-events-too 路由，不得重新创建无事件输入的 InputPointerSource。");
// 场景：部分 Windows 输入栈不会把物理滚轮送入 XAML 或目标 WndProc，必须有进程范围低级输入观察器。
Assert(
    MouseWheelScrollPolicy.ShouldObserveLowLevelWheel(0, 0x020A, -120, targetProcessId: 42, currentProcessId: 42),
    "光标位于本进程窗口时必须观察低级垂直滚轮输入。");
Assert(
    !MouseWheelScrollPolicy.ShouldObserveLowLevelWheel(0, 0x020A, -120, targetProcessId: 7, currentProcessId: 42) &&
    !MouseWheelScrollPolicy.ShouldObserveLowLevelWheel(-1, 0x020A, -120, targetProcessId: 42, currentProcessId: 42),
    "低级滚轮观察器不得接管其他进程或无效钩子事件。");
Assert(
    mainWindowSource.Contains("_lowLevelWheelInput.Start()", StringComparison.Ordinal) &&
    mainWindowSource.Contains("ScheduleLowLevelWheelFallback", StringComparison.Ordinal),
    "主窗口必须启动独立低级滚轮观察器，并通过延迟偏移检查兜底。");

var aiServiceSourcePath = Path.GetFullPath(Path.Combine(
    Directory.GetCurrentDirectory(), "client", "ADBControl.Desktop", "Services", "AiService.cs"));
var aiServiceSource = (await File.ReadAllTextAsync(aiServiceSourcePath)).Replace("\r\n", "\n", StringComparison.Ordinal);
Assert(
    aiServiceSource.Contains("Task.Run(() => ReadStreamingAssistantAsync", StringComparison.Ordinal),
    "AI SSE 读取和 JSON 分片解析必须脱离 UI 线程。");
var longThinkingPreview = AiStreamRenderPolicy.BuildThinkingPreview(new string('思', 5000), 800);
Assert(
    longThinkingPreview.Length <= 802 && longThinkingPreview.StartsWith("…", StringComparison.Ordinal),
    "流式思考预览必须有长度上限，同时保留最新内容。");
Assert(AiStreamRenderPolicy.FlushDelay >= TimeSpan.FromMilliseconds(80), "AI 流式 UI 刷新频率必须受控，不能按每个 token 强制布局。");
Assert(
    !mainWindowSource.Contains("_messageList.UpdateLayout();", StringComparison.Ordinal) &&
    !mainWindowSource.Contains("_aiMessageScroller.UpdateLayout();", StringComparison.Ordinal),
    "AI 自动滚动不得循环强制同步布局，否则生成期间会阻塞整个窗口。");

var automationPageSourcePath = Path.GetFullPath(Path.Combine(
    Directory.GetCurrentDirectory(), "client", "ADBControl.Desktop", "Views", "AutomationTaskPage.cs"));
var automationPageSource = (await File.ReadAllTextAsync(automationPageSourcePath)).Replace("\r\n", "\n", StringComparison.Ordinal);
Assert(
    automationPageSource.Contains("Text = \"任务定义 JSON\"", StringComparison.Ordinal) &&
    automationPageSource.Contains("AutomationTaskSerializer.Serialize(existing)", StringComparison.Ordinal),
    "编辑现有任务时必须明确显示完整 JSON 定义，包括 AI 创建的任务。");
Assert(
    mainWindowSource.Contains("terminal.AddHandler(UIElement.KeyDownEvent, terminalKeyHandler, true)", StringComparison.Ordinal),
    "终端必须接收 TextBox 已处理的 Enter 路由事件。");
var interactiveSurfaceSourcePath = Path.GetFullPath(Path.Combine(
    Directory.GetCurrentDirectory(), "client", "ADBControl.Desktop", "Controls", "InteractiveSurface.cs"));
var interactiveSurfaceSource = (await File.ReadAllTextAsync(interactiveSurfaceSourcePath)).Replace("\r\n", "\n", StringComparison.Ordinal);
Assert(
    interactiveSurfaceSource.Contains("preserveContentForeground", StringComparison.Ordinal) &&
    mainWindowSource.Contains("preserveContentForeground: true", StringComparison.Ordinal),
    "文件列表交互表面必须保留文件夹和文件各自的图标颜色。");
Console.WriteLine("AI context, compatibility, failures, choices, and global wheel policy tests passed.");

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

if (Environment.GetEnvironmentVariable("ADB_CONTROL_PACKAGE_INTEGRATION") == "1")
{
    var deviceId = Environment.GetEnvironmentVariable("ADB_CONTROL_DEVICE_ID")
        ?? throw new InvalidOperationException("ADB_CONTROL_DEVICE_ID is required for package integration.");
    var adb = new AdbService();
    var device = new DeviceModel { DeviceId = deviceId, DisplayName = "Package Integration", IsConnected = true };
    var catalog = await new DevicePackageCatalogService(
        new DevicePackageCatalogGateway(adb, new CompanionAppService(adb)),
        new EmptyPackageNameLookup()).LoadAsync(device);
    Assert(catalog.Success, $"真机应用目录必须成功：{catalog.FatalError?.Message}");
    Console.WriteLine($"Package integration diagnostics: packages={catalog.Packages.Count}, labels={catalog.ResolvedDisplayNameCount}, icons={catalog.IconCount}, issues={string.Join(",", catalog.Issues.Select(issue => issue.ErrorCode))}.");
    Assert(catalog.Packages.Count > 0, "真机应用目录必须返回软件包。");
    Assert(catalog.ResolvedDisplayNameCount == catalog.Packages.Count, "每个真机软件包都必须由本机 PackageManager 返回 App 名称。");
    Assert(catalog.IconCount == catalog.Packages.Count, "每个真机软件包都必须返回可验证的真实 PNG 图标。");
    Console.WriteLine($"Package integration passed: {catalog.Packages.Count} labels and {catalog.IconCount} icons.");
}

if (Environment.GetEnvironmentVariable("ADB_CONTROL_LOCK_INTEGRATION") == "1")
{
    var deviceId = Environment.GetEnvironmentVariable("ADB_CONTROL_DEVICE_ID")
        ?? throw new InvalidOperationException("ADB_CONTROL_DEVICE_ID is required for lock state integration.");
    var adb = new AdbService();
    var adbState = await new DeviceLockService(adb).GetStateAsync(deviceId);
    Assert(adbState != DeviceLockState.Unknown, "真机 ADB 多信号锁屏查询必须返回明确状态。");

    var device = new DeviceModel { DeviceId = deviceId, DisplayName = "Lock Integration", IsConnected = true };
    var companionResult = await new CompanionAppService(adb).ExecuteCommandAsync(
        device,
        "android.device.power",
        "device.state",
        new Dictionary<string, object?>());
    var snapshotMarker = companionResult.Stdout.LastIndexOf(" data=", StringComparison.Ordinal);
    Assert(companionResult.Success && snapshotMarker >= 0, "真机 Companion device.state 必须返回结果快照。");
    var snapshotJson = companionResult.Stdout[(snapshotMarker + " data=".Length)..].Trim();
    using var snapshotDocument = System.Text.Json.JsonDocument.Parse(snapshotJson);
    using var envelopeDocument = System.Text.Json.JsonDocument.Parse($"{{\"payload\":{snapshotDocument.RootElement.GetRawText()}}}");
    Assert(
        DeviceLockService.TryParseCompanionState(envelopeDocument.RootElement, out var companionState) &&
        companionState != DeviceLockState.Unknown,
        "真机 Companion Framework 锁屏查询必须返回明确状态。");
    Console.WriteLine($"Lock integration passed: adb={adbState}, companion={companionState}.");
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
static async Task VerifyForegroundConnectivityIsolationAsync()
{
    var gateway = new BlockingConnectionGateway();
    var devices = new DeviceService(new SettingsService(), gateway);
    var staleDevice = new DeviceModel
    {
        DeviceId = "192.168.3.10:5555",
        DisplayName = "Offline",
        ConnectionKind = "wireless",
        IpAddress = "192.168.3.10",
        Port = 5555,
    };
    var onlineDevice = new DeviceModel
    {
        DeviceId = "192.168.3.20:5555",
        DisplayName = "Online",
        ConnectionKind = "wireless",
        IpAddress = "192.168.3.20",
        Port = 5555,
        IsConnected = true,
    };
    devices.Devices.Add(staleDevice);
    devices.Devices.Add(onlineDevice);

    var refresh = devices.RefreshConnectivityAsync();
    await gateway.OfflineConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
    var foregroundProbe = devices.EnsureDeviceOnlineAsync(onlineDevice);
    var completed = await Task.WhenAny(foregroundProbe, Task.Delay(250));

    gateway.ReleaseOfflineConnect.TrySetResult();
    await refresh;
    Assert(ReferenceEquals(completed, foregroundProbe), "已知在线设备的前台操作不能等待其他设备的自动重连。");
    Assert(await foregroundProbe, "已知在线设备必须保持可操作状态。");
}

static async Task VerifyMdnsBoundEndpointPolicyAsync()
{
    var gateway = new BlockingConnectionGateway();
    var logger = new RecordingDeviceConnectionLogger();
    var devices = new DeviceService(new SettingsService(), gateway, logger);
    var staleMdnsDevice = new DeviceModel
    {
        DeviceId = "192.168.3.30:5555",
        DisplayName = "Stale mDNS device",
        ConnectionKind = "wireless",
        IpAddress = "192.168.3.30",
        Port = 5555,
        MdnsServiceId = "adb-stable-device",
    };
    devices.Devices.Add(staleMdnsDevice);

    await devices.RefreshConnectivityAsync();
    Assert(gateway.ConnectEndpoints.Count == 0, "后台自动重连不能尝试 mDNS 已绑定设备的过期端口。");
    Assert(!logger.Entries.Any(entry => entry.Stage == "automatic-connect" && entry.Outcome == "failed"), "没有 mDNS 候选属于预期跳过，不能记录为连接失败。");

    await devices.ConnectSavedWirelessDeviceAsync(staleMdnsDevice);
    Assert(gateway.ConnectEndpoints.Contains("192.168.3.30:5555"), "用户显式连接时仍应允许尝试保存端点。");
}


sealed class FakeDevicePackageCatalogGateway : IDevicePackageCatalogGateway
{
    private readonly IReadOnlyList<string> _packageNames;
    private readonly string _iconPngBase64;

    public FakeDevicePackageCatalogGateway(IReadOnlyList<string> packageNames, string iconPngBase64)
    {
        _packageNames = packageNames;
        _iconPngBase64 = iconPngBase64;
    }

    public List<IReadOnlyList<string>> MetadataRequests { get; } = new();
    public int TransientMetadataFailuresRemaining { get; set; }
    public int MetadataAttemptCount { get; private set; }

    public Task<AdbCommandResult> ListPackagesAsync(DeviceModel device, CancellationToken cancellationToken)
    {
        var output = string.Join('\n', _packageNames.Select(packageName => $"package:{packageName}"));
        return Task.FromResult(new AdbCommandResult(0, output, string.Empty));
    }

    public Task<AdbCommandResult> ReadPackageDumpAsync(DeviceModel device, CancellationToken cancellationToken)
        => Task.FromResult(new AdbCommandResult(0, string.Empty, string.Empty));

    public Task<AdbCommandResult> ReadCompanionMetadataAsync(
        DeviceModel device,
        IReadOnlyList<string> packageNames,
        CancellationToken cancellationToken)
    {
        MetadataAttemptCount++;
        if (TransientMetadataFailuresRemaining > 0)
        {
            TransientMetadataFailuresRemaining--;
            return Task.FromResult(new AdbCommandResult(1, string.Empty, "error: device offline"));
        }

        MetadataRequests.Add(packageNames.ToArray());
        var apps = packageNames.Select(packageName => new
        {
            packageName,
            label = $"App {packageName}",
            enabled = true,
            system = false,
            sourceDir = $"/data/app/{packageName}/base.apk",
            iconPngBase64 = _iconPngBase64,
        });
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            requestId = "catalog-test",
            ok = true,
            result = new
            {
                apps,
                count = packageNames.Count,
                total = packageNames.Count,
                offset = 0,
                limit = 64,
                queryMode = "explicit",
            },
        });
        return Task.FromResult(new AdbCommandResult(0, $"data={payload}", string.Empty));
    }
}

sealed class FakeAiDeviceInventory : IAiDeviceInventory
{
    private readonly IReadOnlyList<DeviceModel> _devices;

    public FakeAiDeviceInventory(IReadOnlyList<DeviceModel> devices)
    {
        _devices = devices;
    }

    public IReadOnlyList<DeviceModel> GetDevices() => _devices;
}

sealed class EmptyPackageNameLookup : IPackageNameLookup
{
    public Task<IReadOnlyDictionary<string, string>> ResolveMissingAsync(
        IEnumerable<string> packageNames,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyDictionary<string, string>>(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
}

sealed class FakeCompanionInstallGateway : ICompanionInstallGateway
{
    public bool DeviceOnline { get; set; } = true;
    public AdbCommandResult InstallResult { get; set; } = new(0, "Success", string.Empty);
    public AdbCommandResult ConfigureResult { get; set; } = new(0, "Starting", string.Empty);
    public bool Responsive { get; set; } = true;
    public Exception? ResponsiveException { get; set; }

    public Task<bool> EnsureDeviceOnlineAsync(DeviceModel device) => Task.FromResult(DeviceOnline);
    public Task<AdbCommandResult> InstallAsync(DeviceModel device) => Task.FromResult(InstallResult);
    public Task<AdbCommandResult> ConfigureConnectionAsync(DeviceModel device, int quicPort) => Task.FromResult(ConfigureResult);
    public Task<bool> IsResponsiveAsync(DeviceModel device) => ResponsiveException is null
        ? Task.FromResult(Responsive)
        : Task.FromException<bool>(ResponsiveException);
}
sealed class BlockingConnectionGateway : IAdbConnectionGateway
{
    public TaskCompletionSource OfflineConnectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseOfflineConnect { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<string> ConnectEndpoints { get; } = new();

    public Task<AdbCommandResult> PairAsync(string ip, int port, string code) =>
        Task.FromResult(new AdbCommandResult(0, "Successfully paired", string.Empty));

    public async Task<AdbCommandResult> ConnectAsync(string ip, int port)
    {
        lock (ConnectEndpoints)
            ConnectEndpoints.Add($"{ip}:{port}");
        if (ip == "192.168.3.10")
        {
            OfflineConnectStarted.TrySetResult();
            await ReleaseOfflineConnect.Task;
            return new AdbCommandResult(1, string.Empty, "timed out");
        }

        return new AdbCommandResult(0, $"connected to {ip}:{port}", string.Empty);
    }

    public Task<AdbCommandResult> DisconnectAsync(string deviceId) =>
        Task.FromResult(new AdbCommandResult(0, $"disconnected {deviceId}", string.Empty));

    public Task<AdbCommandResult> DevicesAsync() =>
        Task.FromResult(new AdbCommandResult(
            0,
            "List of devices attached\n192.168.3.20:5555 device product:test model:Online device:test\n",
            string.Empty));

    public Task<AdbCommandResult> GetStateAsync(string deviceId) =>
        Task.FromResult(new AdbCommandResult(0, "device", string.Empty));

    public Task<(AdbCommandResult Result, IReadOnlyList<AdbMdnsService> Services)> DiscoverMdnsServicesAsync() =>
        Task.FromResult<(AdbCommandResult, IReadOnlyList<AdbMdnsService>)>((
            new AdbCommandResult(0, "List of discovered mdns services", string.Empty),
            Array.Empty<AdbMdnsService>()));
}
sealed class RecordingDeviceConnectionLogger : IDeviceConnectionLogger
{
    public List<DeviceConnectionLogEntry> Entries { get; } = new();

    public Task WriteAsync(DeviceConnectionLogEntry entry)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }
}

sealed class SequenceDeviceLockStateSource : IDeviceLockStateSource
{
    private readonly DeviceLockState[] _states;
    private int _queryCount;

    public SequenceDeviceLockStateSource(params DeviceLockState[] states)
    {
        _states = states;
    }

    public Task<DeviceLockState> GetStateAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var index = Math.Min(Interlocked.Increment(ref _queryCount) - 1, _states.Length - 1);
        return Task.FromResult(_states[index]);
    }
}
