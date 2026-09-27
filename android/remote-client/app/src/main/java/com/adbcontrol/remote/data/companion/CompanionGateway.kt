package com.adbcontrol.remote.data.companion

import com.adbcontrol.remote.data.RemoteRepository
import com.adbcontrol.remote.model.RemoteResult
import org.json.JSONArray
import org.json.JSONObject

/**
 * 伴侣能力网关：`device.invoke` 的类型化封装。
 * 能力/操作 ID 与参数键全部对照伴侣 App `AndroidCapabilityCatalog` 与各 FeatureHandler；
 * 远程协议只有请求/响应，因此需要实时媒体流的能力（投屏/实时摄像头）在这里不可用，
 * 由页面呈现明确说明，不伪造成功。
 */
class CompanionGateway(private val repository: RemoteRepository) {

    fun screenshot(deviceId: String, maxSize: Int = 960): RemoteResult<JSONObject> = repository.invoke(
        deviceId, CAP_ACCESSIBILITY, "accessibility.screenshot", JSONObject().put("maxSize", maxSize),
    )

    fun deviceState(deviceId: String): RemoteResult<JSONObject> = repository.invoke(
        deviceId, CAP_DEVICE_POWER, "device.state",
    )

    fun wake(deviceId: String): RemoteResult<JSONObject> = repository.invoke(
        deviceId, CAP_DEVICE_POWER, "device.wake",
    )

    fun tap(deviceId: String, x: Int, y: Int, coordinateWidth: Int = 0, coordinateHeight: Int = 0): RemoteResult<JSONObject> {
        val args = JSONObject().put("x", x).put("y", y)
        if (coordinateWidth > 0 && coordinateHeight > 0) {
            // 坐标空间映射：截图分辨率 → 物理屏幕（与桌面端 CompanionProjectionSession 一致）。
            args.put("coordinateWidth", coordinateWidth).put("coordinateHeight", coordinateHeight)
        }
        return repository.invoke(deviceId, CAP_ACCESSIBILITY, "accessibility.touch.tap", args)
    }

    fun swipe(
        deviceId: String,
        startX: Int, startY: Int, endX: Int, endY: Int,
        durationMs: Int = 250,
        coordinateWidth: Int = 0, coordinateHeight: Int = 0,
    ): RemoteResult<JSONObject> {
        val args = JSONObject()
            .put("startX", startX).put("startY", startY)
            .put("endX", endX).put("endY", endY)
            .put("durationMs", durationMs)
        if (coordinateWidth > 0 && coordinateHeight > 0) {
            args.put("coordinateWidth", coordinateWidth).put("coordinateHeight", coordinateHeight)
        }
        return repository.invoke(deviceId, CAP_ACCESSIBILITY, "accessibility.touch.swipe", args)
    }

    fun globalAction(deviceId: String, action: String): RemoteResult<JSONObject> = repository.invoke(
        deviceId, CAP_ACCESSIBILITY, "accessibility.global.$action",
    )

    fun accessibilityStatus(deviceId: String): RemoteResult<JSONObject> = repository.invoke(
        deviceId, CAP_ACCESSIBILITY, "accessibility.status",
    )

    fun inputText(deviceId: String, text: String): RemoteResult<JSONObject> = repository.invoke(
        deviceId, CAP_INPUT, "input.text", JSONObject().put("text", text),
    )

    fun inputKey(deviceId: String, keyCode: Int): RemoteResult<JSONObject> = repository.invoke(
        deviceId, CAP_INPUT, "input.key", JSONObject().put("keyCode", keyCode),
    )

    fun volumeGet(deviceId: String): RemoteResult<JSONObject> = repository.invoke(deviceId, CAP_VOLUME, "volume.get")

    fun volumeSet(deviceId: String, level: Int): RemoteResult<JSONObject> = repository.invoke(
        deviceId, CAP_VOLUME, "volume.set", JSONObject().put("level", level),
    )

    fun clipboardRead(deviceId: String): RemoteResult<JSONObject> = repository.invoke(
        deviceId, "android.clipboard.read", "clipboard.read",
    )

    fun clipboardWrite(deviceId: String, text: String): RemoteResult<JSONObject> = repository.invoke(
        deviceId, "android.clipboard.write", "clipboard.write", JSONObject().put("text", text),
    )

    fun appList(
        deviceId: String,
        includeSystem: Boolean = false,
        includeIcons: Boolean = false,
        iconSizePx: Int = 48,
        offset: Int = 0,
        limit: Int = 200,
        packageNames: List<String> = emptyList(),
    ): RemoteResult<JSONObject> {
        val args = JSONObject()
            .put("includeSystem", includeSystem)
            .put("includeIcons", includeIcons)
            .put("iconSizePx", iconSizePx)
            .put("offset", offset)
            .put("limit", limit)
        if (packageNames.isNotEmpty()) {
            args.put("packageNames", JSONArray(packageNames))
        }
        return repository.invoke(deviceId, CAP_APP_LIST, "app.list", args)
    }

    fun smsRead(deviceId: String, limit: Int = 20): RemoteResult<JSONObject> = repository.invoke(
        deviceId, "android.sms.read", "sms.read", JSONObject().put("limit", limit),
    )

    fun smsSend(deviceId: String, number: String, text: String, direct: Boolean): RemoteResult<JSONObject> = repository.invoke(
        deviceId, "android.sms.send", "sms.send",
        JSONObject().put("number", number).put("text", text).put("direct", direct),
    )

    fun phoneCall(deviceId: String, number: String): RemoteResult<JSONObject> = repository.invoke(
        deviceId, "android.phone.call", "phone.call", JSONObject().put("number", number),
    )

    fun showSurface(deviceId: String): RemoteResult<JSONObject> = repository.invoke(
        deviceId, "android.ui.background_surface", "ui.surface.show",
    )

    fun projectionStart(deviceId: String, width: Int, height: Int, bitrateMbps: Int, frameRate: Int): RemoteResult<JSONObject> =
        repository.invoke(
            deviceId, CAP_PROJECTION, "projection.start",
            JSONObject()
                .put("width", width).put("height", height)
                .put("bitrate", bitrateMbps * 1_000_000)
                .put("frameRate", frameRate),
        )

    fun projectionStop(deviceId: String): RemoteResult<JSONObject> = repository.invoke(
        deviceId, CAP_PROJECTION, "projection.stop",
    )

    fun projectionStatus(deviceId: String): RemoteResult<JSONObject> = repository.invoke(
        deviceId, CAP_PROJECTION, "projection.status",
    )

    companion object {
        const val CAP_ACCESSIBILITY = "android.accessibility.control"
        const val CAP_DEVICE_POWER = "android.device.power"
        const val CAP_INPUT = "android.input.ime"
        const val CAP_VOLUME = "android.volume.media"
        const val CAP_APP_LIST = "android.app.list"
        const val CAP_PROJECTION = "android.screen.projection"
    }

    /** 未枚举能力/操作的通用透传（AI agent 的 companion_call 使用）。 */
    fun repositoryInvoke(deviceId: String, capabilityId: String, operation: String, args: JSONObject): RemoteResult<JSONObject> =
        repository.invoke(deviceId, capabilityId, operation, args)
}
