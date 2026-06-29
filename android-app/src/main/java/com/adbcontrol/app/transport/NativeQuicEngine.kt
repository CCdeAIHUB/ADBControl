package com.adbcontrol.app.transport

class NativeQuicEngine(alpn: String = "adbcontrol") : AutoCloseable {
    private var handle: Long = nativeCreateClient(alpn)

    fun connect(host: String, port: Int) {
        check(handle != 0L) { "NativeQuicEngine is closed" }
        nativeConnect(handle, host, port)
    }

    fun sendControlMessage(bytes: ByteArray) {
        check(handle != 0L) { "NativeQuicEngine is closed" }
        nativeSendControlMessage(handle, bytes)
    }

    fun sendStreamChunk(streamId: String, bytes: ByteArray, endOfStream: Boolean = false) {
        check(handle != 0L) { "NativeQuicEngine is closed" }
        nativeSendStreamChunk(handle, streamId, bytes, endOfStream)
    }

    override fun close() {
        val current = handle
        handle = 0
        if (current != 0L) nativeClose(current)
    }

    private external fun nativeCreateClient(alpn: String): Long
    private external fun nativeConnect(handle: Long, host: String, port: Int)
    private external fun nativeSendControlMessage(handle: Long, bytes: ByteArray)
    private external fun nativeSendStreamChunk(handle: Long, streamId: String, bytes: ByteArray, endOfStream: Boolean)
    private external fun nativeClose(handle: Long)

    companion object {
        init {
            System.loadLibrary("adbcontrol_quic")
        }
    }
}
