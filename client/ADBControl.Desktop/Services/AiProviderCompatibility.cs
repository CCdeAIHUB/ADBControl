using ADBControl.Desktop.Models;

namespace ADBControl.Desktop.Services;

public readonly record struct AiProviderProfile(string ProviderName, bool SupportsImageContent, string Reason);

public static class AiProviderCompatibility
{
    public static AiProviderProfile Resolve(AiModelSettings model)
    {
        var host = Uri.TryCreate(model.ApiUrl, UriKind.Absolute, out var uri)
            ? uri.Host
            : string.Empty;
        if (string.Equals(host, "api.deepseek.com", StringComparison.OrdinalIgnoreCase))
        {
            return new AiProviderProfile(
                "DeepSeek",
                SupportsImageContent: false,
                "该端点已返回只接受 text 内容的契约错误；为避免 HTTP 400，客户端不会发送 image_url 内容块。");
        }

        return new AiProviderProfile(
            string.IsNullOrWhiteSpace(host) ? "OpenAI-compatible" : host,
            SupportsImageContent: true,
            "兼容端点的图像能力未知；若返回明确的不支持错误，客户端将移除图像后受控重试一次。");
    }

    public static IReadOnlyList<AiConversationMessage> PrepareMessages(
        IReadOnlyList<AiConversationMessage> messages,
        AiProviderProfile profile,
        out int omittedImageCount)
    {
        omittedImageCount = 0;
        var prepared = new List<AiConversationMessage>(messages.Count);
        foreach (var message in messages)
        {
            var attachments = new List<AiAttachment>();
            foreach (var attachment in message.Attachments)
            {
                if (!profile.SupportsImageContent && attachment.IsImage)
                {
                    omittedImageCount++;
                    continue;
                }

                attachments.Add(new AiAttachment
                {
                    Name = attachment.Name,
                    Path = attachment.Path,
                    IsImage = attachment.IsImage,
                });
            }

            prepared.Add(new AiConversationMessage
            {
                Role = message.Role,
                Text = message.Text,
                ThinkingText = message.ThinkingText,
                ToolCallId = message.ToolCallId,
                ToolName = message.ToolName,
                Attachments = attachments,
                ToolCalls = message.ToolCalls.Select(call => new AiAgentToolCall
                {
                    Id = call.Id,
                    Name = call.Name,
                    ArgumentsJson = call.ArgumentsJson,
                }).ToList(),
            });
        }

        if (omittedImageCount > 0)
        {
            var note = $"[兼容性提示：当前模型不支持图像输入，本轮已省略 {omittedImageCount} 张图像。]";
            var target = prepared.LastOrDefault(message => string.Equals(message.Role, "user", StringComparison.Ordinal));
            if (target is not null)
                target.Text = string.IsNullOrWhiteSpace(target.Text) ? note : $"{target.Text}\n\n{note}";
        }

        return prepared;
    }

    public static bool IsImageContentUnsupported(int statusCode, string responseBody)
    {
        if (statusCode != 400 || string.IsNullOrWhiteSpace(responseBody))
            return false;

        return responseBody.Contains("image_url", StringComparison.OrdinalIgnoreCase) &&
            (responseBody.Contains("expected", StringComparison.OrdinalIgnoreCase) ||
             responseBody.Contains("unknown variant", StringComparison.OrdinalIgnoreCase) ||
             responseBody.Contains("deserialize", StringComparison.OrdinalIgnoreCase));
    }
}
