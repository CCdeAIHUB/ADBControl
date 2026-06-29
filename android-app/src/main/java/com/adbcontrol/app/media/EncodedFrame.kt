package com.adbcontrol.app.media

data class EncodedFrame(
    val bytes: ByteArray,
    val timestampUs: Long,
    val keyFrame: Boolean,
    val endOfStream: Boolean = false,
) {
    init {
        require(timestampUs >= 0) { "timestampUs must be non-negative" }
    }

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is EncodedFrame) return false
        return bytes.contentEquals(other.bytes) &&
            timestampUs == other.timestampUs &&
            keyFrame == other.keyFrame &&
            endOfStream == other.endOfStream
    }

    override fun hashCode(): Int {
        var result = bytes.contentHashCode()
        result = 31 * result + timestampUs.hashCode()
        result = 31 * result + keyFrame.hashCode()
        result = 31 * result + endOfStream.hashCode()
        return result
    }
}

fun interface EncodedFrameSink {
    fun onFrame(frame: EncodedFrame)
}
