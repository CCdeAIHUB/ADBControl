package com.adbcontrol.remote.ui.device

import kotlin.math.min
import kotlin.math.roundToInt

/**
 * 屏幕画面布局与控制坐标的唯一换算入口。
 *
 * 画面始终按 contain 规则保持源宽高比；落在黑边/留白的触控必须被拒绝，避免被错误
 * 压缩到设备边缘。截图可以是服务端缩放图，因此映射目标尺寸与内容尺寸分开传入。
 */
object ScreenViewportMapper {
    data class Viewport(
        val left: Float,
        val top: Float,
        val width: Float,
        val height: Float,
    ) {
        val right: Float get() = left + width
        val bottom: Float get() = top + height
    }

    data class Point(val x: Int, val y: Int)

    fun fit(containerWidth: Int, containerHeight: Int, contentWidth: Int, contentHeight: Int): Viewport {
        if (containerWidth <= 0 || containerHeight <= 0 || contentWidth <= 0 || contentHeight <= 0) {
            return Viewport(0f, 0f, 0f, 0f)
        }
        val scale = min(
            containerWidth.toFloat() / contentWidth,
            containerHeight.toFloat() / contentHeight,
        )
        val width = contentWidth * scale
        val height = contentHeight * scale
        return Viewport(
            left = (containerWidth - width) / 2f,
            top = (containerHeight - height) / 2f,
            width = width,
            height = height,
        )
    }

    fun map(x: Float, y: Float, viewport: Viewport, targetWidth: Int, targetHeight: Int): Point? {
        if (viewport.width <= 0f || viewport.height <= 0f || targetWidth <= 0 || targetHeight <= 0) return null
        if (x < viewport.left || x > viewport.right || y < viewport.top || y > viewport.bottom) return null
        val normalizedX = ((x - viewport.left) / viewport.width).coerceIn(0f, 1f)
        val normalizedY = ((y - viewport.top) / viewport.height).coerceIn(0f, 1f)
        return Point(
            (normalizedX * (targetWidth - 1)).roundToInt().coerceIn(0, targetWidth - 1),
            (normalizedY * (targetHeight - 1)).roundToInt().coerceIn(0, targetHeight - 1),
        )
    }

    /** 让设备物理尺寸方向与当前视频/截图方向一致。 */
    fun orientTarget(targetWidth: Int, targetHeight: Int, contentWidth: Int, contentHeight: Int): Pair<Int, Int> {
        val targetLandscape = targetWidth > targetHeight
        val contentLandscape = contentWidth > contentHeight
        return if (targetLandscape == contentLandscape) {
            targetWidth to targetHeight
        } else {
            targetHeight to targetWidth
        }
    }
}
