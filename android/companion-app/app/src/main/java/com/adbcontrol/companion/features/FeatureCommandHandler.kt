package com.adbcontrol.companion.features

import com.adbcontrol.companion.core.CompanionCommandContext
import com.adbcontrol.companion.core.CompanionCommandResult

interface FeatureCommandHandler {
    val capabilityIds: Set<String>
    val operations: Set<String>

    fun canHandle(context: CompanionCommandContext): Boolean {
        return capabilityIds.contains(context.capabilityId) && operations.contains(context.operation)
    }

    fun handle(context: CompanionCommandContext): CompanionCommandResult
}

fun Map<String, Any?>.stringArg(name: String): String? {
    return this[name]?.toString()?.takeIf { it.isNotBlank() }
}

fun Map<String, Any?>.intArg(name: String): Int? {
    val value = this[name] ?: return null
    return when (value) {
        is Int -> value
        is Long -> value.toInt()
        is Double -> value.toInt()
        is Float -> value.toInt()
        is Number -> value.toInt()
        is String -> value.toIntOrNull()
        else -> null
    }
}

fun Map<String, Any?>.booleanArg(name: String): Boolean {
    val value = this[name] ?: return false
    return when (value) {
        is Boolean -> value
        is String -> value.equals("true", ignoreCase = true)
        is Number -> value.toInt() != 0
        else -> false
    }
}

@Suppress("UNCHECKED_CAST")
fun Map<String, Any?>.mapListArg(name: String): List<Map<String, Any?>> {
    return this[name] as? List<Map<String, Any?>> ?: emptyList()
}
