package com.adbcontrol.companion

import android.app.Activity
import android.os.Bundle
import android.widget.TextView
import com.adbcontrol.companion.core.AndroidCapabilityCatalog

class MainActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val capabilityCount = AndroidCapabilityCatalog.defaultCapabilities().size
        val textView = TextView(this).apply {
            text = "ADBControl Companion\nCapabilities declared: $capabilityCount\nPairing UI will be added after Core QUIC transport is connected."
            textSize = 18f
            setPadding(32, 32, 32, 32)
        }

        setContentView(textView)
    }
}
