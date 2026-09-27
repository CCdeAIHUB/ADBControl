package com.adbcontrol.remote.data.ai

import com.adbcontrol.remote.data.automation.AutomationSerializer
import com.adbcontrol.remote.data.automation.AutomationStore
import com.adbcontrol.remote.data.automation.AutomationValidator
import com.adbcontrol.remote.model.AutomationTaskDefinition
import org.json.JSONArray
import org.json.JSONObject

/** Agent 工具 schema（对齐桌面端 AiService.BuildTools 的工具名与参数）。 */
object AiToolCatalog {

    fun buildTools(hasDevice: Boolean): JSONArray = JSONArray().apply {
        fun tool(name: String, description: String, properties: JSONObject, required: JSONArray = JSONArray()) {
            val parameters = JSONObject()
                .put("type", "object")
                .put("properties", properties)
                .put("required", required)
            put(
                JSONObject()
                    .put("type", "function")
                    .put("function", JSONObject()
                        .put("name", name)
                        .put("description", description)
                        .put("parameters", parameters)),
            )
        }
        tool("device_list", "列出当前账号可用的远程设备及其在线状态。", JSONObject())
        tool("observe_screen", "截取当前设备屏幕（需要设备上的伴侣 App 与无障碍授权），返回截图供观察。", JSONObject().put(
            "maxSize", JSONObject().put("type", "integer").put("description", "截图最长边像素，默认 960"),
        ))
        tool("select_device", "多设备场景下向用户展示选择卡，明确后续操作的目标设备。", JSONObject())
        tool("ask_user_choice", "需要用户做决定时展示选择卡。", JSONObject()
            .put("question", JSONObject().put("type", "string"))
            .put("options", JSONObject().put("type", "array").put("items", JSONObject().put("type", "string")))
            .put("multiSelect", JSONObject().put("type", "boolean")),
            JSONArray().put("question").put("options"))
        tool("device_unlock", "设备锁屏时执行唤醒+上滑手势（不输入任何凭据）。", JSONObject())
        tool("adb_shell", "在目标设备执行一条 adb shell 命令；受权限模式约束，破坏性命令会被拒绝或要求确认。", JSONObject().put(
            "command", JSONObject().put("type", "string").put("description", "shell 命令，不含 adb -s 前缀"),
        ), JSONArray().put("command"))
        tool("adb_ui_dump", "导出当前界面结构（uiautomator dump），用于确认控件与文本。", JSONObject())
        tool("adb_tap", "点击屏幕坐标（截图原始像素）。", JSONObject()
            .put("x", JSONObject().put("type", "integer"))
            .put("y", JSONObject().put("type", "integer")),
            JSONArray().put("x").put("y"))
        tool("adb_swipe", "从起点滑动到终点（截图原始像素，durationMs 1-3000）。", JSONObject()
            .put("startX", JSONObject().put("type", "integer"))
            .put("startY", JSONObject().put("type", "integer"))
            .put("endX", JSONObject().put("type", "integer"))
            .put("endY", JSONObject().put("type", "integer"))
            .put("durationMs", JSONObject().put("type", "integer")),
            JSONArray().put("startX").put("startY").put("endX").put("endY"))
        tool("companion_call", "调用目标设备伴侣 App 能力（剪贴板、音量、应用列表、短信等）。", JSONObject()
            .put("capabilityId", JSONObject().put("type", "string"))
            .put("operation", JSONObject().put("type", "string"))
            .put("args", JSONObject().put("type", "object")),
            JSONArray().put("capabilityId").put("operation"))
        tool("task_list", "列出本机自动化任务。", JSONObject())
        tool("task_create", "根据 JSON DSL 创建自动化任务（与桌面端同一任务格式）。", JSONObject().put(
            "task", JSONObject().put("type", "object").put("description", "AutomationTaskDefinition JSON"),
        ), JSONArray().put("task"))
        tool("task_set_enabled", "启用/停用任务。", JSONObject()
            .put("taskId", JSONObject().put("type", "string"))
            .put("enabled", JSONObject().put("type", "boolean")),
            JSONArray().put("taskId").put("enabled"))
        tool("task_run", "立即运行一次任务。", JSONObject().put("taskId", JSONObject().put("type", "string")), JSONArray().put("taskId"))
        tool("task_delete", "删除任务。", JSONObject().put("taskId", JSONObject().put("type", "string")), JSONArray().put("taskId"))
        if (!hasDevice) {
            // 无设备上下文时移除设备强相关工具，避免模型误调用。
            removeDeviceTools(this)
        }
    }

    private fun removeDeviceTools(array: JSONArray) {
        val deviceBound = setOf("observe_screen", "adb_shell", "adb_ui_dump", "adb_tap", "adb_swipe", "device_unlock", "companion_call")
        val kept = JSONArray()
        for (index in 0 until array.length()) {
            val item = array.optJSONObject(index) ?: continue
            val name = item.optJSONObject("function")?.optString("name").orEmpty()
            if (name !in deviceBound) kept.put(item)
        }
        // JSONArray 原地重建。
        while (array.length() > 0) array.remove(0)
        for (index in 0 until kept.length()) array.put(kept.get(index))
    }

    /** task_create 的导入通道：校验后入库，返回结果 JSON。 */
    fun importTask(taskJson: JSONObject, store: AutomationStore): String = try {
        val task: AutomationTaskDefinition = AutomationSerializer.deserialize(taskJson.toString())
        AutomationValidator.validate(task)
        store.upsertTask(task.copy(updatedAtEpochMs = System.currentTimeMillis()))
        JSONObject().put("ok", true).put("taskId", task.id).toString()
    } catch (error: Exception) {
        JSONObject().put("ok", false).put("errorCode", "AUTOMATION_TASK_INVALID").put("error", error.message ?: "任务解析失败").toString()
    }
}
