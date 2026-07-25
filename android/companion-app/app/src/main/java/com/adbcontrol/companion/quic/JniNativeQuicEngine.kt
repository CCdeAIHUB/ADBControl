package com.adbcontrol.companion.quic

import java.util.concurrent.locks.ReentrantReadWriteLock
import kotlin.concurrent.read
import kotlin.concurrent.write

class JniNativeQuicEngine : NativeQuicEngine {
    private val lifecycleLock = ReentrantReadWriteLock()
    private var handle: Long = 0L

    override fun connect(endpoint: String, serverName: String, certificateDer: ByteArray) {
        lifecycleLock.write {
            if (handle != 0L) nativeClose(handle)
            handle = nativeConnect(endpoint, serverName, certificateDer)
            check(handle != 0L) { "原生 QUIC 引擎连接失败。" }
        }
    }

    override fun send(envelope: QuicEnvelope) {
        lifecycleLock.read {
            check(handle != 0L) { "原生 QUIC 引擎尚未连接。" }
            nativeSendEnvelope(handle, QuicJsonCodec.encodeEnvelope(envelope))
        }
    }

    override fun poll(timeoutMs: Int): QuicEnvelope? {
        return lifecycleLock.read {
            if (handle == 0L) null
            else nativePollEnvelope(handle, timeoutMs)?.let(QuicJsonCodec::decodeEnvelope)
        }
    }

    override fun startVideoStream(metadata: QuicVideoStreamMetadata) {
        lifecycleLock.read {
            check(handle != 0L) { "原生 QUIC 引擎尚未连接。" }
            nativeStartVideoStream(handle, QuicJsonCodec.encodeVideoMetadata(metadata))
        }
    }

    override fun sendVideoFrame(
        sessionId: String,
        presentationTimeUs: Long,
        flags: Int,
        data: ByteArray,
    ): Boolean {
        return lifecycleLock.read {
            handle != 0L && nativeSendVideoFrame(handle, sessionId, presentationTimeUs, flags, data)
        }
    }

    override fun stopVideoStream(sessionId: String) {
        lifecycleLock.read {
            if (handle != 0L) nativeStopVideoStream(handle, sessionId)
        }
    }

    override fun close() {
        lifecycleLock.write {
            if (handle != 0L) {
                nativeClose(handle)
                handle = 0L
            }
        }
    }

    override fun certificateFingerprintSha256(): String? {
        return lifecycleLock.read {
            if (handle == 0L) null else nativeCertificateFingerprintSha256(handle)
        }
    }

    private external fun nativeConnect(endpoint: String, serverName: String, certificateDer: ByteArray): Long
    private external fun nativeSendEnvelope(handle: Long, envelopeJson: String)
    private external fun nativePollEnvelope(handle: Long, timeoutMs: Int): String?
    private external fun nativeStartVideoStream(handle: Long, metadataJson: String)
    private external fun nativeSendVideoFrame(
        handle: Long,
        sessionId: String,
        presentationTimeUs: Long,
        flags: Int,
        data: ByteArray,
    ): Boolean
    private external fun nativeStopVideoStream(handle: Long, sessionId: String)
    private external fun nativeClose(handle: Long)
    private external fun nativeCertificateFingerprintSha256(handle: Long): String?

    companion object {
        fun installAsProvider() {
            System.loadLibrary("adbcontrol_quic")
            NativeQuicEngineProvider.installFactory { JniNativeQuicEngine() }
        }
    }
}
