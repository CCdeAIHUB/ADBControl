package com.adbcontrol.companion.quic

interface QuicTransport {
    fun connect(endpoint: String)
    fun send(envelope: QuicEnvelope)
    fun close()
}

class UnconfiguredQuicTransport : QuicTransport {
    override fun connect(endpoint: String) {
        throw IllegalStateException("QUIC 传输尚未配置连接地址：$endpoint")
    }

    override fun send(envelope: QuicEnvelope) {
        throw IllegalStateException("QUIC 传输尚未配置，无法发送消息：${envelope.messageId}")
    }

    override fun close() = Unit
}
