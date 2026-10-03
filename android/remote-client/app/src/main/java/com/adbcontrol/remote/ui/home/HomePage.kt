package com.adbcontrol.remote.ui.home

import android.content.Context
import android.view.View
import android.view.ViewGroup
import android.widget.LinearLayout
import com.adbcontrol.remote.core.AppGraph
import com.adbcontrol.remote.data.log.AppDiagnostics
import com.adbcontrol.remote.model.RemoteResult
import com.adbcontrol.remote.model.Session
import com.adbcontrol.remote.ui.common.BasePage
import com.adbcontrol.remote.ui.common.PageHost

/**
 * 首页：问候、设备概览、常用功能宫格、最近动态。
 * 信息密度与操作路径按中国 App 习惯设计：状态一眼可见、常用功能一次点击可达。
 */
class HomePage(
    context: Context,
    host: PageHost,
    private val graph: AppGraph,
    private val session: Session,
) : BasePage(context, host) {

    override fun build(): View {
        val body = column(12)
        body.addView(row {
            addView(column(3) {
                addView(text(greetingText(), 22f, pal.text, true))
                addView(text("加密连接 · ${session.username}", 12f, pal.muted))
            }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
            addView(badge("QUIC 已固定证书", pal.brand))
        })
        body.addView(deviceSummaryCard())
        body.addView(sectionTitle("常用功能", "点击直达，操作均经远程 Core 执行"))
        body.addView(featureGrid())
        body.addView(sectionTitle("最近动态", "会话内关键事件（不含命令内容）"))
        body.addView(recentActivityCard())
        body.addView(text("安全提示：证书已固定，密码与会话令牌不会写入磁盘。", 11f, pal.muted))
        return scroll(body)
    }

    private fun greetingText(): String {
        val hour = java.util.Calendar.getInstance().get(java.util.Calendar.HOUR_OF_DAY)
        val part = when (hour) {
            in 5..10 -> "早上好"
            in 11..12 -> "中午好"
            in 13..17 -> "下午好"
            in 18..22 -> "晚上好"
            else -> "夜深了"
        }
        return "$part，${session.username}"
    }

    private fun deviceSummaryCard(): View {
        val summaryText = text("正在读取设备…", 12f, pal.muted)
        val card = card(column(10) {
            addView(text("设备概览", 15f, pal.text, true))
            addView(summaryText)
        })
        host.runRemote({ graph.repository.devices(session.assignedDevices) }) { result ->
            summaryText.text = when (result) {
                is RemoteResult.Failure -> "读取失败：${result.error.message}"
                is RemoteResult.Success -> if (result.value.isEmpty()) {
                    "暂无设备，请联系管理员分配"
                } else {
                    "${result.value.size} 台设备已分配，到“设备”页查看详情"
                }
            }
        }
        return card
    }

    private fun featureGrid(): View {
        val grid = column(10)
        listOf(
            listOf(listOf("🖥", "预览控制", "截图与远程触控"), listOf("⌨️", "终端", "执行远程命令")),
            listOf(listOf("🤖", "AI 助手", "多模型设备协作"), listOf("📁", "文件管理", "浏览、上传与下载")),
        ).forEach { rowItems ->
            grid.addView(row {
                rowItems.forEachIndexed { index, item ->
                    val (emoji, title, subtitle) = item
                    val action = if (title == "AI 助手") ({ host.openAiChat(null) }) else ({ openFirstDeviceOrNotify() })
                    addView(
                        // 高度用 WRAP_CONTENT：宫格内容（图标+两行文字）约 105dp，
                        // 固定 92dp 会把文字压到一起（此前“界面重叠”的主要来源）。
                        featureTile(title, subtitle, emoji, action),
                        LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f).apply {
                            if (index > 0) leftMargin = dp(10)
                        },
                    )
                }
            })
        }
        return grid
    }

    /** 快捷入口需要设备上下文：优先打开第一台设备，否则提示到设备页。 */
    private fun openFirstDeviceOrNotify() {
        host.runRemote({ graph.repository.devices(session.assignedDevices) }) { result ->
            when (result) {
                is RemoteResult.Failure -> host.notify(result.error.message)
                is RemoteResult.Success -> {
                    val first = result.value.firstOrNull()
                    if (first == null) {
                        host.notify("暂无设备，请到“设备”页查看，或联系管理员分配")
                    } else {
						host.runRemote({ graph.repository.enrichDevice(first.id) }) { enriched ->
							val current = (enriched as? RemoteResult.Success)?.value?.copy(
								companionState = first.companionState, appVersion = first.appVersion,
							) ?: first
							host.openDevice(current)
						}
                    }
                }
            }
        }
    }

    private fun recentActivityCard(): View {
        val events = AppDiagnostics.snapshot().takeLast(6).reversed()
        if (events.isEmpty()) {
            return card(emptyView("暂无动态", "关键事件会展示在这里", "📭"))
        }
        return card(column(8) {
            events.forEach { event ->
                addView(row {
                    addView(View(this@HomePage.context).apply {
                        background = shape(if (event.ok) pal.success else pal.danger, 4)
                    }, LinearLayout.LayoutParams(dp(6), dp(6)))
                    addView(text(event.display(), 11f, pal.secondary), LinearLayout.LayoutParams(
                        0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f,
                    ).apply { leftMargin = dp(8) })
                })
            }
        })
    }
}
