package com.adbcontrol.remote.data.log

import com.adbcontrol.remote.data.RemoteRepository
import com.adbcontrol.remote.model.RemoteResult
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledExecutorService
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

/** Best-effort bounded upload; failures stay queued and never block user operations. */
class RemoteDiagnosticReporter(private val repository: RemoteRepository) {
    private val running = AtomicBoolean(false)
    private var scheduler: ScheduledExecutorService? = null

    fun start() {
        if (!running.compareAndSet(false, true)) return
        scheduler = Executors.newSingleThreadScheduledExecutor { runnable ->
            Thread(runnable, "remote-diagnostic-uploader").apply { isDaemon = true }
        }.also { executor ->
            executor.scheduleWithFixedDelay(::flush, 5, 10, TimeUnit.SECONDS)
        }
    }

    fun stop() {
        running.set(false)
        scheduler?.shutdownNow()
        scheduler = null
    }

    internal fun flush() {
        if (!repository.hasSession) return
        val batch = AppDiagnostics.pendingBatch()
        if (batch.isEmpty()) return
        when (repository.reportDiagnostics(AppDiagnostics.currentSessionId(), batch)) {
            is RemoteResult.Success -> AppDiagnostics.acknowledgeThrough(batch.last().sequence)
            is RemoteResult.Failure -> Unit
        }
    }
}
