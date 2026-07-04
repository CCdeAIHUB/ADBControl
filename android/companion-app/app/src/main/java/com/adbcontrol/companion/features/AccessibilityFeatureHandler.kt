package com.adbcontrol.companion.features

import android.accessibilityservice.AccessibilityService
import android.content.Context
import android.content.Intent
import android.provider.Settings
import com.adbcontrol.companion.accessibility.CompanionAccessibilityService
import com.adbcontrol.companion.core.CompanionCommandContext
import com.adbcontrol.companion.core.CompanionCommandResult

class AccessibilityFeatureHandler(private val context: Context) : FeatureCommandHandler {
    override val capabilityIds: Set<String> = setOf("android.accessibility.control")
    override val operations: Set<String> = setOf(
        "accessibility.status",
        "accessibility.global.back",
        "accessibility.global.home",
        "accessibility.global.recents",
        "accessibility.global.notifications",
        "accessibility.global.quickSettings",
        "accessibility.global.powerDialog",
    )

    override fun handle(context: CompanionCommandContext): CompanionCommandResult {
        return when (context.operation) {
            "accessibility.status" -> CompanionCommandResult.success(
                requestId = context.requestId,
                result = mapOf("enabled" to CompanionAccessibilityService.isReady()),
            )
            "accessibility.global.back" -> perform(context, AccessibilityService.GLOBAL_ACTION_BACK, "返回")
            "accessibility.global.home" -> perform(context, AccessibilityService.GLOBAL_ACTION_HOME, "主页")
            "accessibility.global.recents" -> perform(context, AccessibilityService.GLOBAL_ACTION_RECENTS, "多任务")
            "accessibility.global.notifications" -> perform(context, AccessibilityService.GLOBAL_ACTION_NOTIFICATIONS, "通知栏")
            "accessibility.global.quickSettings" -> perform(context, AccessibilityService.GLOBAL_ACTION_QUICK_SETTINGS, "快捷设置")
            "accessibility.global.powerDialog" -> perform(context, AccessibilityService.GLOBAL_ACTION_POWER_DIALOG, "电源菜单")
            else -> CompanionCommandResult.failure(
                requestId = context.requestId,
                errorCode = "COMPANION_OPERATION_NOT_SUPPORTED",
                message = "不支持的无障碍操作：${context.operation}",
                module = "companion.accessibility",
                recoverable = false,
            )
        }
    }

    private fun perform(command: CompanionCommandContext, action: Int, label: String): CompanionCommandResult {
        if (!CompanionAccessibilityService.isReady()) {
            openAccessibilitySettings()
            return CompanionCommandResult.failure(
                requestId = command.requestId,
                errorCode = "COMPANION_ACCESSIBILITY_NOT_ENABLED",
                message = "ADBControl 无障碍辅助尚未启用，无法执行 $label。",
                module = "companion.accessibility",
                recoverable = true,
                suggestion = "请在 Android 无障碍设置中启用 ADBControl 伴侣 App。",
            )
        }

        val performed = CompanionAccessibilityService.performGlobalAction(action)
        return if (performed) {
            CompanionCommandResult.success(
                requestId = command.requestId,
                result = mapOf("performed" to true, "action" to label),
            )
        } else {
            CompanionCommandResult.failure(
                requestId = command.requestId,
                errorCode = "COMPANION_ACCESSIBILITY_ACTION_FAILED",
                message = "Android 无障碍服务未能执行 $label。",
                module = "companion.accessibility",
                recoverable = true,
                suggestion = "请确认设备未锁屏、系统允许该无障碍全局动作后重试。",
            )
        }
    }

    private fun openAccessibilitySettings() {
        context.startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS).apply {
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        })
    }
}
