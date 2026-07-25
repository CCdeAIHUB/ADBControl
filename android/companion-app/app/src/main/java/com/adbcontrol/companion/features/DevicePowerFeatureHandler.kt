package com.adbcontrol.companion.features

import android.content.Context
import android.os.PowerManager
import com.adbcontrol.companion.core.CompanionCommandContext
import com.adbcontrol.companion.core.CompanionCommandResult

class DevicePowerFeatureHandler(private val context: Context) : FeatureCommandHandler {
    override val capabilityIds: Set<String> = setOf("android.device.power")
    override val operations: Set<String> = setOf("device.wake")

    override fun handle(context: CompanionCommandContext): CompanionCommandResult {
        val powerManager = this.context.getSystemService(PowerManager::class.java)
        val alreadyInteractive = powerManager.isInteractive
        if (!alreadyInteractive) {
            wakeScreen(powerManager)
        }
        return CompanionCommandResult.success(
            requestId = context.requestId,
            result = mapOf(
                "awake" to true,
                "alreadyInteractive" to alreadyInteractive,
            ),
        )
    }

    @Suppress("DEPRECATION")
    private fun wakeScreen(powerManager: PowerManager) {
        powerManager.newWakeLock(
            PowerManager.SCREEN_BRIGHT_WAKE_LOCK or
                PowerManager.ACQUIRE_CAUSES_WAKEUP or
                PowerManager.ON_AFTER_RELEASE,
            "ADBControl:UnlockWake",
        ).acquire(WAKE_TIMEOUT_MS)
    }

    private companion object {
        const val WAKE_TIMEOUT_MS = 2_000L
    }
}
