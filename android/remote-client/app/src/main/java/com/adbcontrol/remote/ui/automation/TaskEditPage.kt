package com.adbcontrol.remote.ui.automation

import android.app.AlertDialog
import android.content.Context
import android.view.View
import android.widget.LinearLayout
import com.adbcontrol.remote.core.AppGraph
import com.adbcontrol.remote.data.automation.AutomationSerializer
import com.adbcontrol.remote.data.automation.AutomationValidator
import com.adbcontrol.remote.model.AutomationExecutionException
import com.adbcontrol.remote.ui.common.BasePage
import com.adbcontrol.remote.ui.common.PageHost
import org.json.JSONObject

/**
 * 任务编辑器（对应桌面端 JSON DSL 编辑器 + 模板）：
 * - 编辑完整任务 JSON（schemaVersion/permissions/triggers/actions 与桌面端同构）；
 * - 内置模板：空白 / 每天17:00 / 每小时 / 打开App / 低电量提醒(condition 触发)；
 * - 保存前经 AutomationValidator 校验，失败显示具体原因（不静默入库）。
 */
class TaskEditPage(
    context: Context,
    host: PageHost,
    private val graph: AppGraph,
    private val existingTaskId: String?,
) : BasePage(context, host) {

    private val editor = input("任务 JSON").apply {
        minHeight = dp(420)
        gravity = android.view.Gravity.TOP
        setTextIsSelectable(true)
    }

    override fun build(): View {
        val body = column(10)
        body.addView(text("任务模板", 15f, pal.text, true))
        body.addView(row {
            addView(secondaryButton("空白") { editor.setText(TEMPLATE_BLANK) }, LinearLayout.LayoutParams(0, dp(42), 1f))
            addView(secondaryButton("每天 17:00") { editor.setText(TEMPLATE_DAILY) }, LinearLayout.LayoutParams(0, dp(42), 1f).apply { leftMargin = dp(6) })
            addView(secondaryButton("每小时") { editor.setText(TEMPLATE_INTERVAL) }, LinearLayout.LayoutParams(0, dp(42), 1f).apply { leftMargin = dp(6) })
        })
        body.addView(row {
            addView(secondaryButton("打开App") { editor.setText(TEMPLATE_APP) }, LinearLayout.LayoutParams(0, dp(42), 1f))
            addView(secondaryButton("条件触发") { editor.setText(TEMPLATE_CONDITION) }, LinearLayout.LayoutParams(0, dp(42), 1f).apply { leftMargin = dp(6) })
            addView(secondaryButton("清空") { editor.setText("") }, LinearLayout.LayoutParams(0, dp(42), 1f).apply { leftMargin = dp(6) })
        })
        body.addView(editor, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, dp(420)))
        body.addView(primaryButton("校验并保存") { save() })
        body.addView(text(
            "DSL 与 Windows 桌面端同构：triggers 支持 manual/once/daily/weekly/interval/cron/condition；" +
                "actions 支持 adb.shell、adb.tap、app.start、companion.call、ai.prompt、flow.if 等。" +
                "任务权限（allowShell/allowCompanion/allowAi/allowUnlock）必须显式开启，动作才会执行。",
            11f, pal.muted,
        ))
        existingTaskId?.let { id ->
            graph.automationStore.getTask(id)?.let { task ->
                editor.setText(AutomationSerializer.serialize(task))
            }
        } ?: editor.setText(TEMPLATE_BLANK)
        return subPage(if (existingTaskId == null) "新建任务" else "编辑任务", body)
    }

    private fun save() {
        val text = editor.text.toString().trim()
        if (text.isBlank()) {
            host.notify("任务内容不能为空")
            return
        }
        try {
            val task = AutomationSerializer.deserialize(text)
            AutomationValidator.validate(task)
            graph.automationStore.upsertTask(task.copy(updatedAtEpochMs = System.currentTimeMillis()))
            host.notify("任务已保存：${task.name}")
            host.popPage()
        } catch (parse: AutomationExecutionException) {
            showError(parse.message)
        } catch (error: org.json.JSONException) {
            showError("JSON 解析失败：${error.message}")
        } catch (error: Exception) {
            showError(error.message ?: "任务保存失败")
        }
    }

    private fun showError(message: String) {
        AlertDialog.Builder(context)
            .setTitle("校验失败")
            .setMessage(message)
            .setPositiveButton("知道了", null)
            .show()
    }

    private fun template(json: String): String = JSONObject(json).toString(2)

    private val TEMPLATE_BLANK = template("""
        {"schemaVersion":1,"name":"新任务","description":"","deviceId":"","enabled":true,
         "concurrencyPolicy":"skip",
         "permissions":{"allowAdb":true,"allowShell":true,"allowCompanion":false,"allowAi":false,"allowAiDeviceTools":false,"allowTaskMutation":false,"allowUnlock":false},
         "triggers":[{"type":"manual"}],
         "actions":[{"type":"log","parameters":{"message":"任务开始"}}]}
    """)

    private val TEMPLATE_DAILY = template("""
        {"schemaVersion":1,"name":"每天 17:00 提醒","deviceId":"","enabled":true,
         "permissions":{"allowAdb":true,"allowShell":true},
         "triggers":[{"type":"daily","at":"17:00"}],
         "actions":[{"type":"adb.keyevent","parameters":{"keyCode":"KEYCODE_WAKEUP"}},
                    {"type":"log","parameters":{"message":"17点了"}}]}
    """)

    private val TEMPLATE_INTERVAL = template("""
        {"schemaVersion":1,"name":"每小时截图","deviceId":"","enabled":true,
         "permissions":{"allowAdb":true,"allowShell":true,"allowCompanion":true},
         "triggers":[{"type":"interval","intervalSeconds":3600}],
         "actions":[{"type":"companion.call","parameters":{"capabilityId":"android.accessibility.control","operation":"accessibility.screenshot","args":"{\"maxSize\":960}"}}]}
    """)

    private val TEMPLATE_APP = template("""
        {"schemaVersion":1,"name":"打开应用","deviceId":"","enabled":true,
         "permissions":{"allowAdb":true,"allowShell":true},
         "triggers":[{"type":"manual"}],
         "actions":[{"type":"app.start","parameters":{"packageName":"com.example.app"}}]}
    """)

    private val TEMPLATE_CONDITION = template("""
        {"schemaVersion":1,"name":"低电量告警","deviceId":"","enabled":true,
         "permissions":{"allowAdb":true,"allowShell":true},
         "triggers":[{"type":"condition","pollIntervalSeconds":60,"edgeOnly":true,"cooldownSeconds":600,
                      "conditions":[{"type":"battery.level","operator":"lessthan","parameters":{"value":"20"}}]}],
         "actions":[{"type":"log","parameters":{"message":"电量低于 20%"}}]}
    """)
}
