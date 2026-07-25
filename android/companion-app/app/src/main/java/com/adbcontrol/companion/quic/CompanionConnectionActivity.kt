package com.adbcontrol.companion.quic

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.util.Log

class CompanionConnectionActivity : Activity() {
    private val mainHandler = Handler(Looper.getMainLooper())
    private var serviceStartRequested = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
    }

    override fun onResume() {
        super.onResume()
        if (serviceStartRequested) return
        serviceStartRequested = true

        try {
            startForegroundService(Intent(this, QuicCompanionService::class.java).apply {
                action = QuicCompanionService.ACTION_CONFIGURE_CONNECTION
                putExtras(intent)
            })
            Log.i(TAG, "QUIC foreground service start requested from resumed activity")
            mainHandler.postDelayed(::finish, FINISH_DELAY_MS)
        } catch (error: Exception) {
            Log.e(TAG, "Unable to start QUIC foreground service", error)
            finish()
        }
    }

    override fun onDestroy() {
        mainHandler.removeCallbacksAndMessages(null)
        super.onDestroy()
    }

    private companion object {
        const val TAG = "ADBControlQuic"
        const val FINISH_DELAY_MS = 600L
    }
}
