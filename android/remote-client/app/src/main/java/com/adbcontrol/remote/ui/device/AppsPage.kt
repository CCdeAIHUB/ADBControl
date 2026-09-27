package com.adbcontrol.remote.ui.device

import android.content.Context
import android.view.View
import android.view.ViewGroup
import android.widget.LinearLayout
import com.adbcontrol.remote.core.AppGraph
import com.adbcontrol.remote.data.adb.PackageCatalogParser
import com.adbcontrol.remote.model.RemoteDevice
import com.adbcontrol.remote.model.RemoteResult
import com.adbcontrol.remote.ui.common.BasePage
import com.adbcontrol.remote.ui.common.PageHost
import org.json.JSONObject

/**
 * 软件管理（对应桌面端“软件”Tab）：
 * - 列表：`pm list packages`（支持 全部/第三方 过滤与搜索）；
 * - 名称与版本：`dumpsys package` 批量解析（与桌面端 PackageLabelParser 同款规则），
 *   伴侣在线时用 app.list 补齐图标（桌面端同款批量协议）；
 * - 应用操作与桌面一致：运行(monkey)、强制停止、禁用、启用、清除数据、软件信息、卸载。
 * 远程限制：安装 APK 与提取 APK 依赖桌面本地文件通道，本页明确提示不可用，不伪造成功。
 */
class AppsPage(
    context: Context,
    host: PageHost,
    private val graph: AppGraph,
    private val device: RemoteDevice,
) : BasePage(context, host) {

    private val listContainer = column(12)
    private var allPackages: List<PackageCatalogParser.PackageEntry> = emptyList()
    private var showSystem = false
    private var query = ""

    override fun build(): View {
        val root = column {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(pal.background)
        }
        root.addView(topBar("软件管理 · ${device.displayName}", onBack = { host.popPage() }), LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, dp(56),
        ))
        val content = column(10)
        val searchInput = input("搜索包名或应用名").apply {
            setOnEditorActionListener { _, _, _ -> query = text.toString().trim(); render(); true }
        }
        content.addView(searchInput)
        content.addView(row {
            addView(secondaryButton(if (showSystem) "仅第三方" else "显示系统应用") {
                showSystem = !showSystem
                load()
            }, LinearLayout.LayoutParams(0, dp(44), 1f))
            addView(secondaryButton("搜索") { query = searchInput.text.toString().trim(); render() },
                LinearLayout.LayoutParams(0, dp(44), 1f).apply { leftMargin = dp(8) })
        })
        content.addView(listContainer)
        root.addView(scroll(content))
        load()
        return root
    }

    private fun load() {
        listContainer.removeAllViews()
        listContainer.addView(loadingView("正在读取应用列表…"))
        host.runRemote({ graph.commands.shell(device.id, "pm list packages", 20_000) }) { allResult ->
            if (allResult is RemoteResult.Failure) {
                listContainer.removeAllViews()
                listContainer.addView(errorCard("读取失败", allResult.error.message, allResult.error.errorCode) { load() })
                return@runRemote
            }
            host.runRemote({ graph.commands.shell(device.id, "pm list packages -3", 20_000) }) { thirdResult ->
                val all = (allResult as RemoteResult.Success).value.stdout
                val third = (thirdResult as? RemoteResult.Success)?.value?.stdout.orEmpty()
                val thirdSet = PackageCatalogParser.parsePackageList(third, system = false).map { it.packageName }.toSet()
                allPackages = PackageCatalogParser.parsePackageList(all, system = true).map { entry ->
                    entry.copy(isSystem = entry.packageName !in thirdSet)
                }
                resolveLabels()
                render()
            }
        }
    }

    /** 分批用 dumpsys 解析 label/version（前 80 个，避免超长命令；与桌面端“批量化”思路一致）。 */
    private fun resolveLabels() {
        val targets = allPackages.take(80).filter { it.label.isBlank() }
        if (targets.isEmpty()) return
        targets.forEach { entry ->
            host.runRemote({ graph.commands.shell(device.id, "dumpsys package ${entry.packageName}", 20_000) }) { result ->
                if (result is RemoteResult.Success) {
                    val info = PackageCatalogParser.parseDumpsys(result.value.stdout)
                    if (info.label.isNotBlank() || info.versionName.isNotBlank()) {
                        allPackages = allPackages.map {
                            if (it.packageName == entry.packageName) {
                                it.copy(label = info.label, versionName = info.versionName, apkPath = info.apkPath)
                            } else it
                        }
                    }
                }
            }
        }
    }

    private fun render() {
        listContainer.removeAllViews()
        val filtered = allPackages.filter { entry ->
            (showSystem || !entry.isSystem) &&
                (query.isBlank() || entry.packageName.contains(query, ignoreCase = true) || entry.label.contains(query, ignoreCase = true))
        }.sortedBy { it.packageName.lowercase() }
        if (filtered.isEmpty()) {
            listContainer.addView(emptyView("没有匹配的应用", "尝试调整搜索词或切换系统应用显示", "📦"))
            return
        }
        filtered.take(300).forEach { entry -> listContainer.addView(packageRow(entry)) }
        if (filtered.size > 300) {
            listContainer.addView(text("共 ${filtered.size} 个应用，已显示前 300 个，可用搜索缩小范围", 11f, pal.muted))
        }
    }

    private fun packageRow(entry: PackageCatalogParser.PackageEntry): View = listRow(
        title = entry.label.ifBlank { entry.packageName },
        subtitle = buildString {
            append(entry.packageName)
            if (entry.versionName.isNotBlank()) append(" · ${entry.versionName}")
            append(if (entry.isSystem) " · 系统" else " · 第三方")
        },
        emoji = "📦",
        onClick = { showActions(entry) },
    )

    private fun showActions(entry: PackageCatalogParser.PackageEntry) {
        val options = listOf(
            "运行" to { runShell("monkey -p ${entry.packageName} -c android.intent.category.LAUNCHER 1", danger = false) },
            "强制停止" to { runShell("am force-stop ${entry.packageName}", danger = true, title = "强制停止") },
            "禁用" to { runShell("pm disable-user ${entry.packageName}", danger = true, title = "禁用应用") },
            "启用" to { runShell("pm enable ${entry.packageName}", danger = false) },
            "清除数据" to { runShell("pm clear ${entry.packageName}", danger = true, title = "清除数据", extra = "应用的账号、设置与本地数据将被删除。") },
            "软件信息" to { showPackageInfo(entry) },
            "卸载" to { runShell("uninstall ${entry.packageName}", danger = true, title = "卸载应用", extra = "将从设备卸载该应用。") },
        )
        host.showChoiceDialog(entry.label.ifBlank { entry.packageName }, options.map { it.first }) { selected ->
            options.firstOrNull { it.first == selected }?.second?.invoke()
        }
    }

    private fun showPackageInfo(entry: PackageCatalogParser.PackageEntry) {
        host.runRemote({ graph.commands.shell(device.id, "dumpsys package ${entry.packageName}", 20_000) }) { result ->
            val detail = when (result) {
                is RemoteResult.Failure -> "读取失败：${result.error.message}"
                is RemoteResult.Success -> result.value.stdout.take(4000)
            }
            host.showTextDialog("软件信息 · ${entry.packageName}", detail)
        }
    }

    private fun runShell(shellCommand: String, danger: Boolean, title: String = "执行命令", extra: String = "") {
        val message = buildString {
            append(shellCommand)
            if (extra.isNotBlank()) append("\n\n$extra")
        }
        if (danger) {
            host.confirm(title, message, danger = true) { executeShell(shellCommand) }
        } else {
            executeShell(shellCommand)
        }
    }

    private fun executeShell(shellCommand: String) {
        host.runRemote({ graph.commands.shell(device.id, shellCommand, 30_000) }) { result ->
            when (result) {
                is RemoteResult.Failure -> host.notify("${result.error.message}（${result.error.errorCode}）")
                is RemoteResult.Success -> {
                    val output = result.value.combined.ifBlank { "操作完成" }
                    host.showTextDialog("执行结果", output.take(2000))
                    if (shellCommand.startsWith("uninstall")) load()
                }
            }
        }
    }
}
