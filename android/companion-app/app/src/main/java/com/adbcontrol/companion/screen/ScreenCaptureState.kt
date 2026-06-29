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
import java.io.File
import java.util.UUID

object ScreenCaptureState {
    private var projection: MediaProjection? = null
    private var activeStreamId: String? = null
    private val videoSessions = mutableMapOf<String, ScreenVideoSession>()

    fun setProjection(context: Context, streamId: String, resultCode: Int, data: Intent) {
        val manager = context.getSystemService(MediaProjectionManager::class.java)
        projection?.stop()
        projection = manager.getMediaProjection(resultCode, data).also { mediaProjection ->
            mediaProjection.registerCallback(
                object : MediaProjection.Callback() {
                    override fun onStop() {
                        if (projection === mediaProjection) {
                            stopAllVideoSessions()
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

    fun startVideoStream(
        context: Context,
        width: Int,
        height: Int,
        bitrate: Int,
        frameRate: Int,
    ): ScreenVideoResult {
        val mediaProjection = projection ?: throw ScreenCaptureException(
            errorCode = "COMPANION_MEDIA_PROJECTION_CONSENT_REQUIRED",
            message = "Screen video stream requires Android MediaProjection user consent.",
            recoverable = true,
            suggestion = "Call stream.open and approve the Android screen capture consent dialog first.",
        )
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
        )
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
        val id = sessionId ?: videoSessions.keys.firstOrNull() ?: throw ScreenCaptureException(
            errorCode = "COMPANION_SCREEN_STREAM_NOT_FOUND",
            message = "No active screen video stream exists.",
            recoverable = true,
        )
        val session = videoSessions.remove(id) ?: throw ScreenCaptureException(
            errorCode = "COMPANION_SCREEN_STREAM_NOT_FOUND",
            message = "Screen video stream is not active: $id",
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
            message = "Screen capture requires Android MediaProjection user consent before screenshots can be captured.",
            recoverable = true,
            suggestion = "Call stream.open and approve the Android screen capture consent dialog first.",
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
        )

        try {
            val image = acquireImage(imageReader, safeTimeoutMs) ?: throw ScreenCaptureException(
                errorCode = "COMPANION_SCREEN_FRAME_TIMEOUT",
                message = "Timed out while waiting for a MediaProjection frame.",
                recoverable = true,
                suggestion = "Keep the device screen awake and retry screenshot.capture.",
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
        projection?.stop()
        projection = null
        activeStreamId = null
    }

    private fun stopAllVideoSessions() {
        val sessions = videoSessions.values.toList()
        videoSessions.clear()
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
