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
import com.adbcontrol.companion.media.EncodedVideoStream
import com.adbcontrol.companion.media.MediaStreamSink
import java.io.File
import java.util.UUID
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

class CameraFeatureHandler(
    private val context: Context,
    private val mediaStreamSink: MediaStreamSink,
) : FeatureCommandHandler {
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
        val realtime = command.args.booleanArg("realtime") ?: false
        val width = command.args.intArg("width") ?: 1280
        val height = command.args.intArg("height") ?: 720
        val bitrate = command.args.intArg("bitrate") ?: 3_000_000
        val frameRate = command.args.intArg("frameRate") ?: 30

        return if (realtime) {
            openRealtimeCamera(command, cameraId, width, height, bitrate, frameRate)
        } else {
            openFileCamera(command, cameraId, width, height, bitrate, frameRate)
        }
    }

    private fun openRealtimeCamera(
        command: CompanionCommandContext,
        cameraId: String,
        width: Int,
        height: Int,
        bitrate: Int,
        frameRate: Int,
    ): CompanionCommandResult {
        val device = openCameraDevice(cameraId) ?: return cameraOpenFailed(command.requestId)
        val encoder = EncodedVideoStream(
            width = width.coerceIn(240, 4096),
            height = height.coerceIn(240, 4096),
            bitrate = bitrate.coerceIn(256_000, 30_000_000),
            frameRate = frameRate.coerceIn(5, 60),
            sink = mediaStreamSink,
        )
        val surface = encoder.start()
        val captureSession = createRecordSession(device, surface)
            ?: run {
                encoder.stop()
                device.close()
                return cameraSessionFailed(command.requestId)
            }
        val request = device.createCaptureRequest(CameraDevice.TEMPLATE_RECORD).apply {
            addTarget(surface)
        }.build()
        captureSession.setRepeatingRequest(request, null, handler)
        sessions[encoder.sessionId()] = CameraSession(
            cameraDevice = device,
            captureSession = captureSession,
            recorder = null,
            outputFile = null,
            encoder = encoder,
        )

        return CompanionCommandResult.success(
            requestId = command.requestId,
            result = mapOf(
                "sessionId" to encoder.sessionId(),
                "cameraId" to cameraId,
                "state" to "streaming",
                "path" to "",
                "width" to width.coerceIn(240, 4096),
                "height" to height.coerceIn(240, 4096),
                "bitrate" to bitrate.coerceIn(256_000, 30_000_000),
                "frameRate" to frameRate.coerceIn(5, 60),
                "format" to "h264-annexb",
                "transport" to "media-sink",
            ),
        )
    }

    private fun openFileCamera(
        command: CompanionCommandContext,
        cameraId: String,
        width: Int,
        height: Int,
        bitrate: Int,
        frameRate: Int,
    ): CompanionCommandResult {
        val sessionId = UUID.randomUUID().toString()
        val outputFile = File(context.filesDir, "camera-streams/$sessionId.mp4")
        outputFile.parentFile?.mkdirs()

        val device = openCameraDevice(cameraId) ?: return cameraOpenFailed(command.requestId)
        val recorder = buildRecorder(outputFile, width, height, bitrate, frameRate)
        val captureSession = createRecordSession(device, recorder.surface)
            ?: run {
                runCatching { recorder.release() }
                device.close()
                return cameraSessionFailed(command.requestId)
            }
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
            encoder = null,
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
                "transport" to "sandbox-file",
            ),
        )
    }

    private fun cameraOpenFailed(requestId: String): CompanionCommandResult {
        return CompanionCommandResult.failure(
            requestId = requestId,
            errorCode = "COMPANION_CAMERA_OPEN_FAILED",
            message = "Android camera failed to open.",
            module = "companion.camera",
            recoverable = true,
        )
    }

    private fun cameraSessionFailed(requestId: String): CompanionCommandResult {
        return CompanionCommandResult.failure(
            requestId = requestId,
            errorCode = "COMPANION_CAMERA_SESSION_FAILED",
            message = "Android failed to create a camera recording session.",
            module = "companion.camera",
            recoverable = true,
        )
    }

    private fun openCameraDevice(cameraId: String): CameraDevice? {
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
                "path" to (session.outputFile?.relativeTo(context.filesDir)?.path ?: ""),
                "sizeBytes" to (session.outputFile?.length() ?: 0),
            ),
        )
    }

    private data class CameraSession(
        val cameraDevice: CameraDevice,
        val captureSession: CameraCaptureSession,
        val recorder: MediaRecorder?,
        val outputFile: File?,
        val encoder: EncodedVideoStream?,
    ) {
        fun close() {
            runCatching { captureSession.stopRepeating() }
            captureSession.close()
            runCatching { recorder?.stop() }
            runCatching { recorder?.reset() }
            runCatching { recorder?.release() }
            runCatching { encoder?.stop() }
            cameraDevice.close()
        }
    }
}
