package com.adbcontrol.app.media

interface EncoderSession : AutoCloseable {
    val running: Boolean
    fun start(sink: EncodedFrameSink)
    override fun close()
}
