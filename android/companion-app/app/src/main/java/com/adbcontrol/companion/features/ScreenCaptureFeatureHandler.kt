package com.adbcontrol.companion.features

import android.content.Context
import android.content.Intent
import com.adbcontrol.companion.core.CompanionCommandContext
import com.adbcontrol.companion.core.CompanionCommandResult
import com.adbcontrol.companion.screen.ScreenCaptureConsentActivity
import com.adbcontrol.companion.screen.ScreenCaptureException
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
        val width = command.args.intArg("width") ?: context.resources.displayMetrics.widthPixels
        val height = command.args.intArg("height") ?: context.resources.displayMetrics.heightPixels
        val timeoutMs = (command.args.intArg("timeoutMs") ?: 1_500).toLong()

        return try {
            val capture = ScreenCaptureState.capturePng(
                context = context,
                width = width,
                height = height,
                timeoutMs = timeoutMs,
            )
            CompanionCommandResult.success(
                requestId = command.requestId,
                result = mapOf(
                    "streamId" to capture.streamId,
                    "path" to capture.path,
                    "width" to capture.width,
                    "height" to capture.height,
                    "sizeBytes" to capture.sizeBytes,
                    "format" to "png",
                    "state" to "captured",
                ),
            )
        } catch (exception: ScreenCaptureException) {
            CompanionCommandResult.failure(
                requestId = command.requestId,
                errorCode = exception.errorCode,
                message = exception.message,
                module = "companion.screen",
                recoverable = exception.recoverable,
                suggestion = exception.suggestion,
            )
        }
    }
}
