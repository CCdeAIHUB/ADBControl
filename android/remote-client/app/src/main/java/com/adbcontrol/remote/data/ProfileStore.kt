package com.adbcontrol.remote.data

import android.content.Context
import com.adbcontrol.remote.model.CoreProfile

/**
 * Core 连接配置存储。证书与指纹均为可选（原生层支持指纹固定与 TOFU），
 * 因此这里只要求 endpoint 存在；serverName 留空时由传输层使用默认值。
 */
class ProfileStore(context: Context) {
    private val preferences = context.getSharedPreferences("core_profile", Context.MODE_PRIVATE)

    fun load(): CoreProfile? {
        val endpoint = preferences.getString("endpoint", null)?.takeIf(String::isNotBlank) ?: return null
        return CoreProfile(
            endpoint = endpoint,
            serverName = preferences.getString("server_name", null).orEmpty(),
            certificateDerBase64 = preferences.getString("certificate", null).orEmpty(),
            fingerprintSha256 = preferences.getString("fingerprint", null).orEmpty(),
        )
    }

    fun save(profile: CoreProfile) {
        preferences.edit()
            .putString("endpoint", profile.endpoint)
            .putString("server_name", profile.serverName)
            .putString("certificate", profile.certificateDerBase64)
            .putString("fingerprint", profile.fingerprintSha256)
            .apply()
    }

    fun clear() = preferences.edit().clear().apply()
}
