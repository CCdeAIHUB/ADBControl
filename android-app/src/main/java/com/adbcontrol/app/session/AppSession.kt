package com.adbcontrol.app.session

import java.util.UUID

data class AppSession(
    val sessionId: String = UUID.randomUUID().toString(),
    val deviceId: String,
    val createdAtEpochMillis: Long = System.currentTimeMillis(),
) {
    init {
        require(deviceId.isNotBlank()) { "deviceId is required" }
    }
}
