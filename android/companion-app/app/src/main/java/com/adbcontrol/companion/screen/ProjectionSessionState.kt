package com.adbcontrol.companion.screen

data class ProjectionOptions(
    val sessionId: String,
    val width: Int,
    val height: Int,
    val bitrate: Int,
    val frameRate: Int,
) {
    companion object {
        fun fromArgs(args: Map<String, Any?>, fallbackWidth: Int, fallbackHeight: Int): ProjectionOptions {
            var width = args.intValue("width", fallbackWidth).coerceIn(240, 3840)
            var height = args.intValue("height", fallbackHeight).coerceIn(240, 3840)
            if ((fallbackWidth < fallbackHeight) != (width < height)) {
                val swap = width
                width = height
                height = swap
            }
            return ProjectionOptions(
                sessionId = args["sessionId"]?.toString()?.takeIf { it.isNotBlank() }
                    ?: java.util.UUID.randomUUID().toString(),
                width = width and -2,
                height = height and -2,
                bitrate = args.intValue("bitrate", 4_000_000).coerceIn(500_000, 30_000_000),
                frameRate = args.intValue("frameRate", 60).coerceIn(15, 120),
            )
        }

        private fun Map<String, Any?>.intValue(name: String, fallback: Int): Int {
            return when (val value = this[name]) {
                is Number -> value.toInt()
                is String -> value.toIntOrNull() ?: fallback
                else -> fallback
            }
        }
    }
}

object ProjectionSessionState {
    enum class State { IDLE, AWAITING_CONSENT, STARTING, STREAMING, STOPPED, FAILED }

    @Volatile var state: State = State.IDLE
        private set
    @Volatile var sessionId: String? = null
        private set
    @Volatile var error: String? = null
        private set

    fun update(newState: State, newSessionId: String?, newError: String? = null) {
        state = newState
        sessionId = newSessionId
        error = newError
    }
}
