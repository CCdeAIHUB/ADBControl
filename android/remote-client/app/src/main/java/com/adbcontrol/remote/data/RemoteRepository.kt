package com.adbcontrol.remote.data

import com.adbcontrol.remote.model.AdbOutput
import com.adbcontrol.remote.model.AppError
import com.adbcontrol.remote.model.RemoteAccount
import com.adbcontrol.remote.model.RemoteDevice
import com.adbcontrol.remote.model.RemoteResult
import com.adbcontrol.remote.model.Session
import com.adbcontrol.remote.model.UserRole

import com.adbcontrol.remote.transport.RemoteTransport
import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID
import com.adbcontrol.remote.data.log.AppDiagnostics

/**
 * 远程协议封装：方法、参数与响应校验全部集中在这里，UI 与业务层不得直接拼协议 JSON。
 * 会话令牌仅保存在内存（与桌面端一致，不落盘）。
 */
class RemoteRepository(private val transport: RemoteTransport) {
    private var sessionToken: String? = null

    val hasSession: Boolean get() = sessionToken != null

    fun login(username: String, password: String): RemoteResult<Session> {
        val result = callRaw("auth.login", JSONObject().put("username", username).put("password", password), authenticated = false)
        return result.map { value ->
            val json = value as? JSONObject ?: error("auth.login result must be an object")
            val token = json.getString("sessionToken")
            Session(
                token = token,
                username = json.optString("username", username),
                role = if (json.optString("role") == "admin") UserRole.ADMIN else UserRole.USER,
                assignedDevices = json.optJSONArray("devices").strings().toSet(),
                passwordChangeRequired = json.optBoolean("passwordChangeRequired"),
            ).also { sessionToken = token }
        }
    }

    fun me(): RemoteResult<Session> = callRaw("auth.me", JSONObject()).map { value ->
        val json = value as? JSONObject ?: error("auth.me result must be an object")
        Session(
            token = sessionToken.orEmpty(),
            username = json.optString("username"),
            role = if (json.optString("role") == "admin") UserRole.ADMIN else UserRole.USER,
            assignedDevices = json.optJSONArray("devices").strings().toSet(),
            passwordChangeRequired = json.optBoolean("passwordChangeRequired"),
        )
    }

    fun changePassword(currentPassword: String, newPassword: String): RemoteResult<Unit> =
        callRaw("auth.changePassword", JSONObject()
            .put("currentPassword", currentPassword)
            .put("newPassword", newPassword)).map { sessionToken = null }

    fun logout(): RemoteResult<Unit> = callRaw("auth.logout", JSONObject()).map { sessionToken = null }

    /**
     * 设备清单：以会话分配设备为基（真实部署中 Core 伴侣注册表为空），
     * 再合并 `device.list` 返回的伴侣连接状态（可能为空，允许缺失）。
     */
    fun devices(assignedDevices: Set<String>): RemoteResult<List<RemoteDevice>> {
        val companions = callRaw("device.list", JSONObject()).map { result ->
            val array = when (result) {
                is JSONArray -> result
                is JSONObject -> result.optJSONArray("devices") ?: JSONArray()
                else -> JSONArray()
            }
            buildList {
                for (index in 0 until array.length()) {
                    val item = array.optJSONObject(index) ?: continue
                    add(Triple(
                        item.optString("deviceId", item.optString("id")),
                        item.optString("connectionState", item.optString("state")),
                        item.optString("appVersion"),
                    ))
                }
            }
        }
        val companionMap = when (companions) {
            is RemoteResult.Success -> companions.value.associateBy({ it.first }, { it.second to it.third })
            is RemoteResult.Failure -> emptyMap()
        }
        return RemoteResult.Success(assignedDevices.map { deviceId ->
            val companion = companionMap[deviceId]
            RemoteDevice(
                id = deviceId,
                companionState = companion?.first.orEmpty(),
                appVersion = companion?.second.orEmpty(),
            )
        })
    }

    /** 采集单台设备的展示信息（品牌/型号/安卓版本/电量），全部经白名单 `adb.exec`。 */
    fun enrichDevice(deviceId: String): RemoteResult<RemoteDevice> {
        val props = adb(deviceId, "shell", "getprop", "ro.product.brand", "ro.product.model", "ro.build.version.release")
        val battery = adb(deviceId, "shell", "dumpsys", "battery")
        return adb(deviceId, "get-state").map { state ->
            val lines = (props as? RemoteResult.Success)?.value?.stdout.orEmpty().lines().map(String::trim)
            val batteryText = (battery as? RemoteResult.Success)?.value?.stdout.orEmpty()
            RemoteDevice(
                id = deviceId,
                adbState = state.stdout.trim(),
                brand = lines.getOrNull(0).orEmpty(),
                model = lines.getOrNull(1).orEmpty(),
                androidVersion = lines.getOrNull(2).orEmpty(),
                batteryLevel = parseBatteryLevel(batteryText),
            )
        }
    }

    fun adb(deviceId: String, vararg args: String): RemoteResult<AdbOutput> {
        val allArgs = JSONArray().put("-s").put(deviceId)
        args.forEach(allArgs::put)
        return callRaw("adb.exec", JSONObject().put("args", allArgs)).map { result ->
            val json = result as? JSONObject ?: error("adb.exec result must be an object")
            AdbOutput(
                exitCode = json.optInt("exitCode", -1),
                stdout = json.optString("stdout"),
                stderr = json.optString("stderr"),
            )
        }
    }

    fun invoke(deviceId: String, capabilityId: String, operation: String, args: JSONObject = JSONObject()): RemoteResult<JSONObject> =
        callRaw("device.invoke", JSONObject()
            .put("deviceId", deviceId)
            .put("capabilityId", capabilityId)
            .put("operation", operation)
            .put("args", args)).map { result ->
            val outer = result as? JSONObject ?: error("device.invoke result must be an object")
            // Core 返回两层：外层 status=envelope，内层 result 是伴侣 App 的实际执行结果。
            // 内层缺失说明宿主 Core 未接线伴侣路由（COMPANION_SESSION_NOT_CONNECTED 已在错误路径返回）。
            val inner = outer.optJSONObject("result")
                ?: outer.optJSONObject("envelope")?.optJSONObject("payload")?.optJSONObject("result")
            inner ?: JSONObject()
        }

    fun getCapabilities(deviceId: String): RemoteResult<Any> =
        callRaw("device.getCapabilities", JSONObject().put("deviceId", deviceId))

    fun getPermissionState(deviceId: String): RemoteResult<Any> =
        callRaw("device.getPermissionState", JSONObject().put("deviceId", deviceId))

    // ---------- 账号管理（admin.*：远程会话通常会被服务端 REMOTE_AUTH_FORBIDDEN 拒绝，页面如实展示） ----------

    fun accounts(): RemoteResult<List<RemoteAccount>> = callRaw("admin.users.list", JSONObject()).map { result ->
        val array = when (result) {
            is JSONArray -> result
            is JSONObject -> result.optJSONArray("users") ?: JSONArray()
            else -> JSONArray()
        }
        buildList {
            for (index in 0 until array.length()) {
                val item = array.optJSONObject(index) ?: continue
                add(RemoteAccount(
                    username = item.optString("username"),
                    role = if (item.optString("role") == "admin") UserRole.ADMIN else UserRole.USER,
                    devices = item.optJSONArray("devices").strings().toSet(),
                    builtIn = item.optBoolean("builtIn"),
                    passwordChangeRequired = item.optBoolean("passwordChangeRequired"),
                ))
            }
        }
    }

    fun createAccount(username: String, password: String): RemoteResult<Any> =
        callRaw("admin.users.create", JSONObject().put("username", username).put("password", password))

    fun deleteAccount(username: String): RemoteResult<Any> =
        callRaw("admin.users.delete", JSONObject().put("username", username))

    fun resetPassword(username: String, password: String): RemoteResult<Any> =
        callRaw("admin.users.resetPassword", JSONObject().put("username", username).put("newPassword", password))

    fun assignDevice(username: String, deviceId: String, assigned: Boolean): RemoteResult<Any> =
        callRaw("admin.devices.assign", JSONObject().put("username", username).put("deviceId", deviceId).put("assigned", assigned))

    fun call(method: String, params: JSONObject = JSONObject()): RemoteResult<Any> = callRaw(method, params)

    fun reportDiagnostics(sessionId: String, events: List<AppDiagnostics.Event>): RemoteResult<Int> {
        val payload = JSONArray()
        events.take(50).forEach { event ->
            payload.put(JSONObject()
                .put("level", event.level)
                .put("phase", event.phase)
                .put("module", event.module)
                .put("ok", event.ok)
                .put("elapsedMs", event.elapsedMs)
                .put("errorCode", event.errorCode)
                .put("detail", event.detail))
        }
        return callRaw("diagnostics.report", JSONObject().put("sessionId", sessionId).put("events", payload)).map { result ->
            (result as? JSONObject)?.optInt("accepted", 0) ?: 0
        }
    }

    private fun callRaw(method: String, params: JSONObject, authenticated: Boolean = true): RemoteResult<Any> {
        val started = System.currentTimeMillis()
        val id = UUID.randomUUID().toString()
        val request = JSONObject().put("id", id).put("method", method).put("params", params)
        if (authenticated) {
            val token = sessionToken ?: return RemoteResult.Failure(
                AppError("REMOTE_AUTH_REQUIRED", "登录状态已失效，请重新登录", "remote.auth", true),
            )
            request.put("sessionToken", token)
        }
        val result = when (val response = transport.request(request.toString())) {
            is RemoteResult.Failure -> response
            is RemoteResult.Success -> parseResponse(id, response.value)
        }
        if (method != "diagnostics.report") {
            AppDiagnostics.record(
                if (result is RemoteResult.Failure) "error" else "info",
                "remote.request", "remote.repository", result is RemoteResult.Success,
                System.currentTimeMillis() - started,
                (result as? RemoteResult.Failure)?.error?.errorCode.orEmpty(),
                "method=$method",
            )
        }
        return result
    }

    private fun parseResponse(requestId: String, text: String): RemoteResult<Any> = try {
        val json = JSONObject(text)
        if (json.optString("id") != requestId) {
            RemoteResult.Failure(AppError("REMOTE_PROTOCOL_ID_MISMATCH", "Core 响应与请求不匹配", "remote.protocol", false))
        } else if (json.optBoolean("ok")) {
            RemoteResult.Success(json.opt("result") ?: JSONObject.NULL)
        } else {
            val error = json.optJSONObject("error") ?: JSONObject()
            RemoteResult.Failure(AppError(
                errorCode = error.optString("errorCode", "REMOTE_UNKNOWN_ERROR"),
                message = error.optString("message", "远程 Core 返回失败"),
                module = error.optString("module", "remote.core"),
                recoverable = error.optBoolean("recoverable"),
                suggestion = error.optString("suggestion").ifBlank { null },
                traceId = error.optString("traceId").ifBlank { null },
            ))
        }
    } catch (error: Throwable) {
        RemoteResult.Failure(AppError("REMOTE_PROTOCOL_DECODE_FAILED", "无法解析 Core 响应", "remote.protocol", false, error.message))
    }

    private inline fun <T, R> RemoteResult<T>.map(transform: (T) -> R): RemoteResult<R> = when (this) {
        is RemoteResult.Failure -> this
        is RemoteResult.Success -> try { RemoteResult.Success(transform(value)) } catch (error: Throwable) {
            RemoteResult.Failure(AppError("REMOTE_PROTOCOL_RESULT_INVALID", "Core 返回的数据格式不受支持", "remote.protocol", false, error.message))
        }
    }

    private fun JSONArray?.strings(): List<String> = buildList {
        val array = this@strings ?: return@buildList
        for (index in 0 until array.length()) array.optString(index).takeIf(String::isNotBlank)?.let(::add)
    }

    private fun parseBatteryLevel(dumpsys: String): Int? {
        val match = Regex("(?m)^\\s*level:\\s*(\\d+)").find(dumpsys) ?: return null
        return match.groupValues[1].toIntOrNull()?.coerceIn(0, 100)
    }
}
