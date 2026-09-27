package com.adbcontrol.remote.transport

class NativeQuicBridge {
    companion object {
        init { System.loadLibrary("adbcontrol_remote_quic") }
    }

    /**
     * 证书信任策略由原生层实现：
     * - [certificateDer] 非空：以该证书的 SHA-256 作为固定指纹；
     * - [fingerprintHex] 非空（64 位十六进制）：直接按指纹固定（推荐，来自 Core 启动日志）；
     * - 两者都空：TOFU——首次连接记录服务器证书指纹（经 [nativeFingerprint] 取回保存）。
     */
    external fun nativeConnect(
        endpoint: String,
        serverName: String,
        certificateDer: ByteArray,
        fingerprintHex: String,
    ): Long

    external fun nativeRequest(handle: Long, requestJson: String, timeoutMs: Long): String?
    external fun nativeFingerprint(handle: Long): String?
    external fun nativeClose(handle: Long)
}
