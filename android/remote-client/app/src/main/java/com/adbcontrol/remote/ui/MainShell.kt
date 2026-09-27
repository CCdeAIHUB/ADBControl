package com.adbcontrol.remote.ui

import android.content.Context
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
import com.adbcontrol.remote.core.AppGraph
import com.adbcontrol.remote.model.Session
import com.adbcontrol.remote.model.UserRole
import com.adbcontrol.remote.ui.automation.TasksPage
import com.adbcontrol.remote.ui.common.*
import com.adbcontrol.remote.ui.devices.DevicesPage
import com.adbcontrol.remote.ui.home.HomePage
import com.adbcontrol.remote.ui.profile.ProfilePage

/**
 * 主壳：底部 4 Tab（首页 / 设备 / 任务 / 我的），符合中国大陆 App 的一级导航习惯。
 * 二级页（设备详情、各工具页、AI 等）由 MainActivity 的页面栈承载，不进入 Tab。
 */
class MainShell(
    private val context: Context,
    private val host: com.adbcontrol.remote.ui.common.PageHost,
    val graph: AppGraph,
    private val session: Session,
) {
    private val content = FrameLayout(context)
    private val navButtons = mutableMapOf<Tab, LinearLayout>()
    private var currentTab: Tab = Tab.HOME

    enum class Tab(val title: String, val emoji: String) {
        HOME("首页", "🏠"),
        DEVICES("设备", "📱"),
        TASKS("任务", "🗂"),
        PROFILE("我的", "👤"),
    }

    fun build(): View {
        val root = LinearLayout(context).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(context.pal.background)
        }
        root.addView(content, LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f,
        ))
        root.addView(bottomBar(), LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, context.dp(62)))
        switchTab(Tab.HOME)
        return root
    }

    private fun bottomBar(): View = context.row {
        gravity = Gravity.CENTER
        setPadding(context.dp(8), context.dp(6), context.dp(8), context.dp(8))
        setBackgroundColor(context.pal.surface)
        listOf(Tab.HOME, Tab.DEVICES, Tab.TASKS, Tab.PROFILE).forEach { tab ->
            val button = context.column(2) {
                gravity = Gravity.CENTER
                addView(context.text(tab.emoji, 19f, context.pal.muted).apply {
                    gravity = Gravity.CENTER
                    tag = "emoji"
                })
                addView(context.text(tab.title, 10f, context.pal.muted, true).apply {
                    gravity = Gravity.CENTER
                    tag = "label"
                })
            }.apply {
                setOnClickListener { switchTab(tab) }
            }
            navButtons[tab] = button
            addView(button, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MATCH_PARENT, 1f))
        }
    }

    private fun switchTab(next: Tab) {
        currentTab = next
        navButtons.forEach { (tab, view) ->
            val active = tab == next
            val emoji = view.findViewWithTag<TextView>("emoji")
            val label = view.findViewWithTag<TextView>("label")
            emoji.setTextColor(if (active) context.pal.brand else context.pal.muted)
            label.setTextColor(if (active) context.pal.brand else context.pal.muted)
        }
        content.removeAllViews()
        val page = when (next) {
            Tab.HOME -> HomePage(context, host, graph, session)
            Tab.DEVICES -> DevicesPage(context, host, graph, session)
            Tab.TASKS -> TasksPage(context, host, graph, session)
            Tab.PROFILE -> ProfilePage(context, host, graph, session)
        }
        content.addView(page.build(), FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT,
        ))
    }
}
