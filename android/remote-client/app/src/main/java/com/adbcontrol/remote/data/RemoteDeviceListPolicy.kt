package com.adbcontrol.remote.data

/** Prefer the Core's live ADB identities; fall back to persisted assignments offline. */
object RemoteDeviceListPolicy {
    fun effectiveIds(assigned: Set<String>, reported: List<String>): List<String> {
        val current = reported.filter(String::isNotBlank).distinct()
        return current.ifEmpty { assigned.filter(String::isNotBlank).distinct() }
    }
}
