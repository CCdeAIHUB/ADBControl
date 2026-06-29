package com.adbcontrol.companion.core

import android.content.Context
import android.content.pm.PackageManager
import android.provider.Settings

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

        // Sensitive Android abilities must stop here when permission is missing.
        // Later command handlers must consume this result and return a QUIC error
        // envelope instead of executing partial platform operations.
        return evaluation
    }

    private fun hasRuntimePermission(permission: String): Boolean {
        return context.checkSelfPermission(permission) == PackageManager.PERMISSION_GRANTED
    }

    private fun hasSpecialGrant(grant: String): Boolean {
        return when (grant) {
            "draw-over-apps" -> Settings.canDrawOverlays(context)
            "foreground-required" -> true
            "sensitive-clip-flag" -> true
            "package-visibility-query" -> true
            "scoped-storage-or-document-picker" -> true
            "media-projection-consent" -> false
            "input-method-service" -> false
            "background-launch-policy" -> false
            "explicit-intent-only" -> true
            else -> false
        }
    }
}
