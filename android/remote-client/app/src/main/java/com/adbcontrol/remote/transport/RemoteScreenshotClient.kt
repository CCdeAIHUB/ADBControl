package com.adbcontrol.remote.transport

import com.adbcontrol.remote.model.AppError
import com.adbcontrol.remote.model.RemoteResult
import okhttp3.OkHttpClient
import okhttp3.Request
import java.net.URLEncoder
import java.util.concurrent.TimeUnit

/** 复用 Web 服务的 ADB PNG 截图能力，不依赖伴侣在线状态。 */
class RemoteScreenshotClient {
    private val http = OkHttpClient.Builder().callTimeout(15, TimeUnit.SECONDS).build()

    fun capture(baseEndpoint: String, token: String, deviceId: String): RemoteResult<ByteArray> = try {
        val encoded = URLEncoder.encode(deviceId, Charsets.UTF_8.name()).replace("+", "%20")
        val request = Request.Builder()
            .url("$baseEndpoint/api/v1/remote/devices/$encoded/screenshot")
            .header("Authorization", "Bearer $token")
            .build()
        http.newCall(request).execute().use { response ->
            if (!response.isSuccessful) {
                RemoteResult.Failure(AppError("SCREENSHOT_HTTP_${response.code}", "截图服务返回 ${response.code}", "screen.screenshot", true))
            } else {
                val bytes = response.body?.bytes() ?: ByteArray(0)
                if (bytes.isEmpty()) RemoteResult.Failure(AppError("SCREENSHOT_EMPTY", "截图服务没有返回图像", "screen.screenshot", true))
                else RemoteResult.Success(bytes)
            }
        }
    } catch (error: Throwable) {
        RemoteResult.Failure(AppError("SCREENSHOT_REQUEST_FAILED", error.message ?: "截图请求失败", "screen.screenshot", true))
    }
}
