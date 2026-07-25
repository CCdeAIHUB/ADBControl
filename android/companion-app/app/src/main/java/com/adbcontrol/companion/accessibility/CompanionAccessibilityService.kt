package com.adbcontrol.companion.accessibility

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.GestureDescription
import android.graphics.Path
import android.view.accessibility.AccessibilityEvent

class CompanionAccessibilityService : AccessibilityService() {
    override fun onServiceConnected() {
        activeService = this
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) = Unit

    override fun onInterrupt() = Unit

    override fun onDestroy() {
        if (activeService === this) {
            activeService = null
        }
        super.onDestroy()
    }

    companion object {
        @Volatile
        private var activeService: CompanionAccessibilityService? = null

        fun isReady(): Boolean = activeService != null

        fun performGlobalAction(action: Int): Boolean {
            return activeService?.performGlobalAction(action) == true
        }

        fun dispatchTap(x: Int, y: Int): Boolean {
            val path = Path().apply { moveTo(x.toFloat(), y.toFloat()) }
            return dispatchGesture(path, durationMs = 60)
        }

        fun dispatchSwipe(startX: Int, startY: Int, endX: Int, endY: Int, durationMs: Int): Boolean {
            val path = Path().apply {
                moveTo(startX.toFloat(), startY.toFloat())
                lineTo(endX.toFloat(), endY.toFloat())
            }
            return dispatchGesture(path, durationMs.coerceIn(1, 3000).toLong())
        }

        private fun dispatchGesture(path: Path, durationMs: Long): Boolean {
            val service = activeService ?: return false
            val gesture = GestureDescription.Builder()
                .addStroke(GestureDescription.StrokeDescription(path, 0L, durationMs))
                .build()
            return service.dispatchGesture(gesture, null, null)
        }
    }
}
