package com.adbcontrol.remote.transport

import com.adbcontrol.remote.data.log.AppDiagnostics
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString
import org.json.JSONObject
import java.net.URLEncoder
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.TimeUnit

class ScreenStreamClient(
    private val baseEndpoint: String,
    private val sessionToken: String,
    private val deviceId: String,
    private val listener: Listener,
) {
    interface Listener {
        fun onMeta(width: Int, height: Int)
        fun onConfig(data: ByteArray)
        fun onFrame(data: ByteArray, presentationTimeUs: Long, keyFrame: Boolean)
        fun onAudioAvailable(available: Boolean)
        fun onAudio(data: ByteArray, presentationTimeUs: Long)
        fun onState(message: String, errorCode: String = "")
    }

    private val http = OkHttpClient.Builder().pingInterval(15, TimeUnit.SECONDS).readTimeout(0, TimeUnit.MILLISECONDS).build()
    private var socket: WebSocket? = null

    fun start(fps: Int = 30) {
        close()
        val encoded = URLEncoder.encode(deviceId, Charsets.UTF_8.name()).replace("+", "%20")
        val url = "$baseEndpoint/api/v1/remote/devices/$encoded/screen?fps=${fps.coerceIn(5, 60)}&protocol=3"
        val request = Request.Builder().url(url).header("Authorization", "Bearer $sessionToken").build()
        AppDiagnostics.record("info", "screen.connect.start", "screen.websocket", true, 0, "", "device=$deviceId")
        socket = http.newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) = listener.onState("实时投屏已连接")
            override fun onMessage(webSocket: WebSocket, text: String) {
                val json = runCatching { JSONObject(text) }.getOrNull() ?: return
                when (json.optString("type")) {
                    "meta" -> {
                        listener.onMeta(json.optInt("width"), json.optInt("height"))
                        listener.onAudioAvailable(json.optString("audioCodec") == "raw-s16le-48000-stereo")
                    }
                    "error" -> listener.onState(json.optString("message", "投屏服务返回错误"), "SCREEN_SERVER_ERROR")
                }
            }
            override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
                val packet = bytes.toByteArray()
                if (packet.size < 9) return
                val kind = packet[0].toInt()
                val pts = ByteBuffer.wrap(packet, 1, 8).order(ByteOrder.BIG_ENDIAN).long
                val payload = packet.copyOfRange(9, packet.size)
                when (kind) {
                    1 -> listener.onConfig(payload)
                    3 -> Unit
                    4 -> listener.onAudio(payload, pts)
                    else -> listener.onFrame(payload, pts, kind == 2)
                }
            }
            override fun onFailure(webSocket: WebSocket, error: Throwable, response: Response?) {
                val code = if (response?.code == 401 || response?.code == 403) "SCREEN_AUTH_FAILED" else "SCREEN_WEBSOCKET_FAILED"
                AppDiagnostics.failure("screen.connect", "screen.websocket", 0, code, "type=${error.javaClass.simpleName}")
                listener.onState(error.message ?: "实时投屏连接失败", code)
            }
            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) = listener.onState("实时投屏已停止")
        })
    }

    fun touch(action: Int, x: Int, y: Int, width: Int, height: Int) {
        socket?.send(JSONObject().put("type", "touch").put("action", action).put("pointerId", 0)
            .put("x", x).put("y", y).put("width", width).put("height", height).toString())
    }

    fun close() {
        socket?.close(1000, "client closed")
        socket = null
    }
}
