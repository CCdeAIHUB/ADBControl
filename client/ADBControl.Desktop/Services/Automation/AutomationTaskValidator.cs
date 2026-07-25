using ADBControl.Desktop.Models;

namespace ADBControl.Desktop.Services.Automation;

public sealed record AutomationValidationResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public static class AutomationTaskValidator
{
    private static readonly HashSet<string> TriggerTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "manual", "once", "daily", "weekly", "interval", "cron", "condition",
    };

    private static readonly HashSet<string> ConditionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "device.connected", "device.authorized", "device.property",
        "screen.on", "screen.locked", "screen.orientation", "ui.element", "ui.text",
        "app.foreground", "activity.foreground", "app.installed", "process.running", "webpage.open",
        "notification.present", "call.state", "headset.connected",
        "battery.level", "battery.charging", "battery.temperature",
        "network.connected", "network.type", "wifi.ssid", "internet.reachable",
        "bluetooth.enabled", "airplane.enabled", "location.enabled", "dnd.enabled", "setting.value",
        "file.exists", "clipboard.contains", "companion.installed", "companion.accessibility.ready", "companion.output",
        "adb.output", "time.window",
    };

    private static readonly HashSet<string> Operators = new(StringComparer.OrdinalIgnoreCase)
    {
        "equals", "notEquals", "contains", "notContains", "startsWith", "endsWith", "regex",
        "greaterThan", "greaterThanOrEqual", "lessThan", "lessThanOrEqual", "in", "exists", "truthy",
    };

    private static readonly HashSet<string> ActionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "adb.shell", "adb.keyEvent", "adb.tap", "adb.swipe", "adb.text",
        "app.start", "url.open", "intent.start", "device.wake", "device.lock", "device.unlock",
        "companion.call", "ai.prompt", "delay", "condition.wait", "flow.if", "flow.repeat", "flow.parallel",
        "log", "fail",
    };

    public static AutomationValidationResult Validate(AutomationTaskDefinition definition)
    {
        var errors = new List<string>();
        if (definition.SchemaVersion != 1)
            errors.Add($"不支持 schemaVersion={definition.SchemaVersion}，当前只支持 1。");
        if (string.IsNullOrWhiteSpace(definition.Name))
            errors.Add("任务名称不能为空。");
        if (definition.Name.Length > 120)
            errors.Add("任务名称不能超过 120 个字符。");
        if (definition.Triggers.Count == 0)
            errors.Add("任务至少需要一个触发器；仅手动运行时使用 manual。");
        if (definition.Actions.Count == 0)
            errors.Add("任务至少需要一个动作。");
        if (definition.Triggers.Select(trigger => trigger.Id).Distinct(StringComparer.Ordinal).Count() != definition.Triggers.Count)
            errors.Add("同一任务内的触发器 id 不能重复。");

        foreach (var trigger in definition.Triggers)
            ValidateTrigger(trigger, errors);

        var actionCount = 0;
        ValidateActions(definition, definition.Actions, errors, depth: 0, ref actionCount);
        if (actionCount > 1000)
            errors.Add("单个任务展开前不能超过 1000 个动作节点。");

        var conditions = definition.Triggers.SelectMany(trigger => trigger.Conditions)
            .Concat(EnumerateActionConditions(definition.Actions))
            .ToList();
        var requiresDevice = conditions.Any(condition => !IsLocalCondition(condition.Type)) ||
            ContainsDeviceAction(definition.Actions);
        if (requiresDevice && string.IsNullOrWhiteSpace(definition.DeviceId))
            errors.Add("任务包含设备条件或设备动作时必须指定 deviceId。");
        if (conditions.Any(condition => !IsLocalCondition(condition.Type)) && !definition.Permissions.AllowAdb)
            errors.Add("设备条件需要 permissions.allowAdb=true。");
        if (conditions.Any(condition => condition.Type.Equals("adb.output", StringComparison.OrdinalIgnoreCase)) && !definition.Permissions.AllowShell)
            errors.Add("adb.output 条件需要 permissions.allowShell=true。");
        if (conditions.Any(RequiresCompanion) && !definition.Permissions.AllowCompanion)
            errors.Add("Companion 条件需要 permissions.allowCompanion=true。");
        return new AutomationValidationResult(errors);
    }

    private static void ValidateTrigger(AutomationTriggerDefinition trigger, List<string> errors)
    {
        if (!TriggerTypes.Contains(trigger.Type))
        {
            errors.Add($"不支持的触发器类型：{trigger.Type}。");
            return;
        }

        switch (trigger.Type.ToLowerInvariant())
        {
            case "once" when trigger.RunAt is null:
                errors.Add("once 触发器必须提供 runAt。");
                break;
            case "daily":
                ValidateAt(trigger, errors);
                break;
            case "weekly":
                ValidateAt(trigger, errors);
                if (trigger.Days.Count == 0)
                    errors.Add("weekly 触发器必须至少选择一天。");
                break;
            case "interval" when trigger.IntervalSeconds < 1:
                errors.Add("intervalSeconds 必须至少为 1 秒。");
                break;
            case "cron":
                if (string.IsNullOrWhiteSpace(trigger.Cron))
                    errors.Add("cron 触发器必须提供 cron 表达式。");
                else if (!AutomationCronExpression.TryParse(trigger.Cron, out _, out var cronError))
                    errors.Add($"Cron 表达式无效：{cronError}");
                break;
            case "condition" when trigger.Conditions.Count == 0:
                errors.Add("condition 触发器必须至少包含一个条件。");
                break;
        }

        if (trigger.Type is "daily" or "weekly" or "cron")
        {
            try
            {
                _ = TimeZoneInfo.FindSystemTimeZoneById(trigger.TimeZoneId);
            }
            catch (TimeZoneNotFoundException)
            {
                errors.Add($"找不到时区：{trigger.TimeZoneId}。");
            }
            catch (InvalidTimeZoneException)
            {
                errors.Add($"时区数据无效：{trigger.TimeZoneId}。");
            }
        }

        if (trigger.PollIntervalSeconds < 1 || trigger.PollIntervalSeconds > 86400)
            errors.Add("pollIntervalSeconds 必须在 1 到 86400 之间。");
        if (trigger.CooldownSeconds < 0)
            errors.Add("cooldownSeconds 不能为负数。");
        ValidateConditions(trigger.Conditions, errors);
    }

    private static void ValidateAt(AutomationTriggerDefinition trigger, List<string> errors)
    {
        if (!TimeOnly.TryParse(trigger.At, out _))
            errors.Add($"{trigger.Type} 触发器的 at 必须是 HH:mm 或 HH:mm:ss。");
    }

    private static void ValidateConditions(IEnumerable<AutomationConditionDefinition> conditions, List<string> errors)
    {
        foreach (var condition in conditions)
        {
            if (!ConditionTypes.Contains(condition.Type))
                errors.Add($"不支持的条件类型：{condition.Type}。");
            if (!Operators.Contains(condition.Operator))
                errors.Add($"不支持的条件比较器：{condition.Operator}。");
            if (condition.Type.Equals("adb.output", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(AutomationParameterReader.GetString(condition.Parameters, "command")))
                errors.Add("adb.output 条件必须提供 command。");
            if (condition.Type.Equals("time.window", StringComparison.OrdinalIgnoreCase) &&
                (!TimeOnly.TryParse(AutomationParameterReader.GetString(condition.Parameters, "start"), out _) ||
                 !TimeOnly.TryParse(AutomationParameterReader.GetString(condition.Parameters, "end"), out _)))
                errors.Add("time.window 条件必须提供有效的 start 和 end。");
        }
    }

    private static void ValidateActions(
        AutomationTaskDefinition definition,
        IEnumerable<AutomationActionDefinition> actions,
        List<string> errors,
        int depth,
        ref int actionCount)
    {
        if (depth > 16)
        {
            errors.Add("动作嵌套不能超过 16 层。");
            return;
        }

        foreach (var action in actions)
        {
            actionCount++;
            if (!ActionTypes.Contains(action.Type))
            {
                errors.Add($"不支持的动作类型：{action.Type}。");
                continue;
            }

            var type = action.Type.ToLowerInvariant();
            if (type.StartsWith("adb.", StringComparison.Ordinal) || type is "app.start" or "url.open" or "intent.start" or "device.wake" or "device.lock" or "device.unlock")
            {
                if (!definition.Permissions.AllowAdb)
                    errors.Add($"动作 {action.Type} 需要 permissions.allowAdb=true。");
            }
            if (type is "adb.shell" or "intent.start" && !definition.Permissions.AllowShell)
                errors.Add($"动作 {action.Type} 需要 permissions.allowShell=true。");
            if (type == "companion.call" && !definition.Permissions.AllowCompanion)
                errors.Add("companion.call 需要 permissions.allowCompanion=true。");
            if (type == "ai.prompt" && !definition.Permissions.AllowAi)
                errors.Add("ai.prompt 需要 permissions.allowAi=true。");
            if (type == "device.unlock" && !definition.Permissions.AllowUnlock)
                errors.Add("device.unlock 需要 permissions.allowUnlock=true。");

            ValidateActionParameters(action, errors);
            ValidateConditions(action.Conditions, errors);
            ValidateActions(definition, action.Actions, errors, depth + 1, ref actionCount);
            ValidateActions(definition, action.ElseActions, errors, depth + 1, ref actionCount);
        }
    }

    private static void ValidateActionParameters(AutomationActionDefinition action, List<string> errors)
    {
        string Required(string name)
        {
            var value = AutomationParameterReader.GetString(action.Parameters, name);
            if (string.IsNullOrWhiteSpace(value))
                errors.Add($"{action.Type} 必须提供 {name}。");
            return value;
        }

        switch (action.Type.ToLowerInvariant())
        {
            case "adb.shell": Required("command"); break;
            case "adb.keyevent": Required("keyCode"); break;
            case "adb.text": Required("text"); break;
            case "app.start": Required("packageName"); break;
            case "url.open": Required("url"); break;
            case "companion.call": Required("capabilityId"); Required("operation"); break;
            case "ai.prompt": Required("prompt"); break;
            case "flow.if" when action.Conditions.Count == 0: errors.Add("flow.if 必须至少包含一个条件。"); break;
            case "flow.repeat" when AutomationParameterReader.GetInt(action.Parameters, "count") is < 1 or > 1000:
                errors.Add("flow.repeat 的 count 必须在 1 到 1000 之间。");
                break;
            case "flow.parallel" when AutomationParameterReader.GetInt(action.Parameters, "maxConcurrency", 4) is < 1 or > 32:
                errors.Add("flow.parallel 的 maxConcurrency 必须在 1 到 32 之间。");
                break;
            case "delay" when AutomationParameterReader.GetInt(action.Parameters, "milliseconds") < 0:
                errors.Add("delay 的 milliseconds 不能为负数。");
                break;
            case "condition.wait" when action.Conditions.Count == 0:
                errors.Add("condition.wait 必须至少包含一个条件。");
                break;
        }
    }

    private static bool ContainsDeviceAction(IEnumerable<AutomationActionDefinition> actions)
    {
        foreach (var action in actions)
        {
            if (!IsLocalAction(action.Type) || action.Conditions.Any(condition => !IsLocalCondition(condition.Type)))
                return true;
            if (ContainsDeviceAction(action.Actions) || ContainsDeviceAction(action.ElseActions))
                return true;
        }
        return false;
    }

    private static IEnumerable<AutomationConditionDefinition> EnumerateActionConditions(IEnumerable<AutomationActionDefinition> actions)
    {
        foreach (var action in actions)
        {
            foreach (var condition in action.Conditions)
                yield return condition;
            foreach (var condition in EnumerateActionConditions(action.Actions))
                yield return condition;
            foreach (var condition in EnumerateActionConditions(action.ElseActions))
                yield return condition;
        }
    }

    private static bool RequiresCompanion(AutomationConditionDefinition condition) =>
        condition.Type.StartsWith("companion.", StringComparison.OrdinalIgnoreCase) ||
        condition.Type.Equals("clipboard.contains", StringComparison.OrdinalIgnoreCase);

    private static bool IsLocalAction(string type) => type.Equals("log", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("fail", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("delay", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("ai.prompt", StringComparison.OrdinalIgnoreCase) ||
        type.StartsWith("flow.", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("condition.wait", StringComparison.OrdinalIgnoreCase);

    private static bool IsLocalCondition(string type) => type.Equals("time.window", StringComparison.OrdinalIgnoreCase);
}
