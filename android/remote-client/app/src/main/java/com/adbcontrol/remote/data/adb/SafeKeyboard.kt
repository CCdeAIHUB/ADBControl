package com.adbcontrol.remote.data.adb

/**
 * 安全键盘：按键→命令映射与互斥语义对齐桌面端 `DeviceKeyboardService.cs`。
 * 关键不变量：
 * - 可打印 ASCII 必须逐字符 `input text '<char>'`（单引号转义），禁止拼长文本/剪贴板注入；
 * - 特殊键发送对应 keyevent 码；
 * - 互斥：busy 时拒绝新键（DEVICE_KEYBOARD_BUSY），锁屏状态 Unknown 时拒绝发送。
 */
object SafeKeyboard {

    // 与桌面端一致的特殊键码。
    val specialKeys: Map<String, Int> = mapOf(
        "backspace" to 67,
        "delete" to 112,
        "enter" to 66,
        "tab" to 61,
        "left" to 21,
        "right" to 22,
        "up" to 19,
        "down" to 20,
        "space" to 62,
    )

    fun isSpecialKey(key: String): Boolean = key in specialKeys

    fun keyeventCommand(key: String): Int? = specialKeys[key]

    /** 可打印 ASCII 字符的输入命令参数（含桌面端同款单引号转义）。 */
    fun printableCommand(character: Char): String? =
        if (character.code in 32..126) escapeSingleQuote(character.toString()) else null

    fun escapeSingleQuote(text: String): String = text.replace("'", "'\\''")

    /** 常用中文 UI 键位标签。 */
    val keyLabels: List<List<String>> = listOf(
        listOf("1", "2", "3", "4", "5", "6", "7", "8", "9", "0"),
        listOf("q", "w", "e", "r", "t", "y", "u", "i", "o", "p"),
        listOf("a", "s", "d", "f", "g", "h", "j", "k", "l"),
        listOf("z", "x", "c", "v", "b", "n", "m"),
    )
}
