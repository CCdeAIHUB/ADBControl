package com.adbcontrol.remote.model

/** AI 模型配置（OpenAI 兼容 /chat/completions）。密钥本机明文保存，与桌面端 settings.json 行为一致。 */
data class AiModelConfig(
    val id: String,
    val name: String,
    val modelId: String,
    val apiUrl: String,
    val apiKey: String,
    val multimodalVerified: Boolean = false,
)

/** 权限模式：与桌面端一致（请求批准 / 替我审批 / 完全访问）。 */
enum class AiPermissionMode(val title: String, val subtitle: String) {
    APPROVE_EVERY("请求批准", "每个动作都需要你确认"),
    AUTO_READONLY("替我审批", "只自动放行只读动作，其余需要确认"),
    FULL_ACCESS("完全访问", "所有动作自动执行，风险自负"),
}

/** 一条对话消息。tool 消息保存工具名与结果文本；图片以 data URL 附在 user 消息。 */
data class ChatMessage(
    val role: String, // system / user / assistant / tool
    val content: String,
    val reasoning: String = "",
    val toolCallId: String = "",
    val toolName: String = "",
    val toolCallsJson: String = "", // assistant 消息携带的 tool_calls 原文
    val imageBase64: String = "", // 用户附件（png/jpg），空表示纯文本
) {
    val hasImage: Boolean get() = imageBase64.isNotBlank()
}
