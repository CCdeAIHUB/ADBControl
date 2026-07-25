package com.adbcontrol.companion.quic

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.os.Build
import android.app.ForegroundServiceStartNotAllowedException
import android.util.Log

class CompanionConnectionReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != ACTION_CONFIGURE_CONNECTION) {
            return
        }

        val serviceIntent = Intent(context, QuicCompanionService::class.java).apply {
            action = QuicCompanionService.ACTION_CONFIGURE_CONNECTION
            putExtras(intent)
        }
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                context.startForegroundService(serviceIntent)
            } else {
                context.startService(serviceIntent)
            }
        } catch (error: ForegroundServiceStartNotAllowedException) {
            Log.w("ADBControlQuic", "Android blocked background FGS start; use CompanionConnectionActivity", error)
        }
    }

    companion object {
        const val ACTION_CONFIGURE_CONNECTION = "com.adbcontrol.companion.CONFIGURE_CONNECTION"
    }
}
