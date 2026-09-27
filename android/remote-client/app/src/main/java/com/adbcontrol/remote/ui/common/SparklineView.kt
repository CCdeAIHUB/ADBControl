package com.adbcontrol.remote.ui.common

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Path
import android.view.View

/** 轻量曲线图（硬件监控使用）：无第三方依赖，支持上限 240 个采样点的滑动窗口。 */
class SparklineView(context: Context) : View(context) {

    private val paintLine = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeWidth = dip(2f)
        strokeCap = Paint.Cap.ROUND
        strokeJoin = Paint.Join.ROUND
    }
    private val paintFill = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.FILL }
    private val paintText = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        textSize = dip(10f)
        color = Color.WHITE
    }

    private fun dip(value: Float): Float = value * resources.displayMetrics.density
    private val path = Path()
    private val fillPath = Path()

    var lineColor: Int = context.pal.brand
        set(value) {
            field = value
            paintLine.color = value
            paintFill.color = withAlpha(value, 0x33)
            invalidate()
        }

    /** 采样点（0–100 归一化后绘制；null 表示缺口）。 */
    private val samples = ArrayDeque<Double>()

    var maxLabel: String = ""
        set(value) {
            field = value
            invalidate()
        }

    fun setSamples(values: List<Double?>) {
        samples.clear()
        values.filterNotNull().takeLast(MAX_POINTS).forEach { samples.add(it.coerceIn(0.0, 100.0)) }
        invalidate()
    }

    fun addSample(value: Double) {
        samples.addLast(value.coerceIn(0.0, 100.0))
        while (samples.size > MAX_POINTS) samples.removeFirst()
        invalidate()
    }

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        val width = width.toFloat()
        val height = height.toFloat()
        canvas.drawColor(context.pal.surfaceMuted)
        paintText.color = context.pal.muted
        if (maxLabel.isNotBlank()) canvas.drawText(maxLabel, dip(6f), dip(14f), paintText)
        if (samples.isEmpty()) return
        val step = if (samples.size > 1) width / (samples.size - 1) else width
        path.reset()
        fillPath.reset()
        samples.forEachIndexed { index, value ->
            val x = width - (samples.size - 1 - index) * step
            val y = height - (value / 100.0).toFloat() * (height - dip(16f)) - dip(4f)
            if (index == 0) path.moveTo(x, y) else path.lineTo(x, y)
        }
        fillPath.set(path)
        fillPath.lineTo(width, height)
        fillPath.lineTo(0f, height)
        fillPath.close()
        canvas.drawPath(fillPath, paintFill)
        paintLine.color = lineColor
        canvas.drawPath(path, paintLine)
    }

    companion object {
        private const val MAX_POINTS = 240
    }
}
