package com.adbcontrol.remote.transport

import com.adbcontrol.remote.model.AppError
import com.adbcontrol.remote.model.RemoteResult
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONObject
import java.net.URLEncoder
import java.time.Instant
import java.util.concurrent.TimeUnit

data class RemoteHardwareSample(
    val capturedAtEpochMs: Long,
    val cpuFrequenciesKHz: List<Long>,
    val memoryTotalKb: Long,
    val memoryAvailableKb: Long,
    val storageTotalKb: Long,
    val storageUsedKb: Long,
    val temperaturesC: List<Double>,
)

data class RemoteHardwareMonitorStatus(
    val running: Boolean,
    val startedAt: String,
    val intervalMillis: Long,
    val sampleCount: Int,
    val samples: List<RemoteHardwareSample>,
    val lastError: String,
    val lastErrorCode: String,
)

/** Authenticated HTTP adapter for the Core-hosted monitor lifecycle. */
class RemoteHardwareMonitorClient {
    private val http = OkHttpClient.Builder().callTimeout(15, TimeUnit.SECONDS).build()
    private val jsonType = "application/json; charset=utf-8".toMediaType()

    fun status(endpoint: String, token: String, deviceId: String) = request(endpoint, token, deviceId, "", "GET")
    fun start(endpoint: String, token: String, deviceId: String) = request(endpoint, token, deviceId, "/start", "POST")
    fun stop(endpoint: String, token: String, deviceId: String) = request(endpoint, token, deviceId, "/stop", "POST")

    private fun request(endpoint: String, token: String, deviceId: String, suffix: String, method: String): RemoteResult<RemoteHardwareMonitorStatus> = try {
        val encoded = URLEncoder.encode(deviceId, Charsets.UTF_8.name()).replace("+", "%20")
        val builder = Request.Builder()
            .url("$endpoint/api/v1/remote/devices/$encoded/hardware-monitor$suffix")
            .header("Authorization", "Bearer $token")
        if (method == "POST") builder.post("{\"metrics\":[\"cpu\",\"memory\",\"storage\",\"temperature\"]}".toRequestBody(jsonType))
        http.newCall(builder.build()).execute().use { response ->
            val text = response.body?.string().orEmpty()
            if (!response.isSuccessful) {
                RemoteResult.Failure(AppError("HARDWARE_MONITOR_HTTP_${response.code}", "后台监控服务返回 ${response.code}", "hardware.monitor", true))
            } else {
                val root = JSONObject(text)
                val json = root.optJSONObject("data") ?: root
                val samples = json.optJSONArray("samples")
                RemoteResult.Success(RemoteHardwareMonitorStatus(
                    running = json.optBoolean("running"), startedAt = json.optString("startedAt"),
                    intervalMillis = json.optLong("intervalMillis", 3000), sampleCount = json.optInt("sampleCount"),
                    samples = buildList {
                        if (samples != null) for (index in 0 until samples.length()) {
                            val item = samples.optJSONObject(index) ?: continue
                            val frequencies = item.optJSONArray("cpuFrequenciesKHz")
                            val temperatures = item.optJSONObject("temperaturesC")
                            add(RemoteHardwareSample(
                                capturedAtEpochMs = runCatching { Instant.parse(item.optString("capturedAt")).toEpochMilli() }.getOrDefault(System.currentTimeMillis()),
                                cpuFrequenciesKHz = buildList { if (frequencies != null) for (i in 0 until frequencies.length()) add(frequencies.optLong(i)) },
                                memoryTotalKb = item.optLong("memoryTotalKb"), memoryAvailableKb = item.optLong("memoryAvailableKb"),
                                storageTotalKb = item.optLong("storageTotalKb"), storageUsedKb = item.optLong("storageUsedKb"),
                                temperaturesC = buildList { temperatures?.keys()?.forEach { add(temperatures.optDouble(it)) } },
                            ))
                        }
                    },
                    lastError = json.optString("lastError"), lastErrorCode = json.optString("lastErrorCode"),
                ))
            }
        }
    } catch (error: Throwable) {
        RemoteResult.Failure(AppError("HARDWARE_MONITOR_REQUEST_FAILED", error.message ?: "后台监控请求失败", "hardware.monitor", true))
    }
}
