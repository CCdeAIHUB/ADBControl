package com.adbcontrol.remote.transport

import android.media.MediaCodec
import android.media.MediaFormat
import android.view.Surface
import com.adbcontrol.remote.data.log.AppDiagnostics
import java.nio.ByteBuffer

/** Small Annex-B H.264 decoder for the Web screen protocol-v2 packet stream. */
class H264SurfaceDecoder(private val surface: Surface) {
    enum class QueueResult { INPUT_BUSY, QUEUED, RENDERED }
    private var codec: MediaCodec? = null
    private var width = 0
    private var height = 0

    @Synchronized
    fun configure(videoWidth: Int, videoHeight: Int, config: ByteArray) {
        close()
        width = videoWidth
        height = videoHeight
        val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height)
        val units = splitAnnexB(config)
        units.firstOrNull { (it.first().toInt() and 0x1F) == 7 }?.let { format.setByteBuffer("csd-0", ByteBuffer.wrap(withStartCode(it))) }
        units.firstOrNull { (it.first().toInt() and 0x1F) == 8 }?.let { format.setByteBuffer("csd-1", ByteBuffer.wrap(withStartCode(it))) }
        codec = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC).also {
            it.configure(format, surface, null, 0)
            it.start()
        }
        AppDiagnostics.record("info", "screen.decoder.ready", "media.codec", true, 0, "", "size=${width}x$height")
    }

    @Synchronized
    fun queue(data: ByteArray, presentationTimeUs: Long, keyFrame: Boolean): QueueResult {
        val decoder = codec ?: return QueueResult.INPUT_BUSY
        val inputIndex = decoder.dequeueInputBuffer(10_000)
        if (inputIndex < 0) return QueueResult.INPUT_BUSY
        val input = decoder.getInputBuffer(inputIndex) ?: return QueueResult.INPUT_BUSY
        input.apply { clear(); put(data) }
        decoder.queueInputBuffer(inputIndex, 0, data.size, presentationTimeUs, if (keyFrame) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0)
        val info = MediaCodec.BufferInfo()
        var rendered = false
        var attempts = 0
        while (attempts++ < 8) {
            val outputIndex = decoder.dequeueOutputBuffer(info, if (attempts == 1) 10_000 else 0)
            when {
                outputIndex >= 0 -> {
                    // Surface 解码器的 output BufferInfo.size 合法地可能为 0；只要不是
                    // codec-config 缓冲区就必须 release(render=true)，否则画面会永久黑屏。
                    val isFrame = H264OutputPolicy.isRenderableBuffer(info.flags)
                    decoder.releaseOutputBuffer(outputIndex, isFrame)
                    rendered = rendered || isFrame
                }
                outputIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> {
                    AppDiagnostics.record("info", "screen.decoder.format_changed", "media.codec", true, 0, "", decoder.outputFormat.toString())
                }
                outputIndex == MediaCodec.INFO_TRY_AGAIN_LATER -> break
                else -> Unit
            }
        }
        return if (rendered) QueueResult.RENDERED else QueueResult.QUEUED
    }

    @Synchronized
    fun close() {
        codec?.let { runCatching { it.stop() }; runCatching { it.release() } }
        codec = null
    }

    private fun splitAnnexB(bytes: ByteArray): List<ByteArray> {
        val starts = mutableListOf<Pair<Int, Int>>()
        var i = 0
        while (i + 3 < bytes.size) {
            val size = when {
                bytes[i] == 0.toByte() && bytes[i + 1] == 0.toByte() && bytes[i + 2] == 1.toByte() -> 3
                i + 4 <= bytes.size && bytes[i] == 0.toByte() && bytes[i + 1] == 0.toByte() && bytes[i + 2] == 0.toByte() && bytes[i + 3] == 1.toByte() -> 4
                else -> 0
            }
            if (size > 0) { starts += i to size; i += size } else i++
        }
        return starts.mapIndexedNotNull { index, (start, codeSize) ->
            val end = starts.getOrNull(index + 1)?.first ?: bytes.size
            bytes.copyOfRange(start + codeSize, end).takeIf { it.isNotEmpty() }
        }
    }

    private fun withStartCode(unit: ByteArray) = byteArrayOf(0, 0, 0, 1) + unit
}

/** Pure decoder-output policy kept outside MediaCodec so the Surface zero-byte regression is contract-tested. */
object H264OutputPolicy {
    fun isRenderableBuffer(flags: Int): Boolean = flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG == 0
}
