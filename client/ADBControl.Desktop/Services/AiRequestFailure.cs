using System.Text.Json;
using ADBControl.Desktop.Models;

namespace ADBControl.Desktop.Services;

public enum AiRequestFailureCategory
{
    Configuration,
    Compatibility,
    Authentication,
    Authorization,
    Validation,
    Payload,
    RateLimit,
    Timeout,
    Network,
    Provider,
    InvalidResponse,
    Unknown,
}

public sealed record AiRequestFailure(
    string ErrorCode,
    AiRequestFailureCategory Category,
    string Title,
    string Summary,
    string Detail,
    string Suggestion,
    string TraceId,
    int? HttpStatusCode,
    bool Recoverable,
    string ProviderErrorType = "",
    string ProviderErrorCode = "",
    string ProviderMessage = "")
{
    public AiChatErrorDetails ToChatError() => new()
    {
        ErrorCode = ErrorCode,
        Category = Category.ToString(),
        Title = Title,
        Summary = Summary,
        Detail = Detail,
        Suggestion = Suggestion,
        TraceId = TraceId,
        HttpStatusCode = HttpStatusCode,
        Recoverable = Recoverable,
    };
}

public sealed class AiRequestException : Exception
{
    public AiRequestException(AiRequestFailure failure, Exception? innerException = null)
        : base(failure.Summary, innerException)
    {
        Failure = failure;
    }

    public AiRequestFailure Failure { get; }
}

public static class AiRequestFailureClassifier
{
    public static AiRequestFailure FromHttp(
        int statusCode,
        string? reasonPhrase,
        string responseBody,
        string traceId)
    {
        var provider = ReadProviderError(responseBody);
        var detail = BuildHttpDetail(statusCode, reasonPhrase, responseBody);
        if (AiProviderCompatibility.IsImageContentUnsupported(statusCode, responseBody))
        {
            return Create(
                "AI_REQUEST_IMAGE_UNSUPPORTED", AiRequestFailureCategory.Compatibility,
                "当前模型不支持图像", "模型服务拒绝了截图或图像附件。",
                detail, "已自动兼容文本模型；如仍需识图，请切换支持视觉输入的模型。",
                traceId, statusCode, true, provider);
        }

        return statusCode switch
        {
            400 => Create("AI_REQUEST_INVALID", AiRequestFailureCategory.Validation, "AI 请求格式无效", "模型服务拒绝了本次请求。", detail, "请检查模型兼容性和请求内容后重试。", traceId, statusCode, false, provider),
            401 => Create("AI_AUTHENTICATION_FAILED", AiRequestFailureCategory.Authentication, "AI 鉴权失败", "API Key 无效、过期或未被服务接受。", detail, "请在设置中检查 API Key。", traceId, statusCode, false, provider),
            403 => Create("AI_ACCESS_DENIED", AiRequestFailureCategory.Authorization, "AI 访问被拒绝", "当前凭据无权访问该模型或接口。", detail, "请检查账号权限、模型授权和服务区域限制。", traceId, statusCode, false, provider),
            404 => Create("AI_ENDPOINT_OR_MODEL_NOT_FOUND", AiRequestFailureCategory.Configuration, "AI 接口或模型不存在", "未找到配置的接口地址或模型。", detail, "请检查 API URL 与模型标识。", traceId, statusCode, false, provider),
            408 => Create("AI_REQUEST_TIMEOUT", AiRequestFailureCategory.Timeout, "AI 请求超时", "模型服务未在限定时间内完成请求。", detail, "请检查网络后重试。", traceId, statusCode, true, provider),
            413 => Create("AI_REQUEST_TOO_LARGE", AiRequestFailureCategory.Payload, "AI 请求内容过大", "对话历史或附件超过服务限制。", detail, "请减少附件、缩短上下文或开始新对话。", traceId, statusCode, true, provider),
            429 => Create("AI_RATE_LIMITED", AiRequestFailureCategory.RateLimit, "AI 请求过于频繁", "服务正在限流或额度暂时不足。", detail, "请稍后重试并检查账户额度。", traceId, statusCode, true, provider),
            >= 500 and <= 599 => Create("AI_PROVIDER_UNAVAILABLE", AiRequestFailureCategory.Provider, "AI 服务暂时不可用", "模型服务发生内部错误。", detail, "请稍后重试；持续发生时请检查服务状态。", traceId, statusCode, true, provider),
            _ => Create("AI_HTTP_FAILED", AiRequestFailureCategory.Unknown, "AI 请求失败", $"模型服务返回 HTTP {statusCode}。", detail, "请展开详情检查服务返回信息。", traceId, statusCode, false, provider),
        };
    }

    public static AiRequestFailure FromException(Exception exception, string traceId, bool timedOut = false)
    {
        if (exception is AiRequestException requestException)
            return requestException.Failure;
        if (timedOut || exception is TaskCanceledException)
        {
            return new AiRequestFailure(
                "AI_REQUEST_TIMEOUT", AiRequestFailureCategory.Timeout, "AI 请求超时",
                "模型服务未在限定时间内完成请求。", exception.ToString(), "请检查网络后重试。",
                traceId, null, true);
        }
        if (exception is HttpRequestException)
        {
            return new AiRequestFailure(
                "AI_NETWORK_FAILED", AiRequestFailureCategory.Network, "AI 网络连接失败",
                "无法连接到模型服务。", exception.ToString(), "请检查网络、代理、DNS 和 API 地址。",
                traceId, null, true);
        }
        if (exception is JsonException)
        {
            return new AiRequestFailure(
                "AI_RESPONSE_INVALID", AiRequestFailureCategory.InvalidResponse, "AI 响应无法解析",
                "模型服务返回了客户端无法识别的数据。", exception.ToString(), "请确认接口兼容 OpenAI Chat Completions 格式。",
                traceId, null, false);
        }
        if (exception is UriFormatException or InvalidOperationException or ArgumentException)
        {
            return new AiRequestFailure(
                "AI_CONFIGURATION_INVALID", AiRequestFailureCategory.Configuration, "AI 配置无效",
                exception.Message, exception.ToString(), "请检查模型、API URL 和 API Key 设置。",
                traceId, null, false);
        }

        return new AiRequestFailure(
            "AI_REQUEST_UNEXPECTED", AiRequestFailureCategory.Unknown, "AI 请求发生异常",
            "处理 AI 请求时发生未预期错误。", exception.ToString(), "请展开详情并使用 traceId 检查日志。",
            traceId, null, false);
    }

    private static AiRequestFailure Create(
        string errorCode,
        AiRequestFailureCategory category,
        string title,
        string summary,
        string detail,
        string suggestion,
        string traceId,
        int statusCode,
        bool recoverable,
        ProviderError provider)
        => new(errorCode, category, title, summary, detail, suggestion, traceId, statusCode, recoverable, provider.Type, provider.Code, provider.Message);

    private static string BuildHttpDetail(int statusCode, string? reasonPhrase, string responseBody)
    {
        var body = string.IsNullOrWhiteSpace(responseBody) ? "(响应正文为空)" : responseBody.Trim();
        if (body.Length > 8000)
            body = body[..8000] + "...";
        return $"HTTP {statusCode} {reasonPhrase}\n{body}";
    }

    private static ProviderError ReadProviderError(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                root = error;
            return new ProviderError(ReadString(root, "type"), ReadString(root, "code"), ReadString(root, "message"));
        }
        catch (JsonException)
        {
            return new ProviderError(string.Empty, string.Empty, string.Empty);
        }
    }

    private static string ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            return string.Empty;
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
    }

    private readonly record struct ProviderError(string Type, string Code, string Message);
}
