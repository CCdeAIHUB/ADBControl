package com.adbcontrol.companion.screen

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.media.projection.MediaProjectionManager
import android.os.Bundle
import com.adbcontrol.companion.quic.CompanionQuicRuntime

class ProjectionConsentActivity : Activity() {
    private lateinit var options: ProjectionOptions

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        options = readOptions(intent)
        if (savedInstanceState == null) {
            ProjectionSessionState.update(
                ProjectionSessionState.State.AWAITING_CONSENT,
                options.sessionId,
            )
            val manager = getSystemService(MediaProjectionManager::class.java)
            startActivityForResult(manager.createScreenCaptureIntent(), REQUEST_CAPTURE)
        }
    }

    @Deprecated("Deprecated by Android; MediaProjection still returns through this callback.")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode != REQUEST_CAPTURE) return
        if (resultCode != RESULT_OK || data == null) {
            ProjectionSessionState.update(
                ProjectionSessionState.State.FAILED,
                options.sessionId,
                "用户未授予屏幕录制权限。",
            )
            runCatching {
                CompanionQuicRuntime.sendProjectionEvent(
                    options.sessionId,
                    "consentDenied",
                    mapOf("message" to "用户未授予屏幕录制权限。"),
                    failed = true,
                )
            }
            finish()
            return
        }

        val serviceIntent = CompanionProjectionService.startIntent(
            this,
            resultCode,
            data,
            options,
        )
        startForegroundService(serviceIntent)
        finish()
    }

    companion object {
        private const val REQUEST_CAPTURE = 4201
        internal const val EXTRA_SESSION_ID = "projectionSessionId"
        internal const val EXTRA_WIDTH = "projectionWidth"
        internal const val EXTRA_HEIGHT = "projectionHeight"
        internal const val EXTRA_BITRATE = "projectionBitrate"
        internal const val EXTRA_FRAME_RATE = "projectionFrameRate"

        fun launch(context: Context, options: ProjectionOptions) {
            context.startActivity(Intent(context, ProjectionConsentActivity::class.java).apply {
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
                putOptions(options)
            })
        }

        internal fun Intent.putOptions(options: ProjectionOptions): Intent {
            return putExtra(EXTRA_SESSION_ID, options.sessionId)
                .putExtra(EXTRA_WIDTH, options.width)
                .putExtra(EXTRA_HEIGHT, options.height)
                .putExtra(EXTRA_BITRATE, options.bitrate)
                .putExtra(EXTRA_FRAME_RATE, options.frameRate)
        }

        internal fun readOptions(intent: Intent): ProjectionOptions {
            return ProjectionOptions(
                sessionId = intent.getStringExtra(EXTRA_SESSION_ID)
                    ?: java.util.UUID.randomUUID().toString(),
                width = intent.getIntExtra(EXTRA_WIDTH, 720),
                height = intent.getIntExtra(EXTRA_HEIGHT, 1280),
                bitrate = intent.getIntExtra(EXTRA_BITRATE, 4_000_000),
                frameRate = intent.getIntExtra(EXTRA_FRAME_RATE, 60),
            )
        }
    }
}
