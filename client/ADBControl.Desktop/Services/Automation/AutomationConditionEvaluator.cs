using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ADBControl.Desktop.Models;

namespace ADBControl.Desktop.Services.Automation;

public sealed class AutomationConditionEvaluator
{
    private static readonly Regex ForegroundActivityRegex = new(
        @"(?:mResumedActivity|topResumedActivity|ResumedActivity)[^\r\n]*?\s(?<package>[A-Za-z0-9._]+?)/(?<activity>[A-Za-z0-9._$]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly IAutomationDeviceGateway _gateway;

    public AutomationConditionEvaluator(IAutomationDeviceGateway gateway)
    {
        _gateway = gateway;
    }

    public async Task<AutomationConditionResult> EvaluateAsync(
        string deviceId,
        AutomationConditionDefinition condition,
        CancellationToken cancellationToken)
    {
        var expected = AutomationParameterReader.GetString(condition.Parameters, "value", "true");
        var actual = await ReadActualValueAsync(deviceId, condition, cancellationToken);
        var matched = Compare(actual, condition.Operator, expected, condition.CaseSensitive);
        if (condition.Negate)
            matched = !matched;
        return new AutomationConditionResult(matched, actual, $"{condition.Type} {condition.Operator} {expected}");
    }

    public async Task<bool> EvaluateGroupAsync(
        string deviceId,
        IReadOnlyList<AutomationConditionDefinition> conditions,
        AutomationConditionMode mode,
        CancellationToken cancellationToken)
    {
        if (conditions.Count == 0)
            return true;
        if (mode == AutomationConditionMode.All)
        {
            foreach (var condition in conditions)
            {
                if (!(await EvaluateAsync(deviceId, condition, cancellationToken)).Matched)
                    return false;
            }
            return true;
        }

        foreach (var condition in conditions)
        {
            if ((await EvaluateAsync(deviceId, condition, cancellationToken)).Matched)
                return true;
        }
        return false;
    }

    public static bool Compare(string actual, string comparisonOperator, string expected, bool caseSensitive)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return comparisonOperator.ToLowerInvariant() switch
        {
            "equals" => string.Equals(actual, expected, comparison),
            "notequals" => !string.Equals(actual, expected, comparison),
            "contains" => actual.Contains(expected, comparison),
            "notcontains" => !actual.Contains(expected, comparison),
            "startswith" => actual.StartsWith(expected, comparison),
            "endswith" => actual.EndsWith(expected, comparison),
            "regex" => RegexMatches(actual, expected, caseSensitive),
            "greaterthan" => Numeric(actual, expected, (left, right) => left > right),
            "greaterthanorequal" => Numeric(actual, expected, (left, right) => left >= right),
            "lessthan" => Numeric(actual, expected, (left, right) => left < right),
            "lessthanorequal" => Numeric(actual, expected, (left, right) => left <= right),
            "in" => expected.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(value => string.Equals(actual, value, comparison)),
            "exists" => !string.IsNullOrWhiteSpace(actual),
            "truthy" => IsTruthy(actual),
            _ => false,
        };
    }

    private async Task<string> ReadActualValueAsync(
        string deviceId,
        AutomationConditionDefinition condition,
        CancellationToken cancellationToken)
    {
        var parameters = condition.Parameters;
        switch (condition.Type.ToLowerInvariant())
        {
            case "device.connected":
            case "device.authorized":
                return (await _gateway.IsConnectedAsync(deviceId, cancellationToken)).ToString().ToLowerInvariant();
            case "device.property":
                return await QueryAsync(deviceId, $"getprop {ShellToken(Required(parameters, "name"))}", cancellationToken);
            case "screen.on":
                return ParseScreenOn(await QueryAsync(deviceId, "dumpsys power", cancellationToken)).ToString().ToLowerInvariant();
            case "screen.locked":
                return ParseLocked(await QueryAsync(deviceId, "dumpsys window policy", cancellationToken));
            case "screen.orientation":
                return ParseOrientation(await QueryAsync(deviceId, "dumpsys input", cancellationToken));
            case "app.foreground":
                return ParseForeground(await QueryAsync(deviceId, "dumpsys activity activities", cancellationToken)).Package;
            case "activity.foreground":
                return ParseForeground(await QueryAsync(deviceId, "dumpsys activity activities", cancellationToken)).Activity;
            case "app.installed":
                return (!string.IsNullOrWhiteSpace(await QueryAsync(deviceId, $"pm path {ShellToken(Required(parameters, "packageName"))}", cancellationToken))).ToString().ToLowerInvariant();
            case "process.running":
                return (!string.IsNullOrWhiteSpace(await QueryAsync(deviceId, $"pidof {ShellToken(Required(parameters, "process"))}", cancellationToken))).ToString().ToLowerInvariant();
            case "webpage.open":
            case "ui.element":
            case "ui.text":
                return await QueryUiAsync(deviceId, cancellationToken);
            case "notification.present":
                return await QueryAsync(deviceId, "dumpsys notification --noredact", cancellationToken);
            case "battery.level":
                return ParseNamedNumber(await QueryAsync(deviceId, "dumpsys battery", cancellationToken), "level");
            case "battery.charging":
                return ParseCharging(await QueryAsync(deviceId, "dumpsys battery", cancellationToken)).ToString().ToLowerInvariant();
            case "battery.temperature":
                return (ParseDouble(ParseNamedNumber(await QueryAsync(deviceId, "dumpsys battery", cancellationToken), "temperature")) / 10d)
                    .ToString("0.0", CultureInfo.InvariantCulture);
            case "network.connected":
                return ParseNetworkConnected(await QueryAsync(deviceId, "dumpsys connectivity", cancellationToken)).ToString().ToLowerInvariant();
            case "network.type":
                return ParseNetworkType(await QueryAsync(deviceId, "dumpsys connectivity", cancellationToken));
            case "wifi.ssid":
                return ParseSsid(await QueryAsync(deviceId, "dumpsys wifi", cancellationToken));
            case "internet.reachable":
                return (await TryQueryAsync(deviceId, "ping -c 1 -W 2 1.1.1.1", cancellationToken)).Success.ToString().ToLowerInvariant();
            case "bluetooth.enabled":
                return NormalizeSetting(await QueryAsync(deviceId, "settings get global bluetooth_on", cancellationToken));
            case "airplane.enabled":
                return NormalizeSetting(await QueryAsync(deviceId, "settings get global airplane_mode_on", cancellationToken));
            case "location.enabled":
                return (ParseDouble(await QueryAsync(deviceId, "settings get secure location_mode", cancellationToken)) > 0).ToString().ToLowerInvariant();
            case "dnd.enabled":
                return (ParseDouble(await QueryAsync(deviceId, "settings get global zen_mode", cancellationToken)) > 0).ToString().ToLowerInvariant();
            case "setting.value":
                return await QueryAsync(deviceId, $"settings get {SettingNamespace(parameters)} {ShellToken(Required(parameters, "name"))}", cancellationToken);
            case "call.state":
                return ParseCallState(await QueryAsync(deviceId, "dumpsys telephony.registry", cancellationToken));
            case "headset.connected":
                return ParseHeadset(await QueryAsync(deviceId, "dumpsys audio", cancellationToken)).ToString().ToLowerInvariant();
            case "file.exists":
                return (await TryQueryAsync(deviceId, $"test -e {ShellToken(Required(parameters, "path"))}", cancellationToken)).Success.ToString().ToLowerInvariant();
            case "clipboard.contains":
                return (await CompanionAsync(deviceId, "android.clipboard", "clipboard.read", new Dictionary<string, object?>(), cancellationToken)).CombinedOutput;
            case "companion.installed":
                return (!string.IsNullOrWhiteSpace(await QueryAsync(deviceId, "pm path com.adbcontrol.companion", cancellationToken))).ToString().ToLowerInvariant();
            case "companion.accessibility.ready":
                return ParseCompanionBoolean(
                    (await CompanionAsync(deviceId, "android.accessibility.control", "accessibility.status", new Dictionary<string, object?>(), cancellationToken)).CombinedOutput,
                    "enabled");
            case "companion.output":
                return (await CompanionAsync(
                    deviceId,
                    Required(parameters, "capabilityId"),
                    Required(parameters, "operation"),
                    AutomationParameterReader.GetObject(parameters, "args"),
                    cancellationToken)).CombinedOutput;
            case "adb.output":
                return await QueryAsync(deviceId, Required(parameters, "command"), cancellationToken);
            case "time.window":
                return IsInTimeWindow(parameters).ToString().ToLowerInvariant();
            default:
                throw new AutomationExecutionException("CONDITION_UNSUPPORTED", $"不支持的条件：{condition.Type}。", "automation.condition");
        }
    }

    private async Task<string> QueryAsync(string deviceId, string command, CancellationToken cancellationToken)
    {
        var result = await TryQueryAsync(deviceId, command, cancellationToken);
        if (!result.Success)
        {
            throw new AutomationExecutionException(
                "CONDITION_ADB_FAILED",
                string.IsNullOrWhiteSpace(result.Stderr) ? $"ADB 条件命令失败，退出码 {result.ExitCode}。" : result.Stderr.Trim(),
                "automation.condition",
                recoverable: true,
                suggestion: "确认设备在线、已授权且当前 Android 版本允许读取该状态。");
        }
        return result.Stdout.Trim();
    }

    private Task<AutomationCommandResult> TryQueryAsync(string deviceId, string command, CancellationToken cancellationToken) =>
        _gateway.ShellAsync(deviceId, command, cancellationToken);

    private async Task<AutomationCommandResult> CompanionAsync(
        string deviceId,
        string capabilityId,
        string operation,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var result = await _gateway.CompanionAsync(deviceId, capabilityId, operation, arguments, cancellationToken);
        if (!result.Success)
        {
            throw new AutomationExecutionException(
                "CONDITION_COMPANION_FAILED",
                string.IsNullOrWhiteSpace(result.Stderr) ? "Companion 条件调用失败。" : result.Stderr.Trim(),
                "automation.condition",
                recoverable: true,
                suggestion: "确认伴侣 App 已安装且对应权限已开启。");
        }
        return result;
    }

    private async Task<string> QueryUiAsync(string deviceId, CancellationToken cancellationToken)
    {
        return await QueryAsync(
            deviceId,
            "sh -c 'uiautomator dump --compressed /sdcard/adbcontrol-automation.xml >/dev/null && cat /sdcard/adbcontrol-automation.xml && rm /sdcard/adbcontrol-automation.xml'",
            cancellationToken);
    }

    private static (string Package, string Activity) ParseForeground(string output)
    {
        var match = ForegroundActivityRegex.Match(output);
        return match.Success
            ? (match.Groups["package"].Value, match.Groups["activity"].Value)
            : (string.Empty, string.Empty);
    }

    private static bool ParseScreenOn(string output) =>
        output.Contains("mWakefulness=Awake", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("Display Power: state=ON", StringComparison.OrdinalIgnoreCase) ||
        Regex.IsMatch(output, @"mScreenOn\s*=\s*true", RegexOptions.IgnoreCase);

    private static string ParseLocked(string output)
    {
        var trueMatch = Regex.IsMatch(output, @"(?:mKeyguardShowing|isStatusBarKeyguard|keyguardShowing)\s*=\s*true", RegexOptions.IgnoreCase);
        var falseMatch = Regex.IsMatch(output, @"(?:mKeyguardShowing|isStatusBarKeyguard|keyguardShowing)\s*=\s*false", RegexOptions.IgnoreCase);
        return trueMatch ? "true" : falseMatch ? "false" : "unknown";
    }

    private static string ParseCompanionBoolean(string output, string propertyName)
    {
        var json = ExtractBroadcastDataJson(output);
        if (json is null)
            return "unknown";

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
                root = result;
            if (root.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return value.GetBoolean().ToString().ToLowerInvariant();
        }
        catch (JsonException)
        {
        }
        return "unknown";
    }

    private static string? ExtractBroadcastDataJson(string output)
    {
        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var index = line.IndexOf("data=", StringComparison.OrdinalIgnoreCase);
            var candidate = index >= 0 ? line[(index + "data=".Length)..].Trim() : line.Trim();
            if (!candidate.StartsWith('{'))
                continue;
            return candidate;
        }
        return null;
    }

    private static string ParseOrientation(string output)
    {
        var match = Regex.Match(output, @"SurfaceOrientation:\s*(\d)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value switch
        {
            "0" => "portrait",
            "1" => "landscape",
            "2" => "reversePortrait",
            "3" => "reverseLandscape",
            _ => match.Groups[1].Value,
        } : string.Empty;
    }

    private static string ParseNamedNumber(string output, string name)
    {
        var match = Regex.Match(output, $@"(?m)^\s*{Regex.Escape(name)}:\s*(-?\d+(?:\.\d+)?)\s*$", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static bool ParseCharging(string output)
    {
        var status = ParseNamedNumber(output, "status");
        return status is "2" or "5" ||
            Regex.IsMatch(output, @"(?m)^\s*(AC|USB|Wireless) powered:\s*true", RegexOptions.IgnoreCase);
    }

    private static bool ParseNetworkConnected(string output) =>
        Regex.IsMatch(output, @"(?:CONNECTED|VALIDATED|isAvailable\(\)=true)", RegexOptions.IgnoreCase) &&
        !Regex.IsMatch(output, @"DISCONNECTED", RegexOptions.IgnoreCase);

    private static string ParseNetworkType(string output)
    {
        if (Regex.IsMatch(output, @"TRANSPORT_WIFI|type:\s*WIFI|type=WIFI", RegexOptions.IgnoreCase)) return "wifi";
        if (Regex.IsMatch(output, @"TRANSPORT_CELLULAR|type:\s*MOBILE|type=MOBILE", RegexOptions.IgnoreCase)) return "cellular";
        if (Regex.IsMatch(output, @"TRANSPORT_ETHERNET|type:\s*ETHERNET", RegexOptions.IgnoreCase)) return "ethernet";
        if (Regex.IsMatch(output, @"TRANSPORT_VPN|type:\s*VPN", RegexOptions.IgnoreCase)) return "vpn";
        return ParseNetworkConnected(output) ? "other" : "offline";
    }

    private static string ParseSsid(string output)
    {
        var match = Regex.Match(output, "(?:SSID|mWifiInfo).*?SSID[:=]\\s*\\\"?(?<ssid>[^,\\r\\n\\\"]+)", RegexOptions.IgnoreCase);
        if (!match.Success)
            match = Regex.Match(output, @"SSID:\s*(?<ssid>[^,\r\n]+)", RegexOptions.IgnoreCase);
        var value = match.Success ? match.Groups["ssid"].Value.Trim().Trim('"') : string.Empty;
        return value.Contains("unknown ssid", StringComparison.OrdinalIgnoreCase) ? string.Empty : value;
    }

    private static string ParseCallState(string output)
    {
        var match = Regex.Match(output, @"mCallState\s*=\s*(\d+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value switch
        {
            "0" => "idle",
            "1" => "ringing",
            "2" => "offhook",
            _ => match.Groups[1].Value,
        } : string.Empty;
    }

    private static bool ParseHeadset(string output) =>
        Regex.IsMatch(output, @"(?:headset|headphones|usb_headset|a2dp)[^\r\n]*(?:connected|state\s*=\s*1|available)", RegexOptions.IgnoreCase);

    private static string NormalizeSetting(string output) => (ParseDouble(output) > 0).ToString().ToLowerInvariant();

    private static string SettingNamespace(IReadOnlyDictionary<string, System.Text.Json.JsonElement> parameters)
    {
        var value = AutomationParameterReader.GetString(parameters, "namespace", "global").ToLowerInvariant();
        return value is "global" or "secure" or "system" ? value : "global";
    }

    private static bool IsInTimeWindow(IReadOnlyDictionary<string, System.Text.Json.JsonElement> parameters)
    {
        if (!TimeOnly.TryParse(Required(parameters, "start"), out var start) ||
            !TimeOnly.TryParse(Required(parameters, "end"), out var end))
            return false;
        var zone = AutomationScheduleCalculator.ResolveTimeZone(AutomationParameterReader.GetString(parameters, "timeZoneId", TimeZoneInfo.Local.Id));
        var now = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
        return start <= end ? now >= start && now <= end : now >= start || now <= end;
    }

    private static string Required(IReadOnlyDictionary<string, System.Text.Json.JsonElement> parameters, string name)
    {
        var value = AutomationParameterReader.GetString(parameters, name);
        if (string.IsNullOrWhiteSpace(value))
            throw new AutomationExecutionException("CONDITION_PARAMETER_MISSING", $"条件缺少参数 {name}。", "automation.condition");
        return value;
    }

    private static string ShellToken(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static bool RegexMatches(string actual, string pattern, bool caseSensitive)
    {
        try
        {
            return Regex.IsMatch(actual, pattern, caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool Numeric(string actual, string expected, Func<double, double, bool> comparison) =>
        double.TryParse(actual.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var left) &&
        double.TryParse(expected.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var right) &&
        comparison(left, right);

    private static double ParseDouble(string value) =>
        double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0;

    private static bool IsTruthy(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("on", StringComparison.OrdinalIgnoreCase) ||
        (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && Math.Abs(number) > double.Epsilon);
}
