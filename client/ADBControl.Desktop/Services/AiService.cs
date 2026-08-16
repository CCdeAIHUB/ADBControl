using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ADBControl.Desktop.Models;
using ADBControl.Desktop.Services.Automation;

namespace ADBControl.Desktop.Services;

public sealed class AiService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly HttpClient _http;
    private readonly IAiRequestLogger _logger;

    public AiService(IAiRequestLogger? logger = null, HttpClient? httpClient = null)
    {
        _logger = logger ?? new AiRequestLogger();
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
    }

    public async Task<AiAgentResponse> SendAsync(
        AiAgentRequest request,
        Func<AiAgentToolCall, Task<AiAgentToolResult>> toolExecutor,
        CancellationToken cancellationToken = default)
    {
        var traceId = Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await SendCoreAsync(request, toolExecutor, traceId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failure = AiRequestFailureClassifier.FromException(ex, traceId, ex is TaskCanceledException);
            await TryLogFailureAsync(request.Model, failure, stopwatch.ElapsedMilliseconds, cancellationToken);
            if (ex is AiRequestException)
                throw;
            throw new AiRequestException(failure, ex);
        }
    }

    private async Task<AiAgentResponse> SendCoreAsync(
        AiAgentRequest request,
        Func<AiAgentToolCall, Task<AiAgentToolResult>> toolExecutor,
        string traceId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Model.ModelId))
            throw new InvalidOperationException("AI 模型标识不能为空。");
        if (string.IsNullOrWhiteSpace(request.Model.ApiUrl))
            throw new InvalidOperationException("AI API URL 不能为空。");
        if (string.IsNullOrWhiteSpace(request.Model.ApiKey))
            throw new InvalidOperationException("AI API Key 不能为空。");

        var endpoint = NormalizeChatCompletionsEndpoint(request.Model.ApiUrl);
        var profile = AiProviderCompatibility.Resolve(request.Model);
        var preparedMessages = AiProviderCompatibility.PrepareMessages(request.Messages, profile, out var omittedImages);
        var warnings = new List<AiAgentWarning>();
        AddImageCompatibilityWarning(warnings, omittedImages, profile.ProviderName);
        var messages = await BuildMessagesAsync(request, preparedMessages, cancellationToken);
        var newMessages = new List<AiConversationMessage>();
        var imageRetryUsed = false;

        while (true)
        {
            var payload = new Dictionary<string, object?>
            {
                ["model"] = request.Model.ModelId,
                ["messages"] = messages,
                ["tools"] = BuildTools(request.AllowInteractiveChoices),
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
            {
                var failure = AiRequestFailureClassifier.FromHttp((int)response.StatusCode, response.ReasonPhrase, json, traceId);
                if (!imageRetryUsed &&
                    failure.ErrorCode == "AI_REQUEST_IMAGE_UNSUPPORTED" &&
                    RemoveImageContent(messages) is var removedImages && removedImages > 0)
                {
                    await TryLogFailureAsync(request.Model, failure, 0, cancellationToken);
                    imageRetryUsed = true;
                    profile = profile with { SupportsImageContent = false };
                    AddImageCompatibilityWarning(warnings, removedImages, profile.ProviderName);
                    continue;
                }
                throw new AiRequestException(failure);
            }

            var assistant = ParseAssistantMessage(json);
            messages.Add(assistant.RawMessage);
            newMessages.Add(assistant.ConversationMessage);

            if (assistant.ToolCalls.Count == 0)
            {
                return new AiAgentResponse
                {
                    Text = assistant.Text,
                    NewMessages = newMessages,
                    Warnings = warnings,
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
        CancellationToken cancellationToken = default,
        Func<IReadOnlyList<AiAgentToolCall>, CancellationToken, Task<IReadOnlyList<AiConversationMessage>>>? afterToolContextProvider = null)
    {
        var traceId = Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await SendStreamingCoreAsync(request, onDelta, toolExecutor, traceId, cancellationToken, afterToolContextProvider);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failure = AiRequestFailureClassifier.FromException(ex, traceId, ex is TaskCanceledException);
            await TryLogFailureAsync(request.Model, failure, stopwatch.ElapsedMilliseconds, cancellationToken);
            if (ex is AiRequestException)
                throw;
            throw new AiRequestException(failure, ex);
        }
    }

    private async Task<AiAgentResponse> SendStreamingCoreAsync(
        AiAgentRequest request,
        Func<AiStreamDelta, Task> onDelta,
        Func<AiAgentToolCall, Task<AiAgentToolResult>> toolExecutor,
        string traceId,
        CancellationToken cancellationToken,
        Func<IReadOnlyList<AiAgentToolCall>, CancellationToken, Task<IReadOnlyList<AiConversationMessage>>>? afterToolContextProvider)
    {
        if (string.IsNullOrWhiteSpace(request.Model.ModelId))
            throw new InvalidOperationException("AI 模型标识不能为空。");
        if (string.IsNullOrWhiteSpace(request.Model.ApiUrl))
            throw new InvalidOperationException("AI API URL 不能为空。");
        if (string.IsNullOrWhiteSpace(request.Model.ApiKey))
            throw new InvalidOperationException("AI API Key 不能为空。");

        var endpoint = NormalizeChatCompletionsEndpoint(request.Model.ApiUrl);
        var profile = AiProviderCompatibility.Resolve(request.Model);
        var preparedMessages = AiProviderCompatibility.PrepareMessages(request.Messages, profile, out var omittedImages);
        var warnings = new List<AiAgentWarning>();
        AddImageCompatibilityWarning(warnings, omittedImages, profile.ProviderName);
        var messages = await BuildMessagesAsync(request, preparedMessages, cancellationToken);
        var newMessages = new List<AiConversationMessage>();
        var finalText = new StringBuilder();
        var finalThinking = new StringBuilder();
        var imageRetryUsed = false;

        while (true)
        {
            var payload = new Dictionary<string, object?>
            {
                ["model"] = request.Model.ModelId,
                ["messages"] = messages,
                ["tools"] = BuildTools(request.AllowInteractiveChoices),
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
                var failure = AiRequestFailureClassifier.FromHttp((int)response.StatusCode, response.ReasonPhrase, errorJson, traceId);
                if (!imageRetryUsed &&
                    failure.ErrorCode == "AI_REQUEST_IMAGE_UNSUPPORTED" &&
                    RemoveImageContent(messages) is var removedImages && removedImages > 0)
                {
                    await TryLogFailureAsync(request.Model, failure, 0, cancellationToken);
                    imageRetryUsed = true;
                    profile = profile with { SupportsImageContent = false };
                    AddImageCompatibilityWarning(warnings, removedImages, profile.ProviderName);
                    continue;
                }
                throw new AiRequestException(failure);
            }

            // SSE providers can emit hundreds of tiny JSON chunks. Parse them on the pool so
            // the caller's WinUI synchronization context remains responsive while streaming.
            var streamResult = await Task.Run(() => ReadStreamingAssistantAsync(
                response,
                onDelta,
                cancellationToken), cancellationToken);
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
                    Warnings = warnings,
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

            if (afterToolContextProvider is not null)
            {
                var contextMessages = await afterToolContextProvider(streamResult.ToolCalls, cancellationToken);
                var preparedContext = AiProviderCompatibility.PrepareMessages(contextMessages, profile, out var omittedContextImages);
                AddImageCompatibilityWarning(warnings, omittedContextImages, profile.ProviderName);
                foreach (var contextMessage in preparedContext)
                {
                    // Post-tool screen observations are scoped to the current request; replaying stale screenshots in later turns would mislead the model.
                    messages.Add(await BuildMessageAsync(contextMessage, cancellationToken));
                }
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

    private static async Task<List<Dictionary<string, object?>>> BuildMessagesAsync(
        AiAgentRequest request,
        IReadOnlyList<AiConversationMessage> conversationMessages,
        CancellationToken cancellationToken)
    {
        var messages = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["role"] = "system",
                ["content"] = BuildSystemPrompt(request),
            },
        };

        foreach (var message in conversationMessages)
            messages.Add(await BuildMessageAsync(message, cancellationToken));

        return messages;
    }

    private static string BuildSystemPrompt(AiAgentRequest request)
    {
        var currentDevice = request.KnownDevices.FirstOrDefault(device =>
            string.Equals(device.DeviceId, request.CurrentDeviceId, StringComparison.OrdinalIgnoreCase));
        if (currentDevice is null && !string.IsNullOrWhiteSpace(request.CurrentDeviceId))
        {
            currentDevice = new DeviceModel
            {
                DeviceId = request.CurrentDeviceId,
                DisplayName = request.CurrentDeviceName ?? request.CurrentDeviceId,
            };
        }
        var deviceContext = AiConversationPolicy.BuildDeviceContext(currentDevice, request.KnownDevices);
        var interactionRules = AiConversationPolicy.BuildInteractionRules(request.AllowInteractiveChoices);
        return
            "你是 ADBControl 内的 AI Agent。你需要用中文简洁回应用户。" +
            "当需要操作 Android 设备时，只能通过工具调用执行，不能编造执行结果。" +
            $"权限模式：{request.PermissionMode}。{deviceContext}{interactionRules}" +
            "查询设备清单、数量或连接状态时直接使用现有设备上下文，必要时调用 device_list，不得要求用户先进入设备详情页。" +
            "只有执行具体设备操作时才需要目标设备；工具参数可传 deviceId。目标不明确时先调用 device_list，再通过单选提问让用户选择，不得自行猜测。" +
            "可用设备工具：adb_shell 用于执行非触控 adb shell；adb_ui_dump 用于读取 Android 页面结构；adb_tap/adb_swipe 用于按绝对像素坐标触控；companion_call 用于调用伴侣 App 暴露的 Android 能力，" +
            "包括 android.input.ime 的 input.text、input.key，以及 android.accessibility.control 的 accessibility.status、accessibility.global.back、accessibility.global.home、accessibility.global.recents、accessibility.global.notifications、accessibility.global.quickSettings、accessibility.global.powerDialog、accessibility.touch.tap、accessibility.touch.swipe。" +
            "需要输入中文或长文本时，优先使用 Companion input.text，不要用 adb_shell input text。" +
            "Companion 触控参数：accessibility.touch.tap 使用 args {x,y}；accessibility.touch.swipe 使用 args {startX,startY,endX,endY,durationMs}。" +
            "禁止通过 adb_shell 执行 input tap、input swipe 或 input touchscreen；必须使用 adb_tap/adb_swipe 或 Companion 触控能力。" +
            "所有坐标都必须使用最新设备截图的原始像素坐标系，原点在左上角，x 向右增加，y 向下增加；点击按钮时优先点击可见控件或 UI dump bounds 的中心，避免贴边点击。" +
            "涉及第三方 App 页面判断时，或执行任何会改变屏幕的点击、滑动、返回、主页、多任务、启动应用后，必须调用 adb_ui_dump 读取当前界面结构；" +
            "桌面端可能会在发送前或工具执行后自动附加当前设备截图；如果 adb_ui_dump 没有返回可用节点，必须优先结合最新截图进行坐标判断，" +
            "仍然不确定时必须说明无法确认当前页面并重新观察，禁止根据历史页面、过期截图印象或按钮位置猜测当前界面。" +
            "你还可以使用 task_list/task_get/task_create/task_update/task_run/task_set_enabled/task_delete 管理自动化任务。创建或修改前必须按下面的 DSL 契约生成完整定义：" +
            AutomationTaskSerializer.AiContract;
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

    private static List<Dictionary<string, object?>> BuildTools(bool allowInteractiveChoices)
    {
        var tools = new List<Dictionary<string, object?>>
        {
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
            new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = "adb_tap",
                    ["description"] = "按当前设备截图的原始像素坐标点击。用于替代 adb_shell input tap；执行前会读取当前截图尺寸并校验坐标是否在屏幕内。",
                    ["parameters"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["x"] = new Dictionary<string, object?>
                            {
                                ["type"] = "integer",
                                ["description"] = "点击点的 x 坐标，使用最新设备截图原始像素坐标。",
                            },
                            ["y"] = new Dictionary<string, object?>
                            {
                                ["type"] = "integer",
                                ["description"] = "点击点的 y 坐标，使用最新设备截图原始像素坐标。",
                            },
                            ["target"] = new Dictionary<string, object?>
                            {
                                ["type"] = "string",
                                ["description"] = "你认为要点击的可见控件或 UI dump 节点名称，用于审批和审计。",
                            },
                            ["reason"] = new Dictionary<string, object?>
                            {
                                ["type"] = "string",
                                ["description"] = "为什么需要点击该位置。",
                            },
                        },
                        ["required"] = new[] { "x", "y", "reason" },
                    },
                },
            },
            new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = "adb_swipe",
                    ["description"] = "按当前设备截图的原始像素坐标滑动。用于替代 adb_shell input swipe；执行前会读取当前截图尺寸并校验起止坐标是否在屏幕内。",
                    ["parameters"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["startX"] = new Dictionary<string, object?>
                            {
                                ["type"] = "integer",
                                ["description"] = "滑动起点 x 坐标。",
                            },
                            ["startY"] = new Dictionary<string, object?>
                            {
                                ["type"] = "integer",
                                ["description"] = "滑动起点 y 坐标。",
                            },
                            ["endX"] = new Dictionary<string, object?>
                            {
                                ["type"] = "integer",
                                ["description"] = "滑动终点 x 坐标。",
                            },
                            ["endY"] = new Dictionary<string, object?>
                            {
                                ["type"] = "integer",
                                ["description"] = "滑动终点 y 坐标。",
                            },
                            ["durationMs"] = new Dictionary<string, object?>
                            {
                                ["type"] = "integer",
                                ["description"] = "滑动持续时间，毫秒。默认 250，最大 3000。",
                            },
                            ["target"] = new Dictionary<string, object?>
                            {
                                ["type"] = "string",
                                ["description"] = "你认为要执行滑动的区域或目标，用于审批和审计。",
                            },
                            ["reason"] = new Dictionary<string, object?>
                            {
                                ["type"] = "string",
                                ["description"] = "为什么需要滑动。",
                            },
                        },
                        ["required"] = new[] { "startX", "startY", "endX", "endY", "reason" },
                    },
                },
            },
            new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = "adb_ui_dump",
                    ["description"] = "读取当前 Android 界面的 uiautomator XML 结构。用于确认页面、按钮文本、可点击节点和控件边界，避免在页面变化后猜测界面。",
                    ["parameters"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["reason"] = new Dictionary<string, object?>
                            {
                                ["type"] = "string",
                                ["description"] = "为什么需要读取当前界面结构。",
                            },
                        },
                    },
                },
            },
            new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = "companion_call",
                    ["description"] = "调用当前设备上 ADBControl 伴侣 App 暴露的 Android 能力。需要设备已安装伴侣 App；敏感操作会按权限模式审批。",
                    ["parameters"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["capabilityId"] = new Dictionary<string, object?>
                            {
                                ["type"] = "string",
                                ["description"] = "伴侣 App 能力标识，例如 android.accessibility.control。",
                            },
                            ["operation"] = new Dictionary<string, object?>
                            {
                                ["type"] = "string",
                                ["description"] = "能力操作名，例如 accessibility.global.back。",
                            },
                            ["args"] = new Dictionary<string, object?>
                            {
                                ["type"] = "object",
                                ["description"] = "操作参数对象；无参数时传空对象。",
                            },
                            ["reason"] = new Dictionary<string, object?>
                            {
                                ["type"] = "string",
                                ["description"] = "为什么需要调用该伴侣能力。",
                            },
                        },
                        ["required"] = new[] { "capabilityId", "operation" },
                    },
                },
            },
        };
        tools.Insert(0, Tool(
            "device_list",
            "列出 ADBControl 中全部已知设备及 ADB、伴侣连接状态；不需要打开设备详情页。",
            new Dictionary<string, object?>()));
        if (allowInteractiveChoices)
        {
            tools.Insert(1, Tool(
                "ask_user_choice",
                "在当前回复过程中向用户提出有限选项问题。只允许单选或多选；需要文字输入时不要调用此工具。",
                new Dictionary<string, object?>
                {
                    ["question"] = StringProperty("要展示给用户的问题。"),
                    ["selectionMode"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["enum"] = new[] { "single", "multiple" },
                        ["description"] = "single 为单选，multiple 为多选。",
                    },
                    ["options"] = new Dictionary<string, object?>
                    {
                        ["type"] = "array",
                        ["items"] = new Dictionary<string, object?> { ["type"] = "string" },
                        ["minItems"] = 2,
                        ["maxItems"] = 8,
                        ["description"] = "2-8 个互不重复的选项。",
                    },
                },
                "question", "selectionMode", "options"));
        }
        AddOptionalDeviceIdToDeviceTools(tools);
        tools.AddRange(BuildAutomationTools());
        return tools;
    }

    private static void AddOptionalDeviceIdToDeviceTools(List<Dictionary<string, object?>> tools)
    {
        var deviceTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "adb_shell", "adb_ui_dump", "adb_tap", "adb_swipe", "companion_call",
        };
        foreach (var tool in tools)
        {
            if (!tool.TryGetValue("function", out var functionValue) ||
                functionValue is not Dictionary<string, object?> function ||
                function.GetValueOrDefault("name") is not string name ||
                !deviceTools.Contains(name) ||
                function.GetValueOrDefault("parameters") is not Dictionary<string, object?> parameters ||
                parameters.GetValueOrDefault("properties") is not Dictionary<string, object?> properties)
            {
                continue;
            }

            properties["deviceId"] = StringProperty("目标设备的 deviceId。省略时使用当前详情设备；没有当前设备时必须先查询并选择。");
        }
    }

    private static IEnumerable<Dictionary<string, object?>> BuildAutomationTools()
    {
        yield return Tool("task_list", "列出自动化任务及其当前、最近和下次运行状态。", new Dictionary<string, object?>());
        yield return Tool("task_get", "读取一个自动化任务的完整 JSON DSL 定义。", new Dictionary<string, object?>
        {
            ["taskId"] = StringProperty("任务 ID。"),
        }, "taskId");
        yield return Tool("task_create", "创建并持久化一个可真实执行的自动化任务。", new Dictionary<string, object?>
        {
            ["definition"] = ObjectProperty("完整任务 JSON DSL 对象。"),
        }, "definition");
        yield return Tool("task_update", "用完整 JSON DSL 定义修改现有自动化任务。", new Dictionary<string, object?>
        {
            ["taskId"] = StringProperty("任务 ID。"),
            ["definition"] = ObjectProperty("修改后的完整任务 JSON DSL 对象。"),
        }, "taskId", "definition");
        yield return Tool("task_run", "立即运行一个自动化任务。", new Dictionary<string, object?>
        {
            ["taskId"] = StringProperty("任务 ID。"),
        }, "taskId");
        yield return Tool("task_set_enabled", "启用或停用一个自动化任务的自动触发。", new Dictionary<string, object?>
        {
            ["taskId"] = StringProperty("任务 ID。"),
            ["enabled"] = new Dictionary<string, object?> { ["type"] = "boolean", ["description"] = "是否启用。" },
        }, "taskId", "enabled");
        yield return Tool("task_delete", "删除自动化任务及其运行历史。", new Dictionary<string, object?>
        {
            ["taskId"] = StringProperty("任务 ID。"),
        }, "taskId");
    }

    private static Dictionary<string, object?> Tool(
        string name,
        string description,
        Dictionary<string, object?> properties,
        params string[] required)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
        if (required.Length > 0)
            parameters["required"] = required;
        return new Dictionary<string, object?>
        {
            ["type"] = "function",
            ["function"] = new Dictionary<string, object?>
            {
                ["name"] = name,
                ["description"] = description,
                ["parameters"] = parameters,
            },
        };
    }

    private static Dictionary<string, object?> StringProperty(string description) => new()
    {
        ["type"] = "string",
        ["description"] = description,
    };

    private static Dictionary<string, object?> ObjectProperty(string description) => new()
    {
        ["type"] = "object",
        ["description"] = description,
        ["additionalProperties"] = true,
    };

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
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var text = new StringBuilder();
        var thinking = new StringBuilder();
        var toolBuilders = new Dictionary<int, StreamingToolCallBuilder>();

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
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
                await onDelta(chunk).ConfigureAwait(false);
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

    private static void AddImageCompatibilityWarning(List<AiAgentWarning> warnings, int omittedImages, string providerName)
    {
        if (omittedImages <= 0 || warnings.Any(warning => warning.Code == "AI_IMAGE_INPUT_OMITTED"))
            return;
        warnings.Add(new AiAgentWarning
        {
            Code = "AI_IMAGE_INPUT_OMITTED",
            Message = $"{providerName} 当前不接受图像内容，本轮已省略 {omittedImages} 张截图或图片。",
        });
    }

    private static int RemoveImageContent(List<Dictionary<string, object?>> messages)
    {
        var removed = 0;
        foreach (var message in messages)
        {
            if (message.GetValueOrDefault("content") is not List<Dictionary<string, object?>> content)
                continue;
            var removedFromMessage = content.RemoveAll(item =>
                item.GetValueOrDefault("type") is string type &&
                string.Equals(type, "image_url", StringComparison.Ordinal));
            if (removedFromMessage == 0)
                continue;

            removed += removedFromMessage;
            var note = $"[兼容性提示：当前模型不支持图像输入，本轮已省略 {removedFromMessage} 张图像。]";
            var textPart = content.FirstOrDefault(item =>
                item.GetValueOrDefault("type") is string type && string.Equals(type, "text", StringComparison.Ordinal));
            if (textPart is null)
            {
                content.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = note });
            }
            else
            {
                var existing = textPart.GetValueOrDefault("text") as string;
                textPart["text"] = string.IsNullOrWhiteSpace(existing) ? note : $"{existing}\n\n{note}";
            }
        }
        return removed;
    }

    private async Task TryLogFailureAsync(
        AiModelSettings model,
        AiRequestFailure failure,
        long elapsedMs,
        CancellationToken _)
    {
        var providerHost = Uri.TryCreate(model.ApiUrl, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
        try
        {
            await _logger.WriteAsync(new AiRequestLogEntry(
                failure.TraceId,
                providerHost,
                model.ModelId,
                failure.HttpStatusCode.HasValue ? "response" : "transport",
                failure.HttpStatusCode,
                failure.ErrorCode,
                failure.ProviderErrorType,
                failure.ProviderErrorCode,
                failure.ProviderMessage,
                elapsedMs), CancellationToken.None);
        }
        catch (Exception logException)
        {
            // Diagnostics must never replace the original AI failure shown to the user.
            Debug.WriteLine($"Failed to write AI request log: {logException}");
        }
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
