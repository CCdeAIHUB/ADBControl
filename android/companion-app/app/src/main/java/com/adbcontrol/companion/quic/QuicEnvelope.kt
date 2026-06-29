package com.adbcontrol.companion.quic

const val COMPANION_PROTOCOL = "adbcontrol-companion-quic"
const val COMPANION_PROTOCOL_VERSION = 1

data class QuicEnvelope(
    val protocol: String = COMPANION_PROTOCOL,
    val version: Int = COMPANION_PROTOCOL_VERSION,
    val messageId: String,
    val traceId: String? = null,
    val deviceId: String? = null,
    val channel: QuicChannel,
    val kind: QuicMessageKind,
    val payload: Map<String, Any?> = emptyMap(),
)

enum class QuicChannel {
    CONTROL,
    DATA,
    MEDIA,
}

enum class QuicMessageKind {
    HELLO,
    HELLO_ACK,
    HEARTBEAT,
    CAPABILITY_LIST,
    PERMISSION_STATE,
    COMMAND_REQUEST,
    COMMAND_RESPONSE,
    STREAM_OPEN,
    STREAM_CHUNK,
    STREAM_CLOSE,
    ERROR,
}

data class CompanionHello(
    val appVersion: String,
    val deviceId: String,
    val deviceName: String,
    val androidSdk: Int,
    val supportedProtocolVersions: List<Int>,
)

data class CompanionCommandRequest(
    val requestId: String,
    val capabilityId: String,
    val operation: String,
    val args: Map<String, Any?> = emptyMap(),
)

data class CompanionError(
    val errorCode: String,
    val message: String,
    val module: String,
    val recoverable: Boolean,
    val suggestion: String? = null,
)
