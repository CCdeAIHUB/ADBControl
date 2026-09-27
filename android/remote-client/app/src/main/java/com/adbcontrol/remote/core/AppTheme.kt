package com.adbcontrol.remote.core

import android.content.Context
import android.content.res.Configuration

/** 主题模式：跟随系统 / 浅色 / 深色。对应桌面端“跟随系统深浅色 + 深色模式”两个设置项。 */
enum class ThemeMode(val title: String) {
    FOLLOW_SYSTEM("跟随系统"),
    LIGHT("浅色模式"),
    DARK("深色模式"),
}

/** 一套完整的界面色板。所有页面只允许从这里取色，禁止硬编码颜色值。 */
data class ThemePalette(
    val brand: Int,
    val brandSoft: Int,
    val background: Int,
    val surface: Int,
    val surfaceMuted: Int,
    val text: Int,
    val secondary: Int,
    val muted: Int,
    val border: Int,
    val success: Int,
    val successSoft: Int,
    val warning: Int,
    val warningSoft: Int,
    val danger: Int,
    val dangerSoft: Int,
    val terminalBackground: Int,
    val terminalText: Int,
    val isDark: Boolean,
)

object ThemePalettes {
    // 品牌蓝沿用原客户端 #1677FF（支付宝式主色），深色色板按 Material 深色层级压暗。
    val LIGHT = ThemePalette(
        brand = 0xFF1677FF.toInt(),
        brandSoft = 0xFFEAF3FF.toInt(),
        background = 0xFFF5F7FA.toInt(),
        surface = 0xFFFFFFFF.toInt(),
        surfaceMuted = 0xFFF8F9FA.toInt(),
        text = 0xFF1F2329.toInt(),
        secondary = 0xFF646A73.toInt(),
        muted = 0xFF8F959E.toInt(),
        border = 0xFFE6E8EB.toInt(),
        success = 0xFF00A870.toInt(),
        successSoft = 0xFFE8FFEF.toInt(),
        warning = 0xFFFF8800.toInt(),
        warningSoft = 0xFFFFF3E0.toInt(),
        danger = 0xFFE34D59.toInt(),
        dangerSoft = 0xFFFFECEC.toInt(),
        terminalBackground = 0xFF111827.toInt(),
        terminalText = 0xFFD1FAE5.toInt(),
        isDark = false,
    )

    val DARK = ThemePalette(
        brand = 0xFF4D9BFF.toInt(),
        brandSoft = 0xFF1B2A41.toInt(),
        background = 0xFF101318.toInt(),
        surface = 0xFF191D24.toInt(),
        surfaceMuted = 0xFF21262F.toInt(),
        text = 0xFFE8EAED.toInt(),
        secondary = 0xFFA8AEB8.toInt(),
        muted = 0xFF7A8089.toInt(),
        border = 0xFF2C323C.toInt(),
        success = 0xFF3DDC97.toInt(),
        successSoft = 0xFF14301F.toInt(),
        warning = 0xFFFFB74D.toInt(),
        warningSoft = 0xFF33270F.toInt(),
        danger = 0xFFFF7080.toInt(),
        dangerSoft = 0xFF3A1A1E.toInt(),
        terminalBackground = 0xFF0B0F16.toInt(),
        terminalText = 0xFF9FE8C0.toInt(),
        isDark = true,
    )
}

/**
 * 主题管理器：持有当前模式与色板。
 * 页面在构建时读取 [palette]，切换主题后由宿主重建界面（Activity.recreate 级别）。
 */
object ThemeManager {
    var mode: ThemeMode = ThemeMode.FOLLOW_SYSTEM
        private set

    fun apply(savedMode: ThemeMode) {
        mode = savedMode
    }

    fun palette(context: Context): ThemePalette = when (mode) {
        ThemeMode.LIGHT -> ThemePalettes.LIGHT
        ThemeMode.DARK -> ThemePalettes.DARK
        ThemeMode.FOLLOW_SYSTEM ->
            if ((context.resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK) ==
                Configuration.UI_MODE_NIGHT_YES
            ) ThemePalettes.DARK else ThemePalettes.LIGHT
    }
}
