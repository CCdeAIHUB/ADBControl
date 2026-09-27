package com.adbcontrol.remote.ui.ai

import android.content.Context
import android.graphics.Color
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import com.adbcontrol.remote.core.AppGraph
import com.adbcontrol.remote.data.ai.AiAgentRuntime
import com.adbcontrol.remote.data.ai.AiClient
import com.adbcontrol.remote.model.AiPermissionMode
import com.adbcontrol.remote.model.RemoteDevice
import com.adbcontrol.remote.model.RemoteResult
import com.adbcontrol.remote.ui.common.BasePage
import com.adbcontrol.remote.ui.common.PageHost
import com.adbcontrol.remote.ui.common.withAlpha

/**
 * AI 助手（对应桌面端 AI 侧边栏的最小完整子集）：
 * - 模型选择 + 权限模式（请求批准/替我审批/完全访问，与桌面一致）；
 * - 流式渲染：思考流折叠提示 + 正文增量上屏；
 * - 工具卡：每个工具执行会显示一行状态；
 * - 附件：手机相册图片（要求模型已通过多模态验证）；
 * - 会话持久化与上下文压缩在 AiConversationStore（阈值与桌面一致）。
 */
class AiChatPage(
    context: Context,
    host: PageHost,
    private val graph: AppGraph,
    private val boundDevice: RemoteDevice?,
) : BasePage(context, host), AiAgentRuntime.Host {

    private val messagesContainer = column(10)
    private lateinit var messagesScroll: ScrollView
    private lateinit var inputBox: EditText
    private lateinit var modelButton: TextView
    private lateinit var modeButton: TextView

    private var streamingView: TextView? = null
    private val cancelFlag = java.util.concurrent.atomic.AtomicBoolean(false)
    private var attachedImage: ByteArray? = null

    override fun build(): View {
        val root = column {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(pal.background)
        }
        root.addView(topBar("AI 助手${boundDevice?.let { " · ${it.displayName}" } ?: ""}", onBack = {
            cancelFlag.set(true)
            host.popPage()
        }))
        // 工具栏：模型 / 权限模式。
        root.addView(row {
            modelButton = text(graph.preferredAiModel()?.name ?: "选择模型", 12f, pal.text, true).apply {
                gravity = Gravity.CENTER
                background = shape(pal.surface, 10, pal.border)
                setPadding(dp(10), dp(8), dp(10), dp(8))
                maxLines = 1
                setOnClickListener { pickModel() }
            }
            addView(modelButton, LinearLayout.LayoutParams(0, dp(36), 1f))
            modeButton = text(graph.interactiveAiRuntime.permissionMode.title, 12f, pal.text, true).apply {
                gravity = Gravity.CENTER
                background = shape(pal.surface, 10, pal.border)
                setPadding(dp(10), dp(8), dp(10), dp(8))
                maxLines = 1
                setOnClickListener { pickMode() }
            }
            addView(modeButton, LinearLayout.LayoutParams(0, dp(36), 1f).apply { leftMargin = dp(8) })
        }.apply {
            setPadding(dp(12), dp(8), dp(12), 0)
        })

        messagesScroll = ScrollView(context).apply {
            setBackgroundColor(pal.background)
            addView(messagesContainer, ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT,
            ))
            setPadding(dp(12), dp(12), dp(12), dp(12))
            clipToPadding = false
        }
        root.addView(messagesScroll, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f))

        root.addView(row {
            addView(secondaryButton("🖼") { host.pickImageFile { bytes -> attachedImage = bytes; host.notify("附件已添加（${bytes.size} 字节）") } },
                LinearLayout.LayoutParams(dp(52), dp(48)))
            inputBox = input("描述你想完成的操作…", multiline = true).apply { minHeight = dp(48) }
            addView(inputBox, LinearLayout.LayoutParams(0, dp(52), 1f).apply {
                leftMargin = dp(8)
                setMargins(dp(8), 0, 0, 0)
            })
            addView(primaryButton("发送") { send() }, LinearLayout.LayoutParams(dp(84), dp(52)).apply { leftMargin = dp(8) })
        }.apply { setPadding(dp(12), dp(8), dp(12), dp(12)) })

        // 注入交互 Host（确认/选择卡都走 PageHost 弹窗）。
        graph.interactiveAiRuntime.host = this
        graph.interactiveAiRuntime.preferredModel = graph.preferredAiModel()
        renderHistory()
        return root
    }

    private fun pickModel() {
        val models = graph.aiModelStore.list()
        if (models.isEmpty()) {
            host.confirm("还没有模型", "是否打开模型管理？") { host.pushPage(AiModelPage(context, host, graph)) }
            return
        }
        host.showChoiceDialog("选择模型", models.map { it.name }) { selected ->
            models.firstOrNull { it.name == selected }?.let { model ->
                graph.settings.preferredAiModelId = model.id
                graph.interactiveAiRuntime.preferredModel = model
                modelButton.text = model.name
            }
        }
    }

    private fun pickMode() {
        host.showChoiceDialog("权限模式", AiPermissionMode.entries.map { "${it.title}（${it.subtitle}）" }) { selected ->
            AiPermissionMode.entries.firstOrNull { "${it.title}（${it.subtitle}）" == selected }?.let { mode ->
                graph.interactiveAiRuntime.permissionMode = mode
                modeButton.text = mode.title
            }
        }
    }

    private fun renderHistory() {
        messagesContainer.removeAllViews()
        graph.interactiveAiRuntime.messages().filter { it.role != "system" }.forEach { message ->
            when (message.role) {
                "user" -> addBubble(message.content.ifBlank { "（图片）" }, fromUser = true)
                "assistant" -> addBubble(message.content, fromUser = false)
                "tool" -> addToolCard(message.toolName, "已完成")
            }
        }
        scrollToBottom()
    }

    private fun send() {
        val text = inputBox.text.toString().trim()
        if (text.isBlank() && attachedImage == null) return
        val image = attachedImage
        attachedImage = null
        inputBox.setText("")
        addBubble(text.ifBlank { "（图片）" }, fromUser = true)
        cancelFlag.set(false)
        setBusy(true)
        val started = System.currentTimeMillis()
        host.runRemote({
            graph.interactiveAiRuntime.sendUserMessage(
                text, imageBase64 = image?.let { android.util.Base64.encodeToString(it, android.util.Base64.NO_WRAP) }.orEmpty(),
                onEvent = ::handleStreamEvent,
                isCancelled = cancelFlag::get,
                onToolRun = { toolName -> mainHandler.post { addToolCard(toolName, "执行中…") } },
            )
        }) { result ->
            setBusy(false)
            streamingView = null
            when (result) {
                is RemoteResult.Failure -> addBubble("出错了：${result.error.message}（${result.error.errorCode}）", fromUser = false, error = true)
                is RemoteResult.Success -> {
                    if (result.value.isBlank()) {
                        addBubble("（模型没有返回内容）", fromUser = false, error = true)
                    } else {
                        renderHistory()
                    }
                }
            }
            host.notify(if (result is RemoteResult.Success) "AI 已回复（${System.currentTimeMillis() - started}ms）" else "AI 请求失败")
        }
    }

    private val mainHandler = android.os.Handler(android.os.Looper.getMainLooper())

    private fun handleStreamEvent(event: AiClient.StreamEvent) {
        mainHandler.post {
            when (event) {
                is AiClient.StreamEvent.Thinking -> {
                    if (streamingView == null) createStreamingView()
                    streamingView?.tag = "thinking"
                }
                is AiClient.StreamEvent.Content -> {
                    if (streamingView == null || streamingView?.tag == "thinking") createStreamingView()
                    streamingView?.append(event.text)
                    scrollToBottom()
                }
            }
        }
    }

    private fun createStreamingView() {
        streamingView = addBubble("", fromUser = false)
    }

    private fun addBubble(content: String, fromUser: Boolean, error: Boolean = false): TextView {
        val bubble = text(content.ifBlank { "…" }, 14f, if (fromUser) Color.WHITE else if (error) pal.danger else pal.text).apply {
            background = shape(
                when {
                    fromUser -> pal.brand
                    error -> withAlpha(pal.danger, 0x22)
                    else -> pal.surface
                },
                12,
            )
            setPadding(dp(12), dp(9), dp(12), dp(9))
        }
        val wrapper = row {
            gravity = if (fromUser) Gravity.END else Gravity.START
            addView(bubble, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 0.86f))
        }
        messagesContainer.addView(wrapper)
        scrollToBottom()
        return bubble
    }

    private fun addToolCard(toolName: String, status: String) {
        messagesContainer.addView(row {
            addView(text("🛠", 13f))
            addView(text("$toolName · $status", 11f, pal.muted), LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT,
            ).apply { leftMargin = dp(4) })
        }.apply { setPadding(dp(8), 0, dp(8), 0) })
        scrollToBottom()
    }

    private fun setBusy(busy: Boolean) {
        inputBox.isEnabled = !busy
    }

    private fun scrollToBottom() {
        messagesScroll.post { messagesScroll.fullScroll(View.FOCUS_DOWN) }
    }

    // ---------- AiAgentRuntime.Host ----------

    override fun currentDeviceId(): String? = boundDevice?.id

    override fun availableDevices(): List<Triple<String, String, String>> {
        // 同步获取设备列表不可行（远程调用在工作线程），返回当前绑定设备与历史会话设备。
        return boundDevice?.let { listOf(Triple(it.id, it.displayName, it.adbState)) } ?: emptyList()
    }

    override fun confirm(title: String, detail: String): Boolean {
        // 工作线程阻塞等待 UI 确认（CountDownLatch），保持与工具循环同步语义。
        val latch = java.util.concurrent.CountDownLatch(1)
        var approved = false
        mainHandler.post {
            android.app.AlertDialog.Builder(context)
                .setTitle(title)
                .setMessage(detail)
                .setNegativeButton("拒绝") { _, _ -> latch.countDown() }
                .setPositiveButton("允许") { _, _ -> approved = true; latch.countDown() }
                .setOnCancelListener { latch.countDown() }
                .show()
        }
        latch.await()
        return approved
    }

    override fun askChoice(question: String, options: List<String>, multiSelect: Boolean): List<String>? {
        val latch = java.util.concurrent.CountDownLatch(1)
        var selected: List<String>? = null
        mainHandler.post {
            val checked = BooleanArray(options.size)
            val builder = android.app.AlertDialog.Builder(context)
                .setTitle(question)
            if (multiSelect) {
                builder.setMultiChoiceItems(options.toTypedArray(), checked) { _, _, _ -> }
                    .setNegativeButton("取消") { _, _ -> latch.countDown() }
                    .setPositiveButton("确认") { _, _ ->
                        selected = options.filterIndexed { index, _ -> checked[index] }
                        latch.countDown()
                    }
            } else {
                builder.setItems(options.toTypedArray()) { _, which ->
                    selected = listOf(options[which])
                    latch.countDown()
                }
                builder.setNegativeButton("取消") { _, _ -> latch.countDown() }
            }
            builder.setOnCancelListener { latch.countDown() }
            builder.show()
        }
        latch.await()
        return selected
    }
}
