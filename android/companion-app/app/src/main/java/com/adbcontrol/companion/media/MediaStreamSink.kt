package com.adbcontrol.companion.media

import java.io.File
import java.io.FileOutputStream

interface MediaStreamSink {
    fun onStreamStarted(sessionId: String, mimeType: String, metadata: Map<String, Any?>)
    fun onConfig(sessionId: String, config: ByteArray, metadata: Map<String, Any?> = emptyMap())
    fun onChunk(sessionId: String, chunk: ByteArray, metadata: Map<String, Any?> = emptyMap())
    fun onStreamStopped(sessionId: String, metadata: Map<String, Any?> = emptyMap())
    fun recommendedSegmentDurationMs(sessionId: String, fallbackMs: Int): Int = fallbackMs
}

class SandboxFileMediaStreamSink(private val rootDir: File) : MediaStreamSink {
    private val openFiles = mutableMapOf<String, File>()
    private val outputStreams = mutableMapOf<String, FileOutputStream>()

    @Synchronized
    override fun onStreamStarted(sessionId: String, mimeType: String, metadata: Map<String, Any?>) {
        val extension = when (mimeType) {
            "video/avc" -> "h264"
            "video/hevc" -> "h265"
            "audio/pcm" -> "pcm"
            else -> "bin"
        }
        val outputFile = File(rootDir, "$sessionId.$extension")
        outputFile.parentFile?.mkdirs()
        outputStreams.remove(sessionId)?.close()
        outputStreams[sessionId] = FileOutputStream(outputFile, false)
        openFiles[sessionId] = outputFile
    }

    @Synchronized
    override fun onConfig(sessionId: String, config: ByteArray, metadata: Map<String, Any?>) {
        append(sessionId, config)
    }

    @Synchronized
    override fun onChunk(sessionId: String, chunk: ByteArray, metadata: Map<String, Any?>) {
        // The legacy file/ADB transport loses MediaCodec output-buffer boundaries. Append an
        // Access Unit Delimiter so the desktop Annex-B parser can release each frame immediately.
        append(sessionId, chunk)
        append(sessionId, ACCESS_UNIT_DELIMITER)
    }

    @Synchronized
    override fun onStreamStopped(sessionId: String, metadata: Map<String, Any?>) {
        outputStreams.remove(sessionId)?.close()
    }

    fun outputPath(sessionId: String): String? = openFiles[sessionId]?.path

    fun outputSize(sessionId: String): Long = openFiles[sessionId]?.length() ?: 0L

    private fun append(sessionId: String, data: ByteArray) {
        // FileOutputStream writes directly without userspace buffering. Keeping one descriptor open
        // avoids reopening the private stream file for every encoded frame while tail reads it live.
        outputStreams[sessionId]?.write(data)
    }

    companion object {
        private val ACCESS_UNIT_DELIMITER = byteArrayOf(0, 0, 0, 1, 0x09, 0xf0.toByte())
    }
}
