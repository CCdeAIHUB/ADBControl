package com.adbcontrol.companion.screen

import android.app.Activity
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.util.Log
import com.adbcontrol.companion.quic.CompanionQuicRuntime

class CompanionProjectionService : Service() {
    private var projection: MediaProjection? = null
    private var encoder: CompanionScreenEncoder? = null
    private var currentSessionId: String? = null

    override fun onCreate() {
        super.onCreate()
        createNotificationChannel()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        Log.i(TAG, "Projection service action=${intent?.action ?: "<restart>"} startId=$startId")
        when (intent?.action) {
            ACTION_STOP -> {
                stopProjection("stopped")
                stopSelf()
            }
            ACTION_START -> startProjection(intent)
        }
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        stopProjection("stopped")
        super.onDestroy()
    }

    private fun startProjection(intent: Intent) {
        val options = ProjectionConsentActivity.readOptions(intent)
        val resultData = intent.getParcelableExtra<Intent>(EXTRA_RESULT_DATA)
        val resultCode = intent.getIntExtra(EXTRA_RESULT_CODE, Activity.RESULT_CANCELED)
        if (resultCode != Activity.RESULT_OK || resultData == null) {
            reportFailure(options.sessionId, "屏幕录制授权数据无效。")
            stopSelf()
            return
        }

        startForeground(NOTIFICATION_ID, buildNotification("正在准备投屏"))
        stopProjection(null)
        currentSessionId = options.sessionId
        ProjectionSessionState.update(ProjectionSessionState.State.STARTING, options.sessionId)
        try {
            val manager = getSystemService(MediaProjectionManager::class.java)
            val activeProjection = manager.getMediaProjection(resultCode, resultData)
                ?: error("Android 未返回有效的 MediaProjection 会话。")
            activeProjection.registerCallback(object : MediaProjection.Callback() {
                override fun onStop() {
                    stopProjection("projectionRevoked")
                    stopSelf()
                }
            }, Handler(Looper.getMainLooper()))
            val activeEncoder = CompanionScreenEncoder(
                activeProjection,
                resources.configuration.densityDpi,
                options,
            )
            projection = activeProjection
            encoder = activeEncoder
            activeEncoder.start()
            ProjectionSessionState.update(ProjectionSessionState.State.STREAMING, options.sessionId)
            Log.i(
                TAG,
                "Projection streaming session=${options.sessionId} size=${options.width}x${options.height} bitrate=${options.bitrate} fps=${options.frameRate}",
            )
            CompanionQuicRuntime.sendProjectionEvent(
                options.sessionId,
                "streaming",
                mapOf(
                    "width" to options.width,
                    "height" to options.height,
                    "bitrate" to options.bitrate,
                    "frameRate" to options.frameRate,
                    "codec" to "h264",
                ),
            )
            getSystemService(NotificationManager::class.java)
                .notify(NOTIFICATION_ID, buildNotification("正在投屏"))
        } catch (error: Throwable) {
            Log.e(TAG, "Unable to start MediaProjection", error)
            reportFailure(options.sessionId, error.message ?: error.javaClass.simpleName)
            stopProjection(null)
            stopSelf()
        }
    }

    private fun stopProjection(reason: String?) {
        val sessionId = currentSessionId
        if (sessionId != null) {
            Log.i(TAG, "Stopping projection session=$sessionId reason=${reason ?: "replacement"}")
        }
        currentSessionId = null
        encoder?.stop()
        encoder = null
        val activeProjection = projection
        projection = null
        runCatching { activeProjection?.stop() }
        if (sessionId != null && reason != null) {
            ProjectionSessionState.update(ProjectionSessionState.State.STOPPED, sessionId)
            runCatching {
                CompanionQuicRuntime.sendProjectionEvent(
                    sessionId,
                    "stopped",
                    mapOf("reason" to reason),
                )
            }
        }
        if (reason != null) stopForeground(STOP_FOREGROUND_REMOVE)
    }

    private fun reportFailure(sessionId: String, message: String) {
        ProjectionSessionState.update(ProjectionSessionState.State.FAILED, sessionId, message)
        runCatching {
            CompanionQuicRuntime.sendProjectionEvent(
                sessionId,
                "failed",
                mapOf("message" to message),
                failed = true,
            )
        }
    }

    private fun createNotificationChannel() {
        getSystemService(NotificationManager::class.java).createNotificationChannel(
            NotificationChannel(
                NOTIFICATION_CHANNEL,
                "ADBControl 投屏",
                NotificationManager.IMPORTANCE_LOW,
            ),
        )
    }

    private fun buildNotification(text: String): Notification {
        return Notification.Builder(this, NOTIFICATION_CHANNEL)
            .setSmallIcon(android.R.drawable.presence_video_online)
            .setContentTitle("ADBControl 伴侣 App")
            .setContentText(text)
            .setOngoing(true)
            .build()
    }

    companion object {
        private const val TAG = "ADBControlProjection"
        private const val NOTIFICATION_CHANNEL = "adbcontrol_projection"
        private const val NOTIFICATION_ID = 15039
        private const val ACTION_START = "com.adbcontrol.companion.PROJECTION_START"
        const val ACTION_STOP = "com.adbcontrol.companion.PROJECTION_STOP"
        private const val EXTRA_RESULT_CODE = "projectionResultCode"
        private const val EXTRA_RESULT_DATA = "projectionResultData"

        fun startIntent(
            context: Context,
            resultCode: Int,
            resultData: Intent,
            options: ProjectionOptions,
        ): Intent {
            return Intent(context, CompanionProjectionService::class.java).apply {
                action = ACTION_START
                putExtra(EXTRA_RESULT_CODE, resultCode)
                putExtra(EXTRA_RESULT_DATA, resultData)
                ProjectionConsentActivity.run { putOptions(options) }
            }
        }

        fun stop(context: Context) {
            context.startService(Intent(context, CompanionProjectionService::class.java).apply {
                action = ACTION_STOP
            })
        }
    }
}
