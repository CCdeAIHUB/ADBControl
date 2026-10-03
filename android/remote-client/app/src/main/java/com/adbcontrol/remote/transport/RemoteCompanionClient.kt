package com.adbcontrol.remote.transport

import com.adbcontrol.remote.model.AppError
import com.adbcontrol.remote.model.RemoteResult
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONArray
import org.json.JSONObject
import java.net.URLEncoder
import java.util.concurrent.TimeUnit

data class RemoteCompanionPayload(val value: Any, val transport: String)

/**
 * Authenticated HTTP adapter for companion access exposed by the Web service.
 * The Web service can transparently fall back to the ADB broadcast bridge when
 * a direct companion QUIC session is absent, matching the browser's behaviour.
 */
class RemoteCompanionClient {
    private val http = OkHttpClient.Builder().callTimeout(20, TimeUnit.SECONDS).build()
    private val jsonType = "application/json; charset=utf-8".toMediaType()

    fun status(endpoint: String, token: String, deviceId: String) =
        request(endpoint, token, deviceId, "/companion/status", "GET")

    fun capabilities(endpoint: String, token: String, deviceId: String) =
        request(endpoint, token, deviceId, "/capabilities", "GET")

    fun permissions(endpoint: String, token: String, deviceId: String) =
        request(endpoint, token, deviceId, "/permissions", "GET")

    fun invoke(endpoint: String, token: String, deviceId: String, capabilityId: String, operation: String, args: JSONObject = JSONObject()) =
        request(endpoint, token, deviceId, "/capabilities/invoke", "POST",
            JSONObject().put("capabilityId", capabilityId).put("operation", operation).put("args", args).toString())

    private fun request(endpoint: String, token: String, deviceId: String, suffix: String, method: String, body: String = ""): RemoteResult<RemoteCompanionPayload> = try {
        val encoded = URLEncoder.encode(deviceId, Charsets.UTF_8.name()).replace("+", "%20")
        val base = endpoint.replaceFirst("wss://", "https://").replaceFirst("ws://", "http://").trimEnd('/')
        val builder = Request.Builder().url("$base/api/v1/remote/devices/$encoded$suffix")
            .header("Authorization", "Bearer $token")
        if (method == "POST") builder.post(body.toRequestBody(jsonType))
        http.newCall(builder.build()).execute().use { response ->
            val text = response.body?.string().orEmpty()
            val root = runCatching { JSONObject(text) }.getOrNull()
            if (!response.isSuccessful) {
                val failure = root?.optJSONObject("error")
                RemoteResult.Failure(AppError(
                    failure?.optString("errorCode")?.takeIf(String::isNotBlank) ?: "COMPANION_HTTP_${response.code}",
                    failure?.optString("message")?.takeIf(String::isNotBlank) ?: "伴侣服务返回 ${response.code}",
                    failure?.optString("module")?.takeIf(String::isNotBlank) ?: "companion.remote",
                    failure?.optBoolean("recoverable", true) ?: true,
                    failure?.optString("suggestion")?.takeIf(String::isNotBlank),
                ))
            } else {
                val raw = root?.opt("data") ?: root ?: JSONObject()
                val value = when (raw) {
                    is JSONObject, is JSONArray -> raw
                    else -> JSONObject().put("value", raw)
                }
                RemoteResult.Success(RemoteCompanionPayload(value, response.header("X-ADBControl-Companion-Transport").orEmpty()))
            }
        }
    } catch (error: Throwable) {
        RemoteResult.Failure(AppError("COMPANION_HTTP_FAILED", error.message ?: "伴侣服务请求失败", "companion.remote", true))
    }
}
