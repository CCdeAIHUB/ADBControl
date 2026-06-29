package com.adbcontrol.companion.quic

import android.app.Service
import android.content.Intent
import android.os.Build
import android.os.IBinder
import com.adbcontrol.companion.core.AndroidCapabilityCatalog
import com.adbcontrol.companion.core.CompanionCommandContext
import com.adbcontrol.companion.core.PermissionGuard
import com.adbcontrol.companion.features.AndroidFeatureDispatcher

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
    private lateinit var featureDispatcher: AndroidFeatureDispatcher
    private var connectionState: CompanionConnectionState = CompanionConnectionState.DISCONNECTED

    override fun onCreate() {
        super.onCreate()
        permissionGuard = PermissionGuard(this)
        featureDispatcher = AndroidFeatureDispatcher(this, permissionGuard)
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

    fun handleCommandRequest(
        deviceId: String,
        traceId: String?,
        request: CompanionCommandRequest,
    ): QuicEnvelope {
        val result = featureDispatcher.dispatch(
            CompanionCommandContext(
                requestId = request.requestId,
                capabilityId = request.capabilityId,
                operation = request.operation,
                args = request.args,
            ),
        )

        return QuicEnvelope(
            messageId = "response-${request.requestId}",
            traceId = traceId,
            deviceId = deviceId,
            channel = QuicChannel.CONTROL,
            kind = if (result.ok) QuicMessageKind.COMMAND_RESPONSE else QuicMessageKind.ERROR,
            payload = result.toPayload(),
        )
    }

    fun connect(endpoint: String) {
        connectionState = CompanionConnectionState.CONNECTING
        transport.connect(endpoint)
        connectionState = CompanionConnectionState.HANDSHAKING
    }

    fun currentState(): CompanionConnectionState = connectionState
}
