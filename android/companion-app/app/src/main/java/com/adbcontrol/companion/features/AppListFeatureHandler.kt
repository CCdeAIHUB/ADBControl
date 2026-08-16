package com.adbcontrol.companion.features

import android.content.Context
import android.content.pm.ApplicationInfo
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.graphics.Canvas
import android.os.Build
import android.util.Base64
import com.adbcontrol.companion.core.CompanionCommandContext
import com.adbcontrol.companion.core.CompanionCommandResult
import java.io.ByteArrayOutputStream

class AppListFeatureHandler(private val context: Context) : FeatureCommandHandler {
    override val capabilityIds: Set<String> = setOf("android.app.list")
    override val operations: Set<String> = setOf("app.list")

    override fun handle(context: CompanionCommandContext): CompanionCommandResult {
        val includeIcons = context.args.booleanArg("includeIcons")
        val requestedLimit = (context.args.intArg("limit") ?: 200).coerceIn(1, 5_000)
        val limit = if (includeIcons) requestedLimit.coerceAtMost(MAX_ICON_PAGE_SIZE) else requestedLimit
        val offset = (context.args.intArg("offset") ?: 0).coerceAtLeast(0)
        val includeSystem = context.args.booleanArg("includeSystem")
        val iconSizePx = (context.args.intArg("iconSizePx") ?: DEFAULT_ICON_SIZE_PX)
            .coerceIn(MIN_ICON_SIZE_PX, MAX_ICON_SIZE_PX)
        val requestedPackages = context.args.stringListArg("packageNames")
            .filter { it.isNotBlank() }
            .distinct()
        if (requestedPackages.size > MAX_EXPLICIT_PACKAGE_COUNT) {
            return CompanionCommandResult.failure(
                requestId = context.requestId,
                errorCode = "COMPANION_INVALID_ARGUMENT",
                message = "packageNames 每次最多允许 $MAX_EXPLICIT_PACKAGE_COUNT 项。",
                module = "companion.app-list",
                recoverable = true,
                suggestion = "请在桌面端分页查询应用元数据。",
            )
        }

        val packageManager = this.context.packageManager
        val lookup = if (requestedPackages.isEmpty()) {
            PackageLookup(installedApplications(packageManager), emptyList())
        } else {
            lookupApplications(packageManager, requestedPackages)
        }
        val visibleApplications = lookup.applications
            .asSequence()
            .filter { includeSystem || !it.isSystemApp() }
            .toList()
        val applications = visibleApplications
            .asSequence()
            .drop(offset)
            .take(limit)
            .map { app -> app.toPayload(packageManager, includeIcons, iconSizePx) }
            .toList()

        return CompanionCommandResult.success(
            requestId = context.requestId,
            result = mapOf(
                "apps" to applications,
                "count" to applications.size,
                "total" to visibleApplications.size,
                "offset" to offset,
                "includeSystem" to includeSystem,
                "includeIcons" to includeIcons,
                "limit" to limit,
                "queryMode" to if (requestedPackages.isEmpty()) "enumerated" else "explicit",
                "unresolvedPackages" to lookup.unresolvedPackages,
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

    private fun lookupApplications(
        packageManager: PackageManager,
        packageNames: List<String>,
    ): PackageLookup {
        val applications = mutableListOf<ApplicationInfo>()
        val unresolved = mutableListOf<String>()
        packageNames.forEach { packageName ->
            val application = try {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                    packageManager.getApplicationInfo(
                        packageName,
                        PackageManager.ApplicationInfoFlags.of(0),
                    )
                } else {
                    @Suppress("DEPRECATION")
                    packageManager.getApplicationInfo(packageName, 0)
                }
            } catch (notFound: PackageManager.NameNotFoundException) {
                null
            } catch (denied: SecurityException) {
                null
            }
            if (application == null) {
                unresolved += packageName
            } else {
                applications.add(application)
            }
        }
        return PackageLookup(applications, unresolved)
    }

    private fun ApplicationInfo.isSystemApp(): Boolean {
        return flags and ApplicationInfo.FLAG_SYSTEM != 0
    }

    private fun ApplicationInfo.toPayload(
        packageManager: PackageManager,
        includeIcons: Boolean,
        iconSizePx: Int,
    ): Map<String, Any?> {
        val payload = linkedMapOf<String, Any?>(
            "packageName" to packageName,
            "label" to loadLabel(packageManager).toString(),
            "enabled" to enabled,
            "system" to isSystemApp(),
            "sourceDir" to sourceDir,
        )
        if (includeIcons) {
            val iconResult = renderIconPng(packageManager, iconSizePx)
            payload["iconPngBase64"] = iconResult.getOrNull()?.let { bytes ->
                Base64.encodeToString(bytes, Base64.NO_WRAP)
            }
            if (iconResult.isFailure) {
                payload["iconErrorCode"] = "APP_ICON_RENDER_FAILED"
            }
        }
        return payload
    }

    private fun ApplicationInfo.renderIconPng(
        packageManager: PackageManager,
        iconSizePx: Int,
    ): Result<ByteArray> = runCatching {
        val drawable = loadIcon(packageManager)
        val bitmap = Bitmap.createBitmap(iconSizePx, iconSizePx, Bitmap.Config.ARGB_8888)
        try {
            val canvas = Canvas(bitmap)
            drawable.setBounds(0, 0, iconSizePx, iconSizePx)
            drawable.draw(canvas)
            ByteArrayOutputStream().use { output ->
                check(bitmap.compress(Bitmap.CompressFormat.PNG, 100, output))
                output.toByteArray()
            }
        } finally {
            bitmap.recycle()
        }
    }

    private data class PackageLookup(
        val applications: List<ApplicationInfo>,
        val unresolvedPackages: List<String>,
    )

    companion object {
        private const val MAX_ICON_PAGE_SIZE = 64
        private const val MAX_EXPLICIT_PACKAGE_COUNT = 64
        private const val DEFAULT_ICON_SIZE_PX = 48
        private const val MIN_ICON_SIZE_PX = 32
        private const val MAX_ICON_SIZE_PX = 96
    }
}
