package com.adbcontrol.companion.core

import android.Manifest
import android.content.ComponentName
import android.content.Context
import android.content.pm.PackageManager
import android.provider.Settings
import android.text.TextUtils
import com.adbcontrol.companion.accessibility.CompanionAccessibilityService

class PermissionGuard(private val context: Context) {
    fun evaluate(capability: CompanionCapability): PermissionEvaluation {
        val missingRuntimePermissions = capability.androidPermissions.filterNot(::hasRuntimePermission)
        val missingSpecialGrants = capability.specialGrants.filterNot(::hasSpecialGrant)

        return PermissionEvaluation(
            capabilityId = capability.id,
            granted = missingRuntimePermissions.isEmpty() && missingSpecialGrants.isEmpty(),
            missingPermissions = missingRuntimePermissions,
            missingSpecialGrants = missingSpecialGrants,
        )
    }

    fun requireGranted(capability: CompanionCapability): PermissionEvaluation {
        val evaluation = evaluate(capability)

        // Sensitive Android abilities must stop here when runtime permission is missing.
        // Some special grants, such as IME activation, are
        // interactive states. Their handlers are allowed to launch Android's grant UI
        // and then return a structured recoverable result if the user has not completed it.
        return evaluation
    }

    private fun hasRuntimePermission(permission: String): Boolean {
        if (permission == Manifest.permission.SYSTEM_ALERT_WINDOW) {
            return true
        }
        return context.checkSelfPermission(permission) == PackageManager.PERMISSION_GRANTED
    }

    private fun hasSpecialGrant(grant: String): Boolean {
        return when (grant) {
            "draw-over-apps" -> Settings.canDrawOverlays(context)
            "foreground-required" -> true
            "sensitive-clip-flag" -> true
            "package-visibility-query" -> true
            "scoped-storage-or-document-picker" -> true
            "input-method-service" -> true
            "accessibility-service" -> isAccessibilityServiceEnabled()
            "background-launch-policy" -> true
            "explicit-intent-only" -> true
            "media-projection-consent" -> true
            else -> false
        }
    }

    private fun isAccessibilityServiceEnabled(): Boolean {
        val expected = ComponentName(context, CompanionAccessibilityService::class.java).flattenToString()
        val enabled = Settings.Secure.getString(
            context.contentResolver,
            Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES,
        ) ?: return false
        val splitter = TextUtils.SimpleStringSplitter(':').apply { setString(enabled) }
        while (splitter.hasNext()) {
            if (splitter.next().equals(expected, ignoreCase = true)) {
                return true
            }
        }
        return false
    }
}
