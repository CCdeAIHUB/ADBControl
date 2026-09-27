package com.adbcontrol.remote.core

import android.content.Context
import android.content.res.Configuration

/** 主题模式：跟随系统 / 浅色 / 深色。对应桌面端“跟随系统深浅色 + 深色模式”两个设置项。 */
enum class ThemeMode(val title: String) {
    FOLLOW_SYSTEM("跟随系统"),
    LIGHT("浅色模式"),
    DARK("深色模式"),
}

/**
 * 一套完整的界面色板。所有页面只允许从这里取色，禁止硬编码颜色值。
 * 配色与 WEBADBControl Web 版（Vue + Tailwind，见 web/src/style.css）对齐：
 * - 品牌绿 brand-600 #16A34A（主按钮/选中态），深色下高亮用 brand-400 #4ADE80；
 * - 背景 浅 #F4F7F5 / 深 #0B100E；卡片 surface 白 / #121815，细描边 slate-200 / white/9。
 */
data class ThemePalette(
    val brand: Int,
    val brandAccent: Int,
    val brandSoft: Int,
    val buttonFill: Int,
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
    // 主按钮两种主题都用 brand-600（白字对比度足够），选中文字/图标浅色用 600、深色用 400。
    private const val BUTTON_GREEN = 0xFF16A34A.toInt()

    val LIGHT = ThemePalette(
        brand = 0xFF16A34A.toInt(),
        brandAccent = 0xFF15803D.toInt(),
        brandSoft = 0xFFDCFCE7.toInt(),
        buttonFill = BUTTON_GREEN,
        background = 0xFFF4F7F5.toInt(),
        surface = 0xFFFFFFFF.toInt(),
        surfaceMuted = 0xFFEFF3F0.toInt(),
        text = 0xFF18221E.toInt(),
        secondary = 0xFF475569.toInt(),
        muted = 0xFF64748B.toInt(),
        border = 0xFFE2E8F0.toInt(),
        success = 0xFF16A34A.toInt(),
        successSoft = 0xFFF0FDF4.toInt(),
        warning = 0xFFD97706.toInt(),
        warningSoft = 0xFFFFFBEB.toInt(),
        danger = 0xFFDC2626.toInt(),
        dangerSoft = 0xFFFEF2F2.toInt(),
        terminalBackground = 0xFF0B1210.toInt(),
        terminalText = 0xFF86EFAC.toInt(),
        isDark = false,
    )

    val DARK = ThemePalette(
        brand = 0xFF4ADE80.toInt(),
        brandAccent = 0xFF4ADE80.toInt(),
        brandSoft = 0xFF12241A.toInt(),
        buttonFill = BUTTON_GREEN,
        background = 0xFF0B100E.toInt(),
        surface = 0xFF121815.toInt(),
        surfaceMuted = 0xFF1B231F.toInt(),
        text = 0xFFE7EEE9.toInt(),
        secondary = 0xFFCBD5E1.toInt(),
        muted = 0xFF8A9790.toInt(),
        border = 0xFF272E2A.toInt(),
        success = 0xFF4ADE80.toInt(),
        successSoft = 0xFF12241A.toInt(),
        warning = 0xFFFBBF24.toInt(),
        warningSoft = 0xFF2A2113.toInt(),
        danger = 0xFFFCA5A5.toInt(),
        dangerSoft = 0xFF2A1416.toInt(),
        terminalBackground = 0xFF0B1210.toInt(),
        terminalText = 0xFF86EFAC.toInt(),
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
