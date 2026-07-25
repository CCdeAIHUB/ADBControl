using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ADBControl.Desktop.Models;

namespace ADBControl.Desktop.Services.Automation;

public static class AutomationTaskSerializer
{
    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();

    public static AutomationTaskDefinition Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new AutomationExecutionException("TASK_DEFINITION_EMPTY", "任务 JSON 不能为空。", "automation.schema");

        try
        {
            var definition = JsonSerializer.Deserialize<AutomationTaskDefinition>(json, JsonOptions)
                ?? throw new AutomationExecutionException("TASK_DEFINITION_EMPTY", "任务 JSON 没有生成任务对象。", "automation.schema");
            Normalize(definition);
            return definition;
        }
        catch (JsonException ex)
        {
            throw new AutomationExecutionException(
                "TASK_SCHEMA_INVALID_JSON",
                $"任务 JSON 无法解析：{ex.Message}",
                "automation.schema",
                suggestion: "检查属性名、引号、逗号和枚举值。",
                innerException: ex);
        }
    }

    public static string Serialize(AutomationTaskDefinition definition)
    {
        Normalize(definition);
        return JsonSerializer.Serialize(definition, JsonOptions);
    }

    public static AutomationTaskDefinition Clone(AutomationTaskDefinition definition) => Deserialize(Serialize(definition));

    public static string CreateTemplate(string? deviceId = null)
    {
        var definition = new AutomationTaskDefinition
        {
            Name = "新建自动化任务",
            DeviceId = deviceId ?? string.Empty,
            Enabled = true,
            Triggers =
            [
                new AutomationTriggerDefinition { Type = "manual" },
            ],
            Actions =
            [
                new AutomationActionDefinition
                {
                    Type = "log",
                    Parameters = AutomationParameterReader.Create(("message", "任务开始")),
                },
            ],
        };
        return Serialize(definition);
    }

    public const string AiContract = """
任务定义使用 JSON DSL。根字段：schemaVersion=1、name、description、deviceId、enabled、concurrencyPolicy(skip|queue|restart|parallel)、permissions、triggers、actions。
permissions 字段：allowAdb、allowShell、allowCompanion、allowAi、allowAiDeviceTools、allowTaskMutation、allowUnlock，必须按动作真实声明。
triggers 类型：manual；once(runAt)；daily(at,timeZoneId)；weekly(days,at,timeZoneId)；interval(intervalSeconds)；cron(cron,timeZoneId)；condition(conditions,conditionMode,pollIntervalSeconds,edgeOnly,cooldownSeconds)。时间触发器也可带 conditions。days 使用 Monday..Sunday。
condition 格式：{type,operator,caseSensitive,negate,parameters:{value,...}}。类型包括 device.connected/device.authorized/device.property、screen.on/screen.locked/screen.orientation、app.foreground/activity.foreground/app.installed/process.running/webpage.open、ui.element/ui.text、notification.present、battery.level/battery.charging/battery.temperature、network.connected/network.type/wifi.ssid/internet.reachable、bluetooth.enabled/airplane.enabled/location.enabled/dnd.enabled/setting.value、call.state/headset.connected/file.exists/clipboard.contains、companion.installed/companion.accessibility.ready/companion.output、adb.output/time.window。
operator 支持 equals/notEquals/contains/notContains/startsWith/endsWith/regex/greaterThan/greaterThanOrEqual/lessThan/lessThanOrEqual/in/exists/truthy。
actions 类型：adb.shell(command)、adb.keyEvent(keyCode)、adb.tap(x,y)、adb.swipe(startX,startY,endX,endY,durationMs)、adb.text(text)、app.start(packageName,activity)、url.open(url)、intent.start(action,data,component,extras)、device.wake、device.lock、device.unlock(pin)、companion.call(capabilityId,operation,args)、ai.prompt(prompt,modelId)、delay(milliseconds)、condition.wait(conditions,conditionMode,timeoutSeconds,pollIntervalSeconds)、flow.if(conditions,actions,elseActions)、flow.repeat(count,actions)、flow.parallel(actions,maxConcurrency)、log(message,level)、fail(message,errorCode)。
创建任务时输出完整 definition 对象，不要省略 name、enabled、permissions、triggers、actions。不要伪造执行结果。
""";

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static void Normalize(AutomationTaskDefinition definition)
    {
        definition.Id = string.IsNullOrWhiteSpace(definition.Id) ? Guid.NewGuid().ToString("N") : definition.Id.Trim();
        definition.Name = definition.Name?.Trim() ?? string.Empty;
        definition.Description ??= string.Empty;
        definition.DeviceId ??= string.Empty;
        definition.Permissions ??= new AutomationPermissionSet();
        definition.Triggers ??= new List<AutomationTriggerDefinition>();
        definition.Actions ??= new List<AutomationActionDefinition>();
        if (definition.CreatedAt == default)
            definition.CreatedAt = DateTimeOffset.UtcNow;
        if (definition.UpdatedAt == default)
            definition.UpdatedAt = definition.CreatedAt;

        foreach (var trigger in definition.Triggers)
        {
            trigger.Id = string.IsNullOrWhiteSpace(trigger.Id) ? Guid.NewGuid().ToString("N") : trigger.Id.Trim();
            trigger.Type = trigger.Type?.Trim() ?? string.Empty;
            trigger.TimeZoneId = string.IsNullOrWhiteSpace(trigger.TimeZoneId) ? TimeZoneInfo.Local.Id : trigger.TimeZoneId.Trim();
            trigger.Days ??= new List<DayOfWeek>();
            trigger.Conditions ??= new List<AutomationConditionDefinition>();
            NormalizeConditions(trigger.Conditions);
        }

        NormalizeActions(definition.Actions);
    }

    private static void NormalizeActions(IEnumerable<AutomationActionDefinition> actions)
    {
        foreach (var action in actions)
        {
            action.Id = string.IsNullOrWhiteSpace(action.Id) ? Guid.NewGuid().ToString("N") : action.Id.Trim();
            action.Type = action.Type?.Trim() ?? string.Empty;
            action.Parameters ??= new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            action.Conditions ??= new List<AutomationConditionDefinition>();
            action.Actions ??= new List<AutomationActionDefinition>();
            action.ElseActions ??= new List<AutomationActionDefinition>();
            NormalizeConditions(action.Conditions);
            NormalizeActions(action.Actions);
            NormalizeActions(action.ElseActions);
        }
    }

    private static void NormalizeConditions(IEnumerable<AutomationConditionDefinition> conditions)
    {
        foreach (var condition in conditions)
        {
            condition.Type = condition.Type?.Trim() ?? string.Empty;
            condition.Operator = string.IsNullOrWhiteSpace(condition.Operator) ? "equals" : condition.Operator.Trim();
            condition.Parameters ??= new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        }
    }
}

public static class AutomationParameterReader
{
    public static Dictionary<string, JsonElement> Create(params (string Name, object? Value)[] values)
    {
        return values.ToDictionary(
            pair => pair.Name,
            pair => JsonSerializer.SerializeToElement(pair.Value, AutomationTaskSerializer.JsonOptions),
            StringComparer.OrdinalIgnoreCase);
    }

    public static string GetString(IReadOnlyDictionary<string, JsonElement> parameters, string name, string defaultValue = "")
    {
        if (!parameters.TryGetValue(name, out var value))
            return defaultValue;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? defaultValue,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            _ => defaultValue,
        };
    }

    public static int GetInt(IReadOnlyDictionary<string, JsonElement> parameters, string name, int defaultValue = 0)
    {
        if (!parameters.TryGetValue(name, out var value))
            return defaultValue;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        return int.TryParse(GetString(parameters, name), out number) ? number : defaultValue;
    }

    public static bool GetBool(IReadOnlyDictionary<string, JsonElement> parameters, string name, bool defaultValue = false)
    {
        if (!parameters.TryGetValue(name, out var value))
            return defaultValue;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return value.GetBoolean();
        return bool.TryParse(GetString(parameters, name), out var parsed) ? parsed : defaultValue;
    }

    public static IReadOnlyDictionary<string, object?> GetObject(IReadOnlyDictionary<string, JsonElement> parameters, string name)
    {
        if (!parameters.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, object?>();
        return value.EnumerateObject().ToDictionary(property => property.Name, property => ToObject(property.Value));
    }

    private static object? ToObject(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Object => value.EnumerateObject().ToDictionary(property => property.Name, property => ToObject(property.Value)),
            JsonValueKind.Array => value.EnumerateArray().Select(ToObject).ToList(),
            _ => value.GetRawText(),
        };
    }
}
