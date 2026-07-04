package com.adbcontrol.companion.features

import android.content.Context
import com.adbcontrol.companion.core.AndroidCapabilityCatalog
import com.adbcontrol.companion.core.CompanionCommandContext
import com.adbcontrol.companion.core.CompanionCommandResult
import com.adbcontrol.companion.core.PermissionGuard
import com.adbcontrol.companion.media.MediaStreamSink
import com.adbcontrol.companion.media.SandboxFileMediaStreamSink
import java.io.File

class AndroidFeatureDispatcher(
    context: Context,
    private val permissionGuard: PermissionGuard,
    mediaStreamSink: MediaStreamSink = SandboxFileMediaStreamSink(File(context.filesDir, "media-streams")),
) {
    private val capabilities = AndroidCapabilityCatalog.defaultCapabilities().associateBy { it.id }
    private val handlers: List<FeatureCommandHandler> = listOf(
        InputFeatureHandler(context),
        ScreenCaptureFeatureHandler(context, mediaStreamSink),
        CameraFeatureHandler(context, mediaStreamSink),
        AudioRecordFeatureHandler(context, mediaStreamSink),
        ClipboardFeatureHandler(context),
        MediaVolumeFeatureHandler(context),
        AccessibilityFeatureHandler(context),
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
                message = "伴侣 App 未声明该能力：${context.capabilityId}",
                module = "companion.dispatcher",
                recoverable = false,
            )

        if (!capability.operations.contains(context.operation)) {
            return CompanionCommandResult.failure(
                requestId = context.requestId,
                errorCode = "COMPANION_OPERATION_NOT_SUPPORTED",
                message = "能力 ${context.capabilityId} 不支持操作 ${context.operation}。",
                module = "companion.dispatcher",
                recoverable = false,
            )
        }

        val permissionEvaluation = permissionGuard.requireGranted(capability)
        if (!permissionEvaluation.granted) {
            return CompanionCommandResult.failure(
                requestId = context.requestId,
                errorCode = "COMPANION_PERMISSION_DENIED",
                message = "伴侣 App 缺少 ${context.capabilityId} 所需的 Android 权限或特殊授权。",
                module = "companion.permission",
                recoverable = true,
                suggestion = permissionEvaluation.describeMissingGrants(),
            )
        }

        val handler = handlers.firstOrNull { it.canHandle(context) }
            ?: return CompanionCommandResult.failure(
                requestId = context.requestId,
                errorCode = "COMPANION_HANDLER_NOT_FOUND",
                message = "未注册 ${context.capabilityId}/${context.operation} 对应的 Android 能力处理器。",
                module = "companion.dispatcher",
                recoverable = true,
            )

        return try {
            handler.handle(context)
        } catch (securityException: SecurityException) {
            CompanionCommandResult.failure(
                requestId = context.requestId,
                errorCode = "COMPANION_SECURITY_EXCEPTION",
                message = securityException.message ?: "Android 因安全策略拒绝了该操作。",
                module = "companion.dispatcher",
                recoverable = true,
                suggestion = "请刷新权限状态，并让用户授予缺失的 Android 权限。",
            )
        } catch (exception: RuntimeException) {
            CompanionCommandResult.failure(
                requestId = context.requestId,
                errorCode = "COMPANION_HANDLER_FAILED",
                message = exception.message ?: "Android 能力处理器执行失败。",
                module = "companion.dispatcher",
                recoverable = true,
            )
        }
    }

    private fun com.adbcontrol.companion.core.PermissionEvaluation.describeMissingGrants(): String {
        val missing = missingPermissions + missingSpecialGrants
        return if (missing.isEmpty()) {
            "请等待伴侣 App 刷新权限状态后重试。"
        } else {
            "缺少：${missing.joinToString()}"
        }
    }
}
