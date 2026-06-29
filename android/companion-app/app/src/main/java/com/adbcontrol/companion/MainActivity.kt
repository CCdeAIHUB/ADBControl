package com.adbcontrol.companion

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.provider.Settings
import android.view.View
import android.view.ViewGroup
import android.view.inputmethod.InputMethodManager
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import com.adbcontrol.companion.core.AndroidCapabilityCatalog
import com.adbcontrol.companion.core.CompanionCapability
import com.adbcontrol.companion.core.PermissionGuard
import com.adbcontrol.companion.screen.ScreenCaptureConsentActivity
import java.util.UUID

class MainActivity : Activity() {
    private lateinit var permissionGuard: PermissionGuard
    private lateinit var content: LinearLayout

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        permissionGuard = PermissionGuard(this)
        renderPermissionGuide()
    }

    override fun onResume() {
        super.onResume()
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
        content = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(32, 32, 32, 32)
        }
        setContentView(ScrollView(this).apply { addView(content) })
        renderPermissionRows()
    }

    private fun renderPermissionRows() {
        content.removeAllViews()
        val capabilities = AndroidCapabilityCatalog.defaultCapabilities()
        val grantedCount = capabilities.count { permissionGuard.evaluate(it).granted }

        content.addView(title("ADBControl Companion"))
        content.addView(
            paragraph(
                "This app only guides Android permissions for the companion service. " +
                    "Remote control UI is intentionally not implemented here.",
            ),
        )
        content.addView(paragraph("Permission readiness: $grantedCount / ${capabilities.size}"))
        content.addView(primaryButton("Refresh permission status") { renderPermissionRows() })

        capabilities.forEach { capability ->
            content.addView(capabilityRow(capability))
        }
    }

    private fun capabilityRow(capability: CompanionCapability): View {
        val evaluation = permissionGuard.evaluate(capability)
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(0, 24, 0, 24)
            layoutParams = LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT,
            )
        }
        row.addView(sectionTitle(if (evaluation.granted) "✅ ${capability.title}" else "⚠️ ${capability.title}"))
        row.addView(paragraph("Capability: ${capability.id}"))
        row.addView(paragraph("Sensitivity: ${capability.sensitivity}"))
        row.addView(paragraph("Operations: ${capability.operations.joinToString()}"))

        if (evaluation.granted) {
            row.addView(paragraph("Status: ready"))
            return row
        }

        if (evaluation.missingPermissions.isNotEmpty()) {
            row.addView(paragraph("Missing runtime permissions: ${evaluation.missingPermissions.joinToString()}"))
            row.addView(primaryButton("Grant runtime permissions") {
                requestPermissions(evaluation.missingPermissions.toTypedArray(), REQUEST_RUNTIME_PERMISSIONS)
            })
        }

        evaluation.missingSpecialGrants.forEach { grant ->
            row.addView(paragraph("Special grant required: $grant"))
            row.addView(primaryButton(actionLabelForGrant(grant)) { openGrantFlow(grant) })
        }

        return row
    }

    private fun openGrantFlow(grant: String) {
        when (grant) {
            "draw-over-apps" -> startActivity(
                Intent(
                    Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
                    Uri.parse("package:$packageName"),
                ),
            )
            "input-method-service" -> startActivity(Intent(Settings.ACTION_INPUT_METHOD_SETTINGS))
            "media-projection-consent" -> startActivity(
                Intent(this, ScreenCaptureConsentActivity::class.java).apply {
                    putExtra(ScreenCaptureConsentActivity.EXTRA_STREAM_ID, "guide-${UUID.randomUUID()}")
                },
            )
            "foreground-required" -> startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS).apply {
                data = Uri.parse("package:$packageName")
            })
            "background-launch-policy" -> startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS).apply {
                data = Uri.parse("package:$packageName")
            })
            "package-visibility-query",
            "scoped-storage-or-document-picker",
            "sensitive-clip-flag",
            "explicit-intent-only" -> renderPermissionRows()
            else -> startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS).apply {
                data = Uri.parse("package:$packageName")
            })
        }
    }

    private fun actionLabelForGrant(grant: String): String {
        return when (grant) {
            "draw-over-apps" -> "Open overlay permission settings"
            "input-method-service" -> "Open keyboard settings"
            "media-projection-consent" -> "Start screen capture consent"
            "foreground-required" -> "Open app settings"
            "background-launch-policy" -> "Open app settings"
            "package-visibility-query" -> "Declared in manifest"
            "scoped-storage-or-document-picker" -> "Handled by sandbox/document picker"
            "sensitive-clip-flag" -> "Handled during clipboard write"
            "explicit-intent-only" -> "Handled by explicit intent policy"
            else -> "Open app settings"
        }
    }

    private fun title(text: String): TextView = TextView(this).apply {
        this.text = text
        textSize = 24f
        setPadding(0, 0, 0, 16)
    }

    private fun sectionTitle(text: String): TextView = TextView(this).apply {
        this.text = text
        textSize = 18f
    }

    private fun paragraph(text: String): TextView = TextView(this).apply {
        this.text = text
        textSize = 14f
        setPadding(0, 6, 0, 6)
    }

    private fun primaryButton(label: String, onClick: () -> Unit): Button = Button(this).apply {
        text = label
        setOnClickListener { onClick() }
    }

    companion object {
        private const val REQUEST_RUNTIME_PERMISSIONS = 1001
    }
}
