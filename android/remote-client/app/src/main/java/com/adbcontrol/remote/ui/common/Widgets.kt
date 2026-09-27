package com.adbcontrol.remote.ui.common

import android.content.Context
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.text.InputType
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.*
import com.adbcontrol.remote.core.ThemeManager
import com.adbcontrol.remote.core.ThemePalette

/**
 * 主题化组件库：全部页面从这里取色与构建控件，禁止在页面里硬编码颜色，
 * 保证深浅色两套主题一致（对齐 SKILL 桌面端组件重绘约束的移动端等价要求）。
 */
val Context.pal: ThemePalette get() = ThemeManager.palette(this)

fun Context.dp(value: Int): Int = (value * resources.displayMetrics.density).toInt()

fun Context.shape(color: Int, radius: Int = 12, strokeColor: Int? = null): GradientDrawable =
    GradientDrawable().apply {
        setColor(color)
        cornerRadius = dp(radius).toFloat()
        strokeColor?.let { setStroke(dp(1), it) }
    }

fun Context.column(spacing: Int = 0, block: LinearLayout.() -> Unit = {}): LinearLayout =
    LinearLayout(this).apply {
        orientation = LinearLayout.VERTICAL
        if (spacing > 0) dividerDrawable = SpaceDrawable(dp(spacing))
        showDividers = if (spacing > 0) LinearLayout.SHOW_DIVIDER_MIDDLE else LinearLayout.SHOW_DIVIDER_NONE
        block()
    }

fun Context.row(block: LinearLayout.() -> Unit = {}): LinearLayout = LinearLayout(this).apply {
    orientation = LinearLayout.HORIZONTAL
    gravity = Gravity.CENTER_VERTICAL
    block()
}

fun Context.text(value: String, size: Float = 14f, color: Int? = null, bold: Boolean = false): TextView =
    TextView(this).apply {
        text = value
        textSize = size
        setTextColor(color ?: pal.text)
        includeFontPadding = false
        if (bold) setTypeface(typeface, Typeface.BOLD)
        setLineSpacing(0f, 1.12f)
    }

fun Context.card(content: View, padding: Int = 16, radius: Int = 16): FrameLayout = FrameLayout(this).apply {
    background = shape(pal.surface, radius, pal.border)
    setPadding(dp(padding), dp(padding), dp(padding), dp(padding))
    addView(content, ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT))
}

fun Context.primaryButton(label: String, onClick: () -> Unit): Button = Button(this).apply {
    text = label
    textSize = 15f
    setTextColor(Color.WHITE)
    isAllCaps = false
    background = shape(pal.brand, 12)
    minHeight = dp(48)
    setOnClickListener { onClick() }
}

fun Context.dangerButton(label: String, onClick: () -> Unit): Button = Button(this).apply {
    text = label
    textSize = 15f
    setTextColor(Color.WHITE)
    isAllCaps = false
    background = shape(pal.danger, 12)
    minHeight = dp(48)
    setOnClickListener { onClick() }
}

fun Context.secondaryButton(label: String, onClick: () -> Unit): Button = Button(this).apply {
    text = label
    textSize = 14f
    setTextColor(pal.brand)
    isAllCaps = false
    background = shape(pal.surface, 12, pal.border)
    minHeight = dp(44)
    setOnClickListener { onClick() }
}

fun Context.input(hintText: String, password: Boolean = false, multiline: Boolean = false, fillColor: Int? = null): EditText =
    EditText(this).apply {
        hint = hintText
        textSize = 15f
        setTextColor(pal.text)
        setHintTextColor(pal.muted)
        setPadding(dp(14), dp(4), dp(14), dp(4))
        minHeight = dp(if (multiline) 100 else 48)
        gravity = if (multiline) Gravity.TOP else Gravity.CENTER_VERTICAL
        // 默认在卡片内使用 surfaceMuted；铺在页面背景上时传入 surface 保证与背景有对比。
        background = shape(fillColor ?: pal.surfaceMuted, 12, pal.border)
        inputType = when {
            password -> InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD
            multiline -> InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_MULTI_LINE
            else -> InputType.TYPE_CLASS_TEXT
        }
    }

fun Context.scroll(content: View): ScrollView = ScrollView(this).apply {
    isFillViewport = true
    clipToPadding = false
    setPadding(dp(16), dp(8), dp(16), dp(24))
    addView(content, ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT))
}

fun View.margin(left: Int = 0, top: Int = 0, right: Int = 0, bottom: Int = 0): View = apply {
    layoutParams = (layoutParams as? ViewGroup.MarginLayoutParams ?: ViewGroup.MarginLayoutParams(
        ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT,
    )).apply { setMargins(context.dp(left), context.dp(top), context.dp(right), context.dp(bottom)) }
}

// ---------- 业务组件 ----------

/** 二级页顶部栏：返回 + 标题 + 右侧动作。 */
fun Context.topBar(title: String, onBack: () -> Unit, action: TextView.() -> Unit = {}): View = row {
    setPadding(dp(8), 0, dp(16), 0)
    minimumHeight = dp(56)
    setBackgroundColor(pal.surface)
    addView(text("‹", 32f, pal.text).apply {
        gravity = Gravity.CENTER
        setOnClickListener { onBack() }
    }, LinearLayout.LayoutParams(dp(44), ViewGroup.LayoutParams.MATCH_PARENT))
    addView(text(title, 17f, pal.text, true), LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
    addView(text("", 14f, pal.brand, true).apply(action), LinearLayout.LayoutParams(
        ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.MATCH_PARENT,
    ))
}

/** 区块标题（宫格/列表上方的小标题）。 */
fun Context.sectionTitle(title: String, hint: String = ""): View = column(3) {
    addView(text(title, 16f, pal.text, true))
    if (hint.isNotBlank()) addView(text(hint, 11f, pal.muted))
}

/** 状态圆点 + 文案。 */
fun Context.statusDot(textValue: String, color: Int): View = row {
    addView(View(this@statusDot).apply {
        background = shape(color, 5)
    }, LinearLayout.LayoutParams(dp(8), dp(8)))
    addView(text(textValue, 12f, color, true), LinearLayout.LayoutParams(
        ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT,
    ).apply { leftMargin = dp(6) })
}

/** 功能宫格块。 */
fun Context.featureTile(title: String, subtitle: String, emoji: String, onClick: () -> Unit): View =
    card(column(6) {
        addView(text(emoji, 22f))
        addView(text(title, 14f, pal.text, true))
        addView(text(subtitle, 10f, pal.muted))
    }, 14, 14).apply {
        setOnClickListener { onClick() }
        foreground = ripple()
    }

/** 指标卡（大数字 + 标签）。 */
fun Context.metricTile(value: String, label: String, color: Int): View = card(column(4) {
    gravity = Gravity.CENTER
    addView(text(value, 20f, color, true).apply { gravity = Gravity.CENTER })
    addView(text(label, 11f, pal.muted).apply { gravity = Gravity.CENTER })
}, 14)

/** 徽标。 */
fun Context.badge(label: String, color: Int, soft: Boolean = true): TextView = text(label, 10f, color, true).apply {
    gravity = Gravity.CENTER
    background = shape(if (soft) withAlpha(color, 0x1E) else color, 10)
    setPadding(dp(8), dp(3), dp(8), dp(3))
}

/** 键值行（硬件详情等）。 */
fun Context.kvRow(key: String, value: String): View = row {
    addView(text(key, 13f, pal.secondary), LinearLayout.LayoutParams(dp(110), ViewGroup.LayoutParams.WRAP_CONTENT))
    addView(text(value, 13f, pal.text, true).apply { setTextIsSelectable(true) }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
}

/** 列表行（左图标/标题/副标题，右侧箭头或动作）。 */
fun Context.listRow(
    title: String,
    subtitle: String,
    onClick: (() -> Unit)? = null,
    trailing: View? = null,
    emoji: String = "",
): View = card(row {
    if (emoji.isNotBlank()) {
        addView(text(emoji, 20f), LinearLayout.LayoutParams(dp(36), ViewGroup.LayoutParams.WRAP_CONTENT))
    }
    addView(column(3) {
        addView(text(title, 15f, pal.text, true))
        if (subtitle.isNotBlank()) addView(text(subtitle, 11f, pal.muted))
    }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f).apply { leftMargin = if (emoji.isBlank()) 0 else dp(8) })
    if (trailing != null) addView(trailing) else addView(text("›", 22f, pal.muted))
}, 12).apply {
    if (onClick != null) {
        setOnClickListener { onClick() }
        foreground = ripple()
    }
}

/** 空态。 */
fun Context.emptyView(title: String, detail: String, emoji: String = "📭"): View = column(8) {
    gravity = Gravity.CENTER
    setPadding(dp(32), dp(48), dp(32), dp(48))
    addView(text(emoji, 40f).apply { gravity = Gravity.CENTER })
    addView(text(title, 16f, pal.text, true).apply { gravity = Gravity.CENTER })
    addView(text(detail, 12f, pal.muted).apply { gravity = Gravity.CENTER })
}

/** 加载态。 */
fun Context.loadingView(message: String = "正在加载…"): View = column(10) {
    gravity = Gravity.CENTER
    setPadding(dp(32), dp(48), dp(32), dp(48))
    addView(ProgressBar(this@loadingView).apply { indeterminateTintList = android.content.res.ColorStateList.valueOf(pal.brand) })
    addView(text(message, 12f, pal.muted).apply { gravity = Gravity.CENTER })
}

/** 错误卡。 */
fun Context.errorCard(title: String, message: String, errorCode: String = "", suggestion: String? = null, onRetry: (() -> Unit)? = null): View =
    card(column(7) {
        addView(text(title, 15f, pal.danger, true))
        addView(text(message, 13f, pal.secondary))
        if (errorCode.isNotBlank()) addView(text(errorCode, 11f, pal.muted))
        suggestion?.let { addView(text("建议：$it", 12f, pal.secondary)) }
        onRetry?.let { addView(secondaryButton("重试", it)) }
    })

/** 水平进度条（自动化任务进度）。 */
fun Context.progressBar(percent: Double): View = ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal).apply {
    max = 100
    progress = percent.toInt().coerceIn(0, 100)
    progressTintList = android.content.res.ColorStateList.valueOf(pal.brand)
    progressBackgroundTintList = android.content.res.ColorStateList.valueOf(pal.surfaceMuted)
    layoutParams = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(8))
}

/** 简单水平分隔线。 */
fun Context.divider(): View = View(this).apply {
    setBackgroundColor(pal.border)
    layoutParams = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(1))
}

/** 透明 ripple 前景（点击反馈）。 */
fun View.ripple(): android.graphics.drawable.Drawable {
    val outward = android.util.TypedValue()
    context.theme.resolveAttribute(android.R.attr.selectableItemBackground, outward, true)
    return context.getDrawable(outward.resourceId) ?: context.shape(context.pal.brandSoft, 12)
}

fun withAlpha(color: Int, alpha: Int): Int = (alpha shl 24) or (color and 0x00FFFFFF)

private class SpaceDrawable(private val size: Int) : android.graphics.drawable.ColorDrawable(Color.TRANSPARENT) {
    override fun getIntrinsicHeight(): Int = size
    override fun getIntrinsicWidth(): Int = size
}

/** 格式化字节数（文件/内存展示）。 */
fun formatBytes(bytes: Long): String = when {
    bytes >= 1 shl 30 -> java.lang.String.format(java.util.Locale.US, "%.2f GB", bytes / 1073741824.0)
    bytes >= 1 shl 20 -> java.lang.String.format(java.util.Locale.US, "%.1f MB", bytes / 1048576.0)
    bytes >= 1 shl 10 -> java.lang.String.format(java.util.Locale.US, "%.1f KB", bytes / 1024.0)
    else -> "$bytes B"
}

/** 格式化 kHz/Hz 频率。 */
fun formatFrequency(hz: Long): String = when {
    hz >= 1_000_000_000 -> java.lang.String.format(java.util.Locale.US, "%.2f GHz", hz / 1_000_000_000.0)
    hz >= 1_000_000 -> java.lang.String.format(java.util.Locale.US, "%.0f MHz", hz / 1_000_000.0)
    hz >= 1_000 -> java.lang.String.format(java.util.Locale.US, "%.0f kHz", hz / 1_000.0)
    else -> "$hz Hz"
}

/** 格式化时间戳。 */
fun formatTime(epochMs: Long): String =
    java.text.SimpleDateFormat("MM-dd HH:mm", java.util.Locale.CHINA).format(java.util.Date(epochMs))
