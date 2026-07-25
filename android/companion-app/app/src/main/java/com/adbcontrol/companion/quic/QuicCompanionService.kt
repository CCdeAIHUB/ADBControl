package com.adbcontrol.companion.quic

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.provider.Settings
import android.util.Base64
import android.util.Log
import com.adbcontrol.companion.BuildConfig
import com.adbcontrol.companion.core.AndroidCapabilityCatalog
import com.adbcontrol.companion.core.CompanionCommandContext
import com.adbcontrol.companion.core.PermissionGuard
import com.adbcontrol.companion.features.AndroidFeatureDispatcher
import com.adbcontrol.companion.media.QuicMediaStreamSink
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong

enum class CompanionConnectionState {
    DISCONNECTED,
    CONNECTING,
    HANDSHAKING,
    READY,
    DEGRADED,
    FAILED,
}

private data class PendingConnectionConfig(
    val endpoint: String,
    val deviceId: String,
    val serverName: String,
    val certificateDer: ByteArray,
)

class QuicCompanionService : Service() {
    @Volatile private var transport: NativeQuicTransport? = null
    private lateinit var permissionGuard: PermissionGuard
    private lateinit var featureDispatcher: AndroidFeatureDispatcher
    private val reconnectHandler = Handler(Looper.getMainLooper())
    private val receiveExecutor = Executors.newSingleThreadExecutor { runnable ->
        Thread(runnable, "ADBControl-quic-control").apply { isDaemon = true }
    }
    private val connectionExecutor = Executors.newSingleThreadExecutor { runnable ->
        Thread(runnable, "ADBControl-quic-connect").apply { isDaemon = true }
    }
    private val connectionInProgress = AtomicBoolean(false)
    @Volatile private var pendingConnectionConfig: PendingConnectionConfig? = null
    private val connectionGeneration = AtomicLong(0)
    @Volatile private var connectionState = CompanionConnectionState.DISCONNECTED
    private var connectedDeviceId: String? = null
    private var certificateFingerprintSha256: String? = null
    private var reconnectAttempts = 0
    private var savedEndpoint: String? = null
    private var savedDeviceId: String? = null
    private var savedServerName: String? = null
    private var savedCertificateDer: ByteArray? = null
    private var reconnectRunnable: Runnable? = null

    override fun onCreate() {
        super.onCreate()
        Log.i(TAG, "QUIC companion service creating")
        permissionGuard = PermissionGuard(this)
        featureDispatcher = AndroidFeatureDispatcher(this, permissionGuard)
        installNativeEngineIfAvailable()
        loadConnectionConfig()
        createNotificationChannel()
        startForeground(NOTIFICATION_ID, buildNotification("等待桌面端连接"))
        Log.i(TAG, "QUIC companion service entered foreground")
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        Log.i(TAG, "QUIC service start action=${intent?.action ?: "<restart>"} startId=$startId")
        if (intent?.action == ACTION_CONFIGURE_CONNECTION) {
            val endpoint = intent.getStringExtra(EXTRA_ENDPOINT)
                ?: buildEndpoint(intent.getStringExtra(EXTRA_HOST), intent.getIntExtra(EXTRA_PORT, -1))
            val deviceId = intent.getStringExtra(EXTRA_DEVICE_ID) ?: defaultDeviceId()
            val serverName = intent.getStringExtra(EXTRA_SERVER_NAME) ?: DEFAULT_SERVER_NAME
            val certificateDer = intent.getStringExtra(EXTRA_CERTIFICATE_DER_BASE64)
                ?.let(::decodeCertificate)
            if (!endpoint.isNullOrBlank() && certificateDer != null) {
                Log.i(TAG, "Received QUIC config endpoint=$endpoint deviceId=$deviceId serverName=$serverName")
                saveConnectionConfig(endpoint, deviceId, serverName, certificateDer)
                reconnectAttempts = 0
                connectWithRetry(endpoint, deviceId, serverName, certificateDer)
            } else {
                Log.e(TAG, "Rejected QUIC config endpoint=$endpoint certificatePresent=${certificateDer != null}")
                connectionState = CompanionConnectionState.FAILED
                updateNotification("连接配置缺少 TLS 证书")
            }
        } else {
            reconnectFromSavedConfig(immediate = true)
        }
        return START_STICKY
    }

    private fun connectWithRetry(
        endpoint: String,
        deviceId: String,
        serverName: String,
        certificateDer: ByteArray,
    ) {
        cancelReconnect()
        pendingConnectionConfig = PendingConnectionConfig(endpoint, deviceId, serverName, certificateDer)
        startPendingConnectionIfIdle()
    }

    private fun startPendingConnectionIfIdle() {
        if (!connectionInProgress.compareAndSet(false, true)) {
            Log.i(TAG, "Queued latest QUIC config behind the active connection attempt")
            return
        }
        connectionState = CompanionConnectionState.CONNECTING
        updateNotification("正在连接桌面端")
        connectionExecutor.execute {
            while (true) {
                val config = pendingConnectionConfig ?: break
                pendingConnectionConfig = null
                try {
                    connect(config.endpoint, config.deviceId, config.serverName, config.certificateDer)
                    reconnectAttempts = 0
                } catch (error: Throwable) {
                    Log.e(TAG, "QUIC connect failed", error)
                    if (pendingConnectionConfig == null) {
                        reconnectHandler.post {
                            connectionState = CompanionConnectionState.FAILED
                            updateNotification("连接失败，正在重试")
                            scheduleReconnect(
                                config.endpoint,
                                config.deviceId,
                                config.serverName,
                                config.certificateDer,
                            )
                        }
                    }
                }
            }
            connectionInProgress.set(false)
            if (pendingConnectionConfig != null) startPendingConnectionIfIdle()
        }
    }

    fun connect(
        endpoint: String,
        deviceId: String,
        serverName: String,
        certificateDer: ByteArray,
    ) {
        Log.i(TAG, "Connecting native QUIC transport to $endpoint")
        connectionState = CompanionConnectionState.CONNECTING
        updateNotification("正在连接桌面端")
        closeCurrentTransport()
        val nativeTransport = NativeQuicTransport(NativeQuicEngineProvider.create())
        nativeTransport.connect(endpoint, serverName, certificateDer)
        transport = nativeTransport
        connectedDeviceId = deviceId
        certificateFingerprintSha256 = nativeTransport.certificateFingerprintSha256()
        CompanionQuicRuntime.attach(nativeTransport, deviceId)
        featureDispatcher = AndroidFeatureDispatcher(
            this,
            permissionGuard,
            QuicMediaStreamSink(nativeTransport, deviceId, certificateFingerprintSha256),
        )
        nativeTransport.send(buildHello(deviceId, Build.MODEL ?: "Android device"))
        nativeTransport.send(buildCapabilityListEnvelope(deviceId))
        nativeTransport.send(buildPermissionStateEnvelope(deviceId))
        connectionState = CompanionConnectionState.HANDSHAKING
        Log.i(TAG, "QUIC TLS connected; hello and capability envelopes sent")
        updateNotification("正在验证桌面端")
        startReceiveLoop(nativeTransport, connectionGeneration.incrementAndGet())
    }

    private fun startReceiveLoop(activeTransport: NativeQuicTransport, generation: Long) {
        receiveExecutor.execute {
            try {
                while (generation == connectionGeneration.get() && transport === activeTransport) {
                    val envelope = activeTransport.poll(POLL_TIMEOUT_MS) ?: continue
                    handleIncomingEnvelope(activeTransport, envelope)
                }
            } catch (error: Throwable) {
                Log.e(TAG, "QUIC control loop stopped", error)
                reconnectHandler.post {
                    if (generation == connectionGeneration.get() && transport === activeTransport) {
                        closeCurrentTransport()
                        connectionState = CompanionConnectionState.DEGRADED
                        updateNotification("连接中断，正在重连")
                        reconnectFromSavedConfig()
                    }
                }
            }
        }
    }

    private fun handleIncomingEnvelope(activeTransport: NativeQuicTransport, envelope: QuicEnvelope) {
        if (envelope.protocol != COMPANION_PROTOCOL || envelope.version != COMPANION_PROTOCOL_VERSION) {
            throw IllegalStateException("桌面端 QUIC 协议或版本不兼容。")
        }
        when (envelope.kind) {
            QuicMessageKind.HELLO_ACK -> {
                connectionState = CompanionConnectionState.READY
                Log.i(TAG, "QUIC helloAck received; connection is ready")
                updateNotification("已连接桌面端")
            }
            QuicMessageKind.HEARTBEAT -> activeTransport.send(
                QuicEnvelope(
                    messageId = "heartbeat-${System.currentTimeMillis()}",
                    traceId = envelope.traceId,
                    deviceId = connectedDeviceId,
                    channel = QuicChannel.CONTROL,
                    kind = QuicMessageKind.HEARTBEAT,
                    payload = mapOf("timestampUnixMs" to System.currentTimeMillis()),
                ),
            )
            QuicMessageKind.COMMAND_REQUEST -> {
                val request = envelope.toCommandRequest()
                activeTransport.send(
                    handleCommandRequest(
                        envelope.deviceId ?: connectedDeviceId ?: defaultDeviceId(),
                        envelope.traceId,
                        request,
                    ),
                )
            }
            else -> Log.d(TAG, "Ignored desktop envelope ${envelope.kind}/${envelope.messageId}")
        }
    }

    private fun QuicEnvelope.toCommandRequest(): CompanionCommandRequest {
        val requestId = payload["requestId"]?.toString() ?: messageId
        val capabilityId = payload["capabilityId"]?.toString()
            ?: error("commandRequest 缺少 capabilityId。")
        val operation = payload["operation"]?.toString()
            ?: error("commandRequest 缺少 operation。")
        @Suppress("UNCHECKED_CAST")
        val args = (payload["args"] as? Map<*, *>)
            ?.entries
            ?.associate { (key, value) -> key.toString() to value }
            ?: emptyMap()
        return CompanionCommandRequest(requestId, capabilityId, operation, args)
    }

    fun buildHello(deviceId: String, deviceName: String): QuicEnvelope {
        return QuicEnvelope(
            messageId = "hello-$deviceId",
            deviceId = deviceId,
            channel = QuicChannel.CONTROL,
            kind = QuicMessageKind.HELLO,
            payload = mapOf(
                "appVersion" to BuildConfig.VERSION_NAME,
                "deviceId" to deviceId,
                "deviceName" to deviceName,
                "androidSdk" to Build.VERSION.SDK_INT,
                "supportedProtocolVersions" to listOf(COMPANION_PROTOCOL_VERSION),
                "certificateFingerprintSha256" to certificateFingerprintSha256,
            ),
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
                "states" to AndroidCapabilityCatalog.defaultCapabilities().map { capability ->
                    val evaluation = permissionGuard.evaluate(capability)
                    mapOf(
                        "capabilityId" to evaluation.capabilityId,
                        "granted" to evaluation.granted,
                        "missingPermissions" to evaluation.missingPermissions,
                        "missingSpecialGrants" to evaluation.missingSpecialGrants,
                        "requiresUserConsent" to capability.requiresUserConsent,
                    )
                },
            ),
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
            payload = result.toPayload() + mapOf(
                "certificateFingerprintSha256" to certificateFingerprintSha256,
            ),
        )
    }

    private fun scheduleReconnect(
        endpoint: String,
        deviceId: String,
        serverName: String,
        certificateDer: ByteArray,
    ) {
        if (reconnectAttempts >= MAX_RECONNECT_ATTEMPTS) return
        reconnectAttempts += 1
        reconnectRunnable = Runnable {
            reconnectRunnable = null
            connectWithRetry(endpoint, deviceId, serverName, certificateDer)
        }.also { reconnectHandler.postDelayed(it, reconnectDelayMs(reconnectAttempts)) }
    }

    private fun reconnectFromSavedConfig(immediate: Boolean = false) {
        val endpoint = savedEndpoint ?: return
        val certificate = savedCertificateDer ?: return
        val deviceId = savedDeviceId ?: defaultDeviceId()
        val serverName = savedServerName ?: DEFAULT_SERVER_NAME
        if (immediate) {
            connectWithRetry(endpoint, deviceId, serverName, certificate)
        } else {
            scheduleReconnect(endpoint, deviceId, serverName, certificate)
        }
    }

    private fun closeCurrentTransport() {
        connectionGeneration.incrementAndGet()
        transport?.let { active ->
            CompanionQuicRuntime.detach(active)
            runCatching { active.close() }
        }
        transport = null
    }

    private fun cancelReconnect() {
        reconnectRunnable?.let(reconnectHandler::removeCallbacks)
        reconnectRunnable = null
    }

    private fun saveConnectionConfig(
        endpoint: String,
        deviceId: String,
        serverName: String,
        certificateDer: ByteArray,
    ) {
        savedEndpoint = endpoint
        savedDeviceId = deviceId
        savedServerName = serverName
        savedCertificateDer = certificateDer
        getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE).edit()
            .putString(PREF_ENDPOINT, endpoint)
            .putString(PREF_DEVICE_ID, deviceId)
            .putString(PREF_SERVER_NAME, serverName)
            .putString(PREF_CERTIFICATE, Base64.encodeToString(certificateDer, Base64.NO_WRAP))
            .apply()
    }

    private fun loadConnectionConfig() {
        val prefs = getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
        savedEndpoint = prefs.getString(PREF_ENDPOINT, null)
        savedDeviceId = prefs.getString(PREF_DEVICE_ID, null)
        savedServerName = prefs.getString(PREF_SERVER_NAME, DEFAULT_SERVER_NAME)
        savedCertificateDer = prefs.getString(PREF_CERTIFICATE, null)?.let(::decodeCertificate)
    }

    private fun installNativeEngineIfAvailable() {
        try {
            JniNativeQuicEngine.installAsProvider()
        } catch (error: Throwable) {
            Log.e(TAG, "Native QUIC library is unavailable", error)
            NativeQuicEngineProvider.clearFactory()
        }
    }

    private fun createNotificationChannel() {
        getSystemService(NotificationManager::class.java).createNotificationChannel(
            NotificationChannel(
                NOTIFICATION_CHANNEL,
                "ADBControl 连接",
                NotificationManager.IMPORTANCE_LOW,
            ),
        )
    }

    private fun buildNotification(text: String): Notification {
        return Notification.Builder(this, NOTIFICATION_CHANNEL)
            .setSmallIcon(android.R.drawable.stat_sys_data_bluetooth)
            .setContentTitle("ADBControl 伴侣 App")
            .setContentText(text)
            .setOngoing(true)
            .build()
    }

    private fun updateNotification(text: String) {
        getSystemService(NotificationManager::class.java)
            .notify(NOTIFICATION_ID, buildNotification(text))
    }

    override fun onDestroy() {
        cancelReconnect()
        closeCurrentTransport()
        receiveExecutor.shutdownNow()
        connectionExecutor.shutdownNow()
        connectionState = CompanionConnectionState.DISCONNECTED
        super.onDestroy()
    }

    fun currentState(): CompanionConnectionState = connectionState

    private fun defaultDeviceId(): String {
        return Settings.Secure.getString(contentResolver, Settings.Secure.ANDROID_ID)
            ?.takeIf { it.isNotBlank() }
            ?: Build.MODEL
            ?: "android-companion"
    }

    companion object {
        const val ACTION_CONFIGURE_CONNECTION = "com.adbcontrol.companion.CONFIGURE_CONNECTION"
        const val EXTRA_ENDPOINT = "endpoint"
        const val EXTRA_HOST = "host"
        const val EXTRA_PORT = "port"
        const val EXTRA_DEVICE_ID = "deviceId"
        const val EXTRA_SERVER_NAME = "serverName"
        const val EXTRA_CERTIFICATE_DER_BASE64 = "certificateDerBase64"
        private const val TAG = "ADBControlQuic"
        private const val DEFAULT_SERVER_NAME = "adbcontrol.local"
        private const val PREFS_NAME = "adbcontrol_companion_connection"
        private const val PREF_ENDPOINT = "endpoint"
        private const val PREF_DEVICE_ID = "deviceId"
        private const val PREF_SERVER_NAME = "serverName"
        private const val PREF_CERTIFICATE = "certificateDerBase64"
        private const val NOTIFICATION_CHANNEL = "adbcontrol_connection"
        private const val NOTIFICATION_ID = 15038
        private const val POLL_TIMEOUT_MS = 50
        private const val MAX_RECONNECT_ATTEMPTS = 60

        fun hasSavedConnection(context: Context): Boolean {
            val prefs = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
            return !prefs.getString(PREF_ENDPOINT, null).isNullOrBlank() &&
                !prefs.getString(PREF_CERTIFICATE, null).isNullOrBlank()
        }

        private fun decodeCertificate(base64: String): ByteArray? = runCatching {
            Base64.decode(base64, Base64.DEFAULT)
        }.getOrNull()?.takeIf { it.isNotEmpty() }

        private fun reconnectDelayMs(attempt: Int): Long {
            return (1_000L shl attempt.coerceAtMost(5)).coerceAtMost(30_000L)
        }

        private fun buildEndpoint(host: String?, port: Int): String? {
            if (host.isNullOrBlank() || port !in 1..65535) return null
            return "quic://$host:$port"
        }
    }
}
