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
        "accessibility.touch.tap",
        "accessibility.touch.swipe",
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
            "accessibility.touch.tap" -> performTap(context)
            "accessibility.touch.swipe" -> performSwipe(context)
            else -> CompanionCommandResult.failure(
                requestId = context.requestId,
                errorCode = "COMPANION_OPERATION_NOT_SUPPORTED",
                message = "不支持的无障碍操作：${context.operation}",
                module = "companion.accessibility",
                recoverable = false,
            )
        }
    }

    private fun performTap(command: CompanionCommandContext): CompanionCommandResult {
        notReadyFailure(command, "触摸点击")?.let { return it }
        val x = intArg(command, "x")
        val y = intArg(command, "y")
        if (x == null || y == null) {
            return invalidArgument(command, "accessibility.touch.tap 需要整数参数 x 和 y。")
        }
        val bounds = screenBounds()
        val coordinateBounds = coordinateBounds(command, bounds)
            ?: return invalidArgument(command, "coordinateWidth 和 coordinateHeight 必须同时提供且大于 0。")
        val point = mapPoint(x, y, coordinateBounds, bounds)
        if (point == null) {
            return invalidArgument(command, "点击坐标 ($x, $y) 超出来源坐标空间 ${coordinateBounds.width}x${coordinateBounds.height}。")
        }

        val dispatched = CompanionAccessibilityService.dispatchTap(point.x, point.y)
        return if (dispatched) {
            CompanionCommandResult.success(
                requestId = command.requestId,
                result = mapOf(
                    "dispatched" to true,
                    "action" to "触摸点击",
                    "x" to point.x,
                    "y" to point.y,
                    "coordinateWidth" to coordinateBounds.width,
                    "coordinateHeight" to coordinateBounds.height,
                    "screenWidth" to bounds.width,
                    "screenHeight" to bounds.height,
                ),
            )
        } else {
            actionFailed(command, "触摸点击")
        }
    }

    private fun performSwipe(command: CompanionCommandContext): CompanionCommandResult {
        notReadyFailure(command, "触摸滑动")?.let { return it }
        val startX = intArg(command, "startX")
        val startY = intArg(command, "startY")
        val endX = intArg(command, "endX")
        val endY = intArg(command, "endY")
        val durationMs = intArg(command, "durationMs") ?: 250
        if (startX == null || startY == null || endX == null || endY == null) {
            return invalidArgument(command, "accessibility.touch.swipe 需要整数参数 startX、startY、endX 和 endY。")
        }
        val bounds = screenBounds()
        val coordinateBounds = coordinateBounds(command, bounds)
            ?: return invalidArgument(command, "coordinateWidth 和 coordinateHeight 必须同时提供且大于 0。")
        val start = mapPoint(startX, startY, coordinateBounds, bounds)
        val end = mapPoint(endX, endY, coordinateBounds, bounds)
        if (start == null || end == null) {
            return invalidArgument(command, "滑动坐标超出来源坐标空间 ${coordinateBounds.width}x${coordinateBounds.height}。")
        }

        val dispatched = CompanionAccessibilityService.dispatchSwipe(start.x, start.y, end.x, end.y, durationMs)
        return if (dispatched) {
            CompanionCommandResult.success(
                requestId = command.requestId,
                result = mapOf(
                    "dispatched" to true,
                    "action" to "触摸滑动",
                    "startX" to start.x,
                    "startY" to start.y,
                    "endX" to end.x,
                    "endY" to end.y,
                    "durationMs" to durationMs.coerceIn(1, 3000),
                    "coordinateWidth" to coordinateBounds.width,
                    "coordinateHeight" to coordinateBounds.height,
                    "screenWidth" to bounds.width,
                    "screenHeight" to bounds.height,
                ),
            )
        } else {
            actionFailed(command, "触摸滑动")
        }
    }

    private fun perform(command: CompanionCommandContext, action: Int, label: String): CompanionCommandResult {
        notReadyFailure(command, label)?.let { return it }

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

    private fun notReadyFailure(command: CompanionCommandContext, label: String): CompanionCommandResult? {
        if (CompanionAccessibilityService.isReady()) {
            return null
        }

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

    private fun actionFailed(command: CompanionCommandContext, label: String): CompanionCommandResult {
        return CompanionCommandResult.failure(
            requestId = command.requestId,
            errorCode = "COMPANION_ACCESSIBILITY_ACTION_FAILED",
            message = "Android 无障碍服务未能执行 $label。",
            module = "companion.accessibility",
            recoverable = true,
            suggestion = "请确认设备未锁屏、系统允许该无障碍动作后重试。",
        )
    }

    private fun invalidArgument(command: CompanionCommandContext, message: String): CompanionCommandResult {
        return CompanionCommandResult.failure(
            requestId = command.requestId,
            errorCode = "COMPANION_INVALID_ARGUMENT",
            message = message,
            module = "companion.accessibility",
            recoverable = false,
        )
    }

    private fun intArg(command: CompanionCommandContext, name: String): Int? {
        return when (val value = command.args[name]) {
            is Int -> value
            is Long -> value.toInt()
            is Double -> value.toInt()
            is Float -> value.toInt()
            is Number -> value.toInt()
            is String -> value.toIntOrNull()
            else -> null
        }
    }

    private fun screenBounds(): ScreenBounds {
        val metrics = context.resources.displayMetrics
        return ScreenBounds(metrics.widthPixels, metrics.heightPixels)
    }

    private fun isPointInsideScreen(x: Int, y: Int, bounds: ScreenBounds): Boolean {
        return x >= 0 && y >= 0 && x < bounds.width && y < bounds.height
    }

    private fun coordinateBounds(command: CompanionCommandContext, screenBounds: ScreenBounds): ScreenBounds? {
        val hasWidth = command.args.containsKey("coordinateWidth")
        val hasHeight = command.args.containsKey("coordinateHeight")
        // Omitting both fields preserves v1 callers that already send physical screen coordinates.
        if (!hasWidth && !hasHeight) return screenBounds
        if (hasWidth != hasHeight) return null

        val width = intArg(command, "coordinateWidth") ?: return null
        val height = intArg(command, "coordinateHeight") ?: return null
        return if (width > 0 && height > 0) ScreenBounds(width, height) else null
    }

    private fun mapPoint(x: Int, y: Int, source: ScreenBounds, target: ScreenBounds): ScreenPoint? {
        if (!isPointInsideScreen(x, y, source)) return null
        return ScreenPoint(
            scaleAxis(x, source.width, target.width),
            scaleAxis(y, source.height, target.height),
        )
    }

    private fun scaleAxis(value: Int, sourceExtent: Int, targetExtent: Int): Int {
        if (sourceExtent == 1 || targetExtent == 1) return 0
        return ((value.toLong() * (targetExtent - 1)) / (sourceExtent - 1))
            .toInt()
            .coerceIn(0, targetExtent - 1)
    }

    private fun openAccessibilitySettings() {
        context.startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS).apply {
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        })
    }

    private data class ScreenBounds(val width: Int, val height: Int)
    private data class ScreenPoint(val x: Int, val y: Int)
}
