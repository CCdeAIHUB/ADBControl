package com.adbcontrol.companion.screen

import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.PixelFormat
import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.Image
import android.media.ImageReader
import android.media.MediaRecorder
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import com.adbcontrol.companion.media.EncodedVideoStream
import com.adbcontrol.companion.media.MediaStreamSink
import java.io.File
import java.util.UUID

object ScreenCaptureState {
    private var projection: MediaProjection? = null
    private var activeStreamId: String? = null
    private val videoSessions = mutableMapOf<String, ScreenVideoSession>()
    private val realtimeSessions = mutableMapOf<String, ScreenRealtimeSession>()

    fun setProjection(context: Context, streamId: String, resultCode: Int, data: Intent) {
        val manager = context.getSystemService(MediaProjectionManager::class.java)
        projection?.stop()
        val mediaProjection = manager.getMediaProjection(resultCode, data) ?: throw ScreenCaptureException(
            errorCode = "COMPANION_MEDIA_PROJECTION_UNAVAILABLE",
            message = "Android 在授权完成后没有返回 MediaProjection 实例。",
            recoverable = true,
            suggestion = "请重新执行 stream.open，并在 Android 截屏授权弹窗中确认。",
        )
        projection = mediaProjection.also { grantedProjection ->
            grantedProjection.registerCallback(
                object : MediaProjection.Callback() {
                    override fun onStop() {
                        if (projection === grantedProjection) {
                            stopAllVideoSessions()
                            stopAllRealtimeSessions()
                            projection = null
                            activeStreamId = null
                        }
                    }
                },
                null,
            )
        }
        activeStreamId = streamId
    }

    fun isProjectionReady(): Boolean = projection != null

    fun currentStreamId(): String? = activeStreamId

    fun startRealtimeVideoStream(
        context: Context,
        width: Int,
        height: Int,
        bitrate: Int,
        frameRate: Int,
        sink: MediaStreamSink,
    ): ScreenVideoResult {
        val mediaProjection = projection ?: throw projectionRequired()
        val safeWidth = width.coerceIn(240, 4096)
        val safeHeight = height.coerceIn(240, 4096)
        val safeBitrate = bitrate.coerceIn(256_000, 30_000_000)
        val safeFrameRate = frameRate.coerceIn(5, 60)
        val encoder = EncodedVideoStream(
            width = safeWidth,
            height = safeHeight,
            bitrate = safeBitrate,
            frameRate = safeFrameRate,
            sink = sink,
        )
        val inputSurface = encoder.start()
        val display = mediaProjection.createVirtualDisplay(
            "ADBControlRealtimeScreen-${encoder.sessionId()}",
            safeWidth,
            safeHeight,
            context.resources.displayMetrics.densityDpi,
            DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR,
            inputSurface,
            null,
            null,
        ) ?: run {
            encoder.stop()
            throw virtualDisplayUnavailable("realtime screen video")
        }
        realtimeSessions[encoder.sessionId()] = ScreenRealtimeSession(encoder.sessionId(), encoder, display)
        return ScreenVideoResult(
            sessionId = encoder.sessionId(),
            path = "",
            width = safeWidth,
            height = safeHeight,
            bitrate = safeBitrate,
            frameRate = safeFrameRate,
        )
    }

    fun startVideoStream(
        context: Context,
        width: Int,
        height: Int,
        bitrate: Int,
        frameRate: Int,
    ): ScreenVideoResult {
        val mediaProjection = projection ?: throw projectionRequired()
        val safeWidth = width.coerceIn(240, 4096)
        val safeHeight = height.coerceIn(240, 4096)
        val safeBitrate = bitrate.coerceIn(256_000, 30_000_000)
        val safeFrameRate = frameRate.coerceIn(5, 60)
        val sessionId = UUID.randomUUID().toString()
        val outputFile = File(context.filesDir, "screen-streams/$sessionId.mp4")
        outputFile.parentFile?.mkdirs()

        @Suppress("DEPRECATION")
        val recorder = MediaRecorder().apply {
            setVideoSource(MediaRecorder.VideoSource.SURFACE)
            setOutputFormat(MediaRecorder.OutputFormat.MPEG_4)
            setVideoEncoder(MediaRecorder.VideoEncoder.H264)
            setVideoSize(safeWidth, safeHeight)
            setVideoEncodingBitRate(safeBitrate)
            setVideoFrameRate(safeFrameRate)
            setOutputFile(outputFile.absolutePath)
            prepare()
        }
        val virtualDisplay = mediaProjection.createVirtualDisplay(
            "ADBControlScreenVideo-$sessionId",
            safeWidth,
            safeHeight,
            context.resources.displayMetrics.densityDpi,
            DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR,
            recorder.surface,
            null,
            null,
        ) ?: run {
            runCatching { recorder.reset() }
            runCatching { recorder.release() }
            throw virtualDisplayUnavailable("screen video recording")
        }
        recorder.start()
        videoSessions[sessionId] = ScreenVideoSession(sessionId, recorder, virtualDisplay, outputFile)

        return ScreenVideoResult(
            sessionId = sessionId,
            path = outputFile.relativeTo(context.filesDir).path,
            width = safeWidth,
            height = safeHeight,
            bitrate = safeBitrate,
            frameRate = safeFrameRate,
        )
    }

    fun stopVideoStream(context: Context, sessionId: String?): ScreenVideoStopResult {
        val id = sessionId ?: realtimeSessions.keys.firstOrNull() ?: videoSessions.keys.firstOrNull() ?: throw ScreenCaptureException(
            errorCode = "COMPANION_SCREEN_STREAM_NOT_FOUND",
            message = "当前没有正在运行的屏幕视频流。",
            recoverable = true,
        )
        realtimeSessions.remove(id)?.let { session ->
            session.stop()
            return ScreenVideoStopResult(sessionId = id, path = "", sizeBytes = 0)
        }
        val session = videoSessions.remove(id) ?: throw ScreenCaptureException(
            errorCode = "COMPANION_SCREEN_STREAM_NOT_FOUND",
            message = "屏幕视频流未处于运行状态：$id",
            recoverable = true,
        )
        session.stop()
        return ScreenVideoStopResult(
            sessionId = id,
            path = session.outputFile.relativeTo(context.filesDir).path,
            sizeBytes = session.outputFile.length(),
        )
    }

    fun capturePng(
        context: Context,
        width: Int,
        height: Int,
        timeoutMs: Long,
    ): ScreenCaptureResult {
        val mediaProjection = projection ?: throw ScreenCaptureException(
            errorCode = "COMPANION_MEDIA_PROJECTION_CONSENT_REQUIRED",
            message = "截图前需要先完成 Android MediaProjection 屏幕采集授权。",
            recoverable = true,
            suggestion = "请先调用 stream.open，并在 Android 屏幕采集授权弹窗中确认。",
        )
        val safeWidth = width.coerceIn(240, 4096)
        val safeHeight = height.coerceIn(240, 4096)
        val safeTimeoutMs = timeoutMs.coerceIn(300, 5_000)
        val densityDpi = context.resources.displayMetrics.densityDpi
        val imageReader = ImageReader.newInstance(safeWidth, safeHeight, PixelFormat.RGBA_8888, 2)
        val virtualDisplay = mediaProjection.createVirtualDisplay(
            "ADBControlScreenshot",
            safeWidth,
            safeHeight,
            densityDpi,
            DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR,
            imageReader.surface,
            null,
            null,
        ) ?: run {
            imageReader.close()
            throw virtualDisplayUnavailable("screenshot capture")
        }

        try {
            val image = acquireImage(imageReader, safeTimeoutMs) ?: throw ScreenCaptureException(
                errorCode = "COMPANION_SCREEN_FRAME_TIMEOUT",
                message = "等待 MediaProjection 画面帧超时。",
                recoverable = true,
                suggestion = "请保持设备屏幕亮起，然后重试 screenshot.capture。",
            )
            image.use { capturedImage ->
                val outputFile = File(context.filesDir, "screenshots/${UUID.randomUUID()}.png")
                outputFile.parentFile?.mkdirs()
                writePng(capturedImage, safeWidth, safeHeight, outputFile)
                return ScreenCaptureResult(
                    streamId = activeStreamId,
                    path = outputFile.relativeTo(context.filesDir).path,
                    width = safeWidth,
                    height = safeHeight,
                    sizeBytes = outputFile.length(),
                )
            }
        } finally {
            virtualDisplay.release()
            imageReader.close()
        }
    }

    fun stop() {
        stopAllVideoSessions()
        stopAllRealtimeSessions()
        projection?.stop()
        projection = null
        activeStreamId = null
    }

    private fun projectionRequired(): ScreenCaptureException {
        return ScreenCaptureException(
            errorCode = "COMPANION_MEDIA_PROJECTION_CONSENT_REQUIRED",
            message = "屏幕视频流需要先完成 Android MediaProjection 用户授权。",
            recoverable = true,
            suggestion = "请先调用 stream.open，并在 Android 屏幕采集授权弹窗中确认。",
        )
    }

    private fun virtualDisplayUnavailable(operation: String): ScreenCaptureException {
        return ScreenCaptureException(
            errorCode = "COMPANION_VIRTUAL_DISPLAY_UNAVAILABLE",
            message = "Android 无法为 $operation 创建虚拟显示。",
            recoverable = true,
            suggestion = "请停止当前屏幕采集会话，保持屏幕解锁后重试。",
        )
    }

    private fun stopAllVideoSessions() {
        val sessions = videoSessions.values.toList()
        videoSessions.clear()
        sessions.forEach { session -> session.stop() }
    }

    private fun stopAllRealtimeSessions() {
        val sessions = realtimeSessions.values.toList()
        realtimeSessions.clear()
        sessions.forEach { session -> session.stop() }
    }

    private fun acquireImage(imageReader: ImageReader, timeoutMs: Long): Image? {
        val deadline = System.currentTimeMillis() + timeoutMs
        var image: Image? = null
        while (image == null && System.currentTimeMillis() < deadline) {
            image = imageReader.acquireLatestImage()
            if (image == null) {
                Thread.sleep(40)
            }
        }
        return image
    }

    private fun writePng(image: Image, width: Int, height: Int, outputFile: File) {
        val plane = image.planes.first()
        val buffer = plane.buffer
        val pixelStride = plane.pixelStride
        val rowStride = plane.rowStride
        val rowPadding = rowStride - pixelStride * width
        val paddedWidth = width + rowPadding / pixelStride
        val paddedBitmap = Bitmap.createBitmap(paddedWidth, height, Bitmap.Config.ARGB_8888)
        paddedBitmap.copyPixelsFromBuffer(buffer)
        val bitmap = if (paddedWidth == width) {
            paddedBitmap
        } else {
            Bitmap.createBitmap(paddedBitmap, 0, 0, width, height).also {
                paddedBitmap.recycle()
            }
        }

        outputFile.outputStream().use { output ->
            bitmap.compress(Bitmap.CompressFormat.PNG, 100, output)
        }
        bitmap.recycle()
    }
}

private data class ScreenVideoSession(
    val sessionId: String,
    val recorder: MediaRecorder,
    val virtualDisplay: VirtualDisplay,
    val outputFile: File,
) {
    fun stop() {
        runCatching { recorder.stop() }
        runCatching { recorder.reset() }
        runCatching { recorder.release() }
        virtualDisplay.release()
    }
}

private data class ScreenRealtimeSession(
    val sessionId: String,
    val encoder: EncodedVideoStream,
    val virtualDisplay: VirtualDisplay,
) {
    fun stop() {
        virtualDisplay.release()
        encoder.stop()
    }
}

data class ScreenCaptureResult(
    val streamId: String?,
    val path: String,
    val width: Int,
    val height: Int,
    val sizeBytes: Long,
)

data class ScreenVideoResult(
    val sessionId: String,
    val path: String,
    val width: Int,
    val height: Int,
    val bitrate: Int,
    val frameRate: Int,
)

data class ScreenVideoStopResult(
    val sessionId: String,
    val path: String,
    val sizeBytes: Long,
)

class ScreenCaptureException(
    val errorCode: String,
    override val message: String,
    val recoverable: Boolean,
    val suggestion: String? = null,
) : RuntimeException(message)
