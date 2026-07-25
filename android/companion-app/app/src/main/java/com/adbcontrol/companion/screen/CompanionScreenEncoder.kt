package com.adbcontrol.companion.screen

import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.media.projection.MediaProjection
import android.os.Build
import android.os.Bundle
import android.util.Log
import com.adbcontrol.companion.quic.CompanionQuicRuntime
import com.adbcontrol.companion.quic.QuicVideoStreamMetadata
import java.nio.ByteBuffer
import java.util.concurrent.atomic.AtomicBoolean

class CompanionScreenEncoder(
    private val projection: MediaProjection,
    private val densityDpi: Int,
    private val options: ProjectionOptions,
) {
    private val running = AtomicBoolean(false)
    private var codec: MediaCodec? = null
    private var virtualDisplay: VirtualDisplay? = null
    private var drainThread: Thread? = null
    @Volatile private var awaitingRecoveryKeyframe = false
    private var sentFrameCount = 0
    private var droppedFrameCount = 0

    fun start() {
        check(running.compareAndSet(false, true)) { "屏幕编码器已经启动。" }
        val encoder = MediaCodec.createEncoderByType(MIME_TYPE)
        val format = MediaFormat.createVideoFormat(MIME_TYPE, options.width, options.height).apply {
            setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
            setInteger(MediaFormat.KEY_BIT_RATE, options.bitrate)
            setInteger(MediaFormat.KEY_FRAME_RATE, options.frameRate)
            setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1)
            setInteger(MediaFormat.KEY_BITRATE_MODE, MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_CBR)
            setInteger(MediaFormat.KEY_PREPEND_HEADER_TO_SYNC_FRAMES, 1)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                setInteger(MediaFormat.KEY_LATENCY, 0)
            }
            setInteger(MediaFormat.KEY_PRIORITY, 0)
        }
        encoder.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
        val inputSurface = encoder.createInputSurface()
        encoder.start()
        codec = encoder
        virtualDisplay = projection.createVirtualDisplay(
            "ADBControl-${options.sessionId}",
            options.width,
            options.height,
            densityDpi,
            DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR,
            inputSurface,
            null,
            null,
        )

        CompanionQuicRuntime.startVideoStream(
            QuicVideoStreamMetadata(
                sessionId = options.sessionId,
                codec = "h264",
                width = options.width,
                height = options.height,
                bitrate = options.bitrate,
                frameRate = options.frameRate,
            ),
        )
        Log.i(
            TAG,
            "Encoder started session=${options.sessionId} size=${options.width}x${options.height} bitrate=${options.bitrate} fps=${options.frameRate}",
        )
        drainThread = Thread(::drainOutput, "ADBControl-screen-encoder").apply {
            isDaemon = true
            start()
        }
    }

    fun stop() {
        if (!running.compareAndSet(true, false)) return
        drainThread?.interrupt()
        runCatching { drainThread?.join(750) }
        drainThread = null
        virtualDisplay?.release()
        virtualDisplay = null
        codec?.let { encoder ->
            runCatching { encoder.stop() }
            runCatching { encoder.release() }
        }
        codec = null
        CompanionQuicRuntime.stopVideoStream(options.sessionId)
    }

    private fun drainOutput() {
        val bufferInfo = MediaCodec.BufferInfo()
        var outputFormatSent = false
        try {
            while (running.get()) {
                val encoder = codec ?: break
                when (val index = encoder.dequeueOutputBuffer(bufferInfo, DEQUEUE_TIMEOUT_US)) {
                    MediaCodec.INFO_TRY_AGAIN_LATER -> Unit
                    MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> {
                        if (!outputFormatSent) {
                            outputFormatSent = true
                            sendCodecSpecificData(encoder.outputFormat)
                        }
                    }
                    else -> if (index >= 0) {
                        val output = encoder.getOutputBuffer(index)
                        if (output != null && bufferInfo.size > 0) {
                            output.position(bufferInfo.offset)
                            output.limit(bufferInfo.offset + bufferInfo.size)
                            val packet = ByteArray(bufferInfo.size)
                            output.get(packet)
                            sendPacket(encoder, packet, bufferInfo)
                        }
                        encoder.releaseOutputBuffer(index, false)
                    }
                }
            }
        } catch (error: Throwable) {
            if (running.get()) {
                Log.e(TAG, "Screen encoder drain failed", error)
                ProjectionSessionState.update(
                    ProjectionSessionState.State.FAILED,
                    options.sessionId,
                    error.message,
                )
                runCatching {
                    CompanionQuicRuntime.sendProjectionEvent(
                        options.sessionId,
                        "encoderFailed",
                        mapOf("message" to (error.message ?: error.javaClass.simpleName)),
                        failed = true,
                    )
                }
            }
        }
    }

    private fun sendCodecSpecificData(format: MediaFormat) {
        listOf("csd-0", "csd-1").forEach { key ->
            format.getByteBuffer(key)?.copyBytes()?.takeIf { it.isNotEmpty() }?.let { config ->
                Log.i(TAG, "Sending $key session=${options.sessionId} bytes=${config.size}")
                CompanionQuicRuntime.sendVideoFrame(
                    options.sessionId,
                    0L,
                    MediaCodec.BUFFER_FLAG_CODEC_CONFIG,
                    config,
                )
            }
        }
    }

    private fun sendPacket(encoder: MediaCodec, packet: ByteArray, info: MediaCodec.BufferInfo) {
        val keyframe = info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME != 0
        if (awaitingRecoveryKeyframe && !keyframe) return
        val queued = CompanionQuicRuntime.sendVideoFrame(
            options.sessionId,
            info.presentationTimeUs,
            info.flags,
            packet,
        )
        if (queued) {
            if (keyframe) awaitingRecoveryKeyframe = false
            sentFrameCount += 1
            if (sentFrameCount == 1) {
                Log.i(
                    TAG,
                    "First encoded frame queued session=${options.sessionId} bytes=${packet.size} ptsUs=${info.presentationTimeUs} keyframe=$keyframe",
                )
            }
            return
        }

        droppedFrameCount += 1
        if (droppedFrameCount == 1 || droppedFrameCount % 30 == 0) {
            Log.w(
                TAG,
                "QUIC video queue full session=${options.sessionId} dropped=$droppedFrameCount; requesting keyframe",
            )
        }
        awaitingRecoveryKeyframe = true
        runCatching {
            encoder.setParameters(Bundle().apply {
                putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0)
            })
        }
    }

    private fun ByteBuffer.copyBytes(): ByteArray {
        val duplicate = duplicate()
        val bytes = ByteArray(duplicate.remaining())
        duplicate.get(bytes)
        return bytes
    }

    companion object {
        private const val TAG = "ADBControlProjection"
        private const val MIME_TYPE = MediaFormat.MIMETYPE_VIDEO_AVC
        private const val DEQUEUE_TIMEOUT_US = 5_000L
    }
}
