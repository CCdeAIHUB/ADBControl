using System.Collections.Concurrent;
using System.Diagnostics;
using ADBControl.Desktop.Models;

namespace ADBControl.Desktop.Services;

public enum CompanionInstallStage
{
    Idle,
    CheckingDevice,
    InstallingPackage,
    ConfiguringConnection,
    WaitingForConnection,
    Succeeded,
    CompletedWithWarning,
    Failed,
}

public sealed record CompanionInstallError(
    string ErrorCode,
    string Message,
    string Module,
    bool Recoverable,
    string Suggestion,
    string TraceId,
    string? TechnicalDetail = null);

public sealed record CompanionInstallStatus(
    CompanionInstallStage Stage,
    string Title,
    string Message,
    string TraceId,
    long ElapsedMilliseconds,
    CompanionInstallError? Error = null)
{
    public bool IsActive => Stage is CompanionInstallStage.CheckingDevice or
        CompanionInstallStage.InstallingPackage or
        CompanionInstallStage.ConfiguringConnection or
        CompanionInstallStage.WaitingForConnection;
}

public sealed record CompanionInstallResult(
    bool Success,
    bool PackageInstalled,
    bool CompanionConnected,
    string TraceId,
    TimeSpan Elapsed,
    CompanionInstallError? Error = null);

public interface ICompanionInstallGateway
{
    Task<bool> EnsureDeviceOnlineAsync(DeviceModel device);
    Task<AdbCommandResult> InstallAsync(DeviceModel device);
    Task<AdbCommandResult> ConfigureConnectionAsync(DeviceModel device, int quicPort);
    Task<bool> IsResponsiveAsync(DeviceModel device);
}

public interface ICompanionInstallLogger
{
    Task WriteAsync(CompanionInstallStatus status, string deviceId);
}

public sealed class NullCompanionInstallLogger : ICompanionInstallLogger
{
    public Task WriteAsync(CompanionInstallStatus status, string deviceId) => Task.CompletedTask;
}

public sealed class CompanionInstallGateway : ICompanionInstallGateway
{
    private readonly DeviceService _devices;
    private readonly CompanionAppService _companion;

    public CompanionInstallGateway(DeviceService devices, CompanionAppService companion)
    {
        _devices = devices;
        _companion = companion;
    }

    public Task<bool> EnsureDeviceOnlineAsync(DeviceModel device) => _devices.EnsureDeviceOnlineAsync(device);
    public Task<AdbCommandResult> InstallAsync(DeviceModel device) => _companion.InstallAsync(device);
    public Task<AdbCommandResult> ConfigureConnectionAsync(DeviceModel device, int quicPort) =>
        _companion.ConfigureConnectionAsync(device, quicPort);
    public Task<bool> IsResponsiveAsync(DeviceModel device) => _companion.IsResponsiveAsync(device);
}

public sealed class CompanionInstallStateMachine
{
    public CompanionInstallStage Stage { get; private set; } = CompanionInstallStage.Idle;

    public void TransitionTo(CompanionInstallStage next)
    {
        if (!IsAllowed(Stage, next))
            throw new InvalidOperationException($"非法的 Companion 安装状态转换：{Stage} -> {next}。");
        Stage = next;
    }

    private static bool IsAllowed(CompanionInstallStage current, CompanionInstallStage next) => current switch
    {
        CompanionInstallStage.Idle => next is CompanionInstallStage.CheckingDevice or CompanionInstallStage.Failed,
        CompanionInstallStage.CheckingDevice => next is CompanionInstallStage.InstallingPackage or CompanionInstallStage.Failed,
        CompanionInstallStage.InstallingPackage => next is CompanionInstallStage.ConfiguringConnection or CompanionInstallStage.Failed,
        CompanionInstallStage.ConfiguringConnection => next is CompanionInstallStage.WaitingForConnection or CompanionInstallStage.CompletedWithWarning or CompanionInstallStage.Failed,
        CompanionInstallStage.WaitingForConnection => next is CompanionInstallStage.Succeeded or CompanionInstallStage.CompletedWithWarning or CompanionInstallStage.Failed,
        _ => false,
    };
}

public static class CompanionInstallFailureClassifier
{
    public static CompanionInstallError FromInstallResult(AdbCommandResult result, string traceId)
    {
        var detail = $"{result.Stdout}\n{result.Stderr}";
        if (detail.Contains("INSTALL_FAILED_USER_RESTRICTED", StringComparison.OrdinalIgnoreCase))
        {
            return Error(
                "COMPANION_INSTALL_USER_RESTRICTED",
                "设备拒绝了 ADB 安装请求。",
                true,
                "请保持设备解锁，在开发者选项中开启“USB 安装/通过 USB 安装”，并在手机端确认安装提示后重试。",
                traceId,
                "INSTALL_FAILED_USER_RESTRICTED");
        }
        if (detail.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE", StringComparison.OrdinalIgnoreCase))
        {
            return Error(
                "COMPANION_INSTALL_SIGNATURE_MISMATCH",
                "设备上已有签名不同的伴侣 App。",
                true,
                "请确认旧版伴侣 App 数据是否需要保留；卸载旧版后再重试安装。",
                traceId,
                "INSTALL_FAILED_UPDATE_INCOMPATIBLE");
        }
        if (result.ExitCode == -1 && detail.Contains("超时", StringComparison.OrdinalIgnoreCase))
        {
            return Error(
                "COMPANION_INSTALL_TIMEOUT",
                "伴侣 App 安装等待超时。",
                true,
                "请保持设备在线并检查手机端是否正在等待安装确认，然后重试。",
                traceId,
                "ADB_INSTALL_TIMEOUT");
        }
        if (detail.Contains("device offline", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("device not found", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("no devices", StringComparison.OrdinalIgnoreCase))
        {
            return Error(
                "COMPANION_INSTALL_DEVICE_UNAVAILABLE",
                "安装过程中设备连接已中断。",
                true,
                "请重新连接无线调试或 USB ADB，确认设备状态为在线后重试。",
                traceId,
                "ADB_DEVICE_UNAVAILABLE");
        }
        if (detail.Contains("未找到内置 Companion APK", StringComparison.OrdinalIgnoreCase))
        {
            return Error(
                "COMPANION_INSTALL_APK_MISSING",
                "桌面客户端缺少内置伴侣 App 安装包。",
                false,
                "请重新构建或重新安装包含 Companion APK 的桌面客户端。",
                traceId,
                "COMPANION_APK_MISSING");
        }

        return Error(
            "COMPANION_INSTALL_ADB_FAILED",
            "ADB 未能完成伴侣 App 安装。",
            true,
            "请确认设备保持解锁并允许 ADB 安装，然后根据错误码和安装日志重试。",
            traceId,
            $"ADB_EXIT_{result.ExitCode}");
    }

    public static CompanionInstallError DeviceUnavailable(string traceId) => Error(
        "COMPANION_INSTALL_DEVICE_UNAVAILABLE",
        "ADB 当前无法访问该设备。",
        true,
        "请重新连接无线调试或 USB ADB，确认设备在线后重试。",
        traceId,
        "ADB_DEVICE_UNAVAILABLE");

    public static CompanionInstallError ConfigurationFailed(AdbCommandResult result, string traceId) => Error(
        "COMPANION_CONFIGURE_FAILED",
        "伴侣 App 已安装，但桌面连接配置下发失败。",
        true,
        "请保持设备与电脑处于同一网络；客户端会保留已安装状态，可稍后重新建立连接。",
        traceId,
        $"ADB_EXIT_{result.ExitCode}");

    public static CompanionInstallError ConnectionTimeout(string traceId) => Error(
        "COMPANION_CONNECTION_TIMEOUT",
        "伴侣 App 已安装，但暂未连接到桌面端。",
        true,
        "请在手机上打开伴侣 App，并允许其网络与后台运行权限。",
        traceId,
        "COMPANION_NOT_RESPONSIVE");

    public static CompanionInstallError Unexpected(Exception exception, string traceId) => Error(
        "COMPANION_INSTALL_UNEXPECTED",
        "安装流程发生未预期错误。",
        true,
        "请根据 traceId 查看 companion-install.log 后重试。",
        traceId,
        exception.GetType().Name);

    public static CompanionInstallError AlreadyRunning(string traceId) => Error(
        "COMPANION_INSTALL_ALREADY_RUNNING",
        "该设备的伴侣 App 安装正在进行。",
        true,
        "请等待当前安装流程结束。",
        traceId,
        "INSTALL_ALREADY_RUNNING");

    private static CompanionInstallError Error(
        string code,
        string message,
        bool recoverable,
        string suggestion,
        string traceId,
        string technicalDetail) => new(
            code,
            message,
            "companion.install",
            recoverable,
            suggestion,
            traceId,
            technicalDetail);
}

public sealed class CompanionInstallWorkflow
{
    private readonly ICompanionInstallGateway _gateway;
    private readonly ICompanionInstallLogger _logger;
    private readonly ConcurrentDictionary<string, byte> _activeDevices = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CompanionInstallStatus> _latestStatuses = new(StringComparer.Ordinal);

    public CompanionInstallWorkflow(ICompanionInstallGateway gateway, ICompanionInstallLogger logger)
    {
        _gateway = gateway;
        _logger = logger;
    }

    public bool TryGetLatestStatus(string deviceId, out CompanionInstallStatus? status) =>
        _latestStatuses.TryGetValue(deviceId, out status);

    public async Task<CompanionInstallResult> RunAsync(
        DeviceModel device,
        int quicPort,
        Action<CompanionInstallStatus> reportProgress)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(reportProgress);

        var traceId = Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();
        var machine = new CompanionInstallStateMachine();
        if (string.IsNullOrWhiteSpace(device.DeviceId) || !_activeDevices.TryAdd(device.DeviceId, 0))
        {
            var error = string.IsNullOrWhiteSpace(device.DeviceId)
                ? CompanionInstallFailureClassifier.DeviceUnavailable(traceId)
                : CompanionInstallFailureClassifier.AlreadyRunning(traceId);
            await PublishAsync(machine, device.DeviceId, CompanionInstallStage.Failed, "无法开始安装", error.Message, traceId, stopwatch, reportProgress, error);
            return new CompanionInstallResult(false, false, false, traceId, stopwatch.Elapsed, error);
        }

        try
        {
            await PublishAsync(machine, device.DeviceId, CompanionInstallStage.CheckingDevice, "正在准备安装", "正在检查 ADB 设备连接…", traceId, stopwatch, reportProgress);
            if (!await _gateway.EnsureDeviceOnlineAsync(device))
            {
                device.IsConnected = false;
                return await FailAsync(machine, device, traceId, stopwatch, reportProgress, CompanionInstallFailureClassifier.DeviceUnavailable(traceId));
            }

            await PublishAsync(machine, device.DeviceId, CompanionInstallStage.InstallingPackage, "正在安装伴侣 App", "正在传输并安装 APK；请保持设备解锁，并留意手机端安装确认。", traceId, stopwatch, reportProgress);
            var install = await _gateway.InstallAsync(device);
            if (!install.Success)
            {
                var error = CompanionInstallFailureClassifier.FromInstallResult(install, traceId);
                if (error.ErrorCode == "COMPANION_INSTALL_DEVICE_UNAVAILABLE")
                    device.IsConnected = false;
                return await FailAsync(machine, device, traceId, stopwatch, reportProgress, error);
            }

            device.IsCompanionInstalled = true;
            await PublishAsync(machine, device.DeviceId, CompanionInstallStage.ConfiguringConnection, "APK 安装完成", "正在下发桌面端安全连接配置…", traceId, stopwatch, reportProgress);
            var configure = await _gateway.ConfigureConnectionAsync(device, quicPort);
            if (!configure.Success)
            {
                device.IsCompanionConnected = false;
                var error = CompanionInstallFailureClassifier.ConfigurationFailed(configure, traceId);
                // The APK is already valid at this point. Keep it installed because connection
                // configuration is recoverable and uninstalling would discard user-granted state.
                await PublishAsync(machine, device.DeviceId, CompanionInstallStage.CompletedWithWarning, "伴侣 App 已安装", $"{error.Message} {error.Suggestion}", traceId, stopwatch, reportProgress, error);
                return new CompanionInstallResult(true, true, false, traceId, stopwatch.Elapsed, error);
            }

            await PublishAsync(machine, device.DeviceId, CompanionInstallStage.WaitingForConnection, "正在建立连接", "正在等待伴侣 App 与桌面端建立安全连接…", traceId, stopwatch, reportProgress);
            device.IsCompanionConnected = await _gateway.IsResponsiveAsync(device);
            if (!device.IsCompanionConnected)
            {
                var error = CompanionInstallFailureClassifier.ConnectionTimeout(traceId);
                await PublishAsync(machine, device.DeviceId, CompanionInstallStage.CompletedWithWarning, "伴侣 App 已安装", $"{error.Message} {error.Suggestion}", traceId, stopwatch, reportProgress, error);
                return new CompanionInstallResult(true, true, false, traceId, stopwatch.Elapsed, error);
            }

            await PublishAsync(machine, device.DeviceId, CompanionInstallStage.Succeeded, "伴侣 App 安装成功", "安装与桌面连接配置均已完成。", traceId, stopwatch, reportProgress);
            return new CompanionInstallResult(true, true, true, traceId, stopwatch.Elapsed);
        }
        catch (Exception exception)
        {
            var error = CompanionInstallFailureClassifier.Unexpected(exception, traceId);
            return await FailAsync(machine, device, traceId, stopwatch, reportProgress, error);
        }
        finally
        {
            _activeDevices.TryRemove(device.DeviceId, out _);
        }
    }

    private async Task<CompanionInstallResult> FailAsync(
        CompanionInstallStateMachine machine,
        DeviceModel device,
        string traceId,
        Stopwatch stopwatch,
        Action<CompanionInstallStatus> reportProgress,
        CompanionInstallError error)
    {
        device.IsCompanionConnected = false;
        await PublishAsync(machine, device.DeviceId, CompanionInstallStage.Failed, "伴侣 App 安装失败", $"{error.Message} {error.Suggestion}", traceId, stopwatch, reportProgress, error);
        return new CompanionInstallResult(false, device.IsCompanionInstalled, false, traceId, stopwatch.Elapsed, error);
    }

    private async Task PublishAsync(
        CompanionInstallStateMachine machine,
        string deviceId,
        CompanionInstallStage stage,
        string title,
        string message,
        string traceId,
        Stopwatch stopwatch,
        Action<CompanionInstallStatus> reportProgress,
        CompanionInstallError? error = null)
    {
        // The state transition is validated before UI and logs observe it, so all consumers
        // receive the same ordered lifecycle and cannot display a fabricated success state.
        machine.TransitionTo(stage);
        var status = new CompanionInstallStatus(stage, title, message, traceId, stopwatch.ElapsedMilliseconds, error);
        _latestStatuses[deviceId] = status;
        reportProgress(status);
        await _logger.WriteAsync(status, deviceId);
    }
}
