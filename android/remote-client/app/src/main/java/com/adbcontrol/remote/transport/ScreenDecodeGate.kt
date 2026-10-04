package com.adbcontrol.remote.transport

/**
 * 在 MediaCodec 完成配置前暂存视频帧。
 * scrcpy 的 SPS/PPS 与首个 IDR 往往紧邻到达；若首 IDR 被丢弃，画面会一直黑到下一个关键帧。
 */
class ScreenDecodeGate(private val maxPendingFrames: Int = 30) {
    data class Frame(val data: ByteArray, val presentationTimeUs: Long, val keyFrame: Boolean)

    private val pending = ArrayDeque<Frame>()
    private var configured = false

    @Synchronized
    fun offer(data: ByteArray, presentationTimeUs: Long, keyFrame: Boolean): List<Frame> {
        val frame = Frame(data, presentationTimeUs, keyFrame)
        if (configured) return listOf(frame)
        if (pending.size >= maxPendingFrames) pending.removeFirst()
        pending.addLast(frame)
        return emptyList()
    }

    @Synchronized
    fun markConfigured(): List<Frame> {
        configured = true
        val frames = pending.toList()
        pending.clear()
        return frames
    }

    @Synchronized
    fun reset() {
        configured = false
        pending.clear()
    }
}
