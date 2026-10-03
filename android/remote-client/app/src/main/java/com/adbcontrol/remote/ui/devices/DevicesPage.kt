package com.adbcontrol.remote.ui.devices

import android.content.Context
import android.view.View
import android.view.ViewGroup
import android.widget.LinearLayout
import com.adbcontrol.remote.core.AppGraph
import com.adbcontrol.remote.model.RemoteDevice
import com.adbcontrol.remote.model.RemoteResult
import com.adbcontrol.remote.model.Session
import com.adbcontrol.remote.ui.common.BasePage
import com.adbcontrol.remote.ui.common.PageHost
import com.adbcontrol.remote.ui.common.ripple

/**
 * 设备 Tab：设备卡列表（对应桌面端设备页）。
 * 清单来自会话已分配设备；每台设备通过白名单 `adb.exec` 采集品牌/型号/安卓版本/电量
 * （与桌面端 `adb devices -l` + getprop 语义一致），伴侣连接状态来自 `device.list`。
 */
class DevicesPage(
    context: Context,
    host: PageHost,
    private val graph: AppGraph,
    private val session: Session,
) : BasePage(context, host) {

    private val listContainer = column(12)
    private val deviceCardViews = mutableMapOf<String, (RemoteDevice) -> Unit>()

    override fun build(): View {
        val body = column(12)
        body.addView(row {
            addView(column(3) {
                addView(text("设备", 22f, pal.text, true))
                addView(text("由 Core 管理员分配，客户端不发起配对连接", 11f, pal.muted))
            }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
            addView(text("刷新", 14f, pal.brand, true).apply { setOnClickListener { load() } })
        })
        body.addView(listContainer)
        load()
        return scroll(body)
    }

    private fun load() {
        deviceCardViews.clear()
        listContainer.removeAllViews()
        listContainer.addView(loadingView("正在读取设备…"))
        host.runRemote({ graph.repository.devices(session.assignedDevices) }) { result ->
            listContainer.removeAllViews()
            when (result) {
                is RemoteResult.Failure -> listContainer.addView(
                    errorCard("读取失败", result.error.message, result.error.errorCode, result.error.suggestion) { load() },
                )
                is RemoteResult.Success -> {
                    if (result.value.isEmpty()) {
                        listContainer.addView(emptyView(
                            "暂无可用设备",
                            "请先在 Core 所在主机连接设备，并由管理员为当前账号分配设备。",
                            "📱",
                        ))
                        return@runRemote
                    }
                    result.value.forEach { device -> listContainer.addView(deviceCard(device)) }
                    result.value.forEach { device -> enrich(device) }
                }
            }
        }
    }

    /** 逐台补齐型号信息；get-state 失败视为 ADB 离线（与桌面端连接判定一致）。 */
    private fun enrich(device: RemoteDevice) {
        host.runRemote({ graph.repository.enrichDevice(device.id) }) { result ->
            if (result is RemoteResult.Success) {
                deviceCardViews[device.id]?.invoke(result.value)
            }
        }
    }

    private fun deviceCard(device: RemoteDevice): View {
		var latestDevice = device
        val title = text(device.displayName, 16f, pal.text, true)
        val subtitle = text(device.subtitle(), 11f, pal.muted)
        val badge = badge("读取中…", pal.muted)
        deviceCardViews[device.id] = { updated ->
			latestDevice = updated.copy(companionState = device.companionState, appVersion = device.appVersion)
			title.text = latestDevice.displayName
			subtitle.text = latestDevice.subtitle()
			val color = if (latestDevice.adbOnline) pal.success else pal.muted
			badge.text = if (latestDevice.adbOnline) "在线" else "离线"
            badge.setTextColor(color)
            badge.background = shape(withAlpha(color, 0x1E), 10)
        }
        return card(row {
            addView(com.adbcontrol.remote.ui.common.FlatIconView(context, com.adbcontrol.remote.ui.common.AppIcon.DEVICE), LinearLayout.LayoutParams(dp(32), dp(32)))
            addView(column(4) {
                addView(row {
                    addView(title, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
                    addView(badge)
                })
                addView(subtitle)
            }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f).apply { leftMargin = dp(8) })
        }, 14).apply {
			setOnClickListener { host.openDevice(latestDevice) }
            foreground = ripple()
        }
    }

    private fun RemoteDevice.subtitle(): String = buildString {
        val parts = listOf(brand, model).filter(String::isNotBlank)
        if (parts.isNotEmpty()) append(parts.joinToString(" "))
        if (androidVersion.isNotBlank()) append(if (isNotEmpty()) " · Android $androidVersion" else "Android $androidVersion")
        batteryLevel?.let { append(if (isNotEmpty()) " · 电量 $it%" else "电量 $it%") }
        if (companionOnline) append(if (isNotEmpty()) " · 伴侣已连接" else "伴侣已连接")
        if (isEmpty()) append(id)
    }
}
