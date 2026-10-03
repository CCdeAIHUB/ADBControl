package com.adbcontrol.remote.data.settings

import android.content.Context
import com.adbcontrol.remote.core.ThemeMode

/** 应用设置（SharedPreferences）。AI 模型配置独立存放在 AiModelStore。 */
class SettingsStore(context: Context) {
    private val preferences = context.getSharedPreferences("app_settings", Context.MODE_PRIVATE)

    var themeMode: ThemeMode
        get() = ThemeMode.entries.firstOrNull { it.name == preferences.getString("theme_mode", null) }
            ?: ThemeMode.FOLLOW_SYSTEM
        set(value) = preferences.edit().putString("theme_mode", value.name).apply()

    /** 预览轮询间隔毫秒（与桌面端一致：500–60000，默认 3000）。 */
    var previewIntervalMs: Int
        get() = preferences.getInt("preview_interval_ms", 3000).coerceIn(1_000, 60_000)
        set(value) = preferences.edit().putInt("preview_interval_ms", value.coerceIn(1_000, 60_000)).apply()

    /** 自动化前台调度开关。 */
    var automationSchedulerEnabled: Boolean
        get() = preferences.getBoolean("automation_scheduler", false)
        set(value) = preferences.edit().putBoolean("automation_scheduler", value).apply()

    /** 上次选中的 AI 模型 ID。 */
    var preferredAiModelId: String
        get() = preferences.getString("ai_preferred_model", "").orEmpty()
        set(value) = preferences.edit().putString("ai_preferred_model", value).apply()
}
