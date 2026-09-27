package com.adbcontrol.remote.transport

import com.adbcontrol.remote.model.AppError
import com.adbcontrol.remote.model.CoreProfile
import com.adbcontrol.remote.model.RemoteResult

interface RemoteTransport : AutoCloseable {
    fun connect(profile: CoreProfile): RemoteResult<String>
    fun request(json: String, timeoutMs: Long = 15_000): RemoteResult<String>
    val connected: Boolean
}

internal fun transportFailure(code: String, message: String, recoverable: Boolean = true) =
    RemoteResult.Failure(AppError(code, message, "remote.transport", recoverable))
