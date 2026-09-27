package com.adbcontrol.remote.transport

import android.util.Base64
import com.adbcontrol.remote.model.CoreProfile
import com.adbcontrol.remote.model.RemoteResult

class QuicRemoteTransport(private val bridge: NativeQuicBridge = NativeQuicBridge()) : RemoteTransport {
    private var handle = 0L
    override val connected: Boolean get() = handle != 0L

    @Synchronized
    override fun connect(profile: CoreProfile): RemoteResult<String> {
        close()
        return try {
            // 证书与指纹都是可选的：有指纹按指纹固定，有证书按证书固定，都没有则 TOFU。
            val certificate = profile.certificateDerBase64
                .filterNot(Char::isWhitespace)
                .takeIf(String::isNotEmpty)
                ?.let { Base64.decode(it, Base64.DEFAULT) }
            val fingerprint = com.adbcontrol.remote.security.FingerprintPolicy
                .normalize(profile.fingerprintSha256).orEmpty()
            handle = bridge.nativeConnect(
                profile.endpoint,
                profile.serverName.ifBlank { DEFAULT_SERVER_NAME },
                jniCertificateBytes(certificate),
                fingerprint,
            )
            if (handle == 0L) transportFailure("REMOTE_QUIC_CONNECT_FAILED", "无法连接远程 Core")
            else RemoteResult.Success(bridge.nativeFingerprint(handle).orEmpty())
        } catch (error: Throwable) {
            handle = 0L
            transportFailure("REMOTE_QUIC_CONNECT_FAILED", error.message ?: "QUIC 连接失败")
        }
    }

    @Synchronized
    override fun request(json: String, timeoutMs: Long): RemoteResult<String> {
        if (!connected) return transportFailure("REMOTE_QUIC_NOT_CONNECTED", "尚未连接远程 Core")
        return try {
            val response = bridge.nativeRequest(handle, json, timeoutMs)
                ?: return transportFailure("REMOTE_QUIC_EMPTY_RESPONSE", "Core 未返回响应")
            RemoteResult.Success(response)
        } catch (error: Throwable) {
            transportFailure("REMOTE_QUIC_REQUEST_FAILED", error.message ?: "远程请求失败")
        }
    }

    @Synchronized
    override fun close() {
        if (handle != 0L) bridge.nativeClose(handle)
        handle = 0L
    }

    private companion object {
        const val DEFAULT_SERVER_NAME = "adbcontrol.local"
    }
}

/**
 * JNI arrays must never be null. An absent certificate means TOFU, represented
 * by a zero-length array; passing null makes JNI conversion fail before the
 * client sends the first UDP packet.
 */
internal fun jniCertificateBytes(certificate: ByteArray?): ByteArray =
    certificate ?: ByteArray(0)
