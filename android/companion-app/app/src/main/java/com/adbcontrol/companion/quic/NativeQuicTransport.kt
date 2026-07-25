package com.adbcontrol.companion.quic

interface NativeQuicEngine {
    fun connect(endpoint: String, serverName: String, certificateDer: ByteArray)
    fun send(envelope: QuicEnvelope)
    fun poll(timeoutMs: Int): QuicEnvelope?
    fun startVideoStream(metadata: QuicVideoStreamMetadata)
    fun sendVideoFrame(
        sessionId: String,
        presentationTimeUs: Long,
        flags: Int,
        data: ByteArray,
    ): Boolean
    fun stopVideoStream(sessionId: String)
    fun certificateFingerprintSha256(): String?
    fun close()
}

data class QuicVideoStreamMetadata(
    val sessionId: String,
    val codec: String,
    val width: Int,
    val height: Int,
    val bitrate: Int,
    val frameRate: Int,
)

class NativeQuicTransport(
    private val engine: NativeQuicEngine = UnavailableNativeQuicEngine(),
) : QuicTransport {
    override fun connect(endpoint: String) {
        error("QUIC server identity is required; use connect(endpoint, serverName, certificateDer).")
    }

    fun connect(endpoint: String, serverName: String, certificateDer: ByteArray) {
        require(endpoint.startsWith("quic://")) {
            "原生 QUIC 传输需要 quic:// 地址，不能使用 HTTP 或 HTTP/3 地址。"
        }
        require(serverName.isNotBlank()) { "QUIC TLS serverName 不能为空。" }
        require(certificateDer.isNotEmpty()) { "QUIC TLS 证书不能为空。" }
        engine.connect(endpoint, serverName, certificateDer)
    }

    override fun send(envelope: QuicEnvelope) {
        engine.send(envelope)
    }

    override fun close() {
        engine.close()
    }

    fun poll(timeoutMs: Int): QuicEnvelope? = engine.poll(timeoutMs)

    fun startVideoStream(metadata: QuicVideoStreamMetadata) = engine.startVideoStream(metadata)

    fun sendVideoFrame(
        sessionId: String,
        presentationTimeUs: Long,
        flags: Int,
        data: ByteArray,
    ): Boolean = engine.sendVideoFrame(sessionId, presentationTimeUs, flags, data)

    fun stopVideoStream(sessionId: String) = engine.stopVideoStream(sessionId)

    fun certificateFingerprintSha256(): String? = engine.certificateFingerprintSha256()
}

class UnavailableNativeQuicEngine : NativeQuicEngine {
    override fun connect(endpoint: String, serverName: String, certificateDer: ByteArray) {
        throw IllegalStateException(
            "尚未内置原生 QUIC 引擎。连接到 $endpoint 前，需要先提供 JNI/原生 QUIC 实现。",
        )
    }

    override fun send(envelope: QuicEnvelope) {
        throw IllegalStateException(
            "尚未内置原生 QUIC 引擎，无法发送消息：${envelope.messageId}",
        )
    }

    override fun poll(timeoutMs: Int): QuicEnvelope? = null

    override fun startVideoStream(metadata: QuicVideoStreamMetadata) {
        throw IllegalStateException("尚未内置原生 QUIC 引擎，无法开启视频流。")
    }

    override fun sendVideoFrame(
        sessionId: String,
        presentationTimeUs: Long,
        flags: Int,
        data: ByteArray,
    ): Boolean = false

    override fun stopVideoStream(sessionId: String) = Unit

    override fun certificateFingerprintSha256(): String? = null

    override fun close() = Unit
}
