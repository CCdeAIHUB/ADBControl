package com.adbcontrol.remote.data.ai

import android.content.Context
import com.adbcontrol.remote.model.ChatMessage
import org.json.JSONArray
import org.json.JSONObject

/**
 * 会话存储与上下文压缩：压缩阈值对齐桌面端 `AiContextMemoryPolicy.cs`
 * （超过 24 条消息或 16000 字符时压缩为 ≤5000 字符的 system 摘要）。
 */
class AiConversationStore(context: Context) {
    private val preferences = context.getSharedPreferences("ai_conversation", Context.MODE_PRIVATE)

    fun load(): List<ChatMessage> = runCatching {
        val array = JSONArray(preferences.getString("messages", "[]"))
        (0 until array.length()).mapNotNull { index ->
            array.optJSONObject(index)?.let { json ->
                ChatMessage(
                    role = json.optString("role"),
                    content = json.optString("content"),
                    reasoning = json.optString("reasoning"),
                    toolCallId = json.optString("toolCallId"),
                    toolName = json.optString("toolName"),
                    toolCallsJson = json.optString("toolCallsJson"),
                    imageBase64 = json.optString("imageBase64"),
                )
            }
        }
    }.getOrDefault(emptyList())

    fun save(messages: List<ChatMessage>) {
        val array = JSONArray()
        messages.forEach { message ->
            array.put(JSONObject()
                .put("role", message.role)
                .put("content", message.content)
                .put("reasoning", message.reasoning)
                .put("toolCallId", message.toolCallId)
                .put("toolName", message.toolName)
                .put("toolCallsJson", message.toolCallsJson)
                .put("imageBase64", message.imageBase64))
        }
        preferences.edit().putString("messages", array.toString()).apply()
    }

    fun clear() = preferences.edit().remove("messages").apply()

    /** 是否需要压缩（阈值与桌面端一致）。 */
    fun needsCompression(messages: List<ChatMessage>): Boolean =
        messages.size > MAX_MESSAGES || messages.sumOf { it.content.length } > MAX_CHARACTERS

    companion object {
        const val MAX_MESSAGES = 24
        const val MAX_CHARACTERS = 16_000
        const val SUMMARY_MAX_CHARACTERS = 5_000
    }
}
