package com.adbcontrol.remote.data.adb

/** Typed key mapping keeps UI labels out of shell command strings. */
object DeviceKeyEvent {
    private val labels = mapOf(
        "返回" to "BACK", "主页" to "HOME", "多任务" to "APP_SWITCH",
        "音量+" to "VOLUME_UP", "音量-" to "VOLUME_DOWN", "电源" to "POWER",
        "点亮" to "WAKEUP", "唤醒" to "WAKEUP", "锁屏" to "SLEEP",
    )

    fun fromLabel(label: String): String? = labels[label]
}
