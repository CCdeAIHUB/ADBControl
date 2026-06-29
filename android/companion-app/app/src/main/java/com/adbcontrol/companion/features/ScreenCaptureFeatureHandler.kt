package com.adbcontrol.companion.features

import android.content.Context
import android.content.Intent
import com.adbcontrol.companion.core.CompanionCommandContext
import com.adbcontrol.companion.core.CompanionCommandResult
import com.adbcontrol.companion.screen.ScreenCaptureConsentActivity
import com.adbcontrol.companion.screen.ScreenCaptureState
import java.util.UUID

class ScreenCaptureFeatureHandler(private val context: Context) : FeatureCommandHandler {
    override val capabilityIds: Set<String> = setOf("android.screen.capture")
    override val operations: Set<String> = setOf("stream.open", "stream.close", "screenshot.capture")

    override fun handle(context: CompanionCommandContext): CompanionCommandResult {
        return when (context.operation) {
            "stream.open" -> requestCaptureConsent(context)
            "stream.close" -> closeCapture(context)
            "screenshot.capture" -> captureScreenshot(context)
            else -> CompanionCommandResult.failure(
                requestId = context.requestId,
                errorCode = "COMPANION_OPERATION_NOT_SUPPORTED",
                message = "Unsupported screen capture operation: ${context.operation}",
                module = "companion.screen",
                recoverable = false,
            )
        }
    }

    private fun requestCaptureConsent(command: CompanionCommandContext): CompanionCommandResult {
        val streamId = command.args.stringArg("streamId") ?: UUID.randomUUID().toString()
        val intent = Intent(context, ScreenCaptureConsentActivity::class.java).apply {
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            putExtra(ScreenCaptureConsentActivity.EXTRA_STREAM_ID, streamId)
        }
        context.startActivity(intent)

        return CompanionCommandResult.success(
            requestId = command.requestId,
            result = mapOf(
                "streamId" to streamId,
                "state" to "consent-requested",
                "projectionReady" to ScreenCaptureState.isProjectionReady(),
            ),
        )
    }

    private fun closeCapture(command: CompanionCommandContext): CompanionCommandResult {
        ScreenCaptureState.stop()
        return CompanionCommandResult.success(
            requestId = command.requestId,
            result = mapOf(
                "state" to "closed",
            ),
        )
    }

    private fun captureScreenshot(command: CompanionCommandContext): CompanionCommandResult {
        if (!ScreenCaptureState.isProjectionReady()) {
            return CompanionCommandResult.failure(
                requestId = command.requestId,
                errorCode = "COMPANION_MEDIA_PROJECTION_CONSENT_REQUIRED",
                message = "Screen capture requires Android MediaProjection user consent before screenshots can be captured.",
                module = "companion.screen",
                recoverable = true,
                suggestion = "Call stream.open and approve the Android screen capture consent dialog first.",
            )
        }

        return CompanionCommandResult.failure(
            requestId = command.requestId,
            errorCode = "COMPANION_SCREEN_ENCODER_NOT_READY",
            message = "MediaProjection consent is available, but screenshot frame extraction is not connected to ImageReader/encoder yet.",
            module = "companion.screen",
            recoverable = true,
            suggestion = "Attach an ImageReader or encoder surface to ScreenCaptureState before calling screenshot.capture.",
        )
    }
}
