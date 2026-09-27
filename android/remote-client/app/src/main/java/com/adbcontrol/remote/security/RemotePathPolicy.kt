package com.adbcontrol.remote.security

object RemotePathPolicy {
    private val protectedRoots = setOf("/", "/sdcard", "/storage/emulated/0")

    fun normalize(value: String): String? {
        val path = value.trim().replace(Regex("/{2,}"), "/").removeSuffix("/").ifBlank { "/" }
        return path.takeIf { it.startsWith('/') && !it.contains('\u0000') && !it.split('/').contains("..") }
    }

    fun canRecursivelyDelete(value: String): Boolean = normalize(value)?.let { it !in protectedRoots } == true
}
