package com.adbcontrol.app.session

data class PermissionState(
    val name: String,
    val granted: Boolean,
    val updatedAtEpochMillis: Long = System.currentTimeMillis(),
) {
    init {
        require(name.isNotBlank()) { "permission name is required" }
    }
}

class PermissionRegistry {
    private val states = mutableMapOf<String, PermissionState>()

    fun update(state: PermissionState) {
        states[state.name] = state
    }

    fun isGranted(name: String): Boolean = states[name]?.granted == true

    fun snapshot(): List<PermissionState> = states.values.toList()
}
