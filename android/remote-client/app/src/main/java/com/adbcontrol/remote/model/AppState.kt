package com.adbcontrol.remote.model

sealed interface AppState {
    data object NeedsCore : AppState
    data object Connecting : AppState
    data class NeedsLogin(val profile: CoreProfile) : AppState
    data class MustChangePassword(val profile: CoreProfile, val session: Session) : AppState
    data class Ready(val profile: CoreProfile, val session: Session) : AppState
    data class Failed(val profile: CoreProfile?, val error: AppError) : AppState
}

object AppStateTransitions {
    fun canTransition(from: AppState, to: AppState): Boolean = when (from) {
        AppState.NeedsCore -> to is AppState.Connecting || to is AppState.Failed
        AppState.Connecting -> to is AppState.NeedsLogin || to is AppState.Failed
        is AppState.NeedsLogin -> to is AppState.MustChangePassword || to is AppState.Ready || to is AppState.Failed
        is AppState.MustChangePassword -> to is AppState.NeedsLogin || to is AppState.Failed
        is AppState.Ready -> to is AppState.NeedsLogin || to is AppState.Failed
        is AppState.Failed -> to is AppState.Connecting || to is AppState.NeedsCore || to is AppState.NeedsLogin
    }
}
