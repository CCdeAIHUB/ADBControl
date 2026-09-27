package com.adbcontrol.remote

import android.app.Application
import com.adbcontrol.remote.core.AppGraph
import com.adbcontrol.remote.data.ProfileStore
import com.adbcontrol.remote.data.log.AppDiagnostics

/** 应用入口：装配组合根并初始化日志（不写任何敏感数据）。 */
class RemoteApplication : Application() {

    lateinit var graph: AppGraph
        private set
    lateinit var profileStore: ProfileStore
        private set

    override fun onCreate() {
        super.onCreate()
        AppDiagnostics.initialize(this)
        val previous = Thread.getDefaultUncaughtExceptionHandler()
        Thread.setDefaultUncaughtExceptionHandler { thread, error ->
            AppDiagnostics.failure(
                "process.crash", "android.runtime", 0, error.javaClass.simpleName,
                "thread=${thread.name}",
            )
            previous?.uncaughtException(thread, error)
        }
        profileStore = ProfileStore(this)
        graph = AppGraph(this)
    }

    override fun onLowMemory() {
        AppDiagnostics.record("warn", "process.lowMemory", "android.runtime", true, 0, "")
        super.onLowMemory()
    }
}
