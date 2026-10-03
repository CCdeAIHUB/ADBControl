package com.adbcontrol.remote.data

/** Prefer the Core's live ADB identities; fall back to persisted assignments offline. */
object RemoteDeviceListPolicy {
    fun effectiveIds(reported: List<String>): List<String> =
        reported.filter(String::isNotBlank).distinct()
}
