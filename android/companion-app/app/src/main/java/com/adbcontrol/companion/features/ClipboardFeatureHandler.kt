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
                message = "Unsupported clipboard operation: ${context.operation}",
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
                message = "Clipboard is empty or Android denied clipboard read for the current app state.",
                module = "companion.clipboard",
                recoverable = true,
                suggestion = "Bring the companion app to foreground or configure it as an allowed input method before reading clipboard.",
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
                message = "clipboard.write requires args.text.",
                module = "companion.clipboard",
                recoverable = false,
            )
        val label = command.args.stringArg("label") ?: "ADBControl Companion"

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
