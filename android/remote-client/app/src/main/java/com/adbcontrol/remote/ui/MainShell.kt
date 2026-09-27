package com.adbcontrol.remote.ui

import android.content.Context
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
import com.adbcontrol.remote.ui.common.FlatIconView
import com.adbcontrol.remote.ui.common.iconFor
import com.adbcontrol.remote.core.AppGraph
import com.adbcontrol.remote.model.Session
import com.adbcontrol.remote.ui.automation.TasksPage
import com.adbcontrol.remote.ui.common.PageHost
import com.adbcontrol.remote.ui.common.column
import com.adbcontrol.remote.ui.common.dp
import com.adbcontrol.remote.ui.common.pal
import com.adbcontrol.remote.ui.common.ripple
import com.adbcontrol.remote.ui.common.row
import com.adbcontrol.remote.ui.common.text
import com.adbcontrol.remote.ui.devices.DevicesPage
import com.adbcontrol.remote.ui.home.HomePage
import com.adbcontrol.remote.ui.profile.ProfilePage

/**
 * 主壳：底部 4 Tab（首页 / 设备 / 任务 / 我的），符合中国大陆 App 的一级导航习惯。
 * 样式对齐 Web 版：surface 底 + 顶部 1dp 分隔线，选中项品牌绿。
 * 系统栏避让由 MainActivity 根容器统一处理。
 */
class MainShell(
    private val context: Context,
    private val host: PageHost,
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
        root.addView(bottomBar(), LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, context.dp(58),
        ))
        switchTab(Tab.HOME)
        return root
    }

    private fun bottomBar(): View {
        val bar = context.row {
            gravity = Gravity.CENTER
            setBackgroundColor(context.pal.surface)
            listOf(Tab.HOME, Tab.DEVICES, Tab.TASKS, Tab.PROFILE).forEach { tab ->
                val button = context.column(2) {
                    gravity = Gravity.CENTER
                    addView(FlatIconView(context, iconFor(tab.title), context.pal.muted).apply { tag = "navIcon" }, LinearLayout.LayoutParams(context.dp(24), context.dp(24)))
                    addView(context.text(tab.title, 11f, context.pal.muted, true).apply {
                        gravity = Gravity.CENTER
                        tag = "label"
                    })
                }.apply {
                    setOnClickListener { switchTab(tab) }
                    foreground = ripple()
                }
                navButtons[tab] = button
                addView(button, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MATCH_PARENT, 1f))
            }
        }
        return FrameLayout(context).apply {
            setBackgroundColor(context.pal.surface)
            addView(bar, FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT,
            ))
            addView(View(this@MainShell.context).apply { setBackgroundColor(context.pal.border) }, FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, context.dp(1), Gravity.TOP,
            ))
        }
    }

    private fun switchTab(next: Tab) {
        currentTab = next
        navButtons.forEach { (tab, view) ->
            val active = tab == next
            val oldIcon = view.findViewWithTag<View>("navIcon")
            val label = view.findViewWithTag<TextView>("label")
            val holder = oldIcon?.parent as? ViewGroup
            val index = holder?.indexOfChild(oldIcon) ?: -1
            if (holder != null && index >= 0) {
                holder.removeViewAt(index)
                holder.addView(FlatIconView(context, iconFor(tab.title), if (active) context.pal.brand else context.pal.muted).apply { tag = "navIcon" }, index, LinearLayout.LayoutParams(context.dp(24), context.dp(24)))
            }
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
