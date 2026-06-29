package com.adbcontrol.companion.quic

interface NativeQuicEngine {
    fun connect(endpoint: String)
    fun send(envelope: QuicEnvelope)
    fun close()
}

class NativeQuicTransport(
    private val engine: NativeQuicEngine = UnavailableNativeQuicEngine(),
) : QuicTransport {
    override fun connect(endpoint: String) {
        require(endpoint.startsWith("quic://")) {
            "Native QUIC transport requires a quic:// endpoint, not HTTP or HTTP/3."
        }
        engine.connect(endpoint)
    }

    override fun send(envelope: QuicEnvelope) {
        engine.send(envelope)
    }

    override fun close() {
        engine.close()
    }
}

class UnavailableNativeQuicEngine : NativeQuicEngine {
    override fun connect(endpoint: String) {
        throw IllegalStateException(
            "Native QUIC engine is not bundled yet. Provide a JNI/native QUIC implementation before connecting to $endpoint.",
        )
    }

    override fun send(envelope: QuicEnvelope) {
        throw IllegalStateException(
            "Native QUIC engine is not bundled yet. Cannot send message: ${envelope.messageId}",
        )
    }

    override fun close() = Unit
}
