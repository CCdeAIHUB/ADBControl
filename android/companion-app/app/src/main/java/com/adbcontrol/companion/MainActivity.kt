package com.adbcontrol.companion

import android.app.Activity
import android.content.Intent
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.drawable.GradientDrawable
import android.net.Uri
import android.os.Bundle
import android.provider.Settings
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.Button
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import com.adbcontrol.companion.core.AndroidCapabilityCatalog
import com.adbcontrol.companion.core.CapabilitySensitivity
import com.adbcontrol.companion.core.CompanionCapability
import com.adbcontrol.companion.core.PermissionGuard
import com.adbcontrol.companion.quic.QuicCompanionService

class MainActivity : Activity() {
    private lateinit var permissionGuard: PermissionGuard
    private lateinit var content: LinearLayout
    private var connectionStartRequested = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        permissionGuard = PermissionGuard(this)
        window.statusBarColor = COLOR_APP
        window.navigationBarColor = COLOR_APP
        renderPermissionGuide()
    }

    override fun onResume() {
        super.onResume()
        if (!connectionStartRequested && QuicCompanionService.hasSavedConnection(this)) {
            connectionStartRequested = true
            startForegroundService(Intent(this, QuicCompanionService::class.java))
        }
        if (::content.isInitialized) {
            renderPermissionRows()
        }
    }

    override fun onRequestPermissionsResult(
        requestCode: Int,
        permissions: Array<out String>,
        grantResults: IntArray,
    ) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        renderPermissionRows()
    }

    private fun renderPermissionGuide() {
        val root = FrameLayout(this)
        root.setBackgroundColor(COLOR_APP)
        root.addView(GridBackgroundView(this))

        content = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(16), dp(16), dp(16), dp(24))
        }

        root.addView(ScrollView(this).apply {
            clipToPadding = false
            addView(content)
        })
        setContentView(root)
        renderPermissionRows()
    }

    private fun renderPermissionRows() {
        content.removeAllViews()
        val capabilities = AndroidCapabilityCatalog.defaultCapabilities()
        val grantedCount = capabilities.count { permissionGuard.evaluate(it).granted }

        content.addView(heroCard(grantedCount, capabilities.size))
        content.addView(sectionHeader("权限能力", "逐项确认伴侣 App 可提供给桌面端的 Android 能力。"))
        capabilities.forEach { capability ->
            content.addView(capabilityCard(capability))
        }
    }

    private fun heroCard(grantedCount: Int, totalCount: Int): View {
        val stack = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(18), dp(18), dp(18), dp(18))
        }
        stack.addView(label("ADBControl 伴侣 App", 24f, COLOR_TEXT, true))
        stack.addView(spacer(8))
        stack.addView(label("为桌面端提供输入、文件、媒体与系统能力。所有敏感能力都需要你在手机端确认授权。", 14f, COLOR_SECONDARY, false))
        stack.addView(spacer(16))

        val progress = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        progress.addView(statusPill("已就绪 $grantedCount / $totalCount", grantedCount == totalCount))
        progress.addView(primaryButton("刷新状态") { renderPermissionRows() }.apply {
            val params = LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(38))
            params.leftMargin = dp(10)
            layoutParams = params
        })
        stack.addView(progress)

        return card(stack, topMargin = 0)
    }

    private fun sectionHeader(title: String, subtitle: String): View {
        val stack = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(2), dp(16), dp(2), dp(4))
        }
        stack.addView(label(title, 18f, COLOR_TEXT, true))
        stack.addView(label(subtitle, 13f, COLOR_SECONDARY, false))
        return stack
    }

    private fun capabilityCard(capability: CompanionCapability): View {
        val evaluation = permissionGuard.evaluate(capability)
        val stack = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(16), dp(14), dp(16), dp(14))
        }

        val head = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        head.addView(label(capability.title, 17f, COLOR_TEXT, true, weight = 1f))
        head.addView(statusPill(if (evaluation.granted) "已就绪" else "需授权", evaluation.granted))
        stack.addView(head)
        stack.addView(spacer(8))
        stack.addView(label("能力标识：${capability.id}", 12f, COLOR_MUTED, false))
        stack.addView(label("敏感等级：${sensitivityText(capability.sensitivity)}", 12f, COLOR_MUTED, false))
        stack.addView(label("操作范围：${capability.operations.joinToString("、")}", 12f, COLOR_MUTED, false))

        if (evaluation.granted) {
            stack.addView(spacer(10))
            stack.addView(label("当前状态：可以被桌面端调用。", 13f, COLOR_SECONDARY, false))
            return card(stack)
        }

        if (evaluation.missingPermissions.isNotEmpty()) {
            stack.addView(spacer(10))
            stack.addView(label("缺少运行时权限：${evaluation.missingPermissions.joinToString("、") { permissionName(it) }}", 13f, COLOR_SECONDARY, false))
            stack.addView(primaryButton("授予运行时权限") {
                requestPermissions(evaluation.missingPermissions.toTypedArray(), REQUEST_RUNTIME_PERMISSIONS)
            })
        }

        evaluation.missingSpecialGrants.forEach { grant ->
            stack.addView(spacer(10))
            stack.addView(label("需要特殊授权：${grantName(grant)}", 13f, COLOR_SECONDARY, false))
            stack.addView(secondaryButton(actionLabelForGrant(grant)) { openGrantFlow(grant) })
        }

        return card(stack)
    }

    private fun openGrantFlow(grant: String) {
        when (grant) {
            "draw-over-apps" -> startActivity(
                Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:$packageName")),
            )
            "input-method-service" -> startActivity(Intent(Settings.ACTION_INPUT_METHOD_SETTINGS))
            "accessibility-service" -> startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
            "foreground-required",
            "background-launch-policy" -> startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS).apply {
                data = Uri.parse("package:$packageName")
            })
            "package-visibility-query",
            "scoped-storage-or-document-picker",
            "sensitive-clip-flag",
            "explicit-intent-only" -> renderPermissionRows()
            "media-projection-consent" -> renderPermissionRows()
            else -> startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS).apply {
                data = Uri.parse("package:$packageName")
            })
        }
    }

    private fun actionLabelForGrant(grant: String): String {
        return when (grant) {
            "draw-over-apps" -> "打开悬浮窗授权"
            "input-method-service" -> "打开输入法设置"
            "accessibility-service" -> "打开无障碍设置"
            "foreground-required" -> "打开应用设置"
            "background-launch-policy" -> "打开应用设置"
            "package-visibility-query" -> "重新检查清单声明"
            "scoped-storage-or-document-picker" -> "重新检查文件授权"
            "sensitive-clip-flag" -> "重新检查剪贴板策略"
            "explicit-intent-only" -> "重新检查 Intent 策略"
            "media-projection-consent" -> "投屏时确认授权"
            else -> "打开应用设置"
        }
    }

    private fun card(child: View, topMargin: Int = 12): View {
        return FrameLayout(this).apply {
            background = rounded(COLOR_SURFACE, dp(18), COLOR_BORDER, 1)
            setPadding(0, 0, 0, 0)
            addView(child)
            layoutParams = LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT,
            ).apply {
                setMargins(0, topMargin.dpValue(), 0, 0)
            }
        }
    }

    private fun statusPill(text: String, ready: Boolean): TextView {
        return label(text, 12f, if (ready) COLOR_PRIMARY else COLOR_SECONDARY, true).apply {
            setPadding(dp(10), dp(5), dp(10), dp(5))
            background = rounded(if (ready) COLOR_PRIMARY_SOFT else COLOR_SURFACE_ALT, dp(999), if (ready) COLOR_PRIMARY else COLOR_BORDER, 1)
        }
    }

    private fun primaryButton(label: String, onClick: () -> Unit): Button {
        return actionButton(label, COLOR_PRIMARY, COLOR_ON_PRIMARY, COLOR_PRIMARY)
            .apply { setOnClickListener { onClick() } }
    }

    private fun secondaryButton(label: String, onClick: () -> Unit): Button {
        return actionButton(label, COLOR_SURFACE_ALT, COLOR_TEXT, COLOR_BORDER)
            .apply { setOnClickListener { onClick() } }
    }

    private fun actionButton(label: String, fill: Int, textColor: Int, stroke: Int): Button {
        return Button(this).apply {
            text = label
            textSize = 13f
            setTextColor(textColor)
            isAllCaps = false
            minHeight = 0
            minWidth = 0
            stateListAnimator = null
            background = rounded(fill, dp(12), stroke, 1)
            setPadding(dp(14), dp(8), dp(14), dp(8))
        }
    }

    private fun label(text: String, size: Float, color: Int, bold: Boolean, weight: Float? = null): TextView {
        return TextView(this).apply {
            this.text = text
            textSize = size
            setTextColor(color)
            includeFontPadding = true
            if (bold) typeface = android.graphics.Typeface.DEFAULT_BOLD
            layoutParams = LinearLayout.LayoutParams(
                if (weight == null) ViewGroup.LayoutParams.WRAP_CONTENT else 0,
                ViewGroup.LayoutParams.WRAP_CONTENT,
                weight ?: 0f,
            )
        }
    }

    private fun spacer(height: Int): View {
        return View(this).apply {
            layoutParams = LinearLayout.LayoutParams(1, dp(height))
        }
    }

    private fun rounded(fill: Int, radius: Int, strokeColor: Int, strokeWidth: Int): GradientDrawable {
        return GradientDrawable().apply {
            shape = GradientDrawable.RECTANGLE
            setColor(fill)
            cornerRadius = radius.toFloat()
            setStroke(dp(strokeWidth), strokeColor)
        }
    }

    private fun sensitivityText(sensitivity: CapabilitySensitivity): String {
        return when (sensitivity) {
            CapabilitySensitivity.LOW -> "低"
            CapabilitySensitivity.MEDIUM -> "中"
            CapabilitySensitivity.HIGH -> "高"
            CapabilitySensitivity.CRITICAL -> "关键"
        }
    }

    private fun grantName(grant: String): String {
        return when (grant) {
            "draw-over-apps" -> "悬浮窗显示"
            "input-method-service" -> "启用 ADBControl 输入法"
            "accessibility-service" -> "启用 ADBControl 无障碍辅助"
            "foreground-required" -> "前台运行要求"
            "background-launch-policy" -> "后台启动策略"
            "package-visibility-query" -> "应用可见性声明"
            "scoped-storage-or-document-picker" -> "文件选择或沙盒存储"
            "sensitive-clip-flag" -> "敏感剪贴板策略"
            "explicit-intent-only" -> "显式 Intent 限制"
            "media-projection-consent" -> "每次投屏时由 Android 确认"
            else -> grant
        }
    }

    private fun permissionName(permission: String): String {
        return permission.substringAfterLast('.')
            .replace('_', ' ')
            .lowercase()
    }

    private fun dp(value: Int): Int = (value * resources.displayMetrics.density).toInt()

    private fun Int.dpValue(): Int = dp(this)

    companion object {
        private const val REQUEST_RUNTIME_PERMISSIONS = 1001
        private val COLOR_APP = Color.rgb(10, 17, 31)
        private val COLOR_SURFACE = Color.rgb(26, 38, 56)
        private val COLOR_SURFACE_ALT = Color.rgb(38, 53, 76)
        private val COLOR_BORDER = 0x667C8DA5.toInt()
        private val COLOR_TEXT = Color.rgb(248, 250, 252)
        private val COLOR_SECONDARY = Color.rgb(203, 213, 225)
        private val COLOR_MUTED = Color.rgb(148, 163, 184)
        private val COLOR_PRIMARY = Color.rgb(34, 197, 94)
        private val COLOR_PRIMARY_SOFT = 0x2622C55E.toInt()
        private val COLOR_ON_PRIMARY = Color.WHITE
    }

    private class GridBackgroundView(context: android.content.Context) : View(context) {
        private val paint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            color = 0x2434495F
            strokeWidth = 1f
        }

        override fun onDraw(canvas: Canvas) {
            super.onDraw(canvas)
            val spacing = 20f * resources.displayMetrics.density
            var x = 0f
            while (x <= width) {
                canvas.drawLine(x, 0f, x, height.toFloat(), paint)
                x += spacing
            }
            var y = 0f
            while (y <= height) {
                canvas.drawLine(0f, y, width.toFloat(), y, paint)
                y += spacing
            }
        }
    }
}
