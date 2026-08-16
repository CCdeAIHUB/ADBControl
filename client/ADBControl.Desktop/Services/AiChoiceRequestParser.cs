using System.Text.Json;
using ADBControl.Desktop.Models;

namespace ADBControl.Desktop.Services;

public static class AiChoiceRequestParser
{
    public static bool TryParse(
        AiAgentToolCall toolCall,
        out AiChoiceRequest? request,
        out string errorCode,
        out string errorMessage)
    {
        request = null;
        errorCode = string.Empty;
        errorMessage = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(toolCall.ArgumentsJson);
            var root = document.RootElement;
            var question = ReadString(root, "question").Trim();
            if (question.Length is < 1 or > 240)
                return Fail("AI_CHOICE_QUESTION_INVALID", "交互问题必须包含 1-240 个字符。", out errorCode, out errorMessage);

            var modeText = ReadString(root, "selectionMode");
            var mode = modeText switch
            {
                "single" => AiChoiceSelectionMode.Single,
                "multiple" => AiChoiceSelectionMode.Multiple,
                _ => (AiChoiceSelectionMode?)null,
            };
            if (mode is null)
                return Fail("AI_CHOICE_MODE_INVALID", "交互提问只支持 single 或 multiple。", out errorCode, out errorMessage);

            if (!root.TryGetProperty("options", out var optionsElement) || optionsElement.ValueKind != JsonValueKind.Array)
                return Fail("AI_CHOICE_OPTIONS_INVALID", "交互提问必须提供选项数组。", out errorCode, out errorMessage);

            var options = optionsElement.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()?.Trim() ?? string.Empty)
                .Where(item => item.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (options.Count is < 2 or > 8 || options.Any(option => option.Length > 80))
                return Fail("AI_CHOICE_OPTIONS_INVALID", "交互提问必须提供 2-8 个不超过 80 字符的唯一选项。", out errorCode, out errorMessage);

            request = new AiChoiceRequest
            {
                ToolCallId = toolCall.Id,
                Question = question,
                SelectionMode = mode.Value,
                Options = options,
            };
            return true;
        }
        catch (JsonException ex)
        {
            return Fail("AI_CHOICE_JSON_INVALID", $"交互提问参数不是有效 JSON：{ex.Message}", out errorCode, out errorMessage);
        }
    }

    public static string BuildResultJson(AiChoiceRequest request)
        => JsonSerializer.Serialize(new
        {
            selectionMode = request.SelectionMode == AiChoiceSelectionMode.Single ? "single" : "multiple",
            selectedOptions = request.SelectedOptions,
        });

    private static string ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool Fail(string code, string message, out string errorCode, out string errorMessage)
    {
        errorCode = code;
        errorMessage = message;
        return false;
    }
}
