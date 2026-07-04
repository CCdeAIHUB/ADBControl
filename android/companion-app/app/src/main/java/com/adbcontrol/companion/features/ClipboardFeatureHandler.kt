package com.adbcontrol.companion.features

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import com.adbcontrol.companion.core.CompanionCommandContext
import com.adbcontrol.companion.core.CompanionCommandResult

class ClipboardFeatureHandler(private val context: Context) : FeatureCommandHandler {
    override val capabilityIds: Set<String> = setOf("android.clipboard.read", "android.clipboard.write")
    override val operations: Set<String> = setOf("clipboard.read", "clipboard.write")

    override fun handle(context: CompanionCommandContext): CompanionCommandResult {
        val clipboard = this.context.getSystemService(ClipboardManager::class.java)

        return when (context.operation) {
            "clipboard.read" -> readClipboard(context, clipboard)
            "clipboard.write" -> writeClipboard(context, clipboard)
            else -> CompanionCommandResult.failure(
                requestId = context.requestId,
                errorCode = "COMPANION_OPERATION_NOT_SUPPORTED",
                message = "不支持的剪贴板操作：${context.operation}",
                module = "companion.clipboard",
                recoverable = false,
            )
        }
    }

    private fun readClipboard(
        command: CompanionCommandContext,
        clipboard: ClipboardManager,
    ): CompanionCommandResult {
        val clip = clipboard.primaryClip
            ?: return CompanionCommandResult.failure(
                requestId = command.requestId,
                errorCode = "COMPANION_CLIPBOARD_EMPTY_OR_INACCESSIBLE",
                message = "剪贴板为空，或 Android 当前状态不允许读取剪贴板。",
                module = "companion.clipboard",
                recoverable = true,
                suggestion = "请将伴侣 App 切到前台，或将其配置为允许读取剪贴板的输入法后再试。",
            )

        val text = clip.getItemAt(0).coerceToText(context)?.toString().orEmpty()
        return CompanionCommandResult.success(
            requestId = command.requestId,
            result = mapOf(
                "text" to text,
                "itemCount" to clip.itemCount,
            ),
        )
    }

    private fun writeClipboard(
        command: CompanionCommandContext,
        clipboard: ClipboardManager,
    ): CompanionCommandResult {
        val text = command.args.stringArg("text")
            ?: return CompanionCommandResult.failure(
                requestId = command.requestId,
                errorCode = "COMPANION_PARAMS_INVALID",
                message = "clipboard.write 需要 args.text 参数。",
                module = "companion.clipboard",
                recoverable = false,
            )
        val label = command.args.stringArg("label") ?: "ADBControl 伴侣"

        clipboard.setPrimaryClip(ClipData.newPlainText(label, text))
        return CompanionCommandResult.success(
            requestId = command.requestId,
            result = mapOf(
                "written" to true,
                "length" to text.length,
            ),
        )
    }
}
