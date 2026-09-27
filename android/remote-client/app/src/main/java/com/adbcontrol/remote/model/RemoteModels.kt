package com.adbcontrol.remote.model

enum class UserRole { ADMIN, USER }

data class CoreProfile(
    val endpoint: String,
    val serverName: String = "",
    val certificateDerBase64: String = "",
    val fingerprintSha256: String = "",
    val webEndpoint: String = "",
)

data class Session(
    val token: String,
    val username: String,
    val role: UserRole,
    val assignedDevices: Set<String>,
    val passwordChangeRequired: Boolean,
)

/**
 * 远程设备条目。
 * 来源与桌面端一致：桌面从 `adb devices -l` 构建设备卡；远程端 ADB 全局命令不在白名单内，
 * 因此以 `auth.login` 返回的已分配设备 ID 为清单，型号/品牌/电量等再经 `adb.exec` 采集。
 * [companionState] 来自 `device.list`（伴侣注册表），当前 Core 部署可能为空，仅作附加信息。
 */
data class RemoteDevice(
    val id: String,
    val name: String = "",
    val adbState: String = "", // device / offline / unauthorized / ""（未知）
    val brand: String = "",
    val model: String = "",
    val androidVersion: String = "",
    val batteryLevel: Int? = null,
    val companionState: String = "", // ready / connecting / disconnected …
    val appVersion: String = "",
) {
    val adbOnline: Boolean get() = adbState == "device"
    val companionOnline: Boolean get() = companionState == "ready"
    val displayName: String get() = name.ifBlank {
        listOf(brand, model).filter(String::isNotBlank).joinToString(" ").ifBlank { id }
    }
}

data class RemoteAccount(
    val username: String,
    val role: UserRole,
    val devices: Set<String>,
    val builtIn: Boolean,
    val passwordChangeRequired: Boolean,
)

/** `adb.exec` 的原始结构化输出（对应 Core 的 AdbCommandResult）。 */
data class AdbOutput(
    val exitCode: Int,
    val stdout: String,
    val stderr: String,
) {
    val success: Boolean get() = exitCode == 0
    val combined: String
        get() = listOf(stdout.trim(), stderr.trim()).filter(String::isNotBlank).joinToString("\n")
}

data class AppError(
    val errorCode: String,
    val message: String,
    val module: String,
    val recoverable: Boolean,
    val suggestion: String? = null,
    val traceId: String? = null,
)

sealed interface RemoteResult<out T> {
    data class Success<T>(val value: T) : RemoteResult<T>
    data class Failure(val error: AppError) : RemoteResult<Nothing>
}
