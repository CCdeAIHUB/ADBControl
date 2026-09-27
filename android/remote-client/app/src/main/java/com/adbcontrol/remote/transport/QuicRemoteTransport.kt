package com.adbcontrol.remote.transport

import android.util.Base64
import com.adbcontrol.remote.model.CoreProfile
import com.adbcontrol.remote.model.RemoteResult
import com.adbcontrol.remote.data.log.AppDiagnostics

class QuicRemoteTransport(private val bridge: NativeQuicBridge = NativeQuicBridge()) : RemoteTransport {
    private var handle = 0L
    private var lastProfile: CoreProfile? = null
    override val connected: Boolean get() = handle != 0L

    @Synchronized
    override fun connect(profile: CoreProfile): RemoteResult<String> {
        val started = System.currentTimeMillis()
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
            if (handle != 0L) lastProfile = profile
            val result = if (handle == 0L) transportFailure("REMOTE_QUIC_CONNECT_FAILED", "无法连接远程 Core")
            else RemoteResult.Success(bridge.nativeFingerprint(handle).orEmpty())
            recordTransport("quic.connect", started, result)
            result
        } catch (error: Throwable) {
            handle = 0L
            transportFailure("REMOTE_QUIC_CONNECT_FAILED", error.message ?: "QUIC 连接失败").also {
                recordTransport("quic.connect", started, it)
            }
        }
    }

    @Synchronized
    override fun request(json: String, timeoutMs: Long): RemoteResult<String> {
        val started = System.currentTimeMillis()
        val diagnosticUpload = json.contains("\"method\":\"diagnostics.report\"")
        if (!connected) return transportFailure("REMOTE_QUIC_NOT_CONNECTED", "尚未连接远程 Core")
        return try {
            val response = bridge.nativeRequest(handle, json, timeoutMs)
                ?: return transportFailure("REMOTE_QUIC_EMPTY_RESPONSE", "Core 未返回响应")
            RemoteResult.Success(response).also { if (!diagnosticUpload) recordTransport("quic.request", started, it) }
        } catch (error: Throwable) {
            val recovered = recoverConnection()
            val method = runCatching { org.json.JSONObject(json).optString("method") }.getOrDefault("")
            if (recovered && method in SAFE_RETRY_METHODS) {
                return try {
                    bridge.nativeRequest(handle, json, timeoutMs)
                        ?.let { RemoteResult.Success(it) }
                        ?: transportFailure("REMOTE_QUIC_EMPTY_RESPONSE", "Core 未返回响应")
                } catch (retryError: Throwable) {
                    transportFailure("REMOTE_QUIC_REQUEST_FAILED", retryError.message ?: "远程请求重试失败")
                }.also { if (!diagnosticUpload) recordTransport("quic.request.retry", started, it) }
            }
            transportFailure(
                "REMOTE_QUIC_REQUEST_FAILED",
                (error.message ?: "远程请求失败") + if (recovered) "；连接已恢复，请重试本次操作" else "",
            ).also {
                if (!diagnosticUpload) recordTransport("quic.request", started, it)
            }
        }
    }

    @Synchronized
    override fun close() {
        if (handle != 0L) bridge.nativeClose(handle)
        handle = 0L
    }

    private fun recoverConnection(): Boolean {
        val profile = lastProfile ?: return false
        runCatching { if (handle != 0L) bridge.nativeClose(handle) }
        handle = 0L
        return try {
            val certificate = profile.certificateDerBase64.filterNot(Char::isWhitespace)
                .takeIf(String::isNotEmpty)?.let { Base64.decode(it, Base64.DEFAULT) }
            val fingerprint = com.adbcontrol.remote.security.FingerprintPolicy
                .normalize(profile.fingerprintSha256).orEmpty()
            handle = bridge.nativeConnect(
                profile.endpoint,
                profile.serverName.ifBlank { DEFAULT_SERVER_NAME },
                jniCertificateBytes(certificate),
                fingerprint,
            )
            (handle != 0L).also { ok ->
                AppDiagnostics.record(
                    if (ok) "warn" else "error", "quic.recover", "remote.quic", ok, 0,
                    if (ok) "" else "REMOTE_QUIC_RECOVER_FAILED",
                )
            }
        } catch (_: Throwable) {
            handle = 0L
            false
        }
    }

    private companion object {
        const val DEFAULT_SERVER_NAME = "adbcontrol.local"
        val SAFE_RETRY_METHODS = setOf(
            "auth.me", "device.list", "device.getCapabilities", "device.getPermissionState", "diagnostics.report",
        )
    }

    private fun recordTransport(phase: String, started: Long, result: RemoteResult<*>) {
        AppDiagnostics.record(
            if (result is RemoteResult.Failure) "error" else "info",
            phase, "remote.quic", result is RemoteResult.Success,
            System.currentTimeMillis() - started,
            (result as? RemoteResult.Failure)?.error?.errorCode.orEmpty(),
        )
    }
}

/**
 * JNI arrays must never be null. An absent certificate means TOFU, represented
 * by a zero-length array; passing null makes JNI conversion fail before the
 * client sends the first UDP packet.
 */
internal fun jniCertificateBytes(certificate: ByteArray?): ByteArray =
    certificate ?: ByteArray(0)
