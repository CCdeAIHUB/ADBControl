using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ADBControl.Desktop.Models;

namespace ADBControl.Desktop.Services;

public sealed class AiService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(90),
    };

    public async Task<AiAgentResponse> SendAsync(
        AiAgentRequest request,
        Func<AiAgentToolCall, Task<AiAgentToolResult>> toolExecutor,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Model.ModelId))
            throw new InvalidOperationException("AI 模型标识不能为空。");
        if (string.IsNullOrWhiteSpace(request.Model.ApiUrl))
            throw new InvalidOperationException("AI API URL 不能为空。");
        if (string.IsNullOrWhiteSpace(request.Model.ApiKey))
            throw new InvalidOperationException("AI API Key 不能为空。");

        var endpoint = NormalizeChatCompletionsEndpoint(request.Model.ApiUrl);
        var messages = await BuildMessagesAsync(request, cancellationToken);
        var newMessages = new List<AiConversationMessage>();

        while (true)
        {
            var payload = new Dictionary<string, object?>
            {
                ["model"] = request.Model.ModelId,
                ["messages"] = messages,
                ["tools"] = BuildTools(),
                ["tool_choice"] = "auto",
                ["temperature"] = 0.2,
            };

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
            };
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.Model.ApiKey);

            using var response = await _http.SendAsync(httpRequest, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"AI 请求失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}\n{TrimForUi(json)}");

            var assistant = ParseAssistantMessage(json);
            messages.Add(assistant.RawMessage);
            newMessages.Add(assistant.ConversationMessage);

            if (assistant.ToolCalls.Count == 0)
            {
                return new AiAgentResponse
                {
                    Text = assistant.Text,
                    NewMessages = newMessages,
                };
            }

            foreach (var toolCall in assistant.ToolCalls)
            {
                var toolResult = await toolExecutor(toolCall);
                var toolContent = JsonSerializer.Serialize(new
                {
                    success = toolResult.Success,
                    content = toolResult.Content,
                }, JsonOptions);
                messages.Add(new Dictionary<string, object?>
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = toolCall.Id,
                    ["content"] = toolContent,
                });
                newMessages.Add(new AiConversationMessage
                {
                    Role = "tool",
                    ToolCallId = toolCall.Id,
                    ToolName = toolCall.Name,
                    Text = toolContent,
                });
            }
        }

    }

    public async Task<AiAgentResponse> SendStreamingAsync(
        AiAgentRequest request,
        Func<AiStreamDelta, Task> onDelta,
        Func<AiAgentToolCall, Task<AiAgentToolResult>> toolExecutor,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Model.ModelId))
            throw new InvalidOperationException("AI 模型标识不能为空。");
        if (string.IsNullOrWhiteSpace(request.Model.ApiUrl))
            throw new InvalidOperationException("AI API URL 不能为空。");
        if (string.IsNullOrWhiteSpace(request.Model.ApiKey))
            throw new InvalidOperationException("AI API Key 不能为空。");

        var endpoint = NormalizeChatCompletionsEndpoint(request.Model.ApiUrl);
        var messages = await BuildMessagesAsync(request, cancellationToken);
        var newMessages = new List<AiConversationMessage>();
        var finalText = new StringBuilder();
        var finalThinking = new StringBuilder();

        while (true)
        {
            var payload = new Dictionary<string, object?>
            {
                ["model"] = request.Model.ModelId,
                ["messages"] = messages,
                ["tools"] = BuildTools(),
                ["tool_choice"] = "auto",
                ["temperature"] = 0.2,
                ["stream"] = true,
            };

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
            };
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.Model.ApiKey);

            using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorJson = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException($"AI 请求失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}\n{TrimForUi(errorJson)}");
            }

            var streamResult = await ReadStreamingAssistantAsync(response, onDelta, cancellationToken);
            finalText.Append(streamResult.Text);
            finalThinking.Append(streamResult.ThinkingText);
            messages.Add(streamResult.RawMessage);
            newMessages.Add(streamResult.ConversationMessage);

            if (streamResult.ToolCalls.Count == 0)
            {
                return new AiAgentResponse
                {
                    Text = finalText.ToString(),
                    ThinkingText = finalThinking.ToString(),
                    NewMessages = newMessages,
                };
            }

            foreach (var toolCall in streamResult.ToolCalls)
            {
                var toolResult = await toolExecutor(toolCall);
                var toolContent = JsonSerializer.Serialize(new
                {
                    success = toolResult.Success,
                    content = toolResult.Content,
                }, JsonOptions);
                messages.Add(new Dictionary<string, object?>
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = toolCall.Id,
                    ["content"] = toolContent,
                });
                newMessages.Add(new AiConversationMessage
                {
                    Role = "tool",
                    ToolCallId = toolCall.Id,
                    ToolName = toolCall.Name,
                    Text = toolContent,
                });
            }
        }

    }

    private static Uri NormalizeChatCompletionsEndpoint(string apiUrl)
    {
        var trimmed = apiUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("AI API URL 不是有效的绝对地址。");

        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return uri;

        return new Uri($"{trimmed.TrimEnd('/')}/chat/completions");
    }

    private static async Task<List<Dictionary<string, object?>>> BuildMessagesAsync(AiAgentRequest request, CancellationToken cancellationToken)
    {
        var messages = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["role"] = "system",
                ["content"] = BuildSystemPrompt(request),
            },
        };

        foreach (var message in request.Messages)
            messages.Add(await BuildMessageAsync(message, cancellationToken));

        return messages;
    }

    private static string BuildSystemPrompt(AiAgentRequest request)
    {
        var device = string.IsNullOrWhiteSpace(request.CurrentDeviceId)
            ? "当前没有打开的设备详情页。"
            : $"当前设备：{request.CurrentDeviceName} ({request.CurrentDeviceId})。";
        return
            "你是 ADBControl 内的 AI Agent。你需要用中文简洁回应用户。" +
            "当需要操作 Android 设备时，只能通过工具调用执行，不能编造执行结果。" +
            $"权限模式：{request.PermissionMode}。{device}" +
            "可用工具：adb_shell，用于在当前设备执行 adb shell 命令。";
    }

    private static async Task<Dictionary<string, object?>> BuildMessageAsync(AiConversationMessage message, CancellationToken cancellationToken)
    {
        if (message.Role == "tool")
        {
            return new Dictionary<string, object?>
            {
                ["role"] = "tool",
                ["tool_call_id"] = message.ToolCallId,
                ["content"] = message.Text,
            };
        }

        if (message.Role == "assistant" && message.ToolCalls.Count > 0)
        {
            return new Dictionary<string, object?>
            {
                ["role"] = "assistant",
                ["content"] = string.IsNullOrWhiteSpace(message.Text) ? null : message.Text,
                ["tool_calls"] = message.ToolCalls.Select(ToToolCallPayload).ToList(),
            };
        }

        if (message.Role == "user" && message.Attachments.Count > 0)
        {
            var content = new List<Dictionary<string, object?>>();
            if (!string.IsNullOrWhiteSpace(message.Text))
            {
                content.Add(new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = message.Text,
                });
            }

            foreach (var attachment in message.Attachments.Where(a => a.IsImage))
            {
                content.Add(new Dictionary<string, object?>
                {
                    ["type"] = "image_url",
                    ["image_url"] = new Dictionary<string, object?>
                    {
                        ["url"] = await BuildDataUrlAsync(attachment, cancellationToken),
                    },
                });
            }

            return new Dictionary<string, object?>
            {
                ["role"] = "user",
                ["content"] = content,
            };
        }

        return new Dictionary<string, object?>
        {
            ["role"] = string.IsNullOrWhiteSpace(message.Role) ? "user" : message.Role,
            ["content"] = message.Text,
        };
    }

    private static Dictionary<string, object?> ToToolCallPayload(AiAgentToolCall toolCall)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = toolCall.Id,
            ["type"] = "function",
            ["function"] = new Dictionary<string, object?>
            {
                ["name"] = toolCall.Name,
                ["arguments"] = toolCall.ArgumentsJson,
            },
        };
    }

    private static async Task<string> BuildDataUrlAsync(AiAttachment attachment, CancellationToken cancellationToken)
    {
        if (!File.Exists(attachment.Path))
            throw new InvalidOperationException($"附件文件不存在：{attachment.Name}");

        var bytes = await File.ReadAllBytesAsync(attachment.Path, cancellationToken);
        var mime = Path.GetExtension(attachment.Path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "image/png",
        };
        return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
    }

    private static List<Dictionary<string, object?>> BuildTools()
    {
        return
        [
            new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = "adb_shell",
                    ["description"] = "在当前 Android 设备上执行 adb shell 命令。只在用户需要读取或控制设备时使用。",
                    ["parameters"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["command"] = new Dictionary<string, object?>
                            {
                                ["type"] = "string",
                                ["description"] = "不包含 adb shell 前缀的 shell 命令，例如 getprop ro.product.model。",
                            },
                            ["reason"] = new Dictionary<string, object?>
                            {
                                ["type"] = "string",
                                ["description"] = "为什么需要执行该命令。",
                            },
                        },
                        ["required"] = new[] { "command" },
                    },
                },
            },
        ];
    }

    private static ParsedAssistantMessage ParseAssistantMessage(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            throw new InvalidOperationException("AI 响应缺少 choices。");

        var message = choices[0].GetProperty("message");
        var text = message.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null
            ? content.GetString() ?? string.Empty
            : string.Empty;

        var toolCalls = new List<AiAgentToolCall>();
        if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in calls.EnumerateArray())
            {
                var function = call.GetProperty("function");
                toolCalls.Add(new AiAgentToolCall
                {
                    Id = call.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N"),
                    Name = function.GetProperty("name").GetString() ?? string.Empty,
                    ArgumentsJson = function.TryGetProperty("arguments", out var arguments)
                        ? arguments.GetString() ?? "{}"
                        : "{}",
                });
            }
        }

        var conversation = new AiConversationMessage
        {
            Role = "assistant",
            Text = text,
            ToolCalls = toolCalls,
        };

        return new ParsedAssistantMessage
        {
            Text = text,
            ToolCalls = toolCalls,
            ConversationMessage = conversation,
            RawMessage = new Dictionary<string, object?>
            {
                ["role"] = "assistant",
                ["content"] = string.IsNullOrWhiteSpace(text) ? null : text,
                ["tool_calls"] = toolCalls.Count == 0 ? null : toolCalls.Select(ToToolCallPayload).ToList(),
            },
        };
    }

    private static async Task<ParsedAssistantMessage> ReadStreamingAssistantAsync(
        HttpResponseMessage response,
        Func<AiStreamDelta, Task> onDelta,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var text = new StringBuilder();
        var thinking = new StringBuilder();
        var toolBuilders = new Dictionary<int, StreamingToolCallBuilder>();

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var data = line["data:".Length..].Trim();
            if (data == "[DONE]")
                break;

            using var document = JsonDocument.Parse(data);
            if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                continue;

            var choice = choices[0];
            if (!choice.TryGetProperty("delta", out var delta))
                continue;

            var chunk = ExtractStreamingSegment(choice, delta);
            if (chunk.Text.Length > 0)
            {
                if (chunk.IsThinking)
                    thinking.Append(chunk.Text);
                else
                    text.Append(chunk.Text);
                await onDelta(chunk);
            }

            if (delta.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
            {
                foreach (var call in calls.EnumerateArray())
                {
                    var index = call.TryGetProperty("index", out var indexElement) ? indexElement.GetInt32() : toolBuilders.Count;
                    if (!toolBuilders.TryGetValue(index, out var builder))
                    {
                        builder = new StreamingToolCallBuilder();
                        toolBuilders[index] = builder;
                    }

                    if (call.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        builder.Id ??= id.GetString();
                    if (call.TryGetProperty("function", out var function))
                    {
                        if (function.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                            builder.Name ??= name.GetString();
                        if (function.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.String)
                            builder.Arguments.Append(args.GetString());
                    }
                }
            }
        }

        var toolCalls = toolBuilders
            .OrderBy(pair => pair.Key)
            .Select(pair => pair.Value.ToToolCall())
            .Where(call => !string.IsNullOrWhiteSpace(call.Name))
            .ToList();

        var conversation = new AiConversationMessage
        {
            Role = "assistant",
            Text = text.ToString(),
            ThinkingText = thinking.ToString(),
            ToolCalls = toolCalls,
        };
        return new ParsedAssistantMessage
        {
            Text = text.ToString(),
            ThinkingText = thinking.ToString(),
            ToolCalls = toolCalls,
            ConversationMessage = conversation,
            RawMessage = new Dictionary<string, object?>
            {
                ["role"] = "assistant",
                ["content"] = text.Length == 0 ? null : text.ToString(),
                ["tool_calls"] = toolCalls.Count == 0 ? null : toolCalls.Select(ToToolCallPayload).ToList(),
            },
        };
    }

    private static string ExtractStreamingText(JsonElement choice, JsonElement delta)
    {
        return ExtractStreamingSegment(choice, delta).Text;
    }

    private static AiStreamDelta ExtractStreamingSegment(JsonElement choice, JsonElement delta)
    {
        foreach (var name in new[] { "reasoning_content", "reasoning" })
        {
            if (delta.TryGetProperty(name, out var value))
            {
                var text = ReadTextValue(value);
                if (!string.IsNullOrEmpty(text))
                    return new AiStreamDelta { Text = text, IsThinking = true };
            }
        }

        foreach (var name in new[] { "content", "text" })
        {
            if (delta.TryGetProperty(name, out var value))
            {
                var text = ReadTextValue(value);
                if (!string.IsNullOrEmpty(text))
                    return new AiStreamDelta { Text = text, IsThinking = false };
            }
        }

        if (choice.TryGetProperty("message", out var message))
        {
            foreach (var name in new[] { "reasoning_content", "reasoning" })
            {
                if (message.TryGetProperty(name, out var value))
                {
                    var text = ReadTextValue(value);
                    if (!string.IsNullOrEmpty(text))
                        return new AiStreamDelta { Text = text, IsThinking = true };
                }
            }

            foreach (var name in new[] { "content", "text" })
            {
                if (message.TryGetProperty(name, out var value))
                {
                    var text = ReadTextValue(value);
                    if (!string.IsNullOrEmpty(text))
                        return new AiStreamDelta { Text = text, IsThinking = false };
                }
            }
        }

        return new AiStreamDelta();
    }

    private static string ReadTextValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Concat(value.EnumerateArray().Select(ReadTextValue)),
            JsonValueKind.Object when value.TryGetProperty("text", out var text) => ReadTextValue(text),
            _ => string.Empty,
        };
    }

    private static string TrimForUi(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        return value.Length <= 1200 ? value : value[..1200] + "...";
    }

    private sealed class ParsedAssistantMessage
    {
        public string Text { get; init; } = string.Empty;
        public string ThinkingText { get; init; } = string.Empty;
        public List<AiAgentToolCall> ToolCalls { get; init; } = new();
        public AiConversationMessage ConversationMessage { get; init; } = new();
        public Dictionary<string, object?> RawMessage { get; init; } = new();
    }

    private sealed class StreamingToolCallBuilder
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();

        public AiAgentToolCall ToToolCall()
        {
            return new AiAgentToolCall
            {
                Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id,
                Name = Name ?? string.Empty,
                ArgumentsJson = Arguments.Length == 0 ? "{}" : Arguments.ToString(),
            };
        }
    }
}
