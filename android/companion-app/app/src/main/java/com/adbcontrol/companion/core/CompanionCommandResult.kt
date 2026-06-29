package com.adbcontrol.companion.core

data class CompanionCommandContext(
    val requestId: String,
    val capabilityId: String,
    val operation: String,
    val args: Map<String, Any?> = emptyMap(),
)

data class CompanionCommandResult(
    val requestId: String,
    val ok: Boolean,
    val result: Map<String, Any?> = emptyMap(),
    val error: CompanionCommandError? = null,
) {
    fun toPayload(): Map<String, Any?> {
        return if (ok) {
            mapOf(
                "requestId" to requestId,
                "ok" to true,
                "result" to result,
            )
        } else {
            mapOf(
                "requestId" to requestId,
                "ok" to false,
                "error" to error?.toPayload(),
            )
        }
    }

    companion object {
        fun success(requestId: String, result: Map<String, Any?> = emptyMap()): CompanionCommandResult {
            return CompanionCommandResult(
                requestId = requestId,
                ok = true,
                result = result,
            )
        }

        fun failure(
            requestId: String,
            errorCode: String,
            message: String,
            module: String,
            recoverable: Boolean,
            suggestion: String? = null,
        ): CompanionCommandResult {
            return CompanionCommandResult(
                requestId = requestId,
                ok = false,
                error = CompanionCommandError(
                    errorCode = errorCode,
                    message = message,
                    module = module,
                    recoverable = recoverable,
                    suggestion = suggestion,
                ),
            )
        }
    }
}

data class CompanionCommandError(
    val errorCode: String,
    val message: String,
    val module: String,
    val recoverable: Boolean,
    val suggestion: String? = null,
) {
    fun toPayload(): Map<String, Any?> {
        return mapOf(
            "errorCode" to errorCode,
            "message" to message,
            "module" to module,
            "recoverable" to recoverable,
            "suggestion" to suggestion,
        )
    }
}
