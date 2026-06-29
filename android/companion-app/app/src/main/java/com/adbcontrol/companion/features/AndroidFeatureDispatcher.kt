package com.adbcontrol.companion.features

import android.content.Context
import com.adbcontrol.companion.core.AndroidCapabilityCatalog
import com.adbcontrol.companion.core.CompanionCommandContext
import com.adbcontrol.companion.core.CompanionCommandResult
import com.adbcontrol.companion.core.PermissionGuard

class AndroidFeatureDispatcher(
    context: Context,
    private val permissionGuard: PermissionGuard,
) {
    private val capabilities = AndroidCapabilityCatalog.defaultCapabilities().associateBy { it.id }
    private val handlers: List<FeatureCommandHandler> = listOf(
        InputFeatureHandler(context),
        ScreenCaptureFeatureHandler(context),
        CameraFeatureHandler(context),
        AudioRecordFeatureHandler(context),
        ClipboardFeatureHandler(context),
        MediaVolumeFeatureHandler(context),
        AppListFeatureHandler(context),
        FileSandboxFeatureHandler(context),
        TelephonyFeatureHandler(context),
        MotionSensorFeatureHandler(context),
        UiFeatureHandler(context),
    )

    fun dispatch(context: CompanionCommandContext): CompanionCommandResult {
        val capability = capabilities[context.capabilityId]
            ?: return CompanionCommandResult.failure(
                requestId = context.requestId,
                errorCode = "COMPANION_CAPABILITY_NOT_FOUND",
                message = "Capability is not declared by Android companion: ${context.capabilityId}",
                module = "companion.dispatcher",
                recoverable = false,
            )

        if (!capability.operations.contains(context.operation)) {
            return CompanionCommandResult.failure(
                requestId = context.requestId,
                errorCode = "COMPANION_OPERATION_NOT_SUPPORTED",
                message = "Operation ${context.operation} is not supported by ${context.capabilityId}.",
                module = "companion.dispatcher",
                recoverable = false,
            )
        }

        val permissionEvaluation = permissionGuard.requireGranted(capability)
        if (!permissionEvaluation.granted) {
            return CompanionCommandResult.failure(
                requestId = context.requestId,
                errorCode = "COMPANION_PERMISSION_DENIED",
                message = "Android companion is missing required permission or special grant for ${context.capabilityId}.",
                module = "companion.permission",
                recoverable = true,
                suggestion = permissionEvaluation.describeMissingGrants(),
            )
        }

        val handler = handlers.firstOrNull { it.canHandle(context) }
            ?: return CompanionCommandResult.failure(
                requestId = context.requestId,
                errorCode = "COMPANION_HANDLER_NOT_FOUND",
                message = "No Android feature handler is registered for ${context.capabilityId}/${context.operation}.",
                module = "companion.dispatcher",
                recoverable = true,
            )

        return try {
            handler.handle(context)
        } catch (securityException: SecurityException) {
            CompanionCommandResult.failure(
                requestId = context.requestId,
                errorCode = "COMPANION_SECURITY_EXCEPTION",
                message = securityException.message ?: "Android rejected the operation for security reasons.",
                module = "companion.dispatcher",
                recoverable = true,
                suggestion = "Refresh permission state and ask the user to grant the missing Android permission.",
            )
        } catch (exception: RuntimeException) {
            CompanionCommandResult.failure(
                requestId = context.requestId,
                errorCode = "COMPANION_HANDLER_FAILED",
                message = exception.message ?: "Android feature handler failed.",
                module = "companion.dispatcher",
                recoverable = true,
            )
        }
    }

    private fun com.adbcontrol.companion.core.PermissionEvaluation.describeMissingGrants(): String {
        val missing = missingPermissions + missingSpecialGrants
        return if (missing.isEmpty()) {
            "Retry after the companion app refreshes permission state."
        } else {
            "Missing: ${missing.joinToString()}"
        }
    }
}
