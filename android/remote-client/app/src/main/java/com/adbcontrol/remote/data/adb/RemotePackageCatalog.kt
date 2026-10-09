package com.adbcontrol.remote.data.adb

import org.json.JSONArray

/** Maps the authenticated remote package API to the same metadata model used by the UI. */
object RemotePackageCatalog {
    fun parse(value: Any): List<PackageCatalogParser.PackageEntry> {
        val array = value as? JSONArray ?: return emptyList()
        return (0 until array.length()).mapNotNull { index ->
            val item = array.optJSONObject(index) ?: return@mapNotNull null
            val packageName = item.optString("package").trim()
            if (packageName.isBlank()) return@mapNotNull null
            PackageCatalogParser.PackageEntry(
                packageName = packageName,
                label = item.optString("displayName"),
                isSystem = item.optBoolean("system"),
                versionName = item.optLong("versionCode").takeIf { it > 0 }?.toString().orEmpty(),
                apkPath = item.optString("apkPath"),
                iconPngBase64 = item.optString("iconPngBase64"),
                iconText = item.optString("iconText").ifBlank { "APP" },
            )
        }
    }
}
