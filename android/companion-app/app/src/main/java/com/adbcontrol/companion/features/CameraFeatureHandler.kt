package com.adbcontrol.companion.features

import android.content.Context
import android.hardware.camera2.CameraCaptureSession
import android.hardware.camera2.CameraDevice
import android.hardware.camera2.CameraManager
import android.media.MediaRecorder
import android.os.Handler
import android.os.HandlerThread
import android.view.Surface
import com.adbcontrol.companion.core.CompanionCommandContext
import com.adbcontrol.companion.core.CompanionCommandResult
import java.io.File
import java.util.UUID
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

class CameraFeatureHandler(private val context: Context) : FeatureCommandHandler {
    override val capabilityIds: Set<String> = setOf("android.camera.stream")
    override val operations: Set<String> = setOf("camera.open", "camera.close")

    private val cameraManager: CameraManager = context.getSystemService(CameraManager::class.java)
    private val sessions = mutableMapOf<String, CameraSession>()
    private val handlerThread = HandlerThread("adbcontrol-camera").apply { start() }
    private val handler = Handler(handlerThread.looper)

    override fun handle(context: CompanionCommandContext): CompanionCommandResult {
        return when (context.operation) {
            "camera.open" -> openCamera(context)
            "camera.close" -> closeCamera(context)
            else -> CompanionCommandResult.failure(
                requestId = context.requestId,
                errorCode = "COMPANION_OPERATION_NOT_SUPPORTED",
                message = "Unsupported camera operation: ${context.operation}",
                module = "companion.camera",
                recoverable = false,
            )
        }
    }

    private fun openCamera(command: CompanionCommandContext): CompanionCommandResult {
        val cameraId = command.args.stringArg("cameraId") ?: cameraManager.cameraIdList.firstOrNull()
            ?: return CompanionCommandResult.failure(
                requestId = command.requestId,
                errorCode = "COMPANION_CAMERA_NOT_FOUND",
                message = "No camera is available on this Android device.",
                module = "companion.camera",
                recoverable = true,
            )
        val sessionId = UUID.randomUUID().toString()
        val width = command.args.intArg("width") ?: 1280
        val height = command.args.intArg("height") ?: 720
        val bitrate = command.args.intArg("bitrate") ?: 3_000_000
        val frameRate = command.args.intArg("frameRate") ?: 30
        val outputFile = File(context.filesDir, "camera-streams/$sessionId.mp4")
        outputFile.parentFile?.mkdirs()

        val device = openCameraDevice(command.requestId, cameraId) ?: return CompanionCommandResult.failure(
            requestId = command.requestId,
            errorCode = "COMPANION_CAMERA_OPEN_FAILED",
            message = "Android camera failed to open.",
            module = "companion.camera",
            recoverable = true,
        )
        val recorder = buildRecorder(outputFile, width, height, bitrate, frameRate)
        val captureSession = createRecordSession(command.requestId, device, recorder.surface)
            ?: return CompanionCommandResult.failure(
                requestId = command.requestId,
                errorCode = "COMPANION_CAMERA_SESSION_FAILED",
                message = "Android failed to create a camera recording session.",
                module = "companion.camera",
                recoverable = true,
            )
        val request = device.createCaptureRequest(CameraDevice.TEMPLATE_RECORD).apply {
            addTarget(recorder.surface)
        }.build()
        captureSession.setRepeatingRequest(request, null, handler)
        recorder.start()
        sessions[sessionId] = CameraSession(
            cameraDevice = device,
            captureSession = captureSession,
            recorder = recorder,
            outputFile = outputFile,
            surfaces = listOf(recorder.surface),
        )

        return CompanionCommandResult.success(
            requestId = command.requestId,
            result = mapOf(
                "sessionId" to sessionId,
                "cameraId" to cameraId,
                "state" to "recording",
                "path" to outputFile.relativeTo(context.filesDir).path,
                "width" to width,
                "height" to height,
                "bitrate" to bitrate,
                "frameRate" to frameRate,
                "format" to "mp4-h264",
            ),
        )
    }

    private fun openCameraDevice(requestId: String, cameraId: String): CameraDevice? {
        val latch = CountDownLatch(1)
        var openedDevice: CameraDevice? = null
        cameraManager.openCamera(
            cameraId,
            object : CameraDevice.StateCallback() {
                override fun onOpened(camera: CameraDevice) {
                    openedDevice = camera
                    latch.countDown()
                }

                override fun onDisconnected(camera: CameraDevice) {
                    camera.close()
                    latch.countDown()
                }

                override fun onError(camera: CameraDevice, error: Int) {
                    camera.close()
                    latch.countDown()
                }
            },
            handler,
        )
        return if (latch.await(3, TimeUnit.SECONDS)) openedDevice else null
    }

    @Suppress("DEPRECATION")
    private fun buildRecorder(
        outputFile: File,
        width: Int,
        height: Int,
        bitrate: Int,
        frameRate: Int,
    ): MediaRecorder {
        return MediaRecorder().apply {
            setVideoSource(MediaRecorder.VideoSource.SURFACE)
            setOutputFormat(MediaRecorder.OutputFormat.MPEG_4)
            setVideoEncoder(MediaRecorder.VideoEncoder.H264)
            setVideoSize(width.coerceIn(240, 4096), height.coerceIn(240, 4096))
            setVideoEncodingBitRate(bitrate.coerceIn(256_000, 30_000_000))
            setVideoFrameRate(frameRate.coerceIn(5, 60))
            setOutputFile(outputFile.absolutePath)
            prepare()
        }
    }

    private fun createRecordSession(
        requestId: String,
        device: CameraDevice,
        surface: Surface,
    ): CameraCaptureSession? {
        val latch = CountDownLatch(1)
        var createdSession: CameraCaptureSession? = null
        device.createCaptureSession(
            listOf(surface),
            object : CameraCaptureSession.StateCallback() {
                override fun onConfigured(session: CameraCaptureSession) {
                    createdSession = session
                    latch.countDown()
                }

                override fun onConfigureFailed(session: CameraCaptureSession) {
                    session.close()
                    latch.countDown()
                }
            },
            handler,
        )
        return if (latch.await(3, TimeUnit.SECONDS)) createdSession else null
    }

    private fun closeCamera(command: CompanionCommandContext): CompanionCommandResult {
        val sessionId = command.args.stringArg("sessionId")
            ?: return CompanionCommandResult.failure(
                requestId = command.requestId,
                errorCode = "COMPANION_PARAMS_INVALID",
                message = "camera.close requires args.sessionId.",
                module = "companion.camera",
                recoverable = false,
            )
        val session = sessions.remove(sessionId)
            ?: return CompanionCommandResult.failure(
                requestId = command.requestId,
                errorCode = "COMPANION_CAMERA_SESSION_NOT_FOUND",
                message = "Camera session is not active: $sessionId",
                module = "companion.camera",
                recoverable = true,
            )
        session.close()
        return CompanionCommandResult.success(
            requestId = command.requestId,
            result = mapOf(
                "sessionId" to sessionId,
                "state" to "closed",
                "path" to session.outputFile.relativeTo(context.filesDir).path,
                "sizeBytes" to session.outputFile.length(),
            ),
        )
    }

    private data class CameraSession(
        val cameraDevice: CameraDevice,
        val captureSession: CameraCaptureSession,
        val recorder: MediaRecorder,
        val outputFile: File,
        val surfaces: List<Surface> = emptyList(),
    ) {
        fun close() {
            runCatching { captureSession.stopRepeating() }
            captureSession.close()
            runCatching { recorder.stop() }
            runCatching { recorder.reset() }
            runCatching { recorder.release() }
            cameraDevice.close()
            surfaces.forEach { surface -> surface.release() }
        }
    }
}
