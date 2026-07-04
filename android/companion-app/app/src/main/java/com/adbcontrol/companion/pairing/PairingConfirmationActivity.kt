package com.adbcontrol.companion.pairing

import android.app.Activity
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.os.Bundle
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.Button
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView

class PairingConfirmationActivity : Activity() {
    private lateinit var decisionStore: PairingDecisionStore

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        decisionStore = PairingDecisionStore(this)
        window.statusBarColor = COLOR_APP
        window.navigationBarColor = COLOR_APP
        renderPairingConfirmation()
    }

    private fun renderPairingConfirmation() {
        val pairingId = intent.getStringExtra(EXTRA_PAIRING_ID)
        val coreName = intent.getStringExtra(EXTRA_CORE_NAME) ?: "ADBControl Core"
        val deviceId = intent.getStringExtra(EXTRA_DEVICE_ID)
        val shortCode = intent.getStringExtra(EXTRA_SHORT_CODE)
        val fingerprint = intent.getStringExtra(EXTRA_CERTIFICATE_FINGERPRINT_SHA256)
        val expiresAt = intent.getLongExtra(EXTRA_EXPIRES_AT_UNIX_MS, 0L)

        val root = FrameLayout(this).apply {
            setBackgroundColor(COLOR_APP)
            addView(GridBackgroundView(this@PairingConfirmationActivity))
        }
        val content = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(16), dp(18), dp(16), dp(24))
        }
        root.addView(ScrollView(this).apply {
            clipToPadding = false
            addView(content)
        })
        setContentView(root)

        content.addView(titleCard())

        if (pairingId.isNullOrBlank() || deviceId.isNullOrBlank() || shortCode.isNullOrBlank() || fingerprint.isNullOrBlank()) {
            content.addView(card(LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL
                setPadding(dp(18), dp(16), dp(18), dp(16))
                addView(label("配对请求无效", 18f, COLOR_TEXT, true))
                addView(label("缺少配对 ID、设备 ID、短代码或证书指纹。", 14f, COLOR_SECONDARY, false))
                addView(primaryButton("关闭") { finish() })
            }))
            return
        }

        content.addView(card(LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(18), dp(16), dp(18), dp(16))
            addView(label("确认配对", 20f, COLOR_TEXT, true))
            addView(label("请确认桌面端显示的 6 位代码与下方一致。通过后，该桌面端可在你授予 Android 权限后调用伴侣 App 能力。", 14f, COLOR_SECONDARY, false))
            addView(codeText(shortCode))
            addView(detail("桌面端", coreName))
            addView(detail("设备", deviceId))
            addView(detail("配对 ID", pairingId))
            if (expiresAt > 0) {
                addView(detail("过期时间 Unix 毫秒", expiresAt.toString()))
            }
            addView(label("证书 SHA-256", 12f, COLOR_MUTED, true))
            addView(fingerprintText(fingerprint))
            addView(buttonRow(pairingId, coreName, deviceId, fingerprint))
        }))
    }

    private fun titleCard(): View {
        return card(LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(18), dp(18), dp(18), dp(18))
            addView(label("ADBControl 伴侣 App", 24f, COLOR_TEXT, true))
            addView(label("桌面端连接确认", 14f, COLOR_SECONDARY, false))
        }, topMargin = 0)
    }

    private fun buttonRow(
        pairingId: String,
        coreName: String,
        deviceId: String,
        fingerprint: String,
    ): View {
        return LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(0, dp(16), 0, 0)
            addView(primaryButton("允许配对") {
                saveDecision(pairingId, coreName, deviceId, fingerprint, PairingDecisionState.APPROVED)
                finish()
            })
            addView(secondaryButton("拒绝") {
                saveDecision(pairingId, coreName, deviceId, fingerprint, PairingDecisionState.REJECTED)
                finish()
            }.apply {
                (layoutParams as LinearLayout.LayoutParams).leftMargin = dp(10)
            })
        }
    }

    private fun saveDecision(
        pairingId: String,
        coreName: String,
        deviceId: String,
        fingerprint: String,
        state: PairingDecisionState,
    ) {
        decisionStore.saveDecision(
            PairingDecision(
                pairingId = pairingId,
                coreName = coreName,
                deviceId = deviceId,
                certificateFingerprintSha256 = fingerprint,
                state = state,
                decidedAtUnixMs = System.currentTimeMillis(),
            ),
        )
    }

    private fun detail(name: String, value: String): TextView {
        return label("$name：$value", 13f, COLOR_SECONDARY, false).apply {
            setPadding(0, dp(5), 0, 0)
        }
    }

    private fun label(text: String, size: Float, color: Int, bold: Boolean): TextView {
        return TextView(this).apply {
            this.text = text
            textSize = size
            setTextColor(color)
            includeFontPadding = true
            if (bold) typeface = Typeface.DEFAULT_BOLD
        }
    }

    private fun codeText(text: String): TextView {
        return TextView(this).apply {
            this.text = text
            textSize = 36f
            letterSpacing = 0.14f
            setTextColor(COLOR_PRIMARY)
            typeface = Typeface.DEFAULT_BOLD
            setPadding(0, dp(18), 0, dp(12))
        }
    }

    private fun fingerprintText(text: String): TextView {
        return label(text.chunked(8).joinToString(":"), 12f, COLOR_SECONDARY, false).apply {
            setPadding(0, dp(6), 0, 0)
        }
    }

    private fun primaryButton(label: String, onClick: () -> Unit): Button {
        return actionButton(label, COLOR_PRIMARY, COLOR_ON_PRIMARY, COLOR_PRIMARY).apply {
            setOnClickListener { onClick() }
        }
    }

    private fun secondaryButton(label: String, onClick: () -> Unit): Button {
        return actionButton(label, COLOR_SURFACE_ALT, COLOR_TEXT, COLOR_BORDER).apply {
            setOnClickListener { onClick() }
        }
    }

    private fun actionButton(label: String, fill: Int, textColor: Int, stroke: Int): Button {
        return Button(this).apply {
            text = label
            textSize = 14f
            setTextColor(textColor)
            isAllCaps = false
            minHeight = 0
            minWidth = 0
            stateListAnimator = null
            background = rounded(fill, dp(12), stroke, 1)
            setPadding(dp(16), dp(9), dp(16), dp(9))
            layoutParams = LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                dp(42),
            )
        }
    }

    private fun card(child: View, topMargin: Int = 12): View {
        return FrameLayout(this).apply {
            background = rounded(COLOR_SURFACE, dp(18), COLOR_BORDER, 1)
            addView(child)
            layoutParams = LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT,
            ).apply {
                setMargins(0, dp(topMargin), 0, 0)
            }
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

    private fun dp(value: Int): Int = (value * resources.displayMetrics.density).toInt()

    companion object {
        const val EXTRA_PAIRING_ID = "com.adbcontrol.companion.pairing.EXTRA_PAIRING_ID"
        const val EXTRA_CORE_NAME = "com.adbcontrol.companion.pairing.EXTRA_CORE_NAME"
        const val EXTRA_DEVICE_ID = "com.adbcontrol.companion.pairing.EXTRA_DEVICE_ID"
        const val EXTRA_SHORT_CODE = "com.adbcontrol.companion.pairing.EXTRA_SHORT_CODE"
        const val EXTRA_CERTIFICATE_FINGERPRINT_SHA256 = "com.adbcontrol.companion.pairing.EXTRA_CERTIFICATE_FINGERPRINT_SHA256"
        const val EXTRA_EXPIRES_AT_UNIX_MS = "com.adbcontrol.companion.pairing.EXTRA_EXPIRES_AT_UNIX_MS"

        private val COLOR_APP = Color.rgb(10, 17, 31)
        private val COLOR_SURFACE = Color.rgb(26, 38, 56)
        private val COLOR_SURFACE_ALT = Color.rgb(38, 53, 76)
        private val COLOR_BORDER = 0x667C8DA5.toInt()
        private val COLOR_TEXT = Color.rgb(248, 250, 252)
        private val COLOR_SECONDARY = Color.rgb(203, 213, 225)
        private val COLOR_MUTED = Color.rgb(148, 163, 184)
        private val COLOR_PRIMARY = Color.rgb(34, 197, 94)
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
