package com.adbcontrol.remote.data.ai

import android.content.Context
import com.adbcontrol.remote.model.AiModelConfig
import org.json.JSONArray
import org.json.JSONObject

/** AI 模型配置存储（应用私有 SharedPreferences；不含会话令牌与聊天正文）。 */
class AiModelStore(context: Context) {
    private val preferences = context.getSharedPreferences("ai_models", Context.MODE_PRIVATE)

    fun list(): List<AiModelConfig> = runCatching {
        val array = JSONArray(preferences.getString("models", "[]"))
        (0 until array.length()).mapNotNull { index ->
            array.optJSONObject(index)?.let { json ->
                AiModelConfig(
                    id = json.optString("id"),
                    name = json.optString("name"),
                    modelId = json.optString("modelId"),
                    apiUrl = json.optString("apiUrl"),
                    apiKey = json.optString("apiKey"),
                    multimodalVerified = json.optBoolean("multimodalVerified"),
                )
            }
        }
    }.getOrDefault(emptyList())

    fun save(model: AiModelConfig) {
        val models = list().filterNot { it.id == model.id } + model
        persist(models)
    }

    fun delete(id: String) {
        persist(list().filterNot { it.id == id })
    }

    fun get(id: String): AiModelConfig? = list().firstOrNull { it.id == id }

    private fun persist(models: List<AiModelConfig>) {
        val array = JSONArray()
        models.forEach { model ->
            array.put(JSONObject()
                .put("id", model.id)
                .put("name", model.name)
                .put("modelId", model.modelId)
                .put("apiUrl", model.apiUrl)
                .put("apiKey", model.apiKey)
                .put("multimodalVerified", model.multimodalVerified))
        }
        preferences.edit().putString("models", array.toString()).apply()
    }
}
