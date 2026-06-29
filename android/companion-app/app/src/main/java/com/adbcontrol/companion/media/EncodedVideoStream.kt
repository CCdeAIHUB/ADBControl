package com.adbcontrol.companion.media

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.view.Surface
import java.util.UUID

class EncodedVideoStream(
    private val sessionId: String = UUID.randomUUID().toString(),
    private val width: Int,
    private val height: Int,
    private val bitrate: Int,
    private val frameRate: Int,
    private val sink: MediaStreamSink,
) {
    private val codec: MediaCodec = MediaCodec.createEncoderByType(MIME_TYPE)
    private val bufferInfo = MediaCodec.BufferInfo()
    private lateinit var inputSurface: Surface
    @Volatile
    private var active = false
    private var worker: Thread? = null

    fun sessionId(): String = sessionId

    fun inputSurface(): Surface = inputSurface

    fun start(): Surface {
        val format = MediaFormat.createVideoFormat(MIME_TYPE, width, height).apply {
            setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
            setInteger(MediaFormat.KEY_BIT_RATE, bitrate)
            setInteger(MediaFormat.KEY_FRAME_RATE, frameRate)
            setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1)
        }
        codec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
        inputSurface = codec.createInputSurface()
        codec.start()
        active = true
        sink.onStreamStarted(
            sessionId,
            MIME_TYPE,
            mapOf("width" to width, "height" to height, "frameRate" to frameRate, "bitrate" to bitrate),
        )
        worker = Thread(::drainLoop, "adbcontrol-video-$sessionId").apply { start() }
        return inputSurface
    }

    fun stop() {
        active = false
        runCatching { codec.signalEndOfInputStream() }
        worker?.join(1_500)
        runCatching { codec.stop() }
        runCatching { codec.release() }
        runCatching { inputSurface.release() }
        sink.onStreamStopped(sessionId)
    }

    private fun drainLoop() {
        while (active) {
            drainOnce()
        }
        repeat(12) { drainOnce() }
    }

    private fun drainOnce() {
        when (val outputIndex = codec.dequeueOutputBuffer(bufferInfo, 10_000)) {
            MediaCodec.INFO_TRY_AGAIN_LATER -> Unit
            MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> sendFormatConfig()
            else -> if (outputIndex >= 0) writeOutputBuffer(outputIndex)
        }
    }

    private fun sendFormatConfig() {
        val format = codec.outputFormat
        val config = format.getByteBuffer("csd-0") ?: return
        val bytes = ByteArray(config.remaining())
        config.get(bytes)
        if (bytes.isNotEmpty()) {
            sink.onConfig(sessionId, bytes, mapOf("kind" to "csd-0"))
        }
    }

    private fun writeOutputBuffer(outputIndex: Int) {
        codec.getOutputBuffer(outputIndex)?.let { outputBuffer ->
            outputBuffer.position(bufferInfo.offset)
            outputBuffer.limit(bufferInfo.offset + bufferInfo.size)
            val bytes = ByteArray(bufferInfo.size)
            outputBuffer.get(bytes)
            if (bytes.isNotEmpty()) {
                val metadata = mapOf(
                    "presentationTimeUs" to bufferInfo.presentationTimeUs,
                    "flags" to bufferInfo.flags,
                )
                if (bufferInfo.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0) {
                    sink.onConfig(sessionId, bytes, metadata)
                } else {
                    sink.onChunk(sessionId, bytes, metadata)
                }
            }
        }
        codec.releaseOutputBuffer(outputIndex, false)
    }

    companion object {
        const val MIME_TYPE = "video/avc"
    }
}
