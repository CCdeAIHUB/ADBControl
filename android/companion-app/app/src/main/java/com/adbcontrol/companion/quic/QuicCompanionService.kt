package com.adbcontrol.companion.quic

import android.app.Service
import android.content.Intent
import android.os.Build
import android.os.IBinder
import com.adbcontrol.companion.core.AndroidCapabilityCatalog
import com.adbcontrol.companion.core.CompanionCommandContext
import com.adbcontrol.companion.core.PermissionGuard
import com.adbcontrol.companion.features.AndroidFeatureDispatcher
import com.adbcontrol.companion.media.QuicMediaStreamSink
import com.adbcontrol.companion.pairing.PairingConfirmationActivity
import com.adbcontrol.companion.pairing.PairingDecisionState
import com.adbcontrol.companion.pairing.PairingDecisionStore


enum class CompanionConnectionState {
    DISCONNECTED,
    CONNECTING,
    HANDSHAKING,
    READY,
    DEGRADED,
    FAILED,
}

class QuicCompanionService : Service() {
    private var transport: QuicTransport = UnconfiguredQuicTransport()
    private lateinit var permissionGuard: PermissionGuard
    private lateinit var featureDispatcher: AndroidFeatureDispatcher
    private lateinit var pairingDecisionStore: PairingDecisionStore
    private var connectionState: CompanionConnectionState = CompanionConnectionState.DISCONNECTED
    private var connectedDeviceId: String? = null
    private var certificateFingerprintSha256: String? = null

    override fun onCreate() {
        super.onCreate()
        permissionGuard = PermissionGuard(this)
        pairingDecisionStore = PairingDecisionStore(this)
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
            "certificateFingerprintSha256" to certificateFingerprintSha256,
        )

        return QuicEnvelope(
            messageId = "hello-$deviceId",
            deviceId = deviceId,
            channel = QuicChannel.CONTROL,
            kind = QuicMessageKind.HELLO,
            payload = hello,
        )
    }

    fun buildCapabilityListEnvelope(deviceId: String): QuicEnvelope {
        return QuicEnvelope(
            messageId = "capability-list-$deviceId",
            deviceId = deviceId,
            channel = QuicChannel.CONTROL,
            kind = QuicMessageKind.CAPABILITY_LIST,
            payload = mapOf(
                "deviceId" to deviceId,
                "certificateFingerprintSha256" to certificateFingerprintSha256,
                "capabilities" to AndroidCapabilityCatalog.defaultCapabilities().map { capability ->
                    mapOf(
                        "id" to capability.id,
                        "title" to capability.title,
                        "androidPermissions" to capability.androidPermissions,
                        "specialGrants" to capability.specialGrants,
                        "sensitivity" to capability.sensitivity.name.lowercase(),
                        "operations" to capability.operations,
                        "requiresUserConsent" to capability.requiresUserConsent,
                    )
                },
            ),
        )
    }

    fun buildPermissionStateEnvelope(deviceId: String): QuicEnvelope {
        return QuicEnvelope(
            messageId = "permission-state-$deviceId",
            deviceId = deviceId,
            channel = QuicChannel.CONTROL,
            kind = QuicMessageKind.PERMISSION_STATE,
            payload = mapOf(
                "deviceId" to deviceId,
                "certificateFingerprintSha256" to certificateFingerprintSha256,
                "states" to buildPermissionState(),
            ),
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

    fun handlePairingRequest(request: CompanionPairingRequest): QuicEnvelope {
        val existing = pairingDecisionStore.getState(request.pairingId)
        if (existing == null) {
            startActivity(Intent(this, PairingConfirmationActivity::class.java).apply {
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                putExtra(PairingConfirmationActivity.EXTRA_PAIRING_ID, request.pairingId)
                putExtra(PairingConfirmationActivity.EXTRA_CORE_NAME, request.coreName)
                putExtra(PairingConfirmationActivity.EXTRA_DEVICE_ID, request.deviceId)
                putExtra(PairingConfirmationActivity.EXTRA_SHORT_CODE, request.shortCode)
                putExtra(PairingConfirmationActivity.EXTRA_CERTIFICATE_FINGERPRINT_SHA256, request.certificateFingerprintSha256)
                putExtra(PairingConfirmationActivity.EXTRA_EXPIRES_AT_UNIX_MS, request.expiresAtUnixMs)
            })
            return pairingResponse(request, "pending-user-confirmation")
        }
        return pairingResponse(
            request,
            if (existing == PairingDecisionState.APPROVED) "approved" else "rejected",
        )
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
            payload = result.toPayload() + mapOf("certificateFingerprintSha256" to certificateFingerprintSha256),
        )
    }

    fun connect(endpoint: String, deviceId: String = "android-companion") {
        connectionState = CompanionConnectionState.CONNECTING
        val engine = NativeQuicEngineProvider.create()
        val nativeTransport = NativeQuicTransport(engine)
        nativeTransport.connect(endpoint)
        transport = nativeTransport
        connectedDeviceId = deviceId
        certificateFingerprintSha256 = (engine as? JniNativeQuicEngine)?.certificateFingerprintSha256()
        featureDispatcher = AndroidFeatureDispatcher(
            this,
            permissionGuard,
            QuicMediaStreamSink(transport, connectedDeviceId, certificateFingerprintSha256),
        )
        transport.send(buildHello(deviceId, Build.MODEL ?: "Android device"))
        transport.send(buildCapabilityListEnvelope(deviceId))
        transport.send(buildPermissionStateEnvelope(deviceId))
        connectionState = CompanionConnectionState.HANDSHAKING
    }

    private fun pairingResponse(request: CompanionPairingRequest, state: String): QuicEnvelope {
        return QuicEnvelope(
            messageId = "pairing-response-${request.pairingId}",
            traceId = request.pairingId,
            deviceId = request.deviceId,
            channel = QuicChannel.CONTROL,
            kind = QuicMessageKind.COMMAND_RESPONSE,
            payload = mapOf(
                "pairingId" to request.pairingId,
                "state" to state,
                "certificateFingerprintSha256" to request.certificateFingerprintSha256,
            ),
        )
    }

    override fun onDestroy() {
        transport.close()
        super.onDestroy()
    }

    fun currentState(): CompanionConnectionState = connectionState
}

data class CompanionPairingRequest(
    val pairingId: String,
    val coreName: String,
    val deviceId: String,
    val shortCode: String,
    val certificateFingerprintSha256: String,
    val expiresAtUnixMs: Long,
)
