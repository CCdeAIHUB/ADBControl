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
        sink.onStreamStarted(sessionId, MIME_TYPE, mapOf("width" to width, "height" to height, "frameRate" to frameRate))
        worker = Thread(::drainLoop, "adbcontrol-video-$sessionId").apply { start() }
        return inputSurface
    }

    fun stop() {
        active = false
        runCatching { codec.signalEndOfInputStream() }
        worker?.join(1_000)
        runCatching { codec.stop() }
        runCatching { codec.release() }
        runCatching { inputSurface.release() }
        sink.onStreamStopped(sessionId)
    }

    private fun drainLoop() {
        while (active) {
            Thread.sleep(16)
        }
    }

    companion object {
        const val MIME_TYPE = "video/avc"
    }
}
