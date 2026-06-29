package com.adbcontrol.companion.features

import android.content.Context
import android.content.pm.ApplicationInfo
import android.content.pm.PackageManager
import android.os.Build
import com.adbcontrol.companion.core.CompanionCommandContext
import com.adbcontrol.companion.core.CompanionCommandResult

class AppListFeatureHandler(private val context: Context) : FeatureCommandHandler {
    override val capabilityIds: Set<String> = setOf("android.app.list")
    override val operations: Set<String> = setOf("app.list")

    override fun handle(context: CompanionCommandContext): CompanionCommandResult {
        val limit = (context.args.intArg("limit") ?: 200).coerceIn(1, 1000)
        val includeSystem = context.args.booleanArg("includeSystem")
        val packageManager = this.context.packageManager
        val applications = installedApplications(packageManager)
            .asSequence()
            .filter { includeSystem || !it.isSystemApp() }
            .take(limit)
            .map { app -> app.toPayload(packageManager) }
            .toList()

        return CompanionCommandResult.success(
            requestId = context.requestId,
            result = mapOf(
                "apps" to applications,
                "count" to applications.size,
                "includeSystem" to includeSystem,
                "limit" to limit,
            ),
        )
    }

    private fun installedApplications(packageManager: PackageManager): List<ApplicationInfo> {
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            packageManager.getInstalledApplications(PackageManager.ApplicationInfoFlags.of(0))
        } else {
            @Suppress("DEPRECATION")
            packageManager.getInstalledApplications(0)
        }
    }

    private fun ApplicationInfo.isSystemApp(): Boolean {
        return flags and ApplicationInfo.FLAG_SYSTEM != 0
    }

    private fun ApplicationInfo.toPayload(packageManager: PackageManager): Map<String, Any?> {
        return mapOf(
            "packageName" to packageName,
            "label" to loadLabel(packageManager).toString(),
            "enabled" to enabled,
            "system" to isSystemApp(),
            "sourceDir" to sourceDir,
        )
    }
}
