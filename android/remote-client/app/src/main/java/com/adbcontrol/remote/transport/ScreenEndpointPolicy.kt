package com.adbcontrol.remote.transport

import java.net.URI

object ScreenEndpointPolicy {
    fun derive(quicEndpoint: String, configured: String): String? {
        val explicit = configured.trim()
        if (explicit.isNotEmpty()) {
            val normalized = when {
                explicit.startsWith("https://") -> "wss://${explicit.removePrefix("https://")}"
                explicit.startsWith("http://") -> "ws://${explicit.removePrefix("http://")}"
                explicit.startsWith("wss://") || explicit.startsWith("ws://") -> explicit
                else -> "wss://$explicit"
            }.trimEnd('/')
            return normalized.takeIf(::allowed)
        }
        val uri = runCatching { URI(quicEndpoint) }.getOrNull() ?: return null
        val host = uri.host ?: return null
        if (!isPrivateHost(host)) return null
        val authority = if (host.contains(':')) "[$host]" else host
        return "ws://$authority:18087"
    }

    private fun allowed(endpoint: String): Boolean {
        val uri = runCatching { URI(endpoint) }.getOrNull() ?: return false
        return uri.scheme == "wss" || (uri.scheme == "ws" && isPrivateHost(uri.host.orEmpty()))
    }

    private fun isPrivateHost(host: String): Boolean {
        if (host == "localhost" || host == "127.0.0.1" || host == "::1") return true
        val parts = host.split('.').mapNotNull(String::toIntOrNull)
        if (parts.size != 4) return false
        return parts[0] == 10 || parts[0] == 127 ||
            (parts[0] == 192 && parts[1] == 168) ||
            (parts[0] == 172 && parts[1] in 16..31)
    }
}
