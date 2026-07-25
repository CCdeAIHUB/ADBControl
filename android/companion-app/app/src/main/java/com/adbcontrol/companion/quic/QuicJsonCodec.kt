package com.adbcontrol.companion.quic

import org.json.JSONArray
import org.json.JSONObject

object QuicJsonCodec {
    fun encodeEnvelope(envelope: QuicEnvelope): String {
        return JSONObject().apply {
            put("protocol", envelope.protocol)
            put("version", envelope.version)
            put("messageId", envelope.messageId)
            envelope.traceId?.let { put("traceId", it) }
            envelope.deviceId?.let { put("deviceId", it) }
            put("channel", envelope.channel.wireName())
            put("kind", envelope.kind.wireName())
            put("payload", toJsonValue(envelope.payload))
        }.toString()
    }

    fun decodeEnvelope(json: String): QuicEnvelope {
        val value = JSONObject(json)
        return QuicEnvelope(
            protocol = value.getString("protocol"),
            version = value.getInt("version"),
            messageId = value.getString("messageId"),
            traceId = value.optNullableString("traceId"),
            deviceId = value.optNullableString("deviceId"),
            channel = QuicChannel.entries.first { it.wireName() == value.getString("channel") },
            kind = QuicMessageKind.entries.first { it.wireName() == value.getString("kind") },
            payload = jsonObjectToMap(value.optJSONObject("payload") ?: JSONObject()),
        )
    }

    fun encodeVideoMetadata(metadata: QuicVideoStreamMetadata): String {
        return JSONObject().apply {
            put("sessionId", metadata.sessionId)
            put("codec", metadata.codec)
            put("width", metadata.width)
            put("height", metadata.height)
            put("bitrate", metadata.bitrate)
            put("frameRate", metadata.frameRate)
        }.toString()
    }

    private fun QuicChannel.wireName(): String = name.lowercase()

    private fun QuicMessageKind.wireName(): String {
        val lower = name.lowercase()
        return lower.substringBefore('_') + lower.substringAfter('_', "")
            .replaceFirstChar { if (name.contains('_')) it.uppercase() else it.toString() }
    }

    private fun JSONObject.optNullableString(name: String): String? {
        return if (!has(name) || isNull(name)) null else getString(name)
    }

    private fun toJsonValue(value: Any?): Any {
        return when (value) {
            null -> JSONObject.NULL
            is Map<*, *> -> JSONObject().apply {
                value.forEach { (key, item) -> put(key.toString(), toJsonValue(item)) }
            }
            is Iterable<*> -> JSONArray().apply { value.forEach { put(toJsonValue(it)) } }
            is Array<*> -> JSONArray().apply { value.forEach { put(toJsonValue(it)) } }
            is Boolean, is Number, is String -> value
            else -> value.toString()
        }
    }

    private fun jsonObjectToMap(value: JSONObject): Map<String, Any?> {
        return value.keys().asSequence().associateWith { key -> fromJsonValue(value.get(key)) }
    }

    private fun fromJsonValue(value: Any?): Any? {
        return when (value) {
            null, JSONObject.NULL -> null
            is JSONObject -> jsonObjectToMap(value)
            is JSONArray -> (0 until value.length()).map { fromJsonValue(value.get(it)) }
            else -> value
        }
    }
}
