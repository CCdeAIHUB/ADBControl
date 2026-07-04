package com.adbcontrol.companion.quic

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

class CompanionConnectionReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != ACTION_CONFIGURE_CONNECTION) {
            return
        }

        val serviceIntent = Intent(context, QuicCompanionService::class.java).apply {
            action = QuicCompanionService.ACTION_CONFIGURE_CONNECTION
            putExtras(intent)
        }
        context.startService(serviceIntent)
    }

    companion object {
        const val ACTION_CONFIGURE_CONNECTION = "com.adbcontrol.companion.CONFIGURE_CONNECTION"
    }
}
