package com.adbcontrol.companion.features

import android.content.Context
import android.app.KeyguardManager
import com.adbcontrol.companion.core.CompanionCommandContext
import com.adbcontrol.companion.core.CompanionCommandResult
import com.adbcontrol.companion.quic.CompanionQuicRuntime
import com.adbcontrol.companion.screen.CompanionProjectionService
import com.adbcontrol.companion.screen.ProjectionConsentActivity
import com.adbcontrol.companion.screen.ProjectionOptions
import com.adbcontrol.companion.screen.ProjectionSessionState

class ProjectionFeatureHandler(private val context: Context) : FeatureCommandHandler {
    override val capabilityIds: Set<String> = setOf("android.screen.projection")
    override val operations: Set<String> = setOf(
        "projection.start",
        "projection.stop",
        "projection.status",
    )

    override fun handle(context: CompanionCommandContext): CompanionCommandResult {
        return when (context.operation) {
            "projection.start" -> start(context)
            "projection.stop" -> stop(context)
            "projection.status" -> status(context)
            else -> error("Unsupported projection operation: ${context.operation}")
        }
    }

    private fun start(command: CompanionCommandContext): CompanionCommandResult {
        if (!CompanionQuicRuntime.isConnected()) {
            return CompanionCommandResult.failure(
                command.requestId,
                "COMPANION_QUIC_NOT_CONNECTED",
                "伴侣 App 尚未建立 QUIC 连接，无法发送投屏视频。",
                "companion.projection",
                true,
            )
        }
        if (context.getSystemService(KeyguardManager::class.java).isKeyguardLocked) {
            return CompanionCommandResult.failure(
                command.requestId,
                "DEVICE_LOCKED",
                "设备处于锁屏状态，请先解锁手机再确认投屏授权。",
                "companion.projection",
                true,
            )
        }
        val metrics = context.resources.displayMetrics
        val options = ProjectionOptions.fromArgs(command.args, metrics.widthPixels, metrics.heightPixels)
        ProjectionConsentActivity.launch(context, options)
        return CompanionCommandResult.success(
            command.requestId,
            mapOf(
                "state" to "awaitingConsent",
                "sessionId" to options.sessionId,
                "width" to options.width,
                "height" to options.height,
                "bitrate" to options.bitrate,
                "frameRate" to options.frameRate,
                "requiresMediaProjectionConsent" to true,
            ),
        )
    }

    private fun stop(command: CompanionCommandContext): CompanionCommandResult {
        CompanionProjectionService.stop(context)
        return CompanionCommandResult.success(
            command.requestId,
            mapOf("state" to "stopping", "sessionId" to ProjectionSessionState.sessionId),
        )
    }

    private fun status(command: CompanionCommandContext): CompanionCommandResult {
        return CompanionCommandResult.success(
            command.requestId,
            mapOf(
                "state" to ProjectionSessionState.state.name.lowercase(),
                "sessionId" to ProjectionSessionState.sessionId,
                "error" to ProjectionSessionState.error,
            ),
        )
    }
}
