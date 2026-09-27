package com.adbcontrol.remote.security

/**
 * 证书指纹输入策略（纯 Kotlin，可被契约测试锁定）。
 * 用户可以从 Core 启动日志复制形如 `ab:cd:ef…` 的 SHA-256 指纹，
 * 本策略负责归一化（去冒号/空白、转小写）与校验（必须 64 位十六进制）。
 */
object FingerprintPolicy {

    fun normalize(value: String): String? {
        val cleaned = value.filterNot { it == ':' || it.isWhitespace() }.lowercase()
        return cleaned.takeIf { it.length == 64 && it.all(::isAsciiHexDigit) }
    }

    private fun isAsciiHexDigit(char: Char): Boolean =
        char in '0'..'9' || char in 'a'..'f'
}
