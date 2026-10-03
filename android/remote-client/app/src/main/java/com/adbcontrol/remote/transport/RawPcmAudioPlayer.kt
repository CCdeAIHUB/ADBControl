package com.adbcontrol.remote.transport

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioTrack

/** Streaming scrcpy raw PCM sink (48 kHz, signed 16-bit little-endian stereo). */
class RawPcmAudioPlayer {
    private var track: AudioTrack? = null

    @Synchronized fun start() {
        if (track != null) return
        val minimum = AudioTrack.getMinBufferSize(48_000, AudioFormat.CHANNEL_OUT_STEREO, AudioFormat.ENCODING_PCM_16BIT)
        track = AudioTrack.Builder()
            .setAudioAttributes(AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_MEDIA).setContentType(AudioAttributes.CONTENT_TYPE_MOVIE).build())
            .setAudioFormat(AudioFormat.Builder().setSampleRate(48_000).setChannelMask(AudioFormat.CHANNEL_OUT_STEREO).setEncoding(AudioFormat.ENCODING_PCM_16BIT).build())
            .setBufferSizeInBytes(maxOf(minimum, 48_000))
            .setTransferMode(AudioTrack.MODE_STREAM)
            .build().also { it.play() }
    }

    @Synchronized fun write(bytes: ByteArray) {
        val output = track ?: return
        output.write(bytes, 0, bytes.size, AudioTrack.WRITE_NON_BLOCKING)
    }

    @Synchronized fun close() {
        track?.let { runCatching { it.stop() }; it.release() }
        track = null
    }
}
