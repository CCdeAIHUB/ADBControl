package com.adbcontrol.remote.ui.device

import android.content.Context
import android.graphics.BitmapFactory
import android.util.Base64
import android.view.View
import android.view.ViewGroup
import android.widget.LinearLayout
import com.adbcontrol.remote.core.AppGraph
import com.adbcontrol.remote.data.adb.LsParser
import com.adbcontrol.remote.security.RemotePathPolicy
import com.adbcontrol.remote.data.io.DownloadsWriter
import com.adbcontrol.remote.model.RemoteDevice
import com.adbcontrol.remote.model.RemoteResult
import com.adbcontrol.remote.ui.common.BasePage
import com.adbcontrol.remote.ui.common.PageHost
import com.adbcontrol.remote.ui.common.formatBytes
import org.json.JSONObject

/**
 * 文件管理（对应桌面端“文件”Tab）：
 * - 浏览：`ls -la -p`（与桌面端同一命令与解析规则），支持进入目录、上一级、路径直达；
 * - 删除：`rm -rf`（RemotePathPolicy 保护根目录与共享存储根）；
 * - 上传：手机本地文件 → 分块 base64 → `printf %s <chunk> | base64 -d >> '<path>'`。
 *   协议约束（必须注释说明）：远程请求上限 1MiB，且 Linux 单参数上限约 128KB，
 *   因此按 ~90KB base64/块（约 64KB 原始字节）分块追加；未来 Core 提供二进制流后应整体替换；
 * - 下载：`base64 <path>`（响应上限 4MiB，约 3MB 文件），解码后存入 Downloads/ADBControl；
 * - 预览：图片直接显示，文本显示前 4000 字符。
 */
class FilesPage(
    context: Context,
    host: PageHost,
    private val graph: AppGraph,
    private val device: RemoteDevice,
) : BasePage(context, host) {

    private var currentPath: String = "/sdcard"
    private val pathInputRef = input("远程路径，例如 /sdcard")
    private val listContainer = column(10)

    override fun build(): View {
        val root = column {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(pal.background)
        }
        root.addView(topBar("文件管理 · ${device.displayName}", onBack = { host.popPage() }), LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, dp(56),
        ))
        val content = column(10)
        pathInputRef.setText(currentPath)
        content.addView(pathInputRef)
        content.addView(row {
            addView(secondaryButton("上一级") { navigate(parentPath(currentPath)) },
                LinearLayout.LayoutParams(0, dp(44), 1f))
            addView(secondaryButton("刷新") { navigate(currentPath) },
                LinearLayout.LayoutParams(0, dp(44), 1f).apply { leftMargin = dp(8) })
        })
        content.addView(row {
            addView(secondaryButton("新建目录") { promptMkdir() },
                LinearLayout.LayoutParams(0, dp(44), 1f))
            addView(secondaryButton("上传文件") { host.pickUploadFile(::uploadFile) },
                LinearLayout.LayoutParams(0, dp(44), 1f).apply { leftMargin = dp(8) })
        })
        content.addView(listContainer)
        root.addView(scroll(content))
        navigate(currentPath)
        return root
    }

    private fun navigate(target: String) {
        val normalized = RemotePathPolicy.normalize(target)
        if (normalized == null) {
            host.notify("路径无效（REMOTE_PATH_INVALID）")
            return
        }
        currentPath = normalized
        pathInputRef.setText(normalized)
        listContainer.removeAllViews()
        listContainer.addView(loadingView("正在读取 $normalized …"))
        host.runRemote({ graph.commands.shell(device.id, "ls -la -p '$normalized'", 20_000) }) { result ->
            listContainer.removeAllViews()
            when (result) {
                is RemoteResult.Failure -> listContainer.addView(
                    errorCard("读取失败", result.error.message, result.error.errorCode) { navigate(currentPath) },
                )
                is RemoteResult.Success -> {
                    if (!result.value.success) {
                        listContainer.addView(errorCard("目录不可读", result.value.stderr.ifBlank { "ls 退出码 ${result.value.exitCode}" }) { navigate(currentPath) })
                        return@runRemote
                    }
                    val entries = LsParser.parse(result.value.stdout)
                    if (entries.isEmpty()) {
                        listContainer.addView(emptyView("空目录", normalized, "📂"))
                        return@runRemote
                    }
                    entries.forEach { entry -> listContainer.addView(entryRow(entry)) }
                }
            }
        }
    }

    private fun entryRow(entry: LsParser.Entry): View = listRow(
        title = entry.name,
        subtitle = buildString {
            append(if (entry.isDirectory) "目录" else if (entry.isSymlink) "链接" else formatBytes(entry.sizeBytes))
            append(" · ${entry.modifiedText}")
        },
        emoji = when {
            entry.isDirectory -> "📁"
            entry.isSymlink -> "🔗"
            else -> "📄"
        },
        onClick = {
            if (entry.isDirectory) {
                navigate(joinPath(currentPath, entry.name))
            } else if (entry.isSymlink) {
                host.notify("符号链接不支持进一步操作")
            } else {
                showFileActions(entry)
            }
        },
    )

    private fun showFileActions(entry: LsParser.Entry) {
        val remotePath = joinPath(currentPath, entry.name)
        host.showChoiceDialog(entry.name, listOf("下载到手机", "预览", "删除")) { selected ->
            when (selected) {
                "下载到手机" -> downloadFile(remotePath)
                "预览" -> previewFile(remotePath, entry)
                "删除" -> host.confirm("删除文件", "$remotePath 将被永久删除。", danger = true) {
                    executeAndRefresh("rm -f '$remotePath'")
                }
            }
        }
    }

    // ---------- 下载 / 预览 ----------

    private fun downloadFile(remotePath: String) {
        host.runRemote({ graph.commands.shell(device.id, "base64 '$remotePath'", 60_000) }) { result ->
            when (result) {
                is RemoteResult.Failure -> host.notify("下载失败：${result.error.message}（${result.error.errorCode}）")
                is RemoteResult.Success -> {
                    if (!result.value.success) {
                        host.notify("设备返回失败：${result.value.stderr.take(200)}")
                        return@runRemote
                    }
                    val bytes = try {
                        Base64.decode(result.value.stdout.trim(), Base64.DEFAULT)
                    } catch (error: Throwable) {
                        host.notify("数据解码失败：${error.message}")
                        return@runRemote
                    }
                    val saved = DownloadsWriter.save(context, remotePath.substringAfterLast('/'), bytes)
                    if (saved == null) {
                        host.notify("保存失败，请检查存储空间")
                    } else {
                        host.notify("已保存到 $saved（${bytes.size} 字节）")
                    }
                }
            }
        }
    }

    private fun previewFile(remotePath: String, entry: LsParser.Entry) {
        val imageExtensions = listOf("png", "jpg", "jpeg", "gif", "webp", "bmp")
        val textExtensions = listOf("txt", "log", "xml", "json", "md", "csv", "properties", "conf", "ini", "html")
        val extension = remotePath.substringAfterLast('.', "").lowercase()
        when {
            extension in imageExtensions -> host.runRemote({ graph.commands.shell(device.id, "base64 '$remotePath'", 60_000) }) { result ->
                if (result is RemoteResult.Success && result.value.success) {
                    val bytes = Base64.decode(result.value.stdout.trim(), Base64.DEFAULT)
                    val bitmap = BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
                    if (bitmap != null) host.showImageDialog(entry.name, bitmap) else host.notify("图片解码失败")
                } else {
                    host.notify("预览失败")
                }
            }
            extension in textExtensions || entry.sizeBytes < 200_000 -> host.runRemote({ graph.commands.shell(device.id, "head -c 4000 '$remotePath'", 30_000) }) { result ->
                if (result is RemoteResult.Success && result.value.success) {
                    host.showTextDialog("预览 · ${entry.name}", result.value.stdout.ifBlank { "(空文件)" })
                } else {
                    host.notify("预览失败")
                }
            }
            else -> host.notify("该类型暂不支持预览，请下载后查看")
        }
    }

    // ---------- 上传 ----------

    /** 分块上传；协议与 argv 双重限制决定了块大小，块间串行追加保证顺序。 */
    private fun uploadFile(name: String, bytes: ByteArray) {
        val maxChunkRaw = 64 * 1024
        if (bytes.size > 20 * 1024 * 1024) {
            host.notify("文件超过 20MB，超出远程协议能力，请使用桌面端传输")
            return
        }
        val remotePath = joinPath(currentPath, name)
        val chunks = bytes.size / maxChunkRaw + if (bytes.size % maxChunkRaw == 0 && bytes.isNotEmpty()) 0 else 1
        if (bytes.isEmpty()) {
            executeAndRefresh("printf '' > '$remotePath'")
            return
        }
        host.notify("开始上传 $name（$chunks 块）…")
        // 先清空目标文件，再逐块追加。
        executeShell("printf '' > '$remotePath'") { ok ->
            if (!ok) return@executeShell
            uploadChunk(bytes, remotePath, 0, chunks)
        }
    }

    private fun uploadChunk(bytes: ByteArray, remotePath: String, index: Int, total: Int) {
        if (index >= total) {
            host.notify("上传完成：$remotePath")
            navigate(currentPath)
            return
        }
        val from = index * 64 * 1024
        val to = (from + 64 * 1024).coerceAtMost(bytes.size)
        val chunk = bytes.copyOfRange(from, to)
        val base64 = Base64.encodeToString(chunk, Base64.NO_WRAP)
        host.runRemote({
            graph.commands.shell(device.id, "printf %s $base64 | base64 -d >> '$remotePath'", 30_000)
        }) { result ->
            val ok = result is RemoteResult.Success && result.value.success
            if (!ok) {
                host.notify("上传第 ${index + 1}/$total 块失败，已中止")
                return@runRemote
            }
            uploadChunk(bytes, remotePath, index + 1, total)
        }
    }

    // ---------- 其他 ----------

    private fun promptMkdir() {
        host.promptInput("新建目录", "输入目录名") { name ->
            val normalized = RemotePathPolicy.normalize(name) ?: return@promptInput host.notify("目录名无效")
            val target = if (normalized.startsWith('/')) normalized else joinPath(currentPath, normalized)
            executeAndRefresh("mkdir -p '$target'")
        }
    }

    private fun executeAndRefresh(shellCommand: String) {
        executeShell(shellCommand) { navigate(currentPath) }
    }

    private fun executeShell(shellCommand: String, done: (Boolean) -> Unit = {}) {
        host.runRemote({ graph.commands.shell(device.id, shellCommand, 30_000) }) { result ->
            when (result) {
                is RemoteResult.Failure -> {
                    host.notify("${result.error.message}（${result.error.errorCode}）")
                    done(false)
                }
                is RemoteResult.Success -> {
                    if (!result.value.success) {
                        host.notify("执行失败：${result.value.stderr.take(200)}")
                    }
                    done(result.value.success)
                }
            }
        }
    }

    private fun parentPath(path: String): String {
        val trimmed = path.trimEnd('/')
        val index = trimmed.lastIndexOf('/')
        return if (index <= 0) "/" else trimmed.substring(0, index)
    }

    private fun joinPath(base: String, name: String): String {
        val cleanName = name.trim().trimStart('/')
        return if (base.endsWith("/")) base + cleanName else "$base/$cleanName"
    }
}

/** 文本输入弹窗（新建目录等）：由 MainActivity 实现。 */
private fun PageHost.promptInput(title: String, hint: String, onConfirm: (String) -> Unit) {
    showPromptDialog(title, hint, onConfirm)
}
