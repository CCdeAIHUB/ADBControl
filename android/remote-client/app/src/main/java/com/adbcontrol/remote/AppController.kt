package com.adbcontrol.remote

import com.adbcontrol.remote.data.ProfileStore
import com.adbcontrol.remote.data.RemoteRepository
import com.adbcontrol.remote.model.*
import com.adbcontrol.remote.transport.RemoteTransport
import java.util.concurrent.ExecutorService

class AppController(
    private val store: ProfileStore,
    private val transport: RemoteTransport,
    val repository: RemoteRepository,
    private val executor: ExecutorService,
    private val onState: (AppState) -> Unit,
) {
    var state: AppState = AppState.NeedsCore
        private set

    fun start() {
        val profile = store.load() ?: return transition(AppState.NeedsCore)
        connect(profile)
    }

    fun connect(profile: CoreProfile) {
        transition(AppState.Connecting)
        executor.execute {
            when (val result = transport.connect(profile)) {
                is RemoteResult.Failure -> transition(AppState.Failed(profile, result.error))
                is RemoteResult.Success -> {
                    val pinned = profile.fingerprintSha256.lowercase().replace(":", "")
                    if (pinned.isNotBlank() && pinned != result.value.lowercase()) {
                        transport.close()
                        transition(AppState.Failed(profile, AppError(
                            "REMOTE_CERTIFICATE_FINGERPRINT_MISMATCH",
                            "Core 证书指纹与已确认的指纹不一致",
                            "remote.security", false,
                        )))
                    } else {
                        val confirmed = profile.copy(fingerprintSha256 = result.value)
                        store.save(confirmed)
                        transition(AppState.NeedsLogin(confirmed))
                    }
                }
            }
        }
    }

    fun login(profile: CoreProfile, username: String, password: String) {
        executor.execute {
            when (val result = repository.login(username.trim(), password)) {
                is RemoteResult.Failure -> transition(AppState.Failed(profile, result.error))
                is RemoteResult.Success -> transition(
                    if (result.value.passwordChangeRequired) AppState.MustChangePassword(profile, result.value)
                    else AppState.Ready(profile, result.value),
                )
            }
        }
    }

    fun changePassword(profile: CoreProfile, session: Session, current: String, replacement: String) {
        executor.execute {
            when (val result = repository.changePassword(current, replacement)) {
                is RemoteResult.Failure -> transition(AppState.Failed(profile, result.error))
                is RemoteResult.Success -> transition(AppState.NeedsLogin(profile))
            }
        }
    }

    fun resetCore() {
        transport.close()
        store.clear()
        transition(AppState.NeedsCore)
    }

    fun logout(profile: CoreProfile) {
        executor.execute {
            repository.logout()
            transition(AppState.NeedsLogin(profile))
        }
    }

    fun requireLogin(profile: CoreProfile) {
        // Session expiry is an explicit state transition. Rendering a login view alone would
        // leave the controller in Ready and make the next successful login an illegal transition.
        transition(AppState.NeedsLogin(profile))
    }

    private fun transition(next: AppState) {
        if (state != next && !AppStateTransitions.canTransition(state, next) && next !is AppState.NeedsCore) {
            val profile = when (next) {
                is AppState.Failed -> next.profile
                else -> null
            }
            state = AppState.Failed(profile, AppError("APP_STATE_TRANSITION_INVALID", "客户端状态流转异常", "app.state", false))
        } else state = next
        onState(state)
    }
}
