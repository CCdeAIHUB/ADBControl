package com.adbcontrol.companion.media

import android.util.Base64
import com.adbcontrol.companion.quic.QuicChannel
import com.adbcontrol.companion.quic.QuicEnvelope
import com.adbcontrol.companion.quic.QuicMessageKind
import com.adbcontrol.companion.quic.QuicTransport
import java.util.UUID

class QuicMediaStreamSink(
    private val transport: QuicTransport,
    private val deviceId: String?,
) : MediaStreamSink {
    override fun onStreamStarted(sessionId: String, mimeType: String, metadata: Map<String, Any?>) {
        transport.send(
            envelope(
                sessionId = sessionId,
                kind = QuicMessageKind.STREAM_OPEN,
                payload = mapOf(
                    "sessionId" to sessionId,
                    "mimeType" to mimeType,
                    "metadata" to metadata,
                ),
            ),
        )
    }

    override fun onConfig(sessionId: String, config: ByteArray, metadata: Map<String, Any?>) {
        sendChunk(sessionId, "config", config, metadata)
    }

    override fun onChunk(sessionId: String, chunk: ByteArray, metadata: Map<String, Any?>) {
        sendChunk(sessionId, "media", chunk, metadata)
    }

    override fun onStreamStopped(sessionId: String, metadata: Map<String, Any?>) {
        transport.send(
            envelope(
                sessionId = sessionId,
                kind = QuicMessageKind.STREAM_CLOSE,
                payload = mapOf(
                    "sessionId" to sessionId,
                    "metadata" to metadata,
                ),
            ),
        )
    }

    private fun sendChunk(
        sessionId: String,
        chunkType: String,
        data: ByteArray,
        metadata: Map<String, Any?>,
    ) {
        transport.send(
            envelope(
                sessionId = sessionId,
                kind = QuicMessageKind.STREAM_CHUNK,
                payload = mapOf(
                    "sessionId" to sessionId,
                    "chunkType" to chunkType,
                    "encoding" to "base64",
                    "data" to Base64.encodeToString(data, Base64.NO_WRAP),
                    "sizeBytes" to data.size,
                    "metadata" to metadata,
                ),
            ),
        )
    }

    private fun envelope(
        sessionId: String,
        kind: QuicMessageKind,
        payload: Map<String, Any?>,
    ): QuicEnvelope {
        return QuicEnvelope(
            messageId = "media-${UUID.randomUUID()}",
            traceId = sessionId,
            deviceId = deviceId,
            channel = QuicChannel.MEDIA,
            kind = kind,
            payload = payload,
        )
    }
}
