package com.adbcontrol.remote.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.os.IBinder
import com.adbcontrol.remote.core.AppGraph
import com.adbcontrol.remote.data.log.AppDiagnostics

/**
 * 自动化前台调度服务：持有调度器生命周期，让任务在离开界面后继续按计划执行。
 * 使用 specialUse 前台服务类型（Android 14+ 要求显式声明用途属性，见 AndroidManifest）。
 * 系统在极端内存压力下仍可能回收进程；页面与设置页均有明示，不承诺绝对保活。
 */
class AutomationForegroundService : Service() {

    private var graph: AppGraph? = null

    override fun onCreate() {
        super.onCreate()
        graph = (application as com.adbcontrol.remote.RemoteApplication).graph
        startForeground(NOTIFICATION_ID, buildNotification("自动化调度运行中"))
        graph?.scheduler?.start()
        running = true
        AppDiagnostics.record("info", "automation.service", "automation", true, 0, "", "scheduler started")
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            stopSelf()
            return START_NOT_STICKY
        }
        graph?.scheduler?.start()
        return START_STICKY
    }

    override fun onDestroy() {
        graph?.scheduler?.stop()
        running = false
        AppDiagnostics.record("info", "automation.service", "automation", true, 0, "", "scheduler stopped")
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    private fun buildNotification(text: String): Notification {
        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        val channel = NotificationChannel(CHANNEL_ID, "自动化调度", NotificationManager.IMPORTANCE_LOW)
        manager.createNotificationChannel(channel)
        val builder = if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.O) {
            android.app.Notification.Builder(this, CHANNEL_ID)
        } else {
            @Suppress("DEPRECATION")
            android.app.Notification.Builder(this)
        }
        return builder
            .setContentTitle("ADBControl 远程控制")
            .setContentText(text)
            .setSmallIcon(android.R.drawable.stat_notify_sync)
            .setOngoing(true)
            .build()
    }

    companion object {
        private const val CHANNEL_ID = "automation_scheduler"
        private const val NOTIFICATION_ID = 15040
        const val ACTION_STOP = "com.adbcontrol.remote.automation.STOP"

        @Volatile
        var running: Boolean = false
            private set
    }
}
