package com.adbcontrol.companion.quic

object NativeQuicEngineProvider {
    @Volatile
    private var factory: (() -> NativeQuicEngine)? = null

    fun installFactory(factory: () -> NativeQuicEngine) {
        this.factory = factory
    }

    fun clearFactory() {
        factory = null
    }

    fun create(): NativeQuicEngine {
        return factory?.invoke() ?: UnavailableNativeQuicEngine()
    }
}
