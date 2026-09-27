package com.adbcontrol.remote.ui.device

import android.annotation.SuppressLint
import android.content.Context
import android.app.Activity
import android.graphics.BitmapFactory
import android.os.Handler
import android.os.Looper
import android.util.Base64
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.ViewGroup
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.HorizontalScrollView
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import android.view.SurfaceHolder
import android.view.SurfaceView
import com.adbcontrol.remote.core.AppGraph
import com.adbcontrol.remote.data.adb.LockStateParser
import com.adbcontrol.remote.data.adb.WmSizeParser
import com.adbcontrol.remote.data.settings.SettingsStore
import com.adbcontrol.remote.model.RemoteDevice
import com.adbcontrol.remote.model.RemoteResult
import com.adbcontrol.remote.ui.common.BasePage
import com.adbcontrol.remote.ui.common.PageHost
import com.adbcontrol.remote.ui.common.withAlpha
import org.json.JSONObject
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.math.abs
import com.adbcontrol.remote.transport.H264SurfaceDecoder
import com.adbcontrol.remote.transport.ScreenEndpointPolicy
import com.adbcontrol.remote.transport.ScreenStreamClient

/**
 * 预览控制页（对应桌面端“预览”Tab + 预览触控 + 锁屏覆盖层 + 安全键盘）：
 * - 截图轮询：伴侣 accessibility.screenshot 通道（远程协议限制：adb screencap 二进制经 JSON 文本会损坏），
 *   间隔 500–60000ms（与桌面端一致，默认 3000）；
 * - 手势触控：点击/长按/滑动经 `input tap/swipe`（桌面端 ADB 路径同款），坐标按截图尺寸等比映射；
 * - 锁屏：锁定时显示覆盖层与“上滑解锁”（桌面端同款手势参数），不保留旧屏图；
 * - 底部快捷条：返回/主页/多任务/音量/电源/截图/键盘。
 */
class PreviewControlPage(
    context: Context,
    host: PageHost,
    private val graph: AppGraph,
    private val device: RemoteDevice,
) : BasePage(context, host) {

    private val mainHandler = Handler(Looper.getMainLooper())
    private val settings = SettingsStore(context.applicationContext)
    private val refreshing = AtomicBoolean(false)

    private val imageView = ImageView(context).apply {
        scaleType = ImageView.ScaleType.FIT_CENTER
        adjustViewBounds = true
        setBackgroundColor(0xFF10131A.toInt())
    }
    private val surfaceView = SurfaceView(context).apply { visibility = View.GONE }
    private val stateText = text("点击“刷新”或开启自动预览", 12f, pal.muted)
    private val lockOverlay = buildLockOverlay().apply { visibility = View.GONE }
    private var lastPngHash: Int = 0
    private var screenSize: WmSizeParser.ScreenSize? = null
    private var latestImageSize: Pair<Int, Int>? = null
    private var autoTimer: Runnable? = null
    private var stream: ScreenStreamClient? = null
    private var decoder: H264SurfaceDecoder? = null
    private var codecConfig: ByteArray? = null
    private var streamSize: Pair<Int, Int>? = null
    private var rootView: LinearLayout? = null
    private var topBarView: View? = null
    private var previewHolderView: FrameLayout? = null
    private var previewAreaView: FrameLayout? = null
    private var controlsView: View? = null
    private var fullScreen = false
    private val exitFullScreenButton = secondaryButton("退出全屏") { toggleFullScreen() }.apply {
        visibility = View.GONE
    }
    private val fullScreenControls = row {
        visibility = View.GONE
        background = shape(withAlpha(pal.terminalBackground, 0xDD), 12, pal.border)
        setPadding(dp(6), dp(6), dp(6), dp(6))
        listOf(
            "返回" to "input keyevent KEYCODE_BACK",
            "主页" to "input keyevent KEYCODE_HOME",
            "多任务" to "input keyevent KEYCODE_APP_SWITCH",
        ).forEach { (label, command) ->
            addView(secondaryButton(label) { sendShell(command) }, LinearLayout.LayoutParams(dp(76), dp(40)).apply { rightMargin = dp(4) })
        }
    }

    override fun build(): View {
        val root = column {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(pal.background)
        }
        rootView = root
        val header = topBar("实时控制 · ${device.displayName}", onBack = { host.popPage() })
        topBarView = header
        root.addView(header, LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, dp(56),
        ))

        // 预览区：带边框圆角卡片，截图/锁屏覆盖层同一容器，触控手势装在容器上。
        val previewArea = FrameLayout(context).apply {
            background = shape(pal.surface, 12, pal.border)
            clipToOutline = true
            outlineProvider = object : android.view.ViewOutlineProvider() {
                override fun getOutline(view: View, outline: android.graphics.Outline) {
                    outline.setRoundRect(0, 0, view.width, view.height, dp(12).toFloat())
                }
            }
        }
        previewAreaView = previewArea
        val previewPadding = FrameLayout(context).apply { setPadding(dp(8), dp(8), dp(8), dp(8)) }
        previewPadding.addView(surfaceView, FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT,
        ))
        previewPadding.addView(imageView, FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT,
        ))
        previewArea.addView(previewPadding, FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT,
        ))
        previewArea.addView(lockOverlay, FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT,
        ))
        previewArea.addView(exitFullScreenButton, FrameLayout.LayoutParams(dp(104), dp(40), Gravity.TOP or Gravity.END).apply {
            topMargin = dp(12)
            rightMargin = dp(12)
        })
        previewArea.addView(fullScreenControls, FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL,
        ).apply { bottomMargin = dp(14) })
        installTouchHandling(previewArea)
        val previewHolder = FrameLayout(context).apply { setBackgroundColor(pal.background) }
        previewHolderView = previewHolder
        previewHolder.setPadding(dp(14), dp(10), dp(14), 0)
        previewHolder.addView(previewArea, FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, dp(280),
        ))
        root.addView(previewHolder, LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT,
        ))
        controlsView = scroll(controlBody())
        root.addView(controlsView, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f))
        surfaceView.holder.addCallback(object : SurfaceHolder.Callback {
            override fun surfaceCreated(holder: SurfaceHolder) = Unit
            override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) = Unit
            override fun surfaceDestroyed(holder: SurfaceHolder) { stopLive("显示区域已释放") }
        })
        refreshScreenSize()
        startAutoIfNeeded()
        return root
    }

    private fun controlBody(): View {
        val body = column(12)
        body.addView(stateText)
        body.addView(row {
            addView(primaryButton("开始实时投屏") { startLive() }, LinearLayout.LayoutParams(0, dp(40), 1f))
            addView(secondaryButton("全屏") { toggleFullScreen() }, LinearLayout.LayoutParams(0, dp(40), 1f).apply { leftMargin = dp(8) })
        })
        var autoToggleRef: android.widget.Button? = null
        val autoButton = secondaryButton("开启自动") {
            if (autoTimer == null) startAuto() else stopAuto()
            autoToggleRef?.text = if (autoTimer == null) "开启自动" else "停止自动"
        }
        autoToggleRef = autoButton
        body.addView(row {
            addView(primaryButton("刷新截图") { refresh() }, LinearLayout.LayoutParams(0, dp(40), 1f))
            addView(autoButton, LinearLayout.LayoutParams(0, dp(40), 1f).apply { leftMargin = dp(8) })
        })
        body.addView(row {
            val intervalInput = input("间隔毫秒 500-60000").apply {
                setText(settings.previewIntervalMs.toString())
                inputType = android.text.InputType.TYPE_CLASS_NUMBER
            }
            addView(intervalInput, LinearLayout.LayoutParams(0, dp(40), 1f))
            addView(secondaryButton("保存间隔") {
                intervalInput.text.toString().toIntOrNull()?.let { settings.previewIntervalMs = it }
                host.notify("预览间隔已保存：${settings.previewIntervalMs}ms")
            }, LinearLayout.LayoutParams(0, dp(40), 1f).apply { leftMargin = dp(8) })
        })
        body.addView(sectionTitle("快捷键", "单击发送，与桌面端预览页快捷键一致"))
        body.addView(quickBar())
        body.addView(text("触控：单击=点击，长按≥500ms=长按，拖动=滑动；坐标按截图分辨率等比映射。", 11f, pal.muted))
        body.addView(text("实时模式与 Web 版共用 scrcpy H.264；连接不可用时仍可使用下方截图兼容模式。", 11f, pal.muted))
        return body
    }

    private fun startLive() {
        val profile = graph.activeProfile
        val session = graph.activeSession
        if (profile == null || session == null) {
            stateText.text = "登录会话已失效，请重新登录"
            return
        }
        val endpoint = ScreenEndpointPolicy.derive(profile.endpoint, profile.webEndpoint)
        if (endpoint == null) {
            stateText.text = "公网投屏必须配置 HTTPS/WSS 投屏服务地址；局域网地址可自动使用 18087 端口"
            return
        }
        if (!surfaceView.holder.surface.isValid) {
            stateText.text = "投屏显示区域尚未就绪，请稍后重试"
            return
        }
        stopAuto()
        stream?.close()
        stream = ScreenStreamClient(endpoint, session.token, device.id, object : ScreenStreamClient.Listener {
            override fun onMeta(width: Int, height: Int) {
                mainHandler.post {
                    streamSize = width to height
                    latestImageSize = streamSize
                    stateText.text = "实时投屏连接成功 · ${width}x$height"
                }
            }
            override fun onConfig(data: ByteArray) {
                mainHandler.post {
                    codecConfig = data
                    val size = streamSize ?: return@post
                    runCatching {
                        decoder?.close()
                        decoder = H264SurfaceDecoder(surfaceView.holder.surface).also { it.configure(size.first, size.second, data) }
                        surfaceView.visibility = View.VISIBLE
                        imageView.visibility = View.GONE
                    }.onFailure {
                        stateText.text = "视频解码初始化失败：${it.message}"
                        com.adbcontrol.remote.data.log.AppDiagnostics.failure("screen.decoder.configure", "media.codec", 0, "SCREEN_DECODER_CONFIG_FAILED", "type=${it.javaClass.simpleName}")
                    }
                }
            }
            override fun onFrame(data: ByteArray, presentationTimeUs: Long, keyFrame: Boolean) {
                runCatching { decoder?.queue(data, presentationTimeUs, keyFrame) }.onFailure {
                    mainHandler.post { stateText.text = "视频帧解码失败：${it.message}" }
                    com.adbcontrol.remote.data.log.AppDiagnostics.failure("screen.decoder.frame", "media.codec", 0, "SCREEN_DECODER_FRAME_FAILED", "type=${it.javaClass.simpleName}")
                }
            }
            override fun onState(message: String, errorCode: String) {
                mainHandler.post {
                    stateText.text = if (errorCode.isBlank()) message else "$message（$errorCode）"
                }
            }
        }).also { it.start(30) }
        stateText.text = "正在建立实时 H.264 投屏…"
    }

    private fun stopLive(message: String = "实时投屏已停止") {
        stream?.close()
        stream = null
        decoder?.close()
        decoder = null
        codecConfig = null
        surfaceView.visibility = View.GONE
        imageView.visibility = View.VISIBLE
        if (message.isNotBlank()) stateText.text = message
    }

    private fun toggleFullScreen() {
        fullScreen = !fullScreen
        topBarView?.visibility = if (fullScreen) View.GONE else View.VISIBLE
        controlsView?.visibility = if (fullScreen) View.GONE else View.VISIBLE
        exitFullScreenButton.visibility = if (fullScreen) View.VISIBLE else View.GONE
        fullScreenControls.visibility = if (fullScreen) View.VISIBLE else View.GONE
        previewHolderView?.layoutParams = LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            if (fullScreen) 0 else ViewGroup.LayoutParams.WRAP_CONTENT,
            if (fullScreen) 1f else 0f,
        )
        previewAreaView?.layoutParams = FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            if (fullScreen) ViewGroup.LayoutParams.MATCH_PARENT else dp(280),
        )
        previewHolderView?.setPadding(if (fullScreen) 0 else dp(14), if (fullScreen) 0 else dp(10), if (fullScreen) 0 else dp(14), 0)
        (context as? Activity)?.window?.decorView?.systemUiVisibility = if (fullScreen) {
            View.SYSTEM_UI_FLAG_FULLSCREEN or View.SYSTEM_UI_FLAG_HIDE_NAVIGATION or View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
        } else View.SYSTEM_UI_FLAG_VISIBLE
    }

    /** 快捷键横向滑动条：固定 72dp 宽度胶囊按钮，永不换行挤压。 */
    private fun quickBar(): View {
        val actions = listOf(
            "返回" to "input keyevent KEYCODE_BACK",
            "主页" to "input keyevent KEYCODE_HOME",
            "多任务" to "input keyevent KEYCODE_APP_SWITCH",
            "音量+" to "input keyevent KEYCODE_VOLUME_UP",
            "音量-" to "input keyevent KEYCODE_VOLUME_DOWN",
            "电源" to "input keyevent KEYCODE_POWER",
            "唤醒" to "input keyevent KEYCODE_WAKEUP",
            "键盘" to "",
        )
        val chips = row {
            setPadding(0, 0, 0, 0)
            actions.forEach { (label, command) ->
                addView(
                    secondaryButton(label) {
                        if (command.isBlank()) {
                            SafeKeyboardSheet(context, host, graph, device) { refresh() }.show()
                        } else {
                            sendShell(command)
                        }
                    },
                    // 88dp：容纳“音量+”等双字符标签（按钮左右内边距 12dp×2）。
                    LinearLayout.LayoutParams(dp(88), dp(40)).apply { rightMargin = dp(8) },
                )
            }
        }
        return HorizontalScrollView(context).apply {
            isHorizontalScrollBarEnabled = false
            clipToPadding = false
            addView(chips, ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT,
            ))
        }
    }

    // ---------- 截图轮询 ----------

    private fun startAutoIfNeeded() {
        if (settings.previewIntervalMs > 0) startAuto()
    }

    private fun startAuto() {
        stopAuto()
        val runnable = object : Runnable {
            override fun run() {
                refresh()
                autoTimer = this
                mainHandler.postDelayed(this, settings.previewIntervalMs.toLong())
            }
        }
        autoTimer = runnable
        mainHandler.postDelayed(runnable, 100)
    }

    private fun stopAuto() {
        autoTimer?.let { mainHandler.removeCallbacks(it) }
        autoTimer = null
    }

    private fun refresh() {
        if (!refreshing.compareAndSet(false, true)) return
        stateText.text = "正在请求截图…"
        host.runRemote({
            graph.companion.screenshot(device.id, maxSize = 960)
        }) { result ->
            refreshing.set(false)
            when (result) {
                is RemoteResult.Failure -> {
                    stateText.text = "${result.error.message}（${result.error.errorCode}）"
                    if (result.error.errorCode == "COMPANION_SCREENSHOT_LOCKED") setLocked(true)
                }
                is RemoteResult.Success -> showPng(result.value)
            }
        }
    }

    private fun showPng(payload: JSONObject) {
        // 伴侣结果：{ok?, result:{format,pngBase64}}；兼容直接平铺 pngBase64。
        val base64 = payload.optString("pngBase64").ifBlank {
            payload.optJSONObject("result")?.optString("pngBase64").orEmpty()
        }
        if (base64.isBlank()) {
            stateText.text = "未返回 PNG 数据；请确认伴侣 App 无障碍授权与前台连接。"
            return
        }
        val bytes = try {
            Base64.decode(base64, Base64.DEFAULT)
        } catch (error: Throwable) {
            stateText.text = "截图数据解析失败：${error.message}"
            return
        }
        // 帧去重：与桌面端一致，相同帧不重复渲染。
        if (bytes.contentHashCode() == lastPngHash) {
            stateText.text = "画面未变化"
            return
        }
        lastPngHash = bytes.contentHashCode()
        val bitmap = BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
        if (bitmap == null) {
            stateText.text = "截图无法解码"
            return
        }
        latestImageSize = bitmap.width to bitmap.height
        imageView.setImageBitmap(bitmap)
        setLocked(false)
        stateText.text = "已更新 · ${bitmap.width}x${bitmap.height} · ${bytes.size / 1024} KiB"
    }

    // ---------- 锁屏覆盖层 ----------

    private fun buildLockOverlay(): View = column {
        gravity = Gravity.CENTER
        // 锁屏遮罩压在截图上：两种主题都用终端深色底，保证遮盖力一致。
        setBackgroundColor(withAlpha(pal.terminalBackground, 0xE6))
        addView(com.adbcontrol.remote.ui.common.FlatIconView(context, com.adbcontrol.remote.ui.common.AppIcon.LOCK, pal.brand), LinearLayout.LayoutParams(dp(48), dp(48)))
        addView(text("设备当前处于锁屏状态", 15f, pal.text, true))
        addView(text("解锁后才能获取屏幕内容", 12f, pal.secondary))
        val unlockButton = primaryButton("上滑解锁") { performUnlock() }
        addView(unlockButton, ViewGroup.LayoutParams(dp(180), dp(42)))
        val keyboardButton = secondaryButton("安全键盘输入 PIN") {
            SafeKeyboardSheet(context, host, graph, device) { refresh() }.show()
        }
        addView(keyboardButton, ViewGroup.LayoutParams(dp(180), dp(40)))
    }

    /** 与桌面端解锁序列一致：唤醒 → 上滑（0.82h→0.28h, 420ms）。PIN 输入交由安全键盘逐键发送。 */
    private fun performUnlock() {
        host.runRemote({ graph.companion.wake(device.id) }) { wakeResult ->
            if (wakeResult is RemoteResult.Failure) {
                host.notify("唤醒失败：${wakeResult.error.message}（${wakeResult.error.errorCode}）")
            }
        }
        host.runRemote({ graph.commands.shell(device.id, "wm size") }) { sizeResult ->
            val size = (sizeResult as? RemoteResult.Success)?.value?.stdout?.let { WmSizeParser.parse(it) }
                ?: WmSizeParser.ScreenSize(1080, 1920)
            host.runRemote({
                graph.commands.shell(
                    device.id,
                    "input touchscreen swipe ${size.width / 2} ${(size.height * 0.8).toInt()} ${size.width / 2} ${(size.height * 0.2).toInt()} 420",
                )
            }) { result ->
                if (result is RemoteResult.Failure) {
                    // ADB 输入被拒（INJECT_EVENTS）时回退伴侣无障碍手势（桌面端同款回退）。
                    host.runRemote({
                        graph.companion.swipe(
                            device.id,
                            size.width / 2, (size.height * 0.82).toInt(),
                            size.width / 2, (size.height * 0.28).toInt(), 420,
                            size.width, size.height,
                        )
                    }) { fallback ->
                        if (fallback is RemoteResult.Failure) {
                            host.notify("解锁失败：${fallback.error.message}")
                        }
                    }
                }
            }
        }
    }

    private fun setLocked(locked: Boolean) {
        lockOverlay.visibility = if (locked) View.VISIBLE else View.GONE
    }

    // ---------- 触控 ----------

    @SuppressLint("ClickableViewAccessibility")
    private fun installTouchHandling(area: View) {
        var downX = 0f
        var downY = 0f
        var downAt = 0L
        area.setOnTouchListener { _, event ->
            when (event.actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    downX = event.x; downY = event.y; downAt = System.currentTimeMillis()
                    dispatchLiveTouch(MotionEvent.ACTION_DOWN, event.x, event.y)
                    true
                }
                MotionEvent.ACTION_MOVE -> {
                    dispatchLiveTouch(MotionEvent.ACTION_MOVE, event.x, event.y)
                    true
                }
                MotionEvent.ACTION_UP -> {
                    val upX = event.x
                    val upY = event.y
                    val elapsed = System.currentTimeMillis() - downAt
                    if (stream != null) dispatchLiveTouch(MotionEvent.ACTION_UP, upX, upY)
                    else dispatchGesture(downX, downY, upX, upY, elapsed)
                    true
                }
                else -> true
            }
        }
    }

    private fun dispatchLiveTouch(action: Int, x: Float, y: Float) {
        val client = stream ?: return
        val size = streamSize ?: return
        val width = imageView.width.coerceAtLeast(1)
        val height = imageView.height.coerceAtLeast(1)
        val videoAspect = size.first.toFloat() / size.second
        val viewAspect = width.toFloat() / height
        val scale = if (viewAspect > videoAspect) height.toFloat() / size.second else width.toFloat() / size.first
        val offsetX = (width - size.first * scale) / 2f
        val offsetY = (height - size.second * scale) / 2f
        val mappedX = ((x - offsetX) / scale).toInt().coerceIn(0, size.first - 1)
        val mappedY = ((y - offsetY) / scale).toInt().coerceIn(0, size.second - 1)
        client.touch(action, mappedX, mappedY, size.first, size.second)
    }

    /** 手势判定与桌面端一致：位移小且 <500ms 为点击；原地点按 ≥500ms 为长按；否则滑动。 */
    private fun dispatchGesture(downX: Float, downY: Float, upX: Float, upY: Float, elapsedMs: Long) {
        val image = latestImageSize ?: run {
            host.notify("请先获取一张截图，再进行触控")
            return
        }
        val screen = screenSize ?: run {
            host.notify("尚未读取设备分辨率，无法映射坐标")
            return
        }
        val viewWidth = imageView.width.toFloat().coerceAtLeast(1f)
        val viewHeight = imageView.height.toFloat().coerceAtLeast(1f)
        fun map(x: Float, y: Float): Pair<Int, Int> {
            val imageAspect = image.first.toFloat() / image.second
            val viewAspect = viewWidth / viewHeight
            val scale: Float
            val offsetX: Float
            val offsetY: Float
            if (viewAspect > imageAspect) {
                scale = viewHeight / image.second
                offsetX = (viewWidth - image.first * scale) / 2f
                offsetY = 0f
            } else {
                scale = viewWidth / image.first
                offsetX = 0f
                offsetY = (viewHeight - image.second * scale) / 2f
            }
            val deviceX = ((x - offsetX) / scale).toInt().coerceIn(0, image.first - 1)
            val deviceY = ((y - offsetY) / scale).toInt().coerceIn(0, image.second - 1)
            return deviceX to deviceY
        }

        val (startX, startY) = map(downX, downY)
        val (endX, endY) = map(upX, upY)
        val moved = abs(upX - downX) > dp(12) || abs(upY - downY) > dp(12)
        when {
            !moved && elapsedMs < 500 -> sendShell("input tap $startX $startY")
            !moved -> sendShell("input swipe $startX $startY $startX $startY ${elapsedMs.coerceIn(650, 3000)}")
            else -> sendShell("input swipe $startX $startY $endX $endY ${elapsedMs.coerceIn(120, 3000)}")
        }
    }

    private fun sendShell(command: String) {
        host.runRemote({ graph.commands.shell(device.id, command) }) { result ->
            when (result) {
                is RemoteResult.Failure -> host.notify("${result.error.message}（${result.error.errorCode}）")
                is RemoteResult.Success -> if (result.value.exitCode != 0) {
                    // INJECT_EVENTS 场景回退到伴侣无障碍触控（桌面端行为一致）。
                    fallbackCompanionTouch(command)
                }
            }
        }
    }

    private fun fallbackCompanionTouch(command: String) {
        val tap = Regex("input tap (\\d+) (\\d+)").find(command)
        val swipe = Regex("input swipe (\\d+) (\\d+) (\\d+) (\\d+)(?: (\\d+))?").find(command)
        val image = latestImageSize
        val screen = screenSize
        if (image == null || screen == null) return
        when {
            tap != null -> host.runRemote({
                graph.companion.tap(device.id, tap.groupValues[1].toInt(), tap.groupValues[2].toInt(), image.first, image.second)
            }) { if (it is RemoteResult.Failure) host.notify(it.error.message) }
            swipe != null -> host.runRemote({
                graph.companion.swipe(
                    device.id,
                    swipe.groupValues[1].toInt(), swipe.groupValues[2].toInt(),
                    swipe.groupValues[3].toInt(), swipe.groupValues[4].toInt(),
                    swipe.groupValues.getOrNull(5)?.toIntOrNull() ?: 250,
                    image.first, image.second,
                )
            }) { if (it is RemoteResult.Failure) host.notify(it.error.message) }
        }
    }

    private fun refreshScreenSize() {
        host.runRemote({ graph.commands.shell(device.id, "wm size") }) { result ->
            if (result is RemoteResult.Success) {
                screenSize = WmSizeParser.parse(result.value.stdout)
            }
        }
    }

    override fun onDetach() {
        stopAuto()
        stopLive("")
        if (fullScreen) toggleFullScreen()
    }
}
