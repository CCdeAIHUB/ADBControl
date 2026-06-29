package com.adbcontrol.companion.quic

import android.content.Context
import org.chromium.net.CronetEngine
import org.chromium.net.CronetException
import org.chromium.net.UploadDataProvider
import org.chromium.net.UploadDataSink
import org.chromium.net.UrlRequest
import org.chromium.net.UrlResponseInfo
import org.json.JSONArray
import org.json.JSONObject
import java.nio.ByteBuffer
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors

class CronetQuicTransport(context: Context) : QuicTransport {
    private val executor: ExecutorService = Executors.newSingleThreadExecutor()
    private val engine: CronetEngine = CronetEngine.Builder(context)
        .enableQuic(true)
        .enableHttp2(true)
        .build()
    private var endpoint: String? = null

    override fun connect(endpoint: String) {
        require(endpoint.startsWith("https://")) {
            "Cronet QUIC transport requires an HTTPS endpoint so HTTP/3 over QUIC can be negotiated."
        }
        this.endpoint = endpoint
    }

    override fun send(envelope: QuicEnvelope) {
        val target = endpoint ?: throw IllegalStateException("Cronet QUIC transport is not connected.")
        val body = envelope.toJson().toString().toByteArray(Charsets.UTF_8)
        val request = engine.newUrlRequestBuilder(target, Callback(), executor)
            .setHttpMethod("POST")
            .addHeader("Content-Type", "application/json")
            .setUploadDataProvider(ByteArrayUploadProvider(body), executor)
            .build()
        request.start()
    }

    override fun close() {
        executor.shutdownNow()
        engine.shutdown()
    }

    private class Callback : UrlRequest.Callback() {
        override fun onRedirectReceived(
            request: UrlRequest,
            info: UrlResponseInfo,
            newLocationUrl: String,
        ) {
            request.followRedirect()
        }

        override fun onResponseStarted(request: UrlRequest, info: UrlResponseInfo) {
            request.read(ByteBuffer.allocateDirect(8 * 1024))
        }

        override fun onReadCompleted(
            request: UrlRequest,
            info: UrlResponseInfo,
            byteBuffer: ByteBuffer,
        ) {
            byteBuffer.clear()
            request.read(byteBuffer)
        }

        override fun onSucceeded(request: UrlRequest, info: UrlResponseInfo) = Unit

        override fun onFailed(request: UrlRequest, info: UrlResponseInfo?, error: CronetException) = Unit
    }

    private class ByteArrayUploadProvider(private val data: ByteArray) : UploadDataProvider() {
        private var offset = 0

        override fun getLength(): Long = data.size.toLong()

        override fun read(uploadDataSink: UploadDataSink, byteBuffer: ByteBuffer) {
            val remaining = data.size - offset
            val count = minOf(byteBuffer.remaining(), remaining)
            byteBuffer.put(data, offset, count)
            offset += count
            uploadDataSink.onReadSucceeded(false)
        }

        override fun rewind(uploadDataSink: UploadDataSink) {
            offset = 0
            uploadDataSink.onRewindSucceeded()
        }
    }
}

private fun QuicEnvelope.toJson(): JSONObject {
    return JSONObject().apply {
        put("protocol", protocol)
        put("version", version)
        put("messageId", messageId)
        put("traceId", traceId)
        put("deviceId", deviceId)
        put("channel", channel.name.lowercase())
        put("kind", kind.name.lowercase())
        put("payload", payload.toJsonValue())
    }
}

private fun Any?.toJsonValue(): Any? {
    return when (this) {
        null -> JSONObject.NULL
        is Map<*, *> -> JSONObject().also { json ->
            this.forEach { (key, value) -> json.put(key.toString(), value.toJsonValue()) }
        }
        is Iterable<*> -> JSONArray().also { array ->
            this.forEach { value -> array.put(value.toJsonValue()) }
        }
        is Array<*> -> JSONArray().also { array ->
            this.forEach { value -> array.put(value.toJsonValue()) }
        }
        else -> this
    }
}
