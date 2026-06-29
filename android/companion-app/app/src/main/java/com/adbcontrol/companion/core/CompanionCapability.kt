package com.adbcontrol.companion.core

data class CompanionCapability(
    val id: String,
    val title: String,
    val androidPermissions: List<String>,
    val specialGrants: List<String>,
    val sensitivity: CapabilitySensitivity,
    val operations: List<String>,
    val requiresUserConsent: Boolean,
)

enum class CapabilitySensitivity {
    LOW,
    MEDIUM,
    HIGH,
    CRITICAL,
}

data class PermissionEvaluation(
    val capabilityId: String,
    val granted: Boolean,
    val missingPermissions: List<String>,
    val missingSpecialGrants: List<String>,
)
