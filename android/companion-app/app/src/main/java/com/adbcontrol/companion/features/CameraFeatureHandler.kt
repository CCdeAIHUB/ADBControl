package com.adbcontrol.companion.features

import android.content.Context
import android.hardware.camera2.CameraCaptureSession
import android.hardware.camera2.CameraDevice
import android.hardware.camera2.CameraManager
import android.os.Handler
import android.os.HandlerThread
import android.view.Surface
import com.adbcontrol.companion.core.CompanionCommandContext
import com.adbcontrol.companion.core.CompanionCommandResult
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
        val latch = CountDownLatch(1)
        var openedDevice: CameraDevice? = null
        var openError: String? = null

        cameraManager.openCamera(
            cameraId,
            object : CameraDevice.StateCallback() {
                override fun onOpened(camera: CameraDevice) {
                    openedDevice = camera
                    latch.countDown()
                }

                override fun onDisconnected(camera: CameraDevice) {
                    openError = "Camera disconnected while opening."
                    camera.close()
                    latch.countDown()
                }

                override fun onError(camera: CameraDevice, error: Int) {
                    openError = "Android camera open failed with error code $error."
                    camera.close()
                    latch.countDown()
                }
            },
            handler,
        )

        if (!latch.await(3, TimeUnit.SECONDS)) {
            return CompanionCommandResult.failure(
                requestId = command.requestId,
                errorCode = "COMPANION_CAMERA_OPEN_TIMEOUT",
                message = "Timed out while opening Android camera.",
                module = "companion.camera",
                recoverable = true,
            )
        }

        val device = openedDevice
            ?: return CompanionCommandResult.failure(
                requestId = command.requestId,
                errorCode = "COMPANION_CAMERA_OPEN_FAILED",
                message = openError ?: "Android camera failed to open.",
                module = "companion.camera",
                recoverable = true,
            )
        sessions[sessionId] = CameraSession(device)
        return CompanionCommandResult.success(
            requestId = command.requestId,
            result = mapOf(
                "sessionId" to sessionId,
                "cameraId" to cameraId,
                "state" to "opened",
                "streaming" to false,
                "note" to "Camera device is open. Preview/encoder surface binding is the next media-pipeline step.",
            ),
        )
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
            ),
        )
    }

    private data class CameraSession(
        val cameraDevice: CameraDevice,
        val captureSession: CameraCaptureSession? = null,
        val surfaces: List<Surface> = emptyList(),
    ) {
        fun close() {
            captureSession?.close()
            cameraDevice.close()
            surfaces.forEach { surface -> surface.release() }
        }
    }
}
