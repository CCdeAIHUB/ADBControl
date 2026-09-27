package com.adbcontrol.remote.ui.device

import android.content.Context
import android.view.View
import android.widget.LinearLayout
import com.adbcontrol.remote.core.AppGraph
import com.adbcontrol.remote.model.RemoteDevice
import com.adbcontrol.remote.model.RemoteResult
import com.adbcontrol.remote.ui.common.BasePage
import com.adbcontrol.remote.ui.common.PageHost
import org.json.JSONArray
import org.json.JSONObject

/**
 * 伴侣能力页（对应桌面端“APP连接”能力面）：
 * - 伴侣安装状态：`pm path com.adbcontrol.companion` + `dumpsys package … | versionName`（桌面端同款命令）；
 * - 能力与权限：`device.getCapabilities` / `device.getPermissionState`（协议原生方法）；
 * - 呼出伴侣主界面：device.invoke `android.ui.background_surface / ui.surface.show`（桌面端同款）；
 * - 明确边界：伴侣 APK 安装与 QUIC 配置下发需要桌面端 ADB 通道（证书/内网地址由桌面下发），
 *   远程端不做也不伪装成功。
 */
class CompanionPage(
    context: Context,
    host: PageHost,
    private val graph: AppGraph,
    private val device: RemoteDevice,
) : BasePage(context, host) {

    private val body = column(12)

    override fun build(): View {
        body.addView(sectionTitle("伴侣 App 状态", "安装在目标设备上，为远程提供能力通道"))
        val statusCard = card(column(8) { addView(text("检测中…", 13f, pal.muted)) })
        body.addView(statusCard)
        body.addView(row {
            addView(secondaryButton("重新检测") { loadStatus(statusCard) }, LinearLayout.LayoutParams(0, dp(44), 1f))
            addView(secondaryButton("呼出伴侣界面") {
                host.runRemote({ graph.companion.showSurface(device.id) }) { result ->
                    when (result) {
                        is RemoteResult.Success -> host.notify("已请求设备呼出伴侣界面")
                        is RemoteResult.Failure -> host.notify("呼出失败：${result.error.message}")
                    }
                }
            }, LinearLayout.LayoutParams(0, dp(44), 1f).apply { leftMargin = dp(8) })
        })
        body.addView(sectionTitle("能力目录", "由伴侣 App 上报，决定远程可调用范围"))
        val capabilityCard = card(column(8) { addView(text("读取中…", 13f, pal.muted)) })
        body.addView(capabilityCard)
        body.addView(sectionTitle("权限状态", "缺失权限会在调用时以 COMPANION_PERMISSION_DENIED 明确失败"))
        val permissionCard = card(column(8) { addView(text("读取中…", 13f, pal.muted)) })
        body.addView(permissionCard)
        body.addView(text(
            "边界说明：安装/更新伴侣 APK、下发 QUIC 配置需要桌面端在设备局域网内完成；" +
                "实时投屏与摄像头实时流需要 Core 的视频下行流，当前远程协议仅提供控制面。",
            11f, pal.muted,
        ))
        loadStatus(statusCard)
        loadCapabilities(capabilityCard, permissionCard)
        return subPage("伴侣能力 · ${device.displayName}", body)
    }

    private fun loadStatus(statusCard: android.widget.FrameLayout) {
        host.runRemote({ graph.commands.shell(device.id, "pm path com.adbcontrol.companion", 15_000) }) { path ->
            host.runRemote({
                graph.commands.shell(device.id, "dumpsys package com.adbcontrol.companion | grep versionName", 15_000)
            }) { version ->
                val columnView = statusCard.getChildAt(0) as LinearLayout
                columnView.removeAllViews()
                val installed = path is RemoteResult.Success && (path as RemoteResult.Success).value.success &&
                    path.value.stdout.contains("package:")
                val versionText = (version as? RemoteResult.Success)?.value?.stdout
                    ?.lineSequence()?.firstOrNull { it.contains("versionName=") }
                    ?.substringAfter("versionName=")?.trim().orEmpty()
                columnView.addView(
                    text(
                        if (installed) "已安装${if (versionText.isNotBlank()) " · v$versionText" else ""}" else "未安装",
                        15f, pal.text, true,
                    ),
                )
                columnView.addView(text(
                    if (installed) "包名 com.adbcontrol.companion；设备端会话状态：${device.companionState.ifBlank { "未注册" }}"
                    else "未检测到伴侣 App；安装需要桌面端执行 adb install（APK 由桌面端内置）。",
                    12f, pal.secondary,
                ))
            }
        }
    }

    private fun loadCapabilities(capabilityCard: android.widget.FrameLayout, permissionCard: android.widget.FrameLayout) {
        host.runRemote({ graph.repository.getCapabilities(device.id) }) { capabilities ->
            val capColumn = capabilityCard.getChildAt(0) as LinearLayout
            capColumn.removeAllViews()
            when (capabilities) {
                is RemoteResult.Failure -> capColumn.addView(text(
                    "读取失败：${capabilities.error.message}（${capabilities.error.errorCode}）。\n" +
                        "常见原因：当前 Core 宿主未接线伴侣会话（COMPANION_SESSION_NOT_CONNECTED）或设备未注册。",
                    12f, pal.secondary,
                ))
                is RemoteResult.Success -> {
                    val array = when (val value = capabilities.value) {
                        is JSONArray -> value
                        else -> JSONArray()
                    }
                    capColumn.addView(text("共 ${array.length()} 项能力", 13f, pal.text, true))
                    for (index in 0 until array.length()) {
                        val item = array.optJSONObject(index) ?: continue
                        val operations = item.optJSONArray("operations")?.let { ops ->
                            (0 until ops.length()).map { ops.optString(it) }.joinToString(" / ")
                        }.orEmpty()
                        capColumn.addView(kvRow(item.optString("id"), operations))
                    }
                }
            }
        }
        host.runRemote({ graph.repository.getPermissionState(device.id) }) { permissions ->
            val permColumn = permissionCard.getChildAt(0) as LinearLayout
            permColumn.removeAllViews()
            when (permissions) {
                is RemoteResult.Failure -> permColumn.addView(text(
                    "读取失败：${permissions.error.message}（${permissions.error.errorCode}）", 12f, pal.secondary,
                ))
                is RemoteResult.Success -> {
                    val array = permissions.value as? JSONArray ?: JSONArray()
                    if (array.length() == 0) {
                        permColumn.addView(text("无权限状态数据", 12f, pal.muted))
                    }
                    for (index in 0 until array.length()) {
                        val item = array.optJSONObject(index) ?: continue
                        val granted = item.optBoolean("granted")
                        val missing = item.optJSONArray("missingPermissions")?.let { array ->
                            (0 until array.length()).mapNotNull { array.optString(it).takeIf(String::isNotBlank) }
                        }.orEmpty()
                        permColumn.addView(kvRow(
                            item.optString("capabilityId"),
                            if (granted) "已授权" else "未授权${if (missing.isEmpty()) "" else " · 缺 ${missing.joinToString()}"}",
                        ))
                    }
                }
            }
        }
    }
}
