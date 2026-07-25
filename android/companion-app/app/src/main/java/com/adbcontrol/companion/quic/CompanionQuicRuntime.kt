package com.adbcontrol.companion.quic

import java.util.UUID

object CompanionQuicRuntime {
    @Volatile
    private var activeTransport: NativeQuicTransport? = null
    @Volatile
    private var activeDeviceId: String? = null

    fun attach(transport: NativeQuicTransport, deviceId: String) {
        activeTransport = transport
        activeDeviceId = deviceId
    }

    fun detach(transport: NativeQuicTransport) {
        if (activeTransport === transport) {
            activeTransport = null
            activeDeviceId = null
        }
    }

    fun send(envelope: QuicEnvelope) {
        requireTransport().send(envelope)
    }

    fun sendProjectionEvent(
        sessionId: String,
        state: String,
        details: Map<String, Any?> = emptyMap(),
        failed: Boolean = false,
    ) {
        send(
            QuicEnvelope(
                messageId = "projection-${UUID.randomUUID()}",
                traceId = sessionId,
                deviceId = activeDeviceId,
                channel = QuicChannel.MEDIA,
                kind = when {
                    failed -> QuicMessageKind.ERROR
                    state == "stopped" -> QuicMessageKind.STREAM_CLOSE
                    else -> QuicMessageKind.STREAM_OPEN
                },
                payload = mapOf(
                    "sessionId" to sessionId,
                    "state" to state,
                    "backend" to "app-media-projection",
                ) + details,
            ),
        )
    }

    fun startVideoStream(metadata: QuicVideoStreamMetadata) {
        requireTransport().startVideoStream(metadata)
    }

    fun sendVideoFrame(
        sessionId: String,
        presentationTimeUs: Long,
        flags: Int,
        data: ByteArray,
    ): Boolean {
        return activeTransport?.sendVideoFrame(
            sessionId,
            presentationTimeUs,
            flags,
            data,
        ) == true
    }

    fun stopVideoStream(sessionId: String) {
        activeTransport?.stopVideoStream(sessionId)
    }

    fun isConnected(): Boolean = activeTransport != null

    private fun requireTransport(): NativeQuicTransport {
        return activeTransport ?: error("伴侣 App 尚未建立 QUIC 连接。")
    }
}
