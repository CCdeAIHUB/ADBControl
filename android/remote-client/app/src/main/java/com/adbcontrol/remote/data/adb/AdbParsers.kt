package com.adbcontrol.remote.data.adb

/**
 * 锁屏状态两段式查询：命令与解析规则逐条对齐桌面端 `AdbLockStateSource.cs` + `DeviceLockService.ParseState`。
 * 背景（桌面端 2026-08-31 事故）：Samsung OneUI 全量 `dumpsys window` 耗时约 3 秒，
 * 会吃掉整个查询预算并把投屏状态拖成 Unknown；因此先用 policy/power/trust 三条轻量命令，
 * 只有在第一段没有任何信号时才降级到全量 `dumpsys window`。
 */
object LockStateParser {

    /** 第一段（快速查询）命令——每条都是独立 shell 命令，grep 语义与桌面端一致。 */
    val primaryCommands = listOf(
        "dumpsys window policy 2>/dev/null | grep -i -E 'keyguard|lockscreen|screenState|interactiveState|showing|mIsShowing' || true",
        "dumpsys power 2>/dev/null | grep -i -E 'mWakefulness=|mScreenOn=|Display Power: state=' || true",
        "dumpsys trust 2>/dev/null | grep -i -E '\\(current\\).*deviceLocked=' || true",
    )

    /** 第二段（OneUI 等慢设备降级）——仅当第一段无信号时执行。 */
    val fallbackCommands = listOf(
        "dumpsys window 2>/dev/null | grep -i -E 'mKeyguardShowing|mShowingLockscreen|isStatusBarKeyguard|mDreamingLockscreen|keyguardShowing' || true",
    )

    /** 查询结果：locked / unlocked / unknown。 */
    sealed interface LockState {
        data object Locked : LockState
        data object Unlocked : LockState
        data object Unknown : LockState
    }

    fun parse(primaryOutput: String): LockState = parseState(primaryOutput)

    fun parseCombined(primaryOutput: String, fallbackOutput: String): LockState =
        parseState(primaryOutput + "\n" + fallbackOutput)

    private fun parseState(output: String): LockState {
        val text = output.lowercase()
        // 规则 1：灭屏 = 锁定（桌面端 DeviceLockService.ParseState 同款信号集合）。
        val screenOff = listOf(
            "screen_state_off", "screen.state.off",
            "interactive_state_sleep", "interactive.state.sleep",
            "mwakefulness=asleep", "mwakefulness=dozing",
            "mscreenon=false", "display power: state=off",
        ).any { text.contains(it) }
        if (screenOff) return LockState.Locked

        // 规则 2：任意 keyguard 布尔信号为 true → 锁定；全部为 false → 未锁；无信号 → Unknown。
        val trueSignals = listOf(
            Regex("mkeyguardshowing\\s*=\\s*true"),
            Regex("mshowinglockscreen\\s*=\\s*true"),
            Regex("isstatusbarkeyguard\\s*=\\s*true"),
            Regex("mdreaminglockscreen\\s*=\\s*true"),
            Regex("keyguardshowing\\s*=\\s*true"),
            Regex("misshowing\\s*=\\s*true"),
            Regex("iskeyguardlocked\\s*=\\s*true"),
            Regex("isdevicelocked\\s*=\\s*true"),
            Regex("showing\\s*=\\s*true"),
            Regex("showingandnotoccluded\\s*=\\s*true"),
            Regex("\\(current\\)[^\\r\\n]*devicelocked\\s*=\\s*true"),
        )
        val falseSignals = listOf(
            Regex("mkeyguardshowing\\s*=\\s*false"),
            Regex("mshowinglockscreen\\s*=\\s*false"),
            Regex("isstatusbarkeyguard\\s*=\\s*false"),
            Regex("mdreaminglockscreen\\s*=\\s*false"),
            Regex("keyguardshowing\\s*=\\s*false"),
            Regex("misshowing\\s*=\\s*false"),
            Regex("iskeyguardlocked\\s*=\\s*false"),
            Regex("isdevicelocked\\s*=\\s*false"),
            Regex("\\(current\\)[^\\r\\n]*devicelocked\\s*=\\s*false"),
        )
        return when {
            trueSignals.any { it.containsMatchIn(text) } -> LockState.Locked
            falseSignals.any { it.containsMatchIn(text) } -> LockState.Unlocked
            else -> LockState.Unknown
        }
    }
}

/** `wm size` 解析：优先 Override size（与桌面端 WmSize 语义一致），其次 Physical size。 */
object WmSizeParser {
    data class ScreenSize(val width: Int, val height: Int)

    fun parse(output: String): ScreenSize? {
        val override = Regex("Override size:\\s*(\\d+)x(\\d+)").find(output)
        val physical = Regex("Physical size:\\s*(\\d+)x(\\d+)").find(output)
        val match = override ?: physical ?: return null
        val width = match.groupValues[1].toIntOrNull() ?: return null
        val height = match.groupValues[2].toIntOrNull() ?: return null
        if (width <= 0 || height <= 0) return null
        return ScreenSize(width, height)
    }
}

/** `ls -la -p <path>` 解析：与桌面端文件管理器一致（9 列、符号链接在 " -> " 处截断）。 */
object LsParser {
    data class Entry(
        val name: String,
        val isDirectory: Boolean,
        val isSymlink: Boolean,
        val sizeBytes: Long,
        val modifiedText: String,
        val permissions: String,
    )

    fun parse(output: String): List<Entry> = output.lineSequence()
        .filter { line -> line.isNotBlank() && !line.startsWith("total ") }
        .mapNotNull(::parseLine)
        .filter { it.name != "." && it.name != ".." }
        .sortedWith(compareByDescending<Entry> { it.isDirectory }.thenBy { it.name.lowercase() })
        .toList()

    private fun parseLine(line: String): Entry? {
        val columns = line.trim().split(Regex("\\s+"))
        if (columns.size < 7) return null
        val permissions = columns[0]
        if (permissions.length < 2) return null
        val isDirectory = permissions.startsWith("d")
        val isSymlink = permissions.startsWith("l")
        // toybox 版本之间是否输出硬链接数并不一致，以 owner/group/size 的相对位置解析。
        val ownerIndex = if (columns.getOrNull(1)?.toLongOrNull() != null) 2 else 1
        val sizeIndex = ownerIndex + 2
        val modifiedIndex = sizeIndex + 1
        if (columns.size <= modifiedIndex + 1) return null
        val size = columns[sizeIndex].toLongOrNull() ?: 0L
        val modifiedCount = when {
            Regex("\\d{4}-\\d{2}-\\d{2}").matches(columns[modifiedIndex]) && columns.getOrNull(modifiedIndex + 1)?.contains(':') == true -> 2
            Regex("\\d{4}-\\d{2}-\\d{2}").matches(columns[modifiedIndex]) -> 1
            else -> 3
        }
        val nameIndex = modifiedIndex + modifiedCount
        if (columns.size <= nameIndex) return null
        val modified = columns.subList(modifiedIndex, nameIndex).joinToString(" ")
        var name = columns.subList(nameIndex, columns.size).joinToString(" ")
        val symlinkIndex = name.indexOf(" -> ")
        if (symlinkIndex > 0) name = name.substring(0, symlinkIndex)
        name = name.trim().removeSuffix("/")
        if (name.isBlank()) return null
        return Entry(name, isDirectory, isSymlink, size, modified, permissions)
    }
}

/** `pm list packages` / `dumpsys package` 解析：对齐桌面端 `DevicePackageCatalogService` + `PackageLabelParser`。 */
object PackageCatalogParser {
    data class PackageEntry(
        val packageName: String,
        val label: String = "",
        val isSystem: Boolean = false,
        val versionName: String = "",
        val apkPath: String = "",
    )

    fun parsePackageList(output: String, system: Boolean): List<PackageEntry> = output.lineSequence()
        .mapNotNull { line ->
            val name = line.trim().removePrefix("package:").trim()
            if (name.isBlank() || name.contains(' ')) null else PackageEntry(packageName = name, isSystem = system)
        }
        .distinctBy { it.packageName }
        .sortedBy { it.packageName.lowercase() }
        .toList()

    /**
     * 从 `dumpsys package <pkg>` 提取 label / versionName / apk 路径。
     * label 规则与桌面端 PackageLabelParser 一致：以 application-label 开头的行，
     * 取第一个冒号后的内容，再丢弃到最后一个等号（兼容 locale 形如 `label-zh: name=` 的输出）。
     */
    fun parseDumpsys(output: String): DumpsysInfo {
        var label = ""
        var version = ""
        var apkPath = ""
        output.lineSequence().forEach { raw ->
            val line = raw.trim()
            if (label.isBlank() && line.startsWith("application-label", ignoreCase = true)) {
                val separator = line.indexOf(':')
                if (separator >= 0) {
                    var candidate = line.substring(separator + 1).trim()
                    val localeSeparator = candidate.lastIndexOf('=')
                    if (localeSeparator >= 0) candidate = candidate.substring(localeSeparator + 1)
                    candidate = candidate.trim().trim('\'', '"')
                    if (candidate.isNotBlank()) label = candidate
                }
            }
            if (version.isBlank() && line.startsWith("versionName=")) {
                version = line.substringAfter('=').trim()
            }
            if (apkPath.isBlank() && line.startsWith("codePath=")) {
                apkPath = line.substringAfter('=').trim()
            }
        }
        return DumpsysInfo(label, version, apkPath)
    }

    data class DumpsysInfo(val label: String, val versionName: String, val apkPath: String)
}
