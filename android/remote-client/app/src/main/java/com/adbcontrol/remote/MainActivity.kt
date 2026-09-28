package com.adbcontrol.remote

import android.annotation.SuppressLint
import android.app.Activity
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.View
import android.view.ViewGroup
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.Toast
import com.adbcontrol.remote.core.AppGraph
import com.adbcontrol.remote.data.log.AppDiagnostics
import com.adbcontrol.remote.model.AppState
import com.adbcontrol.remote.model.RemoteDevice
import com.adbcontrol.remote.model.RemoteResult
import com.adbcontrol.remote.ui.AuthScreens
import com.adbcontrol.remote.ui.MainShell
import com.adbcontrol.remote.ui.common.Page
import com.adbcontrol.remote.ui.common.PageHost
import com.adbcontrol.remote.ui.common.themedDialogBuilder
import com.adbcontrol.remote.navigation.PageStackState

/**
 * 唯一 Activity：
 * - 认证态（NeedsCore/Connecting/NeedsLogin/MustChangePassword/Failed）全屏渲染；
 * - Ready 态 = MainShell（底部 Tab）+ 二级页面栈（设备详情与各工具页），返回键逐级回退；
 * - 会话失效（REMOTE_AUTH_*）自动回到登录页（状态机只能由 AppController 流转）。
 */
class MainActivity : Activity(), PageHost {

    private val mainHandler = Handler(Looper.getMainLooper())
    private lateinit var graph: AppGraph
    private lateinit var controller: AppController
    private lateinit var rootContainer: FrameLayout

    // 页面栈：主壳在底，二级页依次覆盖。
    private val pageStack = mutableListOf<Page>()
    private val pageStackState = PageStackState()
    private var currentSession: AppState.Ready? = null
    private var lastBackAt = 0L

    private var pendingImagePick: ((ByteArray) -> Unit)? = null
    private var pendingFilePick: ((name: String, bytes: ByteArray) -> Unit)? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val application = application as RemoteApplication
        graph = application.graph
        AppDiagnostics.initialize(this)
        applySystemBarColors()

        controller = AppController(application.profileStore, graph.transport, graph.repository, graph.executor) { state ->
            mainHandler.post { render(state) }
        }
        rootContainer = FrameLayout(this)
        setContentView(rootContainer)
        installSystemBarInsets()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            onBackInvokedDispatcher.registerOnBackInvokedCallback(
                android.window.OnBackInvokedDispatcher.PRIORITY_DEFAULT,
            ) { handleBack() }
        }
        controller.start()
    }

    private fun applySystemBarColors() {
        val palette = com.adbcontrol.remote.core.ThemeManager.palette(this)
        window.statusBarColor = palette.background
        window.navigationBarColor = palette.background
        window.setBackgroundDrawable(android.graphics.drawable.ColorDrawable(palette.background))
        val flags = window.decorView.systemUiVisibility
        window.decorView.systemUiVisibility = if (palette.isDark) {
            flags and View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR.inv()
        } else {
            flags or View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR
        }
    }

    /**
     * 全面屏重叠修复：targetSdk 35+ 强制 edge-to-edge，内容会顶到状态栏/手势条下面。
     * 在根容器统一避让系统栏与刘海（状态栏/手势条区域显示主题背景色），
     * 子页面不再各自处理，保证所有页面一次性修复。
     */
    private fun installSystemBarInsets() {
        rootContainer.setOnApplyWindowInsetsListener { view, insets ->
            val rect = systemBarInsets(insets)
            view.setPadding(rect.left, rect.top, rect.right, rect.bottom)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                android.view.WindowInsets.CONSUMED
            } else {
                @Suppress("DEPRECATION")
                insets.consumeSystemWindowInsets()
            }
        }
    }

    private fun systemBarInsets(insets: android.view.WindowInsets): android.graphics.Rect {
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            val bars = insets.getInsets(
                android.view.WindowInsets.Type.systemBars() or android.view.WindowInsets.Type.displayCutout(),
            )
            android.graphics.Rect(bars.left, bars.top, bars.right, bars.bottom)
        } else {
            @Suppress("DEPRECATION")
            android.graphics.Rect(
                insets.systemWindowInsetLeft,
                insets.systemWindowInsetTop,
                insets.systemWindowInsetRight,
                insets.systemWindowInsetBottom,
            )
        }
    }

    private fun render(state: AppState) {
        when (state) {
            is AppState.Ready -> {
                graph.activeProfile = state.profile
                graph.activeSession = state.session
                currentSession = state
                pageStack.clear()
                pageStackState.clear()
                rootContainer.removeAllViews()
                rootContainer.addView(MainShell(this, this, graph, state.session).build(), ViewGroup.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT,
                ))
            }
            else -> {
                graph.activeSession = null
                currentSession = null
                pageStack.clear()
                pageStackState.clear()
                rootContainer.removeAllViews()
                rootContainer.addView(authView(state), ViewGroup.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT,
                ))
            }
        }
    }

    private fun authView(state: AppState): View {
        val auth = AuthScreens(this)
        return when (state) {
            AppState.NeedsCore -> auth.setup { profile ->
                if (profile.endpoint.isBlank()) {
                    notify("请填写服务器地址")
                } else controller.connect(profile)
            }
            AppState.Connecting -> auth.loading()
            is AppState.NeedsLogin -> auth.login(
                state.profile,
                onLogin = { username, password ->
                    if (username.isBlank() || password.isBlank()) notify("请输入用户名和密码")
                    else controller.login(state.profile, username, password)
                },
                onReset = controller::resetCore,
            )
            is AppState.MustChangePassword -> auth.changePassword { current, replacement ->
                controller.changePassword(state.profile, state.session, current, replacement)
            }
            is AppState.Failed -> auth.failure(
                state.error,
                onRetry = { state.profile?.let(controller::connect) ?: controller.start() },
                onReset = controller::resetCore,
            )
            is AppState.Ready -> throw IllegalStateException("Ready state must not reach authView")
        }
    }

    // ---------- PageHost ----------

    override fun <T> runRemote(action: () -> RemoteResult<T>, done: (RemoteResult<T>) -> Unit) {
        graph.executor.execute {
            val started = System.currentTimeMillis()
            val result = try {
                action()
            } catch (error: Throwable) {
                RemoteResult.Failure(
                    com.adbcontrol.remote.model.AppError(
                        "REMOTE_CLIENT_OPERATION_FAILED", error.message ?: "客户端操作失败", "remote.client", true,
                    ),
                )
            }
            AppDiagnostics.record(
                if (result is RemoteResult.Failure) "error" else "info",
                "request.end", "remote.client",
                result is RemoteResult.Success,
                System.currentTimeMillis() - started,
                (result as? RemoteResult.Failure)?.error?.errorCode.orEmpty(),
            )
            mainHandler.post {
                if (result is RemoteResult.Failure && result.error.errorCode in SESSION_INVALID_CODES) {
                    currentSession?.let { controller.requireLogin(it.profile) }
                } else {
                    done(result)
                }
            }
        }
    }

    override fun notify(message: String) {
        Toast.makeText(this, message, Toast.LENGTH_LONG).show()
    }

    override fun confirm(title: String, message: String, danger: Boolean, action: () -> Unit) {
        themedDialogBuilder()
            .setTitle(title)
            .setMessage(message)
            .setNegativeButton("取消", null)
            .setPositiveButton(if (danger) "确认执行" else "继续") { _, _ -> action() }
            .show()
    }

    override fun pushPage(page: Page) {
        // 每次只展示栈顶页面。旧实现让 MainShell 和所有历史页同时可见，造成详情页透明叠层。
        rootContainer.getChildAt(rootContainer.childCount - 1)?.visibility = View.GONE
        pageStack.add(page)
        pageStackState.push()
        rootContainer.addView(page.build(), ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT,
        ))
    }

    override fun popPage() {
        val top = pageStack.removeLastOrNull() ?: return
        check(pageStackState.pop())
        top.onDetach()
        // 只移除栈顶视图；下层是 MainShell 或更早的页面，保持其状态不变。
        if (rootContainer.childCount > 0) {
            rootContainer.removeViewAt(rootContainer.childCount - 1)
        }
        rootContainer.getChildAt(rootContainer.childCount - 1)?.visibility = View.VISIBLE
    }

    override fun openDevice(device: RemoteDevice) {
        pushPage(com.adbcontrol.remote.ui.devices.DeviceDetailPage(this, this, graph, device))
    }

    override fun openAiChat(deviceId: String?) {
        val bound = deviceId?.let { id ->
            currentSession?.let { ready ->
                ready.session.assignedDevices.firstOrNull { it == id }?.let { RemoteDevice(id = it) } ?: RemoteDevice(id = id)
            } ?: RemoteDevice(id = id)
        }
        pushPage(com.adbcontrol.remote.ui.ai.AiChatPage(this, this, graph, bound))
    }

    override fun pickImageFile(onPicked: (ByteArray) -> Unit) {
        pendingImagePick = onPicked
        val intent = Intent(Intent.ACTION_GET_CONTENT).apply {
            type = "image/*"
            addCategory(Intent.CATEGORY_OPENABLE)
        }
        startActivityForResult(Intent.createChooser(intent, "选择图片"), REQUEST_IMAGE)
    }

    override fun pickUploadFile(onPicked: (name: String, bytes: ByteArray) -> Unit) {
        pendingFilePick = onPicked
        val intent = Intent(Intent.ACTION_GET_CONTENT).apply {
            type = "*/*"
            addCategory(Intent.CATEGORY_OPENABLE)
        }
        startActivityForResult(Intent.createChooser(intent, "选择文件"), REQUEST_FILE)
    }

    @Deprecated("Deprecated in Java")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (resultCode != RESULT_OK || data?.data == null) return
        val uri = data.data!!
        val bytes = try {
            contentResolver.openInputStream(uri)?.use { it.readBytes() }
        } catch (error: Exception) {
            null
        }
        if (bytes == null) {
            notify("读取文件失败")
            return
        }
        when (requestCode) {
            REQUEST_IMAGE -> {
                // 大图压缩到 2MB 以内（模型请求体积约束）。
                val compressed = compressForUpload(bytes)
                pendingImagePick?.invoke(compressed)
            }
            REQUEST_FILE -> {
                val name = uri.lastPathSegment?.substringAfterLast('/') ?: "upload.bin"
                pendingFilePick?.invoke(name, bytes)
            }
        }
        pendingImagePick = null
        pendingFilePick = null
    }

    private fun compressForUpload(bytes: ByteArray): ByteArray {
        if (bytes.size <= 2 * 1024 * 1024) return bytes
        val bitmap = BitmapFactory.decodeByteArray(bytes, 0, bytes.size) ?: return bytes
        var quality = 80
        while (quality > 20) {
            val output = java.io.ByteArrayOutputStream()
            bitmap.compress(Bitmap.CompressFormat.JPEG, quality, output)
            val compressed = output.toByteArray()
            if (compressed.size <= 2 * 1024 * 1024) return compressed
            quality -= 15
        }
        return bytes
    }

    override fun showChoiceDialog(title: String, options: List<String>, onSelected: (String) -> Unit) {
        themedDialogBuilder()
            .setTitle(title)
            .setItems(options.toTypedArray()) { _, which -> onSelected(options[which]) }
            .setNegativeButton("取消", null)
            .show()
    }

    override fun showTextDialog(title: String, message: String) {
        themedDialogBuilder()
            .setTitle(title)
            .setMessage(message.ifBlank { "（无内容）" })
            .setPositiveButton("关闭", null)
            .show()
    }

    override fun showPromptDialog(title: String, hint: String, onConfirm: (String) -> Unit) {
        val input = EditText(this).apply { setPadding(dp(40), dp(24), dp(40), 0) }
        input.hint = hint
        themedDialogBuilder()
            .setTitle(title)
            .setView(input)
            .setNegativeButton("取消", null)
            .setPositiveButton("确定") { _, _ -> onConfirm(input.text.toString().trim()) }
            .show()
    }

    override fun showImageDialog(title: String, bitmap: Bitmap) {
        val imageView = android.widget.ImageView(this).apply {
            setImageBitmap(bitmap)
            adjustViewBounds = true
            setPadding(dp(12), dp(12), dp(12), dp(12))
        }
        themedDialogBuilder()
            .setTitle(title)
            .setView(imageView)
            .setPositiveButton("关闭", null)
            .show()
    }

    override fun recreateForTheme() {
        applySystemBarColors()
        currentSession?.let(::render) ?: controller.start()
    }

    override fun logout() {
        currentSession?.let { controller.logout(it.profile) }
    }

    override fun resetCore() = controller.resetCore()

    // ---------- 返回键 ----------

    // Android 13+ 已在 onCreate 注册预测返回；此入口只服务旧系统。Lint 无法识别该版本分流。
    @SuppressLint("GestureBackNavigation")
    @Deprecated("Android 13+ uses OnBackInvokedDispatcher")
    override fun onBackPressed() {
        // Android 12 及以下仍由 Activity 回调；Android 13+ 由上面的预测返回回调处理。
        handleBack()
    }

    private fun handleBack() {
        if (pageStack.isNotEmpty()) {
            popPage()
            return
        }
        if (currentSession != null) {
            val now = System.currentTimeMillis()
            if (now - lastBackAt < 2_000) {
                finish()
            } else {
                lastBackAt = now
                notify("再按一次返回键退出")
            }
            return
        }
        finish()
    }

    override fun onDestroy() {
        if (isFinishing) {
            graph.shutdown()
        }
        super.onDestroy()
    }

    private fun dp(value: Int): Int = (value * resources.displayMetrics.density).toInt()

    companion object {
        private const val REQUEST_IMAGE = 4001
        private const val REQUEST_FILE = 4002

        private val SESSION_INVALID_CODES = setOf("REMOTE_AUTH_REQUIRED", "REMOTE_AUTH_SESSION_INVALID")
    }
}
