package com.adbcontrol.remote.ui.ai

import android.app.AlertDialog
import android.content.Context
import android.view.View
import android.widget.LinearLayout
import com.adbcontrol.remote.core.AppGraph
import com.adbcontrol.remote.data.ai.AiClient
import com.adbcontrol.remote.model.AiModelConfig
import com.adbcontrol.remote.model.RemoteResult
import com.adbcontrol.remote.ui.common.BasePage
import com.adbcontrol.remote.ui.common.PageHost
import java.util.UUID

/**
 * AI 模型管理（对应桌面端设置页“AI 模型管理”）：
 * - 添加时发送 1×1 PNG 探针验证多模态（与桌面端一致），未验证模型发送图片会被拒绝；
 * - 密钥仅保存在本机应用私有目录（与桌面端 settings.json 行为一致，页面有明示）。
 */
class AiModelPage(
    context: Context,
    host: PageHost,
    private val graph: AppGraph,
) : BasePage(context, host) {

    private val listContainer = column(10)

    override fun build(): View {
        val body = column(12)
        body.addView(text("模型列表", 16f, pal.text, true))
        body.addView(listContainer)
        body.addView(text(
            "兼容 OpenAI /chat/completions 协议。API Key 保存在本机私有存储；" +
                "添加时会发送 1×1 图片验证多模态能力。",
            11f, pal.muted,
        ))
        body.addView(primaryButton("添加模型") { showAddDialog() })
        refresh()
        return subPage("AI 模型", body)
    }

    private fun refresh() {
        listContainer.removeAllViews()
        val models = graph.aiModelStore.list()
        if (models.isEmpty()) {
            listContainer.addView(emptyView("还没有模型", "添加一个 OpenAI 兼容模型后即可使用 AI 助手", "🤖"))
            return
        }
        models.forEach { model ->
            listContainer.addView(listRow(
                title = model.name,
                subtitle = "${model.modelId} · ${if (model.multimodalVerified) "多模态已验证" else "纯文本"}",
                emoji = "🤖",
                trailing = text("删除", 13f, pal.danger, true).apply {
                    setOnClickListener {
                        host.confirm("删除模型", "将删除 ${model.name} 的配置。", danger = true) {
                            graph.aiModelStore.delete(model.id)
                            if (graph.settings.preferredAiModelId == model.id) graph.settings.preferredAiModelId = ""
                            refresh()
                        }
                    }
                    setPadding(dp(10), dp(6), dp(4), dp(6))
                },
            ))
        }
    }

    private fun showAddDialog() {
        val name = input("显示名称，例如 GPT-4o")
        val modelId = input("模型标识，例如 gpt-4o")
        val apiUrl = input("API 地址，例如 https://api.example.com/v1")
        val apiKey = input("API Key", password = true)
        val wrapper = column(8) {
            addView(name); addView(modelId); addView(apiUrl); addView(apiKey)
        }
        AlertDialog.Builder(context)
            .setTitle("添加模型")
            .setView(wrapper)
            .setNegativeButton("取消", null)
            .setPositiveButton("添加并验证") { _, _ ->
                val config = AiModelConfig(
                    id = UUID.randomUUID().toString().replace("-", "").take(12),
                    name = name.text.toString().trim(),
                    modelId = modelId.text.toString().trim(),
                    apiUrl = apiUrl.text.toString().trim(),
                    apiKey = apiKey.text.toString().trim(),
                )
                when {
                    config.name.isBlank() || config.modelId.isBlank() || config.apiUrl.isBlank() -> {
                        host.notify("请填写名称、模型标识与 API 地址")
                    }
                    else -> verifyAndSave(config)
                }
            }.show()
    }

    private fun verifyAndSave(config: AiModelConfig) {
        host.notify("正在验证模型…")
        host.runRemote({ graph.aiClient.probeMultimodal(config) }) { result ->
            when (result) {
                is RemoteResult.Failure -> {
                    // 验证失败仍然允许保存为纯文本模型（与桌面端“拒绝多模态场景”而非“拒绝保存”一致）。
                    host.confirm("验证未通过", "${result.error.message}。仍要保存为纯文本模型吗？") {
                        graph.aiModelStore.save(config.copy(multimodalVerified = false))
                        refresh()
                    }
                }
                is RemoteResult.Success -> {
                    graph.aiModelStore.save(config.copy(multimodalVerified = true))
                    host.notify("模型已保存，多模态验证通过")
                    refresh()
                }
            }
        }
    }
}
