package com.adbcontrol.companion.accessibility

import android.accessibilityservice.AccessibilityService
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
    }
}
