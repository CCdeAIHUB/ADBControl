package com.adbcontrol.companion.quic

class JniNativeQuicEngine : NativeQuicEngine {
    private var handle: Long = 0L

    override fun connect(endpoint: String) {
        handle = nativeConnect(endpoint)
    }

    override fun send(envelope: QuicEnvelope) {
        check(handle != 0L) { "Native QUIC engine is not connected." }
        nativeSend(handle, envelope.messageId, envelope.channel.name, envelope.kind.name, envelope.payload.toString())
    }

    override fun close() {
        if (handle != 0L) {
            nativeClose(handle)
            handle = 0L
        }
    }

    fun certificateFingerprintSha256(): String? {
        return if (handle == 0L) null else nativeCertificateFingerprintSha256(handle)
    }

    private external fun nativeConnect(endpoint: String): Long
    private external fun nativeSend(handle: Long, messageId: String, channel: String, kind: String, payload: String)
    private external fun nativeClose(handle: Long)
    private external fun nativeCertificateFingerprintSha256(handle: Long): String?

    companion object {
        fun installAsProvider() {
            System.loadLibrary("adbcontrol_quic")
            NativeQuicEngineProvider.installFactory { JniNativeQuicEngine() }
        }
    }
}
