package com.adbcontrol.companion.media

import java.io.File

interface MediaStreamSink {
    fun onStreamStarted(sessionId: String, mimeType: String, metadata: Map<String, Any?>)
    fun onConfig(sessionId: String, config: ByteArray, metadata: Map<String, Any?> = emptyMap())
    fun onChunk(sessionId: String, chunk: ByteArray, metadata: Map<String, Any?> = emptyMap())
    fun onStreamStopped(sessionId: String, metadata: Map<String, Any?> = emptyMap())
}

class SandboxFileMediaStreamSink(private val rootDir: File) : MediaStreamSink {
    private val openFiles = mutableMapOf<String, File>()

    override fun onStreamStarted(sessionId: String, mimeType: String, metadata: Map<String, Any?>) {
        val extension = when (mimeType) {
            "video/avc" -> "h264"
            "video/hevc" -> "h265"
            "audio/pcm" -> "pcm"
            else -> "bin"
        }
        val outputFile = File(rootDir, "$sessionId.$extension")
        outputFile.parentFile?.mkdirs()
        outputFile.writeBytes(ByteArray(0))
        openFiles[sessionId] = outputFile
    }

    override fun onConfig(sessionId: String, config: ByteArray, metadata: Map<String, Any?>) {
        append(sessionId, config)
    }

    override fun onChunk(sessionId: String, chunk: ByteArray, metadata: Map<String, Any?>) {
        append(sessionId, chunk)
    }

    override fun onStreamStopped(sessionId: String, metadata: Map<String, Any?>) = Unit

    fun outputPath(sessionId: String): String? = openFiles[sessionId]?.path

    fun outputSize(sessionId: String): Long = openFiles[sessionId]?.length() ?: 0L

    private fun append(sessionId: String, data: ByteArray) {
        val file = openFiles[sessionId] ?: return
        file.appendBytes(data)
    }
}
