package com.adbcontrol.companion.features

import android.content.Context
import android.content.Intent
import android.provider.Settings
import com.adbcontrol.companion.core.CompanionCommandContext
import com.adbcontrol.companion.core.CompanionCommandResult
import com.adbcontrol.companion.input.CompanionInputMethodService

class InputFeatureHandler(private val context: Context) : FeatureCommandHandler {
    override val capabilityIds: Set<String> = setOf("android.input.ime")
    override val operations: Set<String> = setOf("input.text", "input.key")

    override fun handle(context: CompanionCommandContext): CompanionCommandResult {
        return when (context.operation) {
            "input.text" -> inputText(context)
            "input.key" -> inputKey(context)
            else -> CompanionCommandResult.failure(
                requestId = context.requestId,
                errorCode = "COMPANION_OPERATION_NOT_SUPPORTED",
                message = "Unsupported input operation: ${context.operation}",
                module = "companion.input",
                recoverable = false,
            )
        }
    }

    private fun inputText(command: CompanionCommandContext): CompanionCommandResult {
        val text = command.args.stringArg("text")
            ?: return CompanionCommandResult.failure(
                requestId = command.requestId,
                errorCode = "COMPANION_PARAMS_INVALID",
                message = "input.text requires args.text.",
                module = "companion.input",
                recoverable = false,
            )

        if (!CompanionInputMethodService.commitText(text)) {
            openInputMethodSettings()
            return CompanionCommandResult.failure(
                requestId = command.requestId,
                errorCode = "COMPANION_IME_NOT_ACTIVE",
                message = "ADBControl input method is not active or has no current input connection.",
                module = "companion.input",
                recoverable = true,
                suggestion = "Enable and select ADBControl Companion IME, then focus a text field before retrying.",
            )
        }

        return CompanionCommandResult.success(
            requestId = command.requestId,
            result = mapOf(
                "committed" to true,
                "length" to text.length,
            ),
        )
    }

    private fun inputKey(command: CompanionCommandContext): CompanionCommandResult {
        val keyCode = command.args.intArg("keyCode")
            ?: return CompanionCommandResult.failure(
                requestId = command.requestId,
                errorCode = "COMPANION_PARAMS_INVALID",
                message = "input.key requires args.keyCode.",
                module = "companion.input",
                recoverable = false,
            )

        if (!CompanionInputMethodService.sendKey(keyCode)) {
            openInputMethodSettings()
            return CompanionCommandResult.failure(
                requestId = command.requestId,
                errorCode = "COMPANION_IME_NOT_ACTIVE",
                message = "ADBControl input method is not active or has no current input connection.",
                module = "companion.input",
                recoverable = true,
                suggestion = "Enable and select ADBControl Companion IME, then focus a text field before retrying.",
            )
        }

        return CompanionCommandResult.success(
            requestId = command.requestId,
            result = mapOf(
                "sent" to true,
                "keyCode" to keyCode,
            ),
        )
    }

    private fun openInputMethodSettings() {
        val intent = Intent(Settings.ACTION_INPUT_METHOD_SETTINGS).apply {
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        }
        context.startActivity(intent)
    }
}
