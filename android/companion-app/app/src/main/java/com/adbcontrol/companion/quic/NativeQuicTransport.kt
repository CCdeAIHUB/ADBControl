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
            "原生 QUIC 传输需要 quic:// 地址，不能使用 HTTP 或 HTTP/3 地址。"
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
            "尚未内置原生 QUIC 引擎。连接到 $endpoint 前，需要先提供 JNI/原生 QUIC 实现。",
        )
    }

    override fun send(envelope: QuicEnvelope) {
        throw IllegalStateException(
            "尚未内置原生 QUIC 引擎，无法发送消息：${envelope.messageId}",
        )
    }

    override fun close() = Unit
}
