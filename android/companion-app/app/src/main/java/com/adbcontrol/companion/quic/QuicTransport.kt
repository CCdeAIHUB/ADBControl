package com.adbcontrol.companion.quic

interface QuicTransport {
    fun connect(endpoint: String)
    fun send(envelope: QuicEnvelope)
    fun close()
}

class UnconfiguredQuicTransport : QuicTransport {
    override fun connect(endpoint: String) {
        throw IllegalStateException("QUIC transport is not configured for endpoint: $endpoint")
    }

    override fun send(envelope: QuicEnvelope) {
        throw IllegalStateException("QUIC transport is not configured for message: ${envelope.messageId}")
    }

    override fun close() = Unit
}
