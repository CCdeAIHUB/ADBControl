package com.adbcontrol.app.media

class FramePipeline(
    private val encoderSession: EncoderSession,
    private val sink: EncodedFrameSink,
) : AutoCloseable {
    var running: Boolean = false
        private set

    fun start() {
        check(!running) { "FramePipeline is already running" }
        encoderSession.start(sink)
        running = true
    }

    override fun close() {
        if (running) {
            encoderSession.close()
            running = false
        }
    }
}
