package com.adbcontrol.companion.features

import android.content.Context
import android.hardware.Sensor
import android.hardware.SensorEvent
import android.hardware.SensorEventListener
import android.hardware.SensorManager
import com.adbcontrol.companion.core.CompanionCommandContext
import com.adbcontrol.companion.core.CompanionCommandResult
import java.util.UUID

class MotionSensorFeatureHandler(private val context: Context) : FeatureCommandHandler {
    override val capabilityIds: Set<String> = setOf("android.sensor.motion")
    override val operations: Set<String> = setOf("sensor.subscribe", "sensor.unsubscribe")

    private val sensorManager: SensorManager = context.getSystemService(SensorManager::class.java)
    private val activeSubscriptions = mutableMapOf<String, SensorSubscription>()

    override fun handle(context: CompanionCommandContext): CompanionCommandResult {
        return when (context.operation) {
            "sensor.subscribe" -> subscribe(context)
            "sensor.unsubscribe" -> unsubscribe(context)
            else -> CompanionCommandResult.failure(
                requestId = context.requestId,
                errorCode = "COMPANION_OPERATION_NOT_SUPPORTED",
                message = "Unsupported sensor operation: ${context.operation}",
                module = "companion.sensor",
                recoverable = false,
            )
        }
    }

    private fun subscribe(command: CompanionCommandContext): CompanionCommandResult {
        val requestedTypes = command.args.stringArg("types")
            ?.split(',')
            ?.map { it.trim() }
            ?.filter { it.isNotEmpty() }
            ?: listOf("accelerometer", "gyroscope", "rotationVector")
        val rate = when (command.args.stringArg("rate")) {
            "fastest" -> SensorManager.SENSOR_DELAY_FASTEST
            "game" -> SensorManager.SENSOR_DELAY_GAME
            "normal" -> SensorManager.SENSOR_DELAY_NORMAL
            else -> SensorManager.SENSOR_DELAY_UI
        }

        val registered = mutableListOf<Map<String, Any?>>()
        val subscriptionId = UUID.randomUUID().toString()
        val listener = SnapshotListener()

        requestedTypes.forEach { typeName ->
            sensorFor(typeName)?.let { sensor ->
                val enabled = sensorManager.registerListener(listener, sensor, rate)
                if (enabled) {
                    registered += mapOf(
                        "type" to typeName,
                        "name" to sensor.name,
                        "vendor" to sensor.vendor,
                    )
                }
            }
        }

        if (registered.isEmpty()) {
            return CompanionCommandResult.failure(
                requestId = command.requestId,
                errorCode = "COMPANION_SENSOR_UNAVAILABLE",
                message = "None of the requested sensors are available or could be registered.",
                module = "companion.sensor",
                recoverable = true,
            )
        }

        activeSubscriptions[subscriptionId] = SensorSubscription(listener)
        return CompanionCommandResult.success(
            requestId = command.requestId,
            result = mapOf(
                "subscriptionId" to subscriptionId,
                "registeredSensors" to registered,
                "rate" to (command.args.stringArg("rate") ?: "ui"),
            ),
        )
    }

    private fun unsubscribe(command: CompanionCommandContext): CompanionCommandResult {
        val subscriptionId = command.args.stringArg("subscriptionId")
            ?: return CompanionCommandResult.failure(
                requestId = command.requestId,
                errorCode = "COMPANION_PARAMS_INVALID",
                message = "sensor.unsubscribe requires args.subscriptionId.",
                module = "companion.sensor",
                recoverable = false,
            )
        val subscription = activeSubscriptions.remove(subscriptionId)
            ?: return CompanionCommandResult.failure(
                requestId = command.requestId,
                errorCode = "COMPANION_SENSOR_SUBSCRIPTION_NOT_FOUND",
                message = "Sensor subscription is not active: $subscriptionId",
                module = "companion.sensor",
                recoverable = true,
            )

        sensorManager.unregisterListener(subscription.listener)
        return CompanionCommandResult.success(
            requestId = command.requestId,
            result = mapOf(
                "subscriptionId" to subscriptionId,
                "closed" to true,
                "lastSample" to subscription.listener.lastSample,
            ),
        )
    }

    private fun sensorFor(typeName: String): Sensor? {
        val type = when (typeName) {
            "accelerometer" -> Sensor.TYPE_ACCELEROMETER
            "gyroscope" -> Sensor.TYPE_GYROSCOPE
            "rotationVector" -> Sensor.TYPE_ROTATION_VECTOR
            else -> return null
        }
        return sensorManager.getDefaultSensor(type)
    }

    private data class SensorSubscription(val listener: SnapshotListener)

    private class SnapshotListener : SensorEventListener {
        var lastSample: Map<String, Any?> = emptyMap()
            private set

        override fun onSensorChanged(event: SensorEvent) {
            lastSample = mapOf(
                "sensorType" to event.sensor.type,
                "sensorName" to event.sensor.name,
                "timestampNs" to event.timestamp,
                "values" to event.values.toList(),
            )
        }

        override fun onAccuracyChanged(sensor: Sensor?, accuracy: Int) = Unit
    }
}
