package com.adbcontrol.remote.data.ai

import com.adbcontrol.remote.data.log.AppDiagnostics
import com.adbcontrol.remote.model.AppError
import com.adbcontrol.remote.model.AiModelConfig
import com.adbcontrol.remote.model.RemoteResult
import org.json.JSONArray
import org.json.JSONObject
import java.io.BufferedReader
import java.io.InputStreamReader
import java.net.HttpURLConnection
import java.net.URL

/**
 * OpenAI 兼容流式客户端（对齐桌面端 AiService 的 /chat/completions + SSE 语义）：
 * - URL 无 /chat/completions 后缀时自动拼接；
 * - temperature 0.2、stream=true；
 * - 解析 delta.content / delta.reasoning_content / delta.reasoning / tool_calls；
 * - 全程阻塞在调用方工作线程执行。
 */
class AiClient {

    fun stream(
        model: AiModelConfig,
        messages: List<JSONObject>,
        tools: JSONArray?,
        onEvent: (StreamEvent) -> Unit,
        isCancelled: () -> Boolean,
    ): RemoteResult<AiTurn> {
        val url = buildChatUrl(model.apiUrl)
        var connection: HttpURLConnection? = null
        val started = System.currentTimeMillis()
        return try {
            val body = JSONObject()
                .put("model", model.modelId)
                .put("temperature", 0.2)
                .put("stream", true)
                .put("messages", JSONArray().apply { messages.forEach { put(it) } })
            if (tools != null && tools.length() > 0) body.put("tools", tools)

            connection = (URL(url).openConnection() as HttpURLConnection).apply {
                requestMethod = "POST"
                connectTimeout = 15_000
                readTimeout = 300_000
                doOutput = true
                setRequestProperty("Content-Type", "application/json")
                if (model.apiKey.isNotBlank()) setRequestProperty("Authorization", "Bearer ${model.apiKey}")
                outputStream.use { it.write(body.toString().toByteArray(Charsets.UTF_8)) }
            }
            val status = connection.responseCode
            if (status !in 200..299) {
                val errorText = connection.errorStream?.bufferedReader()?.use(BufferedReader::readText).orEmpty()
                val code = classifyHttpError(status)
                AppDiagnostics.failure("ai.request", "ai.client", System.currentTimeMillis() - started, code, "http=$status")
                return RemoteResult.Failure(AppError(
                    code, "AI 请求失败（HTTP $status）", "ai.client", recoverable = true,
                    suggestion = if (status == 401) "请检查 API Key。" else "请检查模型服务地址与网络。",
                ).withDetail(errorText.take(300)))
            }

            val content = StringBuilder()
            val reasoning = StringBuilder()
            val toolCalls = LinkedHashMap<String, ToolCallAccumulator>()
            connection.inputStream.use { input ->
                val reader = BufferedReader(InputStreamReader(input, Charsets.UTF_8))
                while (true) {
                    if (isCancelled()) return RemoteResult.Failure(AppError("AI_REQUEST_CANCELLED", "已暂停生成。", "ai.client", true))
                    val line = reader.readLine() ?: break
                    if (!line.startsWith("data:")) continue
                    val payload = line.removePrefix("data:").trim()
                    if (payload == "[DONE]") break
                    val chunk = runCatching { JSONObject(payload) }.getOrNull() ?: continue
                    val choices = chunk.optJSONArray("choices") ?: continue
                    val delta = choices.optJSONObject(0)?.optJSONObject("delta") ?: continue
                    delta.optString("reasoning_content").takeIf(String::isNotEmpty)?.let { reasoning.append(it); onEvent(StreamEvent.Thinking(it)) }
                    if (reasoning.isEmpty()) {
                        delta.optString("reasoning").takeIf(String::isNotEmpty)?.let { reasoning.append(it); onEvent(StreamEvent.Thinking(it)) }
                    }
                    delta.optString("content").takeIf(String::isNotEmpty)?.let { content.append(it); onEvent(StreamEvent.Content(it)) }
                    delta.optJSONArray("tool_calls")?.let { calls ->
                        for (index in 0 until calls.length()) {
                            val call = calls.optJSONObject(index) ?: continue
                            val callIndex = call.optInt("index", 0).toString()
                            val accumulator = toolCalls.getOrPut(callIndex) {
                                ToolCallAccumulator(call.optString("id"), call.optJSONObject("function")?.optString("name").orEmpty())
                            }
                            call.optString("id").takeIf(String::isNotEmpty)?.let { accumulator.id = it }
                            call.optJSONObject("function")?.optString("name")?.takeIf(String::isNotEmpty)?.let { accumulator.name = it }
                            accumulator.arguments.append(call.optJSONObject("function")?.optString("arguments").orEmpty())
                        }
                    }
                }
            }
            AppDiagnostics.record("info", "ai.request.end", "ai.client", true, System.currentTimeMillis() - started, "", "model=${model.modelId}")
            RemoteResult.Success(AiTurn(content.toString(), reasoning.toString(), toolCalls.values.map { it.toToolCall() }))
        } catch (error: Exception) {
            AppDiagnostics.failure("ai.request", "ai.client", System.currentTimeMillis() - started, "AI_REQUEST_FAILED")
            RemoteResult.Failure(AppError("AI_REQUEST_FAILED", error.message ?: "AI 请求失败", "ai.client", true))
        } finally {
            connection?.disconnect()
        }
    }

    /** 添加模型时的多模态探针：发送 1×1 PNG 验证模型是否支持图片输入（与桌面端一致）。 */
    fun probeMultimodal(model: AiModelConfig): RemoteResult<Boolean> {
        val url = buildChatUrl(model.apiUrl)
        var connection: HttpURLConnection? = null
        return try {
            val message = JSONObject().put("role", "user").put(
                "content", JSONArray()
                    .put(JSONObject().put("type", "text").put("text", "请回答:1"))
                    .put(JSONObject().put("type", "image_url").put(
                        "image_url", JSONObject().put("url", "data:image/png;base64,$ONE_PIXEL_PNG_BASE64"),
                    )),
            )
            val body = JSONObject()
                .put("model", model.modelId)
                .put("messages", JSONArray().put(message))
                .put("max_tokens", 8)
            connection = (URL(url).openConnection() as HttpURLConnection).apply {
                requestMethod = "POST"
                connectTimeout = 15_000
                readTimeout = 60_000
                doOutput = true
                setRequestProperty("Content-Type", "application/json")
                if (model.apiKey.isNotBlank()) setRequestProperty("Authorization", "Bearer ${model.apiKey}")
                outputStream.use { it.write(body.toString().toByteArray(Charsets.UTF_8)) }
            }
            val status = connection.responseCode
            if (status in 200..299) RemoteResult.Success(true) else RemoteResult.Failure(
                AppError("AI_MODEL_MULTIMODAL_REQUIRED", "模型不支持图片输入", "ai.client", true, "请选择支持多模态的模型。"),
            )
        } catch (error: Exception) {
            RemoteResult.Failure(AppError("AI_PROBE_FAILED", error.message ?: "多模态验证失败", "ai.client", true))
        } finally {
            connection?.disconnect()
        }
    }

    private fun classifyHttpError(status: Int): String = when (status) {
        401, 403 -> "AI_AUTH_FAILED"
        429 -> "AI_RATE_LIMITED"
        in 500..599 -> "AI_PROVIDER_DOWN"
        else -> "AI_REQUEST_REJECTED"
    }

    private fun AppError.withDetail(detail: String) = AppError(errorCode, message, module, recoverable, detail, traceId)

    data class ToolCallAccumulator(var id: String, var name: String) {
        val arguments = StringBuilder()

        fun toToolCall() = ToolCall(id, name, arguments.toString())
    }

    sealed interface StreamEvent {
        data class Thinking(val text: String) : StreamEvent
        data class Content(val text: String) : StreamEvent
    }

    data class ToolCall(val id: String, val name: String, val argumentsJson: String)
    data class AiTurn(val content: String, val reasoning: String, val toolCalls: List<ToolCall>) {
        val hasToolCalls: Boolean get() = toolCalls.isNotEmpty()
    }

    companion object {
        fun buildChatUrl(apiUrl: String): String {
            val trimmed = apiUrl.trim().trimEnd('/')
            return if (trimmed.endsWith("/chat/completions")) trimmed else "$trimmed/chat/completions"
        }

        // 1×1 透明 PNG。
        private const val ONE_PIXEL_PNG_BASE64 =
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="
    }
}
