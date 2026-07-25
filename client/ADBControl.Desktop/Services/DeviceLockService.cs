using System.Text.RegularExpressions;
using System.Text.Json;
using ADBControl.Desktop.Models;

namespace ADBControl.Desktop.Services;

public sealed class DeviceLockService
{
    private readonly AdbService _adb;
    private readonly CompanionQuicServer? _companionQuic;

    public DeviceLockService(AdbService adb, CompanionQuicServer? companionQuic = null)
    {
        _adb = adb;
        _companionQuic = companionQuic;
    }

    public async Task<DeviceLockState> GetStateAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var result = await _adb.ShellAsync(deviceId, "dumpsys window policy; dumpsys window", cancellationToken);
        return result.Success ? ParseState(result.Stdout) : DeviceLockState.Unknown;
    }

    public async Task<AdbCommandResult> UnlockAsync(string deviceId, string? pin, CancellationToken cancellationToken = default)
    {
        var normalizedPin = pin?.Trim() ?? string.Empty;
        if (normalizedPin.Length > 0 && !Regex.IsMatch(normalizedPin, "^[0-9]{4,16}$"))
            return new AdbCommandResult(1, string.Empty, "PIN 必须是 4 到 16 位数字。");

        var sizeResult = await _adb.ShellAsync(deviceId, "wm size", cancellationToken);
        if (!sizeResult.Success)
            return sizeResult;
        var (width, height) = TryParseScreenSize(sizeResult.Stdout, out var parsedWidth, out var parsedHeight)
            ? (parsedWidth, parsedHeight)
            : (1080, 1920);

        var commands = BuildUnlockCommands(width, height, normalizedPin);
        var companionSteps = BuildCompanionUnlockSteps(width, height, normalizedPin);
        AdbCommandResult? latest = null;
        for (var index = 0; index < commands.Count; index++)
        {
            latest = await _adb.ShellAsync(deviceId, commands[index], cancellationToken);
            if (!latest.Success && IsInputInjectionDenied(latest))
            {
                var step = companionSteps[index];
                var companionResult = await ExecuteCompanionStepAsync(
                    deviceId,
                    step.CapabilityId,
                    step.Operation,
                    step.Args,
                    cancellationToken);
                if (!companionResult.Success)
                {
                    return new AdbCommandResult(
                        1,
                        string.Empty,
                        $"设备系统拒绝 ADB 输入注入。{FailureText(companionResult)}");
                }
                latest = companionResult;
            }
            if (!latest.Success)
                return latest;

            // Keyguard needs a short interval after wake and after the swipe animation before input.
            if (index == 0)
                await Task.Delay(180, cancellationToken);
            else if (index == 1 && normalizedPin.Length > 0)
                await Task.Delay(320, cancellationToken);
        }

        return latest ?? new AdbCommandResult(1, string.Empty, "未生成解锁命令。");
    }

    public static bool IsInputInjectionDenied(AdbCommandResult result)
    {
        var output = $"{result.Stdout}\n{result.Stderr}";
        return output.Contains("INJECT_EVENTS", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Injecting input events requires", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<AdbCommandResult> UnlockPinAsync(string deviceId, string pin, CancellationToken cancellationToken = default)
    {
        return await UnlockAsync(deviceId, pin, cancellationToken);
    }

    public static IReadOnlyList<string> BuildUnlockCommands(int width, int height, string? pin)
    {
        var normalizedPin = pin?.Trim() ?? string.Empty;
        var centerX = Math.Max(1, width / 2);
        var startY = Math.Max(1, height * 4 / 5);
        var endY = Math.Max(1, height / 5);
        var commands = new List<string>
        {
            "input keyevent KEYCODE_WAKEUP",
            $"input touchscreen swipe {centerX} {startY} {centerX} {endY} 420",
        };
        if (normalizedPin.Length > 0)
        {
            commands.Add($"input text {normalizedPin}");
            commands.Add("input keyevent KEYCODE_ENTER");
        }
        return commands;
    }

    private static IReadOnlyList<CompanionUnlockStep> BuildCompanionUnlockSteps(int width, int height, string pin)
    {
        var centerX = Math.Max(1, width / 2);
        var startY = Math.Max(1, height * 4 / 5);
        var endY = Math.Max(1, height / 5);
        var steps = new List<CompanionUnlockStep>
        {
            new("android.device.power", "device.wake", new Dictionary<string, object?>()),
            new(
                "android.accessibility.control",
                "accessibility.touch.swipe",
                new Dictionary<string, object?>
                {
                    ["startX"] = centerX,
                    ["startY"] = startY,
                    ["endX"] = centerX,
                    ["endY"] = endY,
                    ["durationMs"] = 420,
                }),
        };
        if (pin.Length > 0)
        {
            steps.Add(new(
                "android.input.ime",
                "input.text",
                new Dictionary<string, object?> { ["text"] = pin }));
            steps.Add(new(
                "android.input.ime",
                "input.key",
                new Dictionary<string, object?> { ["keyCode"] = 66 }));
        }
        return steps;
    }

    private async Task<AdbCommandResult> ExecuteCompanionStepAsync(
        string deviceId,
        string capabilityId,
        string operation,
        IReadOnlyDictionary<string, object?> args,
        CancellationToken cancellationToken)
    {
        if (_companionQuic is null)
            return new AdbCommandResult(1, string.Empty, "客户端未启用伴侣 App 控制通道。请开启小米“USB 调试（安全设置）”后重试。");

        CompanionQuicDeviceSession session;
        try
        {
            session = _companionQuic.TryGetDevice(deviceId, out var connected)
                ? connected!
                : await _companionQuic.WaitForDeviceAsync(deviceId, TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException)
        {
            return new AdbCommandResult(
                1,
                string.Empty,
                $"伴侣 App 控制通道未连接，无法使用备用输入通道。请确认 APP 已连接，或开启小米“USB 调试（安全设置）”。{Environment.NewLine}{ex.Message}");
        }

        try
        {
            var envelope = await session.SendCommandAsync(
                capabilityId,
                operation,
                args,
                TimeSpan.FromSeconds(5),
                cancellationToken);
            if (!envelope.TryGetProperty("payload", out var payload))
                return new AdbCommandResult(1, string.Empty, "伴侣 App 返回了无效的命令响应。");
            if (payload.TryGetProperty("ok", out var ok) && ok.GetBoolean())
                return new AdbCommandResult(0, $"伴侣 App 已执行 {operation}。", string.Empty);

            var detail = ReadCompanionError(payload);
            return new AdbCommandResult(1, string.Empty, detail);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException or JsonException)
        {
            return new AdbCommandResult(1, string.Empty, $"伴侣 App 执行 {operation} 失败：{ex.Message}");
        }
    }

    private static string ReadCompanionError(JsonElement payload)
    {
        if (!payload.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            return "伴侣 App 未能执行输入命令。";
        var message = error.TryGetProperty("message", out var messageValue) ? messageValue.GetString() : null;
        var suggestion = error.TryGetProperty("suggestion", out var suggestionValue) ? suggestionValue.GetString() : null;
        return string.Join(
            Environment.NewLine,
            new[] { message, suggestion }.Where(value => !string.IsNullOrWhiteSpace(value))!) is { Length: > 0 } detail
                ? detail
                : "伴侣 App 未能执行输入命令。";
    }

    private static string FailureText(AdbCommandResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Stderr))
            return result.Stderr.Trim();
        if (!string.IsNullOrWhiteSpace(result.Stdout))
            return result.Stdout.Trim();
        return $"命令退出码 {result.ExitCode}。";
    }

    public static DeviceLockState ParseState(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return DeviceLockState.Unknown;

        if (Regex.IsMatch(output, @"(?:screenState\s*=\s*SCREEN_STATE_OFF|interactiveState\s*=\s*INTERACTIVE_STATE_SLEEP|mWakefulness\s*=\s*Asleep)", RegexOptions.IgnoreCase))
            return DeviceLockState.Locked;

        var values = Regex.Matches(output, @"(?:mKeyguardShowing|mShowingLockscreen|isStatusBarKeyguard|mDreamingLockscreen)\s*[=:]\s*(true|false)", RegexOptions.IgnoreCase)
            .Select(match => bool.TryParse(match.Groups[1].Value, out var value) ? value : (bool?)null)
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToList();

        if (values.Count == 0)
            return DeviceLockState.Unknown;
        return values.Any(value => value) ? DeviceLockState.Locked : DeviceLockState.Unlocked;
    }

    public static bool TryParseScreenSize(string output, out int width, out int height)
    {
        width = 0;
        height = 0;
        var matches = Regex.Matches(output ?? string.Empty, @"(?<width>\d+)x(?<height>\d+)");
        if (matches.Count == 0)
            return false;

        var match = matches[^1];
        return int.TryParse(match.Groups["width"].Value, out width) &&
            int.TryParse(match.Groups["height"].Value, out height) &&
            width > 0 && height > 0;
    }

    private sealed record CompanionUnlockStep(
        string CapabilityId,
        string Operation,
        IReadOnlyDictionary<string, object?> Args);
}
