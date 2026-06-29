package com.adbcontrol.companion.features

import com.adbcontrol.companion.core.CompanionCommandContext
import com.adbcontrol.companion.core.CompanionCommandResult

class PlaceholderSensitiveStreamHandler : FeatureCommandHandler {
    override val capabilityIds: Set<String> = setOf(
        "android.input.ime",
        "android.screen.capture",
        "android.camera.stream",
        "android.audio.record",
    )
    override val operations: Set<String> = setOf(
        "input.text",
        "input.key",
        "stream.open",
        "stream.close",
        "screenshot.capture",
        "camera.open",
        "camera.close",
        "audio.record.start",
        "audio.record.stop",
    )

    override fun handle(context: CompanionCommandContext): CompanionCommandResult {
        return CompanionCommandResult.failure(
            requestId = context.requestId,
            errorCode = "COMPANION_STREAM_PIPELINE_NOT_READY",
            message = "${context.capabilityId}/${context.operation} requires the dedicated media or input pipeline that is not implemented in this phase.",
            module = "companion.stream",
            recoverable = true,
            suggestion = "Keep the permission state visible and connect the dedicated encoder/input pipeline before enabling this operation.",
        )
    }
}
