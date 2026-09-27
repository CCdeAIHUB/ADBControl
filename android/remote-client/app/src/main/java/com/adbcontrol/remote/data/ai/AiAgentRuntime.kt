package com.adbcontrol.remote.data.ai

import com.adbcontrol.remote.data.adb.DeviceCommandGateway
import com.adbcontrol.remote.data.automation.AutomationStore
import com.adbcontrol.remote.data.companion.CompanionGateway
import com.adbcontrol.remote.data.log.AppDiagnostics
import com.adbcontrol.remote.model.AiModelConfig
import com.adbcontrol.remote.model.AiPermissionMode
import com.adbcontrol.remote.model.AppError
import com.adbcontrol.remote.model.ChatMessage
import com.adbcontrol.remote.model.RemoteResult
import org.json.JSONArray
import org.json.JSONObject

/**
 * Agent 工具环：工具集合与语义对照桌面端 `AiService.BuildTools` + `AiAgentToolService`。
 * 差异（远程协议所致，需向用户明示）：
 * - observe_screen 走伴侣 accessibility.screenshot（Core 侧 screencap 二进制经 JSON 文本通道会损坏）；
 * - 任务工具操作本机自动化存储（桌面端为桌面本地 SQLite），Core 无自动化 API。
 * UI 交互（确认/选择卡）通过 [Host] 回调注入，保持本类不含 UI 代码。
 */
class AiAgentRuntime(
    private val client: AiClient,
    private val conversationStore: AiConversationStore,
    private val commands: DeviceCommandGateway,
    private val companion: CompanionGateway,
    private val automationStore: AutomationStore,
    private val taskRunner: (taskId: String) -> Boolean,
) {
    interface Host {
        fun currentDeviceId(): String?
        fun availableDevices(): List<Triple<String, String, String>> // id / 名称 / 状态
        /** 阻塞式确认（工作线程调用；UI 用 CountDownLatch 实现）。返回 true 表示允许。 */
        fun confirm(title: String, detail: String): Boolean
        /** 阻塞式选择卡；返回选中项 id，取消返回 null。 */
        fun askChoice(question: String, options: List<String>, multiSelect: Boolean): List<String>?
    }

    var host: Host? = null
    var permissionMode: AiPermissionMode = AiPermissionMode.AUTO_READONLY
    var preferredModel: AiModelConfig? = null

    private var pendingImageBase64: String = ""

    fun messages(): List<ChatMessage> = conversationStore.load()

    fun clearConversation() {
        conversationStore.clear()
        pendingImageBase64 = ""
    }

    /** 发送一条用户消息并执行工具循环；返回助手最终文本（含工具过程通过回调流式上屏）。 */
    fun sendUserMessage(
        text: String,
        imageBase64: String = "",
        onEvent: (AiClient.StreamEvent) -> Unit,
        isCancelled: () -> Boolean,
        onToolRun: (String) -> Unit,
    ): RemoteResult<String> {
        val model = preferredModel ?: return RemoteResult.Failure(
            AppError("AI_MODEL_NOT_CONFIGURED", "尚未配置 AI 模型", "ai.agent", true, "请先在 AI 助手中添加模型。"),
        )
        if (AiPolicies.multimodalRequired(imageBase64.isNotEmpty(), model.multimodalVerified)) {
            return RemoteResult.Failure(
                AppError("AI_MODEL_MULTIMODAL_REQUIRED", "当前模型不支持图片输入", "ai.agent", true, "请更换已通过多模态验证的模型。"),
            )
        }
        pendingImageBase64 = imageBase64
        val history = conversationStore.load().toMutableList()
        if (history.none { it.role == "system" }) {
            history.add(0, ChatMessage("system", buildSystemPrompt(systemDeviceSummary())))
        }
        history.add(ChatMessage("user", text, imageBase64 = imageBase64))
        conversationStore.save(history)
        return runLoop(model, onEvent, isCancelled, onToolRun)
    }

    private fun runLoop(
        model: AiModelConfig,
        onEvent: (AiClient.StreamEvent) -> Unit,
        isCancelled: () -> Boolean,
        onToolRun: (String) -> Unit,
    ): RemoteResult<String> {
        var iterations = 0
        while (iterations < MAX_TOOL_ITERATIONS) {
            iterations++
            if (conversationStore.needsCompression(conversationStore.load())) {
                compressConversation(model, onEvent, isCancelled) ?: return RemoteResult.Failure(
                    AppError("AI_CONTEXT_COMPRESS_FAILED", "上下文压缩失败", "ai.agent", true),
                )
            }
            val requestMessages = buildRequestMessages()
            val tools = AiToolCatalog.buildTools(host?.currentDeviceId() != null)
            val turn = when (val result = client.stream(model, requestMessages, tools, onEvent, isCancelled)) {
                is RemoteResult.Failure -> return result
                is RemoteResult.Success -> result.value
            }
            val history = conversationStore.load().toMutableList()
            history.add(ChatMessage("assistant", turn.content, reasoning = turn.reasoning, toolCallsJson = encodeToolCalls(turn.toolCalls)))
            conversationStore.save(history)
            if (!turn.hasToolCalls) {
                return RemoteResult.Success(turn.content)
            }
            turn.toolCalls.forEach { call ->
                onToolRun(call.name)
                val resultText = executeTool(call.name, call.argumentsJson)
                conversationStore.save(conversationStore.load() + ChatMessage(
                    role = "tool", content = resultText.take(MAX_TOOL_RESULT_CHARS), toolCallId = call.id, toolName = call.name,
                ))
            }
        }
        return RemoteResult.Failure(AppError("AI_TOOL_LOOP_OVERFLOW", "工具调用次数超过上限", "ai.agent", true))
    }

    /** 工具执行总入口（错误一律转成给模型可读的 JSON 文本，不向模型抛异常）。 */
    private fun executeTool(name: String, argumentsJson: String): String {
        AppDiagnostics.record("info", "ai.tool", "ai.agent", true, 0, "", "tool=$name")
        return try {
            val args = if (argumentsJson.isBlank()) JSONObject() else JSONObject(argumentsJson)
            when (name) {
                "device_list" -> toolDeviceList()
                "observe_screen" -> toolObserveScreen(args)
                "select_device" -> toolSelectDevice()
                "ask_user_choice" -> toolAskChoice(args)
                "device_unlock" -> toolDeviceUnlock()
                "adb_shell" -> toolAdbShell(args)
                "adb_ui_dump" -> toolAdbUiDump()
                "adb_tap" -> toolTap(args)
                "adb_swipe" -> toolSwipe(args)
                "companion_call" -> toolCompanionCall(args)
                "task_list" -> toolTaskList()
                "task_create" -> toolTaskCreate(args)
                "task_set_enabled" -> toolTaskSetEnabled(args)
                "task_run" -> toolTaskRun(args)
                "task_delete" -> toolTaskDelete(args)
                else -> errorJson("AI_TOOL_UNKNOWN", "未知工具：$name")
            }
        } catch (error: Throwable) {
            errorJson("AI_TOOL_FAILED", error.message ?: "工具执行失败")
        }
    }

    private fun toolDeviceList(): String = JSONArray().apply {
        host?.availableDevices()?.forEach { (id, name, state) ->
            put(JSONObject().put("deviceId", id).put("name", name).put("state", state))
        }
    }.toString()

    private fun toolObserveScreen(args: JSONObject): String {
        val deviceId = requireDevice()
        val maxSize = args.optInt("maxSize", 960)
        return when (val result = companion.screenshot(deviceId, maxSize)) {
            is RemoteResult.Failure -> errorJson(result.error.errorCode, result.error.message)
            is RemoteResult.Success -> {
                val base64 = result.value.optString("pngBase64")
                if (base64.isBlank()) {
                    errorJson("AI_OBSERVE_EMPTY", "伴侣未返回截图数据")
                } else {
                    pendingImageBase64 = base64
                    JSONObject().put("ok", true).put("note", "屏幕截图已获取，图片将以用户消息附件提供；坐标以截图原始像素、左上角为原点。").toString()
                }
            }
        }
    }

    private fun toolSelectDevice(): String {
        val devices = host?.availableDevices().orEmpty()
        if (devices.isEmpty()) return errorJson("AI_NO_DEVICE", "当前没有可用设备")
        val choice = host?.askChoice("请选择要操作的设备", devices.map { "${it.second}（${it.first}）" }, multiSelect = false)
        val index = choice?.firstOrNull()?.let { selected -> devices.indexOfFirst { "${it.second}（${it.first}）" == selected } } ?: -1
        return if (index < 0) errorJson("AI_CHOICE_CANCELLED", "用户取消了设备选择")
        else JSONObject().put("deviceId", devices[index].first).toString()
    }

    private fun toolAskChoice(args: JSONObject): String {
        val question = args.optString("question", "请选择")
        val options = args.optJSONArray("options")?.let { array -> (0 until array.length()).map { array.optString(it) } }.orEmpty()
        if (options.isEmpty()) return errorJson("AI_TOOL_ARGUMENT_INVALID", "options 不能为空")
        val multiSelect = args.optBoolean("multiSelect", false)
        val selected = host?.askChoice(question, options, multiSelect)
            ?: return errorJson("AI_CHOICE_CANCELLED", "用户取消了选择")
        return JSONArray(selected).toString()
    }

    private fun toolDeviceUnlock(): String {
        val deviceId = requireDevice()
        return when (val result = commands.queryLockState(deviceId)) {
            is RemoteResult.Failure -> errorJson(result.error.errorCode, result.error.message)
            is RemoteResult.Success -> when (result.value.state) {
                com.adbcontrol.remote.data.adb.LockStateParser.LockState.Unlocked ->
                    JSONObject().put("ok", true).put("state", "unlocked").put("note", "设备已解锁，未执行任何手势。").toString()
                com.adbcontrol.remote.data.adb.LockStateParser.LockState.Unknown ->
                    errorJson("DEVICE_LOCK_STATE_UNRECOGNIZED", "无法识别锁屏状态，拒绝盲操作解锁手势")
                else -> {
                    // 与桌面端一致：只做唤醒+上滑，绝不猜测或输入凭据。
                    when (val wake = companion.wake(deviceId)) {
                        is RemoteResult.Failure -> errorJson(wake.error.errorCode, wake.error.message)
                        is RemoteResult.Success -> {
                            val screen = commands.screenSize(deviceId).size
                            val width = screen?.width ?: 1080
                            val height = screen?.height ?: 1920
                            when (val swipe = companion.swipe(deviceId, width / 2, (height * 0.82).toInt(), width / 2, (height * 0.28).toInt(), 350)) {
                                is RemoteResult.Failure -> errorJson(swipe.error.errorCode, swipe.error.message)
                                is RemoteResult.Success -> JSONObject().put("ok", true).put("state", "swiped").put("note", "已执行唤醒与上滑。").toString()
                            }
                        }
                    }
                }
            }
        }
    }

    private fun toolAdbShell(args: JSONObject): String {
        val deviceId = requireDevice()
        val command = args.optString("command").trim()
        if (command.isBlank()) return errorJson("AI_TOOL_ARGUMENT_INVALID", "command 不能为空")
        if (command.startsWith("input ")) {
            return errorJson("AI_TOOL_DENIED", "禁止用 adb_shell 发送触摸/按键，必须使用 adb_tap/adb_swipe")
        }
        return when (AiPolicies.classifyShell(command, permissionMode)) {
            AiPolicies.ShellDecision.DENY -> errorJson("AI_TOOL_DENIED", "命令被安全策略拒绝：$command")
            AiPolicies.ShellDecision.CONFIRM -> {
                val approved = host?.confirm("AI 请求执行命令", command) ?: false
                if (!approved) errorJson("AI_TOOL_REJECTED", "用户拒绝了该命令") else runShell(deviceId, command)
            }
            AiPolicies.ShellDecision.ALLOW -> runShell(deviceId, command)
        }
    }

    private fun runShell(deviceId: String, command: String): String = when (val result = commands.shell(deviceId, command, 30_000)) {
        is RemoteResult.Failure -> errorJson(result.error.errorCode, result.error.message)
        is RemoteResult.Success -> JSONObject()
            .put("exitCode", result.value.exitCode)
            .put("stdout", result.value.stdout.take(20_000))
            .put("stderr", result.value.stderr.take(4_000))
            .toString()
    }

    private fun toolAdbUiDump(): String {
        val deviceId = requireDevice()
        val command = "sh -c 'uiautomator dump --compressed /sdcard/adbcontrol-window.xml >/dev/null && cat /sdcard/adbcontrol-window.xml && rm /sdcard/adbcontrol-window.xml'"
        return when (val result = commands.shell(deviceId, command, 30_000)) {
            is RemoteResult.Failure -> errorJson(result.error.errorCode, result.error.message)
            is RemoteResult.Success -> {
                val output = result.value.stdout
                // 与桌面一致：超长输出做中段截断，保留头尾结构。
                if (output.length <= 80_000) output else output.take(40_000) + "\n…[中段截断]…\n" + output.takeLast(40_000)
            }
        }
    }

    private fun toolTap(args: JSONObject): String = touchArgs(args) { x, y -> runShell(requireDevice(), "input tap $x $y") }

    private fun toolSwipe(args: JSONObject): String {
        val startX = args.optInt("startX", -1)
        val startY = args.optInt("startY", -1)
        val endX = args.optInt("endX", -1)
        val endY = args.optInt("endY", -1)
        val duration = args.optInt("durationMs", 250).coerceIn(1, 3000)
        val deviceId = requireDevice()
        val screen = commands.screenSize(deviceId).size
        if (screen == null) return errorJson("AI_SCREEN_SIZE_UNKNOWN", "无法读取屏幕分辨率")
        fun inside(x: Int, y: Int) = x >= 0 && y >= 0 && x < screen.width && y < screen.height
        if (!inside(startX, startY) || !inside(endX, endY)) {
            return errorJson("AI_COORDINATE_OUT_OF_RANGE", "坐标超出屏幕 ${screen.width}x${screen.height}")
        }
        return runShell(deviceId, "input swipe $startX $startY $endX $endY $duration")
    }

    private fun touchArgs(args: JSONObject, run: (Int, Int) -> String): String {
        val x = args.optInt("x", -1)
        val y = args.optInt("y", -1)
        val deviceId = requireDevice()
        val screen = commands.screenSize(deviceId).size
        if (screen == null) return errorJson("AI_SCREEN_SIZE_UNKNOWN", "无法读取屏幕分辨率")
        if (x < 0 || y < 0 || x >= screen.width || y >= screen.height) {
            return errorJson("AI_COORDINATE_OUT_OF_RANGE", "坐标 ($x,$y) 超出屏幕 ${screen.width}x${screen.height}")
        }
        return run(x, y)
    }

    private fun toolCompanionCall(args: JSONObject): String {
        val deviceId = requireDevice()
        val capabilityId = args.optString("capabilityId")
        val operation = args.optString("operation")
        if (capabilityId.isBlank() || operation.isBlank()) return errorJson("AI_TOOL_ARGUMENT_INVALID", "capabilityId/operation 不能为空")
        if (!AiPolicies.isLowRiskCompanion(operation) && permissionMode != AiPermissionMode.FULL_ACCESS) {
            val approved = host?.confirm("AI 请求调用伴侣能力", "$capabilityId / $operation") ?: false
            if (!approved) return errorJson("AI_TOOL_REJECTED", "用户拒绝了该调用")
        }
        val callArgs = args.optJSONObject("args") ?: JSONObject()
        return when (val result = companionTapThrough(deviceId, capabilityId, operation, callArgs)) {
            is RemoteResult.Failure -> errorJson(result.error.errorCode, result.error.message)
            is RemoteResult.Success -> result.value.toString().take(20_000)
        }
    }

    private fun companionTapThrough(
        deviceId: String,
        capabilityId: String,
        operation: String,
        args: JSONObject,
    ): RemoteResult<JSONObject> = when {
        capabilityId == CompanionGateway.CAP_ACCESSIBILITY && operation == "accessibility.status" -> companion.accessibilityStatus(deviceId)
        capabilityId == CompanionGateway.CAP_ACCESSIBILITY && operation == "accessibility.screenshot" -> companion.screenshot(deviceId, args.optInt("maxSize", 960))
        capabilityId == CompanionGateway.CAP_DEVICE_POWER && operation == "device.state" -> companion.deviceState(deviceId)
        capabilityId == CompanionGateway.CAP_DEVICE_POWER && operation == "device.wake" -> companion.wake(deviceId)
        capabilityId == CompanionGateway.CAP_VOLUME && operation == "volume.get" -> companion.volumeGet(deviceId)
        capabilityId == CompanionGateway.CAP_VOLUME && operation == "volume.set" -> companion.volumeSet(deviceId, args.optInt("level", 0))
        capabilityId == CompanionGateway.CAP_INPUT && operation == "input.text" -> companion.inputText(deviceId, args.optString("text"))
        capabilityId == CompanionGateway.CAP_INPUT && operation == "input.key" -> companion.inputKey(deviceId, args.optInt("keyCode"))
        capabilityId == CompanionGateway.CAP_APP_LIST && operation == "app.list" -> companion.appList(deviceId)
        capabilityId == "android.clipboard.read" && operation == "clipboard.read" -> companion.clipboardRead(deviceId)
        else -> companion.repositoryInvoke(deviceId, capabilityId, operation, args)
    }

    private fun toolTaskList(): String = JSONArray().apply {
        automationStore.listTasks().forEach { task ->
            put(JSONObject()
                .put("id", task.id)
                .put("name", task.name)
                .put("description", task.description)
                .put("deviceId", task.deviceId)
                .put("enabled", task.enabled))
        }
    }.toString()

    private fun toolTaskCreate(args: JSONObject): String {
        if (permissionMode != AiPermissionMode.FULL_ACCESS) {
            val approved = host?.confirm("AI 请求创建自动化任务", args.optJSONObject("task")?.optString("name").orEmpty()) ?: false
            if (!approved) return errorJson("AI_TOOL_REJECTED", "用户拒绝了任务创建")
        }
        val taskJson = args.optJSONObject("task") ?: return errorJson("AI_TOOL_ARGUMENT_INVALID", "缺少 task 对象")
        return AiToolCatalog.importTask(taskJson, automationStore)
    }

    private fun toolTaskSetEnabled(args: JSONObject): String {
        val id = args.optString("taskId")
        val task = automationStore.getTask(id) ?: return errorJson("AUTOMATION_TASK_NOT_FOUND", "任务不存在")
        if (permissionMode != AiPermissionMode.FULL_ACCESS) {
            val approved = host?.confirm("AI 请求修改任务状态", task.name) ?: false
            if (!approved) return errorJson("AI_TOOL_REJECTED", "用户拒绝了修改")
        }
        automationStore.setTaskEnabled(id, args.optBoolean("enabled", !task.enabled))
        return JSONObject().put("ok", true).toString()
    }

    private fun toolTaskRun(args: JSONObject): String {
        val id = args.optString("taskId")
        val task = automationStore.getTask(id) ?: return errorJson("AUTOMATION_TASK_NOT_FOUND", "任务不存在")
        val accepted = taskRunner(id)
        return JSONObject().put("ok", accepted).put("note", if (accepted) "已提交运行" else "并发策略拒绝或运行中").toString()
    }

    private fun toolTaskDelete(args: JSONObject): String {
        val id = args.optString("taskId")
        val task = automationStore.getTask(id) ?: return errorJson("AUTOMATION_TASK_NOT_FOUND", "任务不存在")
        if (permissionMode != AiPermissionMode.FULL_ACCESS) {
            val approved = host?.confirm("AI 请求删除任务", task.name) ?: false
            if (!approved) return errorJson("AI_TOOL_REJECTED", "用户拒绝了删除")
        }
        automationStore.deleteTask(id)
        return JSONObject().put("ok", true).toString()
    }

    private fun requireDevice(): String = host?.currentDeviceId()?.takeIf(String::isNotBlank)
        ?: throw IllegalStateException("未选择设备；请先在设备详情中打开 AI 或用 select_device 选择。")

    /** 系统提示词的设备上下文摘要（无设备时为空）。 */
    private fun systemDeviceSummary(): String {
        val deviceId = host?.currentDeviceId().orEmpty()
        return if (deviceId.isBlank()) "" else "当前绑定设备：$deviceId"
    }

    private fun buildRequestMessages(): List<JSONObject> {
        val messages = mutableListOf<JSONObject>()
        conversationStore.load().forEach { message ->
            when (message.role) {
                "system" -> messages.add(JSONObject().put("role", "system").put("content", message.content))
                "user" -> {
                    if (message.hasImage) {
                        // 图片消息由 observe_screen/附件产生；OpenAI 格式拆成 text+image_url 两段。
                        messages.add(JSONObject().put("role", "user").put("content", JSONArray()
                            .put(JSONObject().put("type", "text").put("text", message.content.ifBlank { "请观察当前屏幕" }))
                            .put(JSONObject().put("type", "image_url").put(
                                "image_url", JSONObject().put("url", "data:image/png;base64,${message.imageBase64}"),
                            ))))
                    } else {
                        messages.add(JSONObject().put("role", "user").put("content", message.content))
                    }
                }
                "assistant" -> {
                    val entry = JSONObject().put("role", "assistant").put("content", message.content)
                    if (message.toolCallsJson.isNotBlank()) entry.put("tool_calls", JSONArray(message.toolCallsJson))
                    messages.add(entry)
                }
                "tool" -> messages.add(JSONObject()
                    .put("role", "tool")
                    .put("tool_call_id", message.toolCallId)
                    .put("content", message.content))
            }
        }
        if (pendingImageBase64.isNotBlank()) {
            // observe_screen 抓到的图尚未入会话时，作为补充 user 消息附上。
            val toolIndex = messages.indexOfLast { it.optString("role") == "tool" }
            messages.add(
                (toolIndex + 1).coerceAtMost(messages.size),
                JSONObject().put("role", "user").put("content", JSONArray()
                    .put(JSONObject().put("type", "text").put("text", "（上面工具产生的屏幕截图）"))
                    .put(JSONObject().put("type", "image_url").put(
                        "image_url", JSONObject().put("url", "data:image/png;base64,$pendingImageBase64"),
                    ))),
            )
            pendingImageBase64 = ""
        }
        return messages
    }

    private fun compressConversation(
        model: AiModelConfig,
        onEvent: (AiClient.StreamEvent) -> Unit,
        isCancelled: () -> Boolean,
    ): Boolean? {
        val history = conversationStore.load()
        if (!conversationStore.needsCompression(history)) return true
        val keepRecent = history.takeLast(6)
        val toCompress = history.dropLast(6).filter { it.role != "system" }
        if (toCompress.isEmpty()) return true
        val summaryRequest = listOf(
            JSONObject().put("role", "system").put(
                "content",
                "把以下对话压缩成不超过 ${AiConversationStore.SUMMARY_MAX_CHARACTERS} 字的要点摘要，保留设备、命令、结论与未完成事项，不要编造。",
            ),
            JSONObject().put("role", "user").put(
                "content",
                toCompress.joinToString("\n") { "${it.role}: ${it.content.take(800)}" },
            ),
        )
        val summary = when (val result = client.stream(model, summaryRequest, null, onEvent, isCancelled)) {
            is RemoteResult.Failure -> return null
            is RemoteResult.Success -> result.value.content.take(AiConversationStore.SUMMARY_MAX_CHARACTERS)
        }
        val compressed = mutableListOf<ChatMessage>()
        history.firstOrNull { it.role == "system" }?.let { compressed.add(it) }
        compressed.add(ChatMessage("system", "历史对话摘要：\n$summary"))
        compressed.addAll(keepRecent)
        conversationStore.save(compressed)
        return true
    }

    private fun encodeToolCalls(calls: List<AiClient.ToolCall>): String = JSONArray().apply {
        calls.forEach { call ->
            put(JSONObject()
                .put("id", call.id)
                .put("type", "function")
                .put("function", JSONObject().put("name", call.name).put("arguments", call.argumentsJson)))
        }
    }.toString()

    private fun errorJson(code: String, message: String): String =
        JSONObject().put("ok", false).put("errorCode", code).put("error", message).toString()

    companion object {
        const val MAX_TOOL_ITERATIONS = 10
        const val MAX_TOOL_RESULT_CHARS = 40_000

        fun buildSystemPrompt(deviceSummary: String): String = listOf(
            "你是 ADBControl 远程控制助手，通过工具帮助用户查看与控制 Android 设备。",
            "规则：",
            "1. 一切设备操作必须通过工具完成，禁止编造命令输出或假装执行。",
            "2. 屏幕坐标基于最新截图的原始像素，左上角为原点；点击用 adb_tap，滑动用 adb_swipe。",
            "3. 在点击/滑动前先用 observe_screen 获取截图，必要时用 adb_ui_dump 校验界面结构。",
            "4. 多设备时必须先用 select_device 明确目标，禁止猜测设备。",
            "5. 破坏性操作（清除数据、卸载、重启等）必须先向用户确认。",
            "6. 回复使用简体中文，简洁直接。",
            deviceSummary,
        ).joinToString("\n")
    }
}
