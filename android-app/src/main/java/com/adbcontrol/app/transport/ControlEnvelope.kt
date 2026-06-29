package com.adbcontrol.app.transport

import org.json.JSONObject

object ControlEnvelope {
    fun hello(appInstanceId: String, appVersion: String, androidSdk: Int, deviceModel: String): ByteArray {
        return JSONObject()
            .put("type", "hello")
            .put("appInstanceId", appInstanceId)
            .put("appVersion", appVersion)
            .put("androidSdk", androidSdk)
            .put("deviceModel", deviceModel)
            .toString()
            .toByteArray(Charsets.UTF_8)
    }

    fun capabilitySync(capabilities: List<String>, permissions: List<String>): ByteArray {
        return JSONObject()
            .put("type", "capability_sync")
            .put("capabilities", capabilities)
            .put("permissions", permissions)
            .toString()
            .toByteArray(Charsets.UTF_8)
    }
}
