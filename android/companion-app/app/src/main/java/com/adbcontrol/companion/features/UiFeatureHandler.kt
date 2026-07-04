package com.adbcontrol.companion.features

import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.graphics.PixelFormat
import android.os.Build
import android.view.Gravity
import android.view.WindowManager
import android.widget.TextView
import com.adbcontrol.companion.MainActivity
import com.adbcontrol.companion.core.CompanionCommandContext
import com.adbcontrol.companion.core.CompanionCommandResult

class UiFeatureHandler(private val context: Context) : FeatureCommandHandler {
    override val capabilityIds: Set<String> = setOf(
        "android.ui.background_surface",
        "android.ui.overlay",
        "android.intent.chain_launch",
    )
    override val operations: Set<String> = setOf(
        "ui.surface.show",
        "overlay.show",
        "overlay.hide",
        "intent.chainLaunch",
    )

    private val windowManager: WindowManager = context.getSystemService(WindowManager::class.java)
    private var overlayView: TextView? = null

    override fun handle(context: CompanionCommandContext): CompanionCommandResult {
        return when (context.operation) {
            "ui.surface.show" -> showSurface(context)
            "overlay.show" -> showOverlay(context)
            "overlay.hide" -> hideOverlay(context)
            "intent.chainLaunch" -> chainLaunch(context)
            else -> CompanionCommandResult.failure(
                requestId = context.requestId,
                errorCode = "COMPANION_OPERATION_NOT_SUPPORTED",
                message = "不支持的界面操作：${context.operation}",
                module = "companion.ui",
                recoverable = false,
            )
        }
    }

    private fun showSurface(command: CompanionCommandContext): CompanionCommandResult {
        val intent = Intent(context, MainActivity::class.java).apply {
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            putExtra("adbcontrol_reason", command.args.stringArg("reason") ?: "桌面端请求打开伴侣 App 界面。")
        }
        context.startActivity(intent)
        return CompanionCommandResult.success(
            requestId = command.requestId,
            result = mapOf("started" to true),
        )
    }

    private fun showOverlay(command: CompanionCommandContext): CompanionCommandResult {
        if (overlayView != null) {
            return CompanionCommandResult.success(
                requestId = command.requestId,
                result = mapOf("visible" to true, "reused" to true),
            )
        }

        val text = command.args.stringArg("text") ?: "ADBControl 伴侣"
        val view = TextView(context).apply {
            this.text = text
            textSize = 16f
            setPadding(24, 16, 24, 16)
        }
        val overlayType = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY
        } else {
            @Suppress("DEPRECATION")
            WindowManager.LayoutParams.TYPE_PHONE
        }
        val params = WindowManager.LayoutParams(
            WindowManager.LayoutParams.WRAP_CONTENT,
            WindowManager.LayoutParams.WRAP_CONTENT,
            overlayType,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL,
            PixelFormat.TRANSLUCENT,
        ).apply {
            gravity = Gravity.TOP or Gravity.CENTER_HORIZONTAL
            y = command.args.intArg("y") ?: 80
        }

        windowManager.addView(view, params)
        overlayView = view
        return CompanionCommandResult.success(
            requestId = command.requestId,
            result = mapOf("visible" to true, "text" to text),
        )
    }

    private fun hideOverlay(command: CompanionCommandContext): CompanionCommandResult {
        overlayView?.let { view ->
            windowManager.removeView(view)
            overlayView = null
        }
        return CompanionCommandResult.success(
            requestId = command.requestId,
            result = mapOf("visible" to false),
        )
    }

    private fun chainLaunch(command: CompanionCommandContext): CompanionCommandResult {
        val intents = command.args.mapListArg("intents")
        if (intents.isEmpty()) {
            return CompanionCommandResult.failure(
                requestId = command.requestId,
                errorCode = "COMPANION_PARAMS_INVALID",
                message = "intent.chainLaunch 需要 args.intents[] 参数。",
                module = "companion.intent",
                recoverable = false,
            )
        }

        val launched = mutableListOf<Map<String, Any?>>()
        intents.forEachIndexed { index, spec ->
            val packageName = spec.stringArg("packageName")
            val className = spec.stringArg("className")
            if (packageName == null || className == null) {
                return CompanionCommandResult.failure(
                    requestId = command.requestId,
                    errorCode = "COMPANION_INTENT_NOT_EXPLICIT",
                    message = "intent.chainLaunch 仅接受显式 packageName + className，错误位置：$index。",
                    module = "companion.intent",
                    recoverable = false,
                )
            }

            val intent = Intent().apply {
                component = ComponentName(packageName, className)
                action = spec.stringArg("action") ?: Intent.ACTION_MAIN
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            }
            context.startActivity(intent)
            launched += mapOf(
                "packageName" to packageName,
                "className" to className,
            )
        }

        return CompanionCommandResult.success(
            requestId = command.requestId,
            result = mapOf(
                "launched" to launched,
                "count" to launched.size,
            ),
        )
    }
}
