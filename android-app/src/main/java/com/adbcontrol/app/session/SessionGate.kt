package com.adbcontrol.app.session

class SessionGate(
    private val permissionRegistry: PermissionRegistry,
) {
    fun requirePermission(permissionName: String) {
        check(permissionRegistry.isGranted(permissionName)) {
            "Required permission is not granted: $permissionName"
        }
    }
}
