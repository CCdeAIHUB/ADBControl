package com.adbcontrol.companion.features

import android.content.Context
import android.media.AudioManager
import com.adbcontrol.companion.core.CompanionCommandContext
import com.adbcontrol.companion.core.CompanionCommandResult

class MediaVolumeFeatureHandler(private val context: Context) : FeatureCommandHandler {
    override val capabilityIds: Set<String> = setOf("android.volume.media")
    override val operations: Set<String> = setOf("volume.get", "volume.set")

    override fun handle(context: CompanionCommandContext): CompanionCommandResult {
        val audioManager = this.context.getSystemService(AudioManager::class.java)

        return when (context.operation) {
            "volume.get" -> volumeState(context, audioManager)
            "volume.set" -> setVolume(context, audioManager)
            else -> CompanionCommandResult.failure(
                requestId = context.requestId,
                errorCode = "COMPANION_OPERATION_NOT_SUPPORTED",
                message = "不支持的音量操作：${context.operation}",
                module = "companion.volume",
                recoverable = false,
            )
        }
    }

    private fun volumeState(
        command: CompanionCommandContext,
        audioManager: AudioManager,
    ): CompanionCommandResult {
        return CompanionCommandResult.success(
            requestId = command.requestId,
            result = buildVolumePayload(audioManager),
        )
    }

    private fun setVolume(
        command: CompanionCommandContext,
        audioManager: AudioManager,
    ): CompanionCommandResult {
        if (audioManager.isVolumeFixed) {
            return CompanionCommandResult.failure(
                requestId = command.requestId,
                errorCode = "COMPANION_VOLUME_FIXED",
                message = "Android 报告该设备使用固定音量策略，无法调整媒体音量。",
                module = "companion.volume",
                recoverable = true,
            )
        }

        val level = command.args.intArg("level")
            ?: return CompanionCommandResult.failure(
                requestId = command.requestId,
                errorCode = "COMPANION_PARAMS_INVALID",
                message = "volume.set 需要 args.level 参数。",
                module = "companion.volume",
                recoverable = false,
            )
        val flags = command.args.intArg("flags") ?: 0
        val min = audioManager.getStreamMinVolume(AudioManager.STREAM_MUSIC)
        val max = audioManager.getStreamMaxVolume(AudioManager.STREAM_MUSIC)
        val boundedLevel = level.coerceIn(min, max)
        val before = audioManager.getStreamVolume(AudioManager.STREAM_MUSIC)

        audioManager.setStreamVolume(AudioManager.STREAM_MUSIC, boundedLevel, flags)

        return CompanionCommandResult.success(
            requestId = command.requestId,
            result = buildVolumePayload(audioManager) + mapOf(
                "previousLevel" to before,
                "requestedLevel" to level,
                "appliedLevel" to boundedLevel,
            ),
        )
    }

    private fun buildVolumePayload(audioManager: AudioManager): Map<String, Any?> {
        return mapOf(
            "stream" to "music",
            "currentLevel" to audioManager.getStreamVolume(AudioManager.STREAM_MUSIC),
            "minLevel" to audioManager.getStreamMinVolume(AudioManager.STREAM_MUSIC),
            "maxLevel" to audioManager.getStreamMaxVolume(AudioManager.STREAM_MUSIC),
            "volumeFixed" to audioManager.isVolumeFixed,
        )
    }
}
