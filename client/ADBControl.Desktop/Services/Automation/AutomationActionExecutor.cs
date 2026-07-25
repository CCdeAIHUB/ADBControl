using System.Text;
using System.Text.Json;
using ADBControl.Desktop.Models;

namespace ADBControl.Desktop.Services.Automation;

public sealed class AutomationActionExecutor
{
    private readonly IAutomationDeviceGateway _gateway;
    private readonly AutomationConditionEvaluator _conditions;

    public AutomationActionExecutor(IAutomationDeviceGateway gateway, AutomationConditionEvaluator conditions)
    {
        _gateway = gateway;
        _conditions = conditions;
    }

    public async Task<string> ExecuteLeafAsync(
        AutomationTaskDefinition task,
        AutomationRunRecord run,
        AutomationActionDefinition action,
        AutomationExecutionControl control,
        Func<AutomationAiRequest, CancellationToken, Task<string>>? aiExecutor,
        Action<AutomationAiOutput>? onAiOutput)
    {
        await control.WaitWhilePausedAsync();
        var cancellationToken = control.CancellationToken;
        var parameters = action.Parameters;
        switch (action.Type.ToLowerInvariant())
        {
            case "log":
                return AutomationParameterReader.GetString(parameters, "message", "记录");
            case "delay":
                await control.DelayAsync(TimeSpan.FromMilliseconds(AutomationParameterReader.GetInt(parameters, "milliseconds")));
                return "等待完成";
            case "condition.wait":
                return await WaitForConditionsAsync(task, action, control);
            case "adb.shell":
                return await ExecuteShellAsync(task, Required(parameters, "command"), cancellationToken);
            case "adb.keyevent":
                return await ExecuteShellAsync(task, $"input keyevent {ShellToken(Required(parameters, "keyCode"))}", cancellationToken);
            case "adb.tap":
                return await TapAsync(task, parameters, cancellationToken);
            case "adb.swipe":
                return await SwipeAsync(task, parameters, cancellationToken);
            case "adb.text":
                return await ExecuteShellAsync(task, $"input text {ShellToken(Required(parameters, "text").Replace(" ", "%s", StringComparison.Ordinal))}", cancellationToken);
            case "app.start":
                return await StartAppAsync(task, parameters, cancellationToken);
            case "url.open":
                return await ExecuteShellAsync(task, $"am start -a android.intent.action.VIEW -d {ShellToken(Required(parameters, "url"))}", cancellationToken);
            case "intent.start":
                return await ExecuteShellAsync(task, BuildIntentCommand(parameters), cancellationToken);
            case "device.wake":
                return await ExecuteShellAsync(task, "input keyevent KEYCODE_WAKEUP", cancellationToken);
            case "device.lock":
                return await ExecuteShellAsync(task, "input keyevent KEYCODE_SLEEP", cancellationToken);
            case "device.unlock":
                return await UnlockAsync(task, parameters, cancellationToken);
            case "companion.call":
                return await ExecuteCompanionAsync(task, parameters, cancellationToken);
            case "ai.prompt":
                return await ExecuteAiAsync(task, run, parameters, aiExecutor, onAiOutput, cancellationToken);
            case "fail":
                throw new AutomationExecutionException(
                    AutomationParameterReader.GetString(parameters, "errorCode", "TASK_SCRIPT_FAILURE"),
                    AutomationParameterReader.GetString(parameters, "message", "任务脚本主动失败。"),
                    "automation.script");
            default:
                throw new AutomationExecutionException("ACTION_UNSUPPORTED", $"不支持的动作：{action.Type}。", "automation.action");
        }
    }

    public static string Describe(AutomationActionDefinition action)
    {
        return action.Type.ToLowerInvariant() switch
        {
            "adb.shell" => $"ADB: {AutomationParameterReader.GetString(action.Parameters, "command")}",
            "ai.prompt" => "调用 AI",
            "companion.call" => $"Companion: {AutomationParameterReader.GetString(action.Parameters, "operation")}",
            "condition.wait" => "等待设备条件",
            "delay" => $"等待 {AutomationParameterReader.GetInt(action.Parameters, "milliseconds")} ms",
            "log" => AutomationParameterReader.GetString(action.Parameters, "message", "记录日志"),
            _ => action.Type,
        };
    }

    private async Task<string> WaitForConditionsAsync(
        AutomationTaskDefinition task,
        AutomationActionDefinition action,
        AutomationExecutionControl control)
    {
        var timeoutSeconds = Math.Clamp(AutomationParameterReader.GetInt(action.Parameters, "timeoutSeconds", 60), 1, 86400);
        var pollSeconds = Math.Clamp(AutomationParameterReader.GetInt(action.Parameters, "pollIntervalSeconds", 2), 1, 3600);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await control.WaitWhilePausedAsync();
            if (await _conditions.EvaluateGroupAsync(task.DeviceId, action.Conditions, action.ConditionMode, control.CancellationToken))
                return "条件已满足";
            await control.DelayAsync(TimeSpan.FromSeconds(pollSeconds));
        }
        throw new AutomationExecutionException(
            "CONDITION_WAIT_TIMEOUT",
            $"等待设备条件超过 {timeoutSeconds} 秒。",
            "automation.action",
            recoverable: true,
            suggestion: "检查设备状态或增加 timeoutSeconds。");
    }

    private async Task<string> TapAsync(
        AutomationTaskDefinition task,
        IReadOnlyDictionary<string, JsonElement> parameters,
        CancellationToken cancellationToken)
    {
        var x = AutomationParameterReader.GetInt(parameters, "x", -1);
        var y = AutomationParameterReader.GetInt(parameters, "y", -1);
        var screen = await _gateway.GetScreenSizeAsync(task.DeviceId, cancellationToken);
        if (x < 0 || y < 0 || x >= screen.Width || y >= screen.Height)
            throw new AutomationExecutionException("ACTION_COORDINATE_OUT_OF_RANGE", $"点击坐标 ({x},{y}) 超出屏幕 {screen.Width}x{screen.Height}。", "automation.action");
        return await ExecuteShellAsync(task, $"input tap {x} {y}", cancellationToken);
    }

    private async Task<string> SwipeAsync(
        AutomationTaskDefinition task,
        IReadOnlyDictionary<string, JsonElement> parameters,
        CancellationToken cancellationToken)
    {
        var startX = AutomationParameterReader.GetInt(parameters, "startX", -1);
        var startY = AutomationParameterReader.GetInt(parameters, "startY", -1);
        var endX = AutomationParameterReader.GetInt(parameters, "endX", -1);
        var endY = AutomationParameterReader.GetInt(parameters, "endY", -1);
        var duration = Math.Clamp(AutomationParameterReader.GetInt(parameters, "durationMs", 250), 1, 3000);
        var screen = await _gateway.GetScreenSizeAsync(task.DeviceId, cancellationToken);
        if (!Inside(startX, startY, screen) || !Inside(endX, endY, screen))
            throw new AutomationExecutionException("ACTION_COORDINATE_OUT_OF_RANGE", $"滑动坐标超出屏幕 {screen.Width}x{screen.Height}。", "automation.action");
        return await ExecuteShellAsync(task, $"input swipe {startX} {startY} {endX} {endY} {duration}", cancellationToken);
    }

    private async Task<string> StartAppAsync(
        AutomationTaskDefinition task,
        IReadOnlyDictionary<string, JsonElement> parameters,
        CancellationToken cancellationToken)
    {
        var packageName = Required(parameters, "packageName");
        var activity = AutomationParameterReader.GetString(parameters, "activity");
        var command = string.IsNullOrWhiteSpace(activity)
            ? $"monkey -p {ShellToken(packageName)} -c android.intent.category.LAUNCHER 1"
            : $"am start -n {ShellToken(packageName + "/" + activity)}";
        return await ExecuteShellAsync(task, command, cancellationToken);
    }

    private async Task<string> UnlockAsync(
        AutomationTaskDefinition task,
        IReadOnlyDictionary<string, JsonElement> parameters,
        CancellationToken cancellationToken)
    {
        var screen = await _gateway.GetScreenSizeAsync(task.DeviceId, cancellationToken);
        var pin = AutomationParameterReader.GetString(parameters, "pin");
        var commands = new List<string>
        {
            "input keyevent KEYCODE_WAKEUP",
            $"input touchscreen swipe {screen.Width / 2} {(int)(screen.Height * 0.82)} {screen.Width / 2} {(int)(screen.Height * 0.28)} 350",
        };
        if (!string.IsNullOrWhiteSpace(pin))
        {
            commands.Add($"input text {ShellToken(pin)}");
            commands.Add("input keyevent KEYCODE_ENTER");
        }

        var output = new List<string>();
        foreach (var command in commands)
            output.Add(await ExecuteShellAsync(task, command, cancellationToken));
        return string.Join(Environment.NewLine, output.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private async Task<string> ExecuteCompanionAsync(
        AutomationTaskDefinition task,
        IReadOnlyDictionary<string, JsonElement> parameters,
        CancellationToken cancellationToken)
    {
        var result = await _gateway.CompanionAsync(
            task.DeviceId,
            Required(parameters, "capabilityId"),
            Required(parameters, "operation"),
            AutomationParameterReader.GetObject(parameters, "args"),
            cancellationToken);
        return EnsureSuccess(result, "ACTION_COMPANION_FAILED", "automation.action");
    }

    private static async Task<string> ExecuteAiAsync(
        AutomationTaskDefinition task,
        AutomationRunRecord run,
        IReadOnlyDictionary<string, JsonElement> parameters,
        Func<AutomationAiRequest, CancellationToken, Task<string>>? aiExecutor,
        Action<AutomationAiOutput>? onAiOutput,
        CancellationToken cancellationToken)
    {
        if (aiExecutor is null)
            throw new AutomationExecutionException("ACTION_AI_NOT_CONFIGURED", "任务运行时没有可用的 AI 执行器。", "automation.ai", recoverable: true, suggestion: "先在设置中添加并选择 AI 模型。");
        var prompt = Required(parameters, "prompt");
        var response = await aiExecutor(
            new AutomationAiRequest(
                task.Id,
                task.Name,
                run.Id,
                task.DeviceId,
                prompt,
                AutomationParameterReader.GetString(parameters, "modelId", string.Empty),
                task.Permissions.AllowAiDeviceTools,
                task.Permissions.AllowTaskMutation),
            cancellationToken);
        if (string.IsNullOrWhiteSpace(response))
            throw new AutomationExecutionException("ACTION_AI_EMPTY_RESPONSE", "AI 没有返回内容。", "automation.ai", recoverable: true);
        onAiOutput?.Invoke(new AutomationAiOutput(task.Id, task.Name, run.Id, prompt, response));
        return response;
    }

    private async Task<string> ExecuteShellAsync(AutomationTaskDefinition task, string command, CancellationToken cancellationToken)
    {
        var result = await _gateway.ShellAsync(task.DeviceId, command, cancellationToken);
        return EnsureSuccess(result, "ACTION_ADB_FAILED", "automation.action");
    }

    private static string EnsureSuccess(AutomationCommandResult result, string errorCode, string module)
    {
        if (!result.Success)
        {
            throw new AutomationExecutionException(
                errorCode,
                string.IsNullOrWhiteSpace(result.Stderr) ? $"设备命令失败，退出码 {result.ExitCode}。" : result.Stderr.Trim(),
                module,
                recoverable: true,
                suggestion: "确认设备在线、已授权且任务权限满足动作要求。");
        }
        var output = result.CombinedOutput;
        return output.Length <= 200_000 ? output : output[..200_000];
    }

    private static string BuildIntentCommand(IReadOnlyDictionary<string, JsonElement> parameters)
    {
        var builder = new StringBuilder("am start");
        Add("-a", AutomationParameterReader.GetString(parameters, "action"));
        Add("-d", AutomationParameterReader.GetString(parameters, "data"));
        Add("-n", AutomationParameterReader.GetString(parameters, "component"));
        foreach (var pair in AutomationParameterReader.GetObject(parameters, "extras"))
        {
            switch (pair.Value)
            {
                case bool boolean: builder.Append(boolean ? " --ez " : " --ez ").Append(ShellToken(pair.Key)).Append(' ').Append(boolean.ToString().ToLowerInvariant()); break;
                case int or long: builder.Append(" --el ").Append(ShellToken(pair.Key)).Append(' ').Append(pair.Value); break;
                default: builder.Append(" --es ").Append(ShellToken(pair.Key)).Append(' ').Append(ShellToken(Convert.ToString(pair.Value) ?? string.Empty)); break;
            }
        }
        return builder.ToString();

        void Add(string flag, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                builder.Append(' ').Append(flag).Append(' ').Append(ShellToken(value));
        }
    }

    private static string Required(IReadOnlyDictionary<string, JsonElement> parameters, string name)
    {
        var value = AutomationParameterReader.GetString(parameters, name);
        if (string.IsNullOrWhiteSpace(value))
            throw new AutomationExecutionException("ACTION_PARAMETER_MISSING", $"动作缺少参数 {name}。", "automation.action");
        return value;
    }

    private static bool Inside(int x, int y, (int Width, int Height) screen) => x >= 0 && y >= 0 && x < screen.Width && y < screen.Height;
    private static string ShellToken(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
