package com.adbcontrol.companion.quic

import android.app.Service
import android.content.Intent
import android.os.Build
import android.os.IBinder
import com.adbcontrol.companion.core.AndroidCapabilityCatalog
import com.adbcontrol.companion.core.PermissionGuard

enum class CompanionConnectionState {
    DISCONNECTED,
    CONNECTING,
    HANDSHAKING,
    READY,
    DEGRADED,
    FAILED,
}

class QuicCompanionService : Service() {
    private val transport: QuicTransport = UnconfiguredQuicTransport()
    private lateinit var permissionGuard: PermissionGuard
    private var connectionState: CompanionConnectionState = CompanionConnectionState.DISCONNECTED

    override fun onCreate() {
        super.onCreate()
        permissionGuard = PermissionGuard(this)
    }

    override fun onBind(intent: Intent?): IBinder? = null

    fun buildHello(deviceId: String, deviceName: String): QuicEnvelope {
        val hello = mapOf(
            "appVersion" to "0.1.0",
            "deviceId" to deviceId,
            "deviceName" to deviceName,
            "androidSdk" to Build.VERSION.SDK_INT,
            "supportedProtocolVersions" to listOf(COMPANION_PROTOCOL_VERSION),
        )

        return QuicEnvelope(
            messageId = "hello-$deviceId",
            deviceId = deviceId,
            channel = QuicChannel.CONTROL,
            kind = QuicMessageKind.HELLO,
            payload = hello,
        )
    }

    fun buildPermissionState(): List<Map<String, Any?>> {
        return AndroidCapabilityCatalog.defaultCapabilities().map { capability ->
            val evaluation = permissionGuard.evaluate(capability)
            mapOf(
                "capabilityId" to evaluation.capabilityId,
                "granted" to evaluation.granted,
                "missingPermissions" to evaluation.missingPermissions,
                "missingSpecialGrants" to evaluation.missingSpecialGrants,
                "requiresUserConsent" to capability.requiresUserConsent,
            )
        }
    }

    fun connect(endpoint: String) {
        connectionState = CompanionConnectionState.CONNECTING
        transport.connect(endpoint)
        connectionState = CompanionConnectionState.HANDSHAKING
    }

    fun currentState(): CompanionConnectionState = connectionState
}
