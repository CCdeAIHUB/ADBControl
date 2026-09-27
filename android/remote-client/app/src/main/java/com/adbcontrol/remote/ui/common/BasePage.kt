package com.adbcontrol.remote.ui.common

import android.content.Context
import android.view.View
import android.widget.Button
import android.widget.EditText
import android.widget.ScrollView
import android.widget.TextView
import com.adbcontrol.remote.core.ThemePalette

/**
 * 页面基类：把主题化组件统一委托给子类使用，页面代码不再重复 `context.` 前缀，
 * 也保证所有页面从 ThemePalette 取色（深浅色一致）。
 */
abstract class BasePage(
    protected val context: Context,
    protected val host: PageHost,
) : Page {
    abstract override fun build(): View

    protected val pal: ThemePalette get() = context.pal

    protected fun dp(value: Int): Int = context.dp(value)
    protected fun shape(color: Int, radius: Int = 12, strokeColor: Int? = null) = context.shape(color, radius, strokeColor)
    protected fun column(spacing: Int = 0, block: android.widget.LinearLayout.() -> Unit = {}): android.widget.LinearLayout = context.column(spacing, block)
    protected fun row(block: android.widget.LinearLayout.() -> Unit = {}): android.widget.LinearLayout = context.row(block)
    protected fun text(value: String, size: Float = 14f, color: Int? = null, bold: Boolean = false): TextView = context.text(value, size, color, bold)
    protected fun card(content: View, padding: Int = 16, radius: Int = 16): android.widget.FrameLayout = context.card(content, padding, radius)
    protected fun primaryButton(label: String, onClick: () -> Unit): Button = context.primaryButton(label, onClick)
    protected fun secondaryButton(label: String, onClick: () -> Unit): Button = context.secondaryButton(label, onClick)
    protected fun dangerButton(label: String, onClick: () -> Unit): Button = context.dangerButton(label, onClick)
    protected fun input(hintText: String, password: Boolean = false, multiline: Boolean = false): EditText = context.input(hintText, password, multiline)
    protected fun scroll(content: View): ScrollView = context.scroll(content)
    protected fun topBar(title: String, onBack: () -> Unit, action: TextView.() -> Unit = {}): View = context.topBar(title, onBack, action)
    protected fun sectionTitle(title: String, hint: String = ""): View = context.sectionTitle(title, hint)
    protected fun statusDot(textValue: String, color: Int): View = context.statusDot(textValue, color)
    protected fun featureTile(title: String, subtitle: String, emoji: String, onClick: () -> Unit): View = context.featureTile(title, subtitle, emoji, onClick)
    protected fun metricTile(value: String, label: String, color: Int): View = context.metricTile(value, label, color)
    protected fun badge(label: String, color: Int, soft: Boolean = true): TextView = context.badge(label, color, soft)
    protected fun kvRow(key: String, value: String): View = context.kvRow(key, value)
    protected fun listRow(
        title: String,
        subtitle: String,
        onClick: (() -> Unit)? = null,
        trailing: View? = null,
        emoji: String = "",
    ): View = context.listRow(title, subtitle, onClick, trailing, emoji)
    protected fun emptyView(title: String, detail: String, emoji: String = "📭"): View = context.emptyView(title, detail, emoji)
    protected fun loadingView(message: String = "正在加载…"): View = context.loadingView(message)
    protected fun errorCard(
        title: String,
        message: String,
        errorCode: String = "",
        suggestion: String? = null,
        onRetry: (() -> Unit)? = null,
    ): View = context.errorCard(title, message, errorCode, suggestion, onRetry)
    protected fun progressBar(percent: Double): View = context.progressBar(percent)
    protected fun divider(): View = context.divider()
    protected fun withAlpha(color: Int, alpha: Int): Int = com.adbcontrol.remote.ui.common.withAlpha(color, alpha)
    protected fun formatBytes(bytes: Long): String = com.adbcontrol.remote.ui.common.formatBytes(bytes)
    protected fun formatFrequency(hz: Long): String = com.adbcontrol.remote.ui.common.formatFrequency(hz)
    protected fun formatTime(epochMs: Long): String = com.adbcontrol.remote.ui.common.formatTime(epochMs)

    /** 二级页标准结构：顶栏 + 可滚动内容。 */
    protected fun subPage(title: String, content: View): View {
        val root = column {
            orientation = android.widget.LinearLayout.VERTICAL
            setBackgroundColor(pal.background)
        }
        root.addView(
            topBar(title, onBack = { host.popPage() }),
            android.widget.LinearLayout.LayoutParams(
                android.view.ViewGroup.LayoutParams.MATCH_PARENT, dp(56),
            ),
        )
        root.addView(scroll(content), android.widget.LinearLayout.LayoutParams(
            android.view.ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f,
        ))
        return root
    }
}
