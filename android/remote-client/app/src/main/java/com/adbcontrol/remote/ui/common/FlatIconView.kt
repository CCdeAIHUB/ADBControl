package com.adbcontrol.remote.ui.common

import android.content.Context
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.Path
import android.view.View

/** Dependency-free, single-weight flat icon set used by every Android page. */
class FlatIconView(context: Context, private val icon: AppIcon, private val tint: Int = context.pal.brand) : View(context) {
    private val paint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = tint
        style = Paint.Style.STROKE
        strokeWidth = context.dp(2).toFloat()
        strokeCap = Paint.Cap.ROUND
        strokeJoin = Paint.Join.ROUND
    }

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        val w = width.toFloat(); val h = height.toFloat(); val l = w * .22f; val r = w * .78f; val t = h * .22f; val b = h * .78f
        when (icon) {
            AppIcon.HOME -> { val p=Path();p.moveTo(l,h*.5f);p.lineTo(w*.5f,t);p.lineTo(r,h*.5f);p.lineTo(r,b);p.lineTo(l,b);p.close();canvas.drawPath(p,paint) }
            AppIcon.DEVICE -> { canvas.drawRoundRect(l,t,r,b,6f,6f,paint);canvas.drawLine(w*.43f,h*.7f,w*.57f,h*.7f,paint) }
            AppIcon.TASK -> { canvas.drawRoundRect(l,t,r,b,5f,5f,paint); for(i in 0..2){val y=h*(.35f+i*.15f);canvas.drawLine(w*.33f,y,w*.4f,y,paint);canvas.drawLine(w*.47f,y,w*.68f,y,paint)} }
            AppIcon.USER -> { canvas.drawCircle(w*.5f,h*.38f,w*.13f,paint);canvas.drawArc(l,h*.48f,r,h*.9f,200f,140f,false,paint) }
            AppIcon.SCREEN -> { canvas.drawRoundRect(l,t,r,h*.68f,5f,5f,paint);canvas.drawLine(w*.42f,b,w*.58f,b,paint);canvas.drawLine(w*.5f,h*.68f,w*.5f,b,paint) }
            AppIcon.FOLDER -> { val p=Path();p.moveTo(l,h*.35f);p.lineTo(w*.42f,h*.35f);p.lineTo(w*.48f,h*.43f);p.lineTo(r,h*.43f);p.lineTo(r,b);p.lineTo(l,b);p.close();canvas.drawPath(p,paint) }
            AppIcon.APP -> { for(x in 0..1)for(y in 0..1)canvas.drawRoundRect(w*(.25f+x*.28f),h*(.25f+y*.28f),w*(.45f+x*.28f),h*(.45f+y*.28f),4f,4f,paint) }
            AppIcon.SETTINGS -> { canvas.drawCircle(w*.5f,h*.5f,w*.12f,paint);canvas.drawCircle(w*.5f,h*.5f,w*.28f,paint) }
            AppIcon.LOG -> { canvas.drawRoundRect(l,t,r,b,5f,5f,paint);canvas.drawLine(w*.32f,h*.38f,w*.68f,h*.38f,paint);canvas.drawLine(w*.32f,h*.52f,w*.68f,h*.52f,paint);canvas.drawLine(w*.32f,h*.66f,w*.58f,h*.66f,paint) }
            AppIcon.TERMINAL -> { canvas.drawRoundRect(l,t,r,b,5f,5f,paint);canvas.drawLine(w*.31f,h*.42f,w*.42f,h*.5f,paint);canvas.drawLine(w*.42f,h*.5f,w*.31f,h*.58f,paint);canvas.drawLine(w*.48f,h*.6f,w*.66f,h*.6f,paint) }
            AppIcon.POWER -> { canvas.drawArc(l,t,r,b,-55f,290f,false,paint);canvas.drawLine(w*.5f,h*.17f,w*.5f,h*.48f,paint) }
            AppIcon.LOCK -> { canvas.drawRoundRect(w*.28f,h*.44f,w*.72f,h*.78f,5f,5f,paint);canvas.drawArc(w*.36f,h*.2f,w*.64f,h*.58f,180f,-180f,false,paint) }
            AppIcon.BACK -> { canvas.drawLine(w*.68f,h*.24f,w*.34f,h*.5f,paint);canvas.drawLine(w*.34f,h*.5f,w*.68f,h*.76f,paint) }
            AppIcon.FORWARD -> { canvas.drawLine(w*.32f,h*.24f,w*.66f,h*.5f,paint);canvas.drawLine(w*.66f,h*.5f,w*.32f,h*.76f,paint) }
            AppIcon.GENERIC -> { canvas.drawCircle(w*.5f,h*.5f,w*.27f,paint);canvas.drawCircle(w*.5f,h*.5f,w*.04f,paint) }
        }
    }
}

enum class AppIcon { HOME, DEVICE, TASK, USER, SCREEN, FOLDER, APP, SETTINGS, LOG, TERMINAL, POWER, LOCK, BACK, FORWARD, GENERIC }

fun iconFor(value: String): AppIcon = when {
    value.contains("主页") || value in setOf("⌂", "🏠") -> AppIcon.HOME
    value.contains("设备") || value.contains("手机") || value in setOf("📱", "📲") -> AppIcon.DEVICE
    value.contains("任务") || value in setOf("🗓", "⏱", "🔔") -> AppIcon.TASK
    value.contains("我的") || value.contains("账号") || value in setOf("👤", "👥") -> AppIcon.USER
    value.contains("预览") || value.contains("投屏") || value in setOf("🖥", "📺") -> AppIcon.SCREEN
    value.contains("文件") || value in setOf("📁", "📂", "📄") -> AppIcon.FOLDER
    value.contains("应用") || value in setOf("📦", "🤖") -> AppIcon.APP
    value.contains("设置") || value == "🎨" -> AppIcon.SETTINGS
    value.contains("日志") -> AppIcon.LOG
    value.contains("终端") -> AppIcon.TERMINAL
    value.contains("重启") || value == "⏻" -> AppIcon.POWER
    value.contains("锁") || value == "🔒" -> AppIcon.LOCK
    else -> AppIcon.GENERIC
}
