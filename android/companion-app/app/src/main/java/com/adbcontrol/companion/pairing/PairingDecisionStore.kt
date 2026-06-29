package com.adbcontrol.companion.pairing

import android.content.Context

class PairingDecisionStore(context: Context) {
    private val preferences = context.getSharedPreferences(PREFERENCES_NAME, Context.MODE_PRIVATE)

    fun saveDecision(decision: PairingDecision) {
        preferences.edit()
            .putString("${decision.pairingId}.state", decision.state.name)
            .putString("${decision.pairingId}.coreName", decision.coreName)
            .putString("${decision.pairingId}.deviceId", decision.deviceId)
            .putString("${decision.pairingId}.certificateFingerprintSha256", decision.certificateFingerprintSha256)
            .putLong("${decision.pairingId}.decidedAtUnixMs", decision.decidedAtUnixMs)
            .apply()
    }

    fun getState(pairingId: String): PairingDecisionState? {
        return preferences.getString("$pairingId.state", null)?.let { state ->
            runCatching { PairingDecisionState.valueOf(state) }.getOrNull()
        }
    }

    companion object {
        private const val PREFERENCES_NAME = "adbcontrol_pairing_decisions"
    }
}

data class PairingDecision(
    val pairingId: String,
    val coreName: String,
    val deviceId: String,
    val certificateFingerprintSha256: String,
    val state: PairingDecisionState,
    val decidedAtUnixMs: Long,
)

enum class PairingDecisionState {
    APPROVED,
    REJECTED,
}
