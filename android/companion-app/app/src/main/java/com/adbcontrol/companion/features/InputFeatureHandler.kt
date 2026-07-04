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
                message = "不支持的输入操作：${context.operation}",
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
                message = "input.text 需要 args.text 参数。",
                module = "companion.input",
                recoverable = false,
            )

        if (!CompanionInputMethodService.commitText(text)) {
            openInputMethodSettings()
            return CompanionCommandResult.failure(
                requestId = command.requestId,
                errorCode = "COMPANION_IME_NOT_ACTIVE",
                message = "ADBControl 输入法未启用，或当前没有可输入的文本焦点。",
                module = "companion.input",
                recoverable = true,
                suggestion = "请启用并选择 ADBControl 伴侣输入法，然后聚焦文本框后重试。",
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
                message = "input.key 需要 args.keyCode 参数。",
                module = "companion.input",
                recoverable = false,
            )

        if (!CompanionInputMethodService.sendKey(keyCode)) {
            openInputMethodSettings()
            return CompanionCommandResult.failure(
                requestId = command.requestId,
                errorCode = "COMPANION_IME_NOT_ACTIVE",
                message = "ADBControl 输入法未启用，或当前没有可输入的文本焦点。",
                module = "companion.input",
                recoverable = true,
                suggestion = "请启用并选择 ADBControl 伴侣输入法，然后聚焦文本框后重试。",
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
