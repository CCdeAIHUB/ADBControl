package com.adbcontrol.remote.data.log

import android.content.Context
import org.json.JSONObject
import java.io.File
import java.util.concurrent.atomic.AtomicLong

object DiagnosticUploadPolicy {
    const val MAX_BATCH = 50
    const val MAX_DETAIL = 200
    private val token = Regex("[A-Za-z0-9_.:+\\-/=@]+")
    fun safeToken(value: String): Boolean = value.isNotBlank() && value.length <= MAX_DETAIL && token.matches(value)
    fun clampDetail(value: String): String = value.take(MAX_DETAIL)
}

/**
 * 统一日志（对齐桌面端统一诊断规范的最小子集）：
 * - 结构化事件（phase/errorCode/耗时），只记录元数据，不记录命令参数、密码、token、截图；
 * - 内存环形缓冲 + 会话文件落盘（应用私有目录），供“日志与诊断”页查看与导出。
 */
object AppDiagnostics {

    private const val MAX_MEMORY_EVENTS = 512
    private const val MAX_FILE_EVENTS = 4096
    private const val MAX_TEXT = 200

    class Event(
        val sequence: Long,
        val atEpochMs: Long,
        val level: String, // info / warn / error
        val phase: String, // connect.ready / request.end / automation.run …
        val module: String,
        val ok: Boolean,
        val elapsedMs: Long,
        val errorCode: String,
        val detail: String,
    ) {
        fun display(): String {
            val time = java.text.SimpleDateFormat("MM-dd HH:mm:ss.SSS", java.util.Locale.US)
                .format(java.util.Date(atEpochMs))
            val code = if (errorCode.isBlank()) "" else " code=$errorCode"
            val extra = if (detail.isBlank()) "" else " $detail"
            return "$time [$level] $phase${if (ok) "" else " FAILED"}${if (elapsedMs > 0) " ${elapsedMs}ms" else ""}$code$extra"
        }
    }

    private val memory = ArrayDeque<Event>()
    private val sequence = AtomicLong()
    private val pending = ArrayDeque<Event>()
    private var sessionFile: File? = null
    private var sessionId: String = ""

    fun initialize(context: Context) {
        if (sessionFile != null) return
        sessionId = java.util.UUID.randomUUID().toString().replace("-", "").take(16)
        val dir = File(context.filesDir, "logs").apply { mkdirs() }
        sessionFile = File(dir, "session-$sessionId.jsonl")
        record("info", "session.start", "app", true, 0, "", "sessionId=$sessionId")
    }

    fun currentSessionId(): String = sessionId

    fun snapshot(): List<Event> = synchronized(memory) { memory.toList() }

    fun record(
        level: String,
        phase: String,
        module: String,
        ok: Boolean,
        elapsedMs: Long,
        errorCode: String,
        detail: String = "",
    ) {
        // 隐私边界：只保留白名单形态的 token（与桌面 diagnostics.rs 的 sanitize 规则一致）。
        val sanitized = detail.split(Regex("\\s+")).joinToString(" ") { token ->
            if (token.length <= MAX_TEXT && token.matches(Regex("[A-Za-z0-9_.:+\\-/=@]+"))) token else "unknown"
        }
        val event = Event(sequence.incrementAndGet(), System.currentTimeMillis(), level, phase, module, ok, elapsedMs, errorCode, sanitized.take(MAX_TEXT))
        synchronized(memory) {
            memory.addLast(event)
            while (memory.size > MAX_MEMORY_EVENTS) memory.removeFirst()
            pending.addLast(event)
            while (pending.size > MAX_FILE_EVENTS) pending.removeFirst()
        }
        appendToFile(event)
    }

    fun failure(phase: String, module: String, elapsedMs: Long, errorCode: String, detail: String = "") =
        record("error", phase, module, false, elapsedMs, errorCode, detail)

    fun pendingBatch(limit: Int = DiagnosticUploadPolicy.MAX_BATCH): List<Event> = synchronized(memory) {
        pending.take(limit.coerceIn(1, DiagnosticUploadPolicy.MAX_BATCH))
    }

    fun acknowledgeThrough(sequence: Long) = synchronized(memory) {
        while (pending.firstOrNull()?.sequence?.let { it <= sequence } == true) pending.removeFirst()
    }

    private fun appendToFile(event: Event) {
        val file = sessionFile ?: return
        try {
            synchronized(this) {
                file.appendText(
                    JSONObject()
                        .put("at", event.atEpochMs)
                        .put("level", event.level)
                        .put("phase", event.phase)
                        .put("module", event.module)
                        .put("ok", event.ok)
                        .put("elapsedMs", event.elapsedMs)
                        .put("errorCode", event.errorCode)
                        .put("detail", event.detail)
                        .toString() + "\n",
                )
                // 有界落盘：超过上限时重写（远程客户端写入频率低，简单重写即可保持有界）。
                if (file.length() > MAX_FILE_EVENTS * 160) {
                    val lines = file.readLines().takeLast(MAX_FILE_EVENTS / 2)
                    file.writeText(lines.joinToString("\n") + "\n")
                }
            }
        } catch (_: Exception) {
            // 日志写失败不允许影响业务主流程（与桌面 DiagnosticStore 的“旁路”语义一致）。
        }
    }
}
