package com.adbcontrol.remote.data.io

import android.content.ContentValues
import android.content.Context
import android.provider.MediaStore
import android.os.Environment

/**
 * 下载保存：写入公共 Downloads/ADBControl 目录（MediaStore，API 29+ 无需存储权限）。
 * 对应桌面端“下载到本地”的能力落点。
 */
object DownloadsWriter {

    /** 返回显示用路径；失败返回 null（调用方呈现明确错误）。 */
    fun save(context: Context, fileName: String, bytes: ByteArray): String? {
        return try {
            val safeName = fileName.replace(Regex("[\\\\/:*?\"<>|]"), "_").ifBlank { "download.bin" }
            val values = ContentValues().apply {
                put(MediaStore.Downloads.DISPLAY_NAME, safeName)
                put(MediaStore.Downloads.MIME_TYPE, "application/octet-stream")
                put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS + "/ADBControl")
                put(MediaStore.Downloads.IS_PENDING, 1)
            }
            val resolver = context.contentResolver
            val uri = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values) ?: return null
            resolver.openOutputStream(uri)?.use { it.write(bytes) } ?: return null
            val done = ContentValues().apply { put(MediaStore.Downloads.IS_PENDING, 0) }
            resolver.update(uri, done, null, null)
            "Download/ADBControl/$safeName"
        } catch (_: Exception) {
            null
        }
    }
}
