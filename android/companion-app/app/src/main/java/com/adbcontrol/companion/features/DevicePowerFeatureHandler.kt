package com.adbcontrol.companion.features

import android.app.KeyguardManager
import android.content.Context
import android.os.PowerManager
import com.adbcontrol.companion.core.CompanionCommandContext
import com.adbcontrol.companion.core.CompanionCommandResult

class DevicePowerFeatureHandler(private val context: Context) : FeatureCommandHandler {
    override val capabilityIds: Set<String> = setOf("android.device.power")
    override val operations: Set<String> = setOf("device.wake", "device.state")

    override fun handle(context: CompanionCommandContext): CompanionCommandResult {
        return when (context.operation) {
            "device.wake" -> wake(context)
            "device.state" -> readState(context)
            else -> CompanionCommandResult.failure(
                context.requestId,
                "COMPANION_OPERATION_NOT_SUPPORTED",
                "不支持的设备电源操作：" + context.operation,
                "companion.device_power",
                false,
            )
        }
    }

    private fun wake(context: CompanionCommandContext): CompanionCommandResult {
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

    private fun readState(context: CompanionCommandContext): CompanionCommandResult {
        val powerManager = this.context.getSystemService(PowerManager::class.java)
        val keyguardManager = this.context.getSystemService(KeyguardManager::class.java)
        val isInteractive = powerManager.isInteractive
        val isKeyguardLocked = keyguardManager.isKeyguardLocked
        val isDeviceLocked = keyguardManager.isDeviceLocked
        val isLocked = !isInteractive || isKeyguardLocked || isDeviceLocked
        return CompanionCommandResult.success(
            requestId = context.requestId,
            result = mapOf(
                "state" to if (isLocked) "locked" else "unlocked",
                "isInteractive" to isInteractive,
                "isKeyguardLocked" to isKeyguardLocked,
                "isDeviceLocked" to isDeviceLocked,
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
