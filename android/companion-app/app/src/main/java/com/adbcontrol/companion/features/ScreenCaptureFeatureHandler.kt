package com.adbcontrol.companion.features

import android.content.Context
import android.content.Intent
import com.adbcontrol.companion.core.CompanionCommandContext
import com.adbcontrol.companion.core.CompanionCommandResult
import com.adbcontrol.companion.media.MediaStreamSink
import com.adbcontrol.companion.screen.ScreenCaptureConsentActivity
import com.adbcontrol.companion.screen.ScreenCaptureException
import com.adbcontrol.companion.screen.ScreenCaptureState
import java.util.UUID

class ScreenCaptureFeatureHandler(
    private val context: Context,
    private val mediaStreamSink: MediaStreamSink,
) : FeatureCommandHandler {
    override val capabilityIds: Set<String> = setOf("android.screen.capture")
    override val operations: Set<String> = setOf("stream.open", "stream.close", "screenshot.capture")

    override fun handle(context: CompanionCommandContext): CompanionCommandResult {
        return when (context.operation) {
            "stream.open" -> openStream(context)
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

    private fun openStream(command: CompanionCommandContext): CompanionCommandResult {
        if (!ScreenCaptureState.isProjectionReady()) {
            return requestCaptureConsent(command)
        }

        val width = command.args.intArg("width") ?: context.resources.displayMetrics.widthPixels
        val height = command.args.intArg("height") ?: context.resources.displayMetrics.heightPixels
        val bitrate = command.args.intArg("bitrate") ?: 4_000_000
        val frameRate = command.args.intArg("frameRate") ?: 30
        val realtime = command.args.booleanArg("realtime") ?: false

        return try {
            val stream = if (realtime) {
                ScreenCaptureState.startRealtimeVideoStream(
                    context = context,
                    width = width,
                    height = height,
                    bitrate = bitrate,
                    frameRate = frameRate,
                    sink = mediaStreamSink,
                )
            } else {
                ScreenCaptureState.startVideoStream(
                    context = context,
                    width = width,
                    height = height,
                    bitrate = bitrate,
                    frameRate = frameRate,
                )
            }
            CompanionCommandResult.success(
                requestId = command.requestId,
                result = mapOf(
                    "sessionId" to stream.sessionId,
                    "path" to stream.path,
                    "width" to stream.width,
                    "height" to stream.height,
                    "bitrate" to stream.bitrate,
                    "frameRate" to stream.frameRate,
                    "format" to if (realtime) "h264-annexb" else "mp4-h264",
                    "transport" to if (realtime) "media-sink" else "sandbox-file",
                    "state" to "streaming",
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
        val sessionId = command.args.stringArg("sessionId")
        return try {
            val closed = if (sessionId == null) {
                ScreenCaptureState.stop()
                null
            } else {
                ScreenCaptureState.stopVideoStream(context, sessionId)
            }
            CompanionCommandResult.success(
                requestId = command.requestId,
                result = mapOf(
                    "state" to "closed",
                    "sessionId" to closed?.sessionId,
                    "path" to closed?.path,
                    "sizeBytes" to closed?.sizeBytes,
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
