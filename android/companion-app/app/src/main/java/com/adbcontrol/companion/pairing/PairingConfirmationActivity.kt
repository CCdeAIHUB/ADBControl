package com.adbcontrol.companion.pairing

import android.app.Activity
import android.os.Bundle
import android.view.ViewGroup
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView

class PairingConfirmationActivity : Activity() {
    private lateinit var decisionStore: PairingDecisionStore

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        decisionStore = PairingDecisionStore(this)
        renderPairingConfirmation()
    }

    private fun renderPairingConfirmation() {
        val pairingId = intent.getStringExtra(EXTRA_PAIRING_ID)
        val coreName = intent.getStringExtra(EXTRA_CORE_NAME) ?: "ADBControl Core"
        val deviceId = intent.getStringExtra(EXTRA_DEVICE_ID)
        val shortCode = intent.getStringExtra(EXTRA_SHORT_CODE)
        val fingerprint = intent.getStringExtra(EXTRA_CERTIFICATE_FINGERPRINT_SHA256)
        val expiresAt = intent.getLongExtra(EXTRA_EXPIRES_AT_UNIX_MS, 0L)

        val content = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(32, 32, 32, 32)
            layoutParams = LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT,
            )
        }
        setContentView(ScrollView(this).apply { addView(content) })

        content.addView(title("ADBControl pairing request"))

        if (pairingId.isNullOrBlank() || deviceId.isNullOrBlank() || shortCode.isNullOrBlank() || fingerprint.isNullOrBlank()) {
            content.addView(paragraph("Invalid pairing request. Missing pairing id, device id, short code, or certificate fingerprint."))
            content.addView(primaryButton("Close") { finish() })
            return
        }

        content.addView(paragraph("Core: $coreName"))
        content.addView(paragraph("Device: $deviceId"))
        content.addView(paragraph("Pairing ID: $pairingId"))
        content.addView(codeText(shortCode))
        content.addView(paragraph("Verify that this 6-digit code matches the code shown by ADBControl Core before approving."))
        content.addView(paragraph("Certificate SHA-256:"))
        content.addView(fingerprintText(fingerprint))
        if (expiresAt > 0) {
            content.addView(paragraph("Expires at Unix ms: $expiresAt"))
        }
        content.addView(paragraph("Approving this request allows this Core instance to control Android companion capabilities after Android permissions are granted."))

        content.addView(primaryButton("Approve pairing") {
            saveDecision(
                pairingId = pairingId,
                coreName = coreName,
                deviceId = deviceId,
                fingerprint = fingerprint,
                state = PairingDecisionState.APPROVED,
            )
            finish()
        })
        content.addView(primaryButton("Reject pairing") {
            saveDecision(
                pairingId = pairingId,
                coreName = coreName,
                deviceId = deviceId,
                fingerprint = fingerprint,
                state = PairingDecisionState.REJECTED,
            )
            finish()
        })
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

    private fun title(text: String): TextView = TextView(this).apply {
        this.text = text
        textSize = 24f
        setPadding(0, 0, 0, 16)
    }

    private fun paragraph(text: String): TextView = TextView(this).apply {
        this.text = text
        textSize = 14f
        setPadding(0, 6, 0, 6)
    }

    private fun codeText(text: String): TextView = TextView(this).apply {
        this.text = text
        textSize = 32f
        letterSpacing = 0.2f
        setPadding(0, 16, 0, 16)
    }

    private fun fingerprintText(text: String): TextView = TextView(this).apply {
        this.text = text.chunked(8).joinToString(":")
        textSize = 12f
        setPadding(0, 6, 0, 16)
    }

    private fun primaryButton(label: String, onClick: () -> Unit): Button = Button(this).apply {
        text = label
        setOnClickListener { onClick() }
    }

    companion object {
        const val EXTRA_PAIRING_ID = "com.adbcontrol.companion.pairing.EXTRA_PAIRING_ID"
        const val EXTRA_CORE_NAME = "com.adbcontrol.companion.pairing.EXTRA_CORE_NAME"
        const val EXTRA_DEVICE_ID = "com.adbcontrol.companion.pairing.EXTRA_DEVICE_ID"
        const val EXTRA_SHORT_CODE = "com.adbcontrol.companion.pairing.EXTRA_SHORT_CODE"
        const val EXTRA_CERTIFICATE_FINGERPRINT_SHA256 = "com.adbcontrol.companion.pairing.EXTRA_CERTIFICATE_FINGERPRINT_SHA256"
        const val EXTRA_EXPIRES_AT_UNIX_MS = "com.adbcontrol.companion.pairing.EXTRA_EXPIRES_AT_UNIX_MS"
    }
}
