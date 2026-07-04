package com.adbcontrol.companion.features

import android.content.Context
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import com.adbcontrol.companion.core.CompanionCommandContext
import com.adbcontrol.companion.core.CompanionCommandResult
import com.adbcontrol.companion.media.MediaStreamSink
import com.adbcontrol.companion.media.SandboxFileMediaStreamSink
import java.io.File
import java.util.UUID

class AudioRecordFeatureHandler(
    private val context: Context,
    private val streamSink: MediaStreamSink = SandboxFileMediaStreamSink(File(context.filesDir, "audio-streams")),
) : FeatureCommandHandler {
    override val capabilityIds: Set<String> = setOf("android.audio.record")
    override val operations: Set<String> = setOf("audio.record.start", "audio.record.stop")

    private val sessions = mutableMapOf<String, AudioRecordSession>()

    override fun handle(context: CompanionCommandContext): CompanionCommandResult {
        return when (context.operation) {
            "audio.record.start" -> startRecording(context)
            "audio.record.stop" -> stopRecording(context)
            else -> CompanionCommandResult.failure(
                requestId = context.requestId,
                errorCode = "COMPANION_OPERATION_NOT_SUPPORTED",
                message = "不支持的音频操作：${context.operation}",
                module = "companion.audio",
                recoverable = false,
            )
        }
    }

    private fun startRecording(command: CompanionCommandContext): CompanionCommandResult {
        val sampleRate = (command.args.intArg("sampleRate") ?: 16_000).coerceIn(8_000, 48_000)
        val maxDurationMs = (command.args.intArg("maxDurationMs") ?: 10_000).coerceIn(1_000, 60_000)
        val minBuffer = AudioRecord.getMinBufferSize(
            sampleRate,
            AudioFormat.CHANNEL_IN_MONO,
            AudioFormat.ENCODING_PCM_16BIT,
        )
        if (minBuffer <= 0) {
            return CompanionCommandResult.failure(
                requestId = command.requestId,
                errorCode = "COMPANION_AUDIO_FORMAT_UNSUPPORTED",
                message = "Android 拒绝了请求的 PCM 录音格式。",
                module = "companion.audio",
                recoverable = true,
            )
        }

        val bufferSize = minBuffer * 2
        val recorder = AudioRecord.Builder()
            .setAudioSource(MediaRecorder.AudioSource.MIC)
            .setAudioFormat(
                AudioFormat.Builder()
                    .setSampleRate(sampleRate)
                    .setChannelMask(AudioFormat.CHANNEL_IN_MONO)
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                    .build(),
            )
            .setBufferSizeInBytes(bufferSize)
            .build()
        val sessionId = UUID.randomUUID().toString()
        val outputFile = File(context.filesDir, "recordings/$sessionId.pcm")
        outputFile.parentFile?.mkdirs()
        val session = AudioRecordSession(
            sessionId = sessionId,
            recorder = recorder,
            outputFile = outputFile,
            sampleRate = sampleRate,
            bufferSize = bufferSize,
            maxDurationMs = maxDurationMs,
            streamSink = streamSink,
        )
        sessions[sessionId] = session
        session.start()

        return CompanionCommandResult.success(
            requestId = command.requestId,
            result = mapOf(
                "sessionId" to sessionId,
                "sampleRate" to sampleRate,
                "encoding" to "pcm_s16le",
                "channels" to 1,
                "path" to outputFile.relativeTo(context.filesDir).path,
                "state" to "streaming",
            ),
        )
    }

    private fun stopRecording(command: CompanionCommandContext): CompanionCommandResult {
        val sessionId = command.args.stringArg("sessionId")
            ?: return CompanionCommandResult.failure(
                requestId = command.requestId,
                errorCode = "COMPANION_PARAMS_INVALID",
                message = "audio.record.stop 需要 args.sessionId 参数。",
                module = "companion.audio",
                recoverable = false,
            )
        val session = sessions.remove(sessionId)
            ?: return CompanionCommandResult.failure(
                requestId = command.requestId,
                errorCode = "COMPANION_AUDIO_SESSION_NOT_FOUND",
                message = "录音会话未处于运行状态：$sessionId",
                module = "companion.audio",
                recoverable = true,
            )

        session.stop()
        return CompanionCommandResult.success(
            requestId = command.requestId,
            result = mapOf(
                "sessionId" to sessionId,
                "path" to session.outputFile.relativeTo(context.filesDir).path,
                "sizeBytes" to session.outputFile.length(),
                "sampleRate" to session.sampleRate,
                "encoding" to "pcm_s16le",
                "state" to "stopped",
            ),
        )
    }

    private class AudioRecordSession(
        val sessionId: String,
        val recorder: AudioRecord,
        val outputFile: File,
        val sampleRate: Int,
        private val bufferSize: Int,
        private val maxDurationMs: Int,
        private val streamSink: MediaStreamSink,
    ) {
        @Volatile
        private var active = false
        private var worker: Thread? = null

        fun start() {
            active = true
            recorder.startRecording()
            streamSink.onStreamStarted(
                sessionId = sessionId,
                mimeType = "audio/pcm",
                metadata = mapOf(
                    "sampleRate" to sampleRate,
                    "channels" to 1,
                    "encoding" to "pcm_s16le",
                ),
            )
            worker = Thread({
                val buffer = ByteArray(bufferSize)
                val deadline = System.currentTimeMillis() + maxDurationMs
                outputFile.outputStream().use { output ->
                    while (active && System.currentTimeMillis() < deadline) {
                        val read = recorder.read(buffer, 0, buffer.size)
                        if (read > 0) {
                            val chunk = buffer.copyOf(read)
                            output.write(chunk)
                            streamSink.onChunk(
                                sessionId = sessionId,
                                chunk = chunk,
                                metadata = mapOf("sampleRate" to sampleRate),
                            )
                        }
                    }
                }
                active = false
                streamSink.onStreamStopped(sessionId)
                recorder.stop()
                recorder.release()
            }, "adbcontrol-audio-$sessionId").apply { start() }
        }

        fun stop() {
            active = false
            worker?.join(1_000)
            runCatching { streamSink.onStreamStopped(sessionId) }
            runCatching { recorder.stop() }
            runCatching { recorder.release() }
        }
    }
}
