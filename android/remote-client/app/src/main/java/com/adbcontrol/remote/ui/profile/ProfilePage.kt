package com.adbcontrol.remote.ui.profile

import android.app.AlertDialog
import android.content.Context
import android.view.View
import android.widget.LinearLayout
import com.adbcontrol.remote.core.AppGraph
import com.adbcontrol.remote.model.Session
import com.adbcontrol.remote.model.UserRole
import com.adbcontrol.remote.navigation.AccessPolicy
import com.adbcontrol.remote.ui.ai.AiModelPage
import com.adbcontrol.remote.ui.common.BasePage
import com.adbcontrol.remote.ui.common.PageHost

/**
 * “我的”页：账号信息、改密、退出、移除 Core 与各设置入口。
 * 账号管理入口仅管理员可见；远程 admin.* 受 Core 安全策略限制（内置管理员仅本机），
 * 调用失败时会显示服务端原始错误码（不伪造成功）。
 */
class ProfilePage(
    context: Context,
    host: PageHost,
    private val graph: AppGraph,
    private val session: Session,
) : BasePage(context, host) {

    private data class Entry(val emoji: String, val title: String, val subtitle: String, val action: () -> Unit)

    override fun build(): View {
        val body = column(14)
        body.addView(card(column(6) {
            addView(text(session.username, 20f, pal.text, true))
            addView(text(
                if (session.role == UserRole.ADMIN) "管理员账号 · ${session.assignedDevices.size} 台已分配设备"
                else "成员账号 · ${session.assignedDevices.size} 台已授权设备",
                12f, pal.secondary,
            ))
        }))
        val entries = buildList {
            add(Entry("⚙", "设置", "主题 · 预览间隔 · 调度") { host.pushPage(SettingsPage(context, host, graph)) })
            add(Entry("🧾", "日志与诊断", "会话事件 · 导出") { host.pushPage(LogsPage(context, host, graph)) })
            if (AccessPolicy.canSeeAccounts(session.role)) {
                add(Entry("👥", "账号管理", "成员账号与设备分配") { host.pushPage(AccountsPage(context, host, graph)) })
                add(Entry("🤖", "AI 模型管理", "模型配置与多模态验证") { host.pushPage(AiModelPage(context, host, graph)) })
            }
        }
        entries.forEach { entry ->
            body.addView(listRow(entry.title, entry.subtitle, emoji = entry.emoji, onClick = entry.action))
        }
        body.addView(secondaryButton("修改登录密码") { showChangePassword() })
        body.addView(secondaryButton("退出登录") {
            host.confirm("退出登录", "退出后需要重新输入账号密码。") { host.logout() }
        })
        body.addView(secondaryButton("移除此 Core") {
            host.confirm("移除 Core", "将删除本机保存的地址和证书信息。", danger = true) { host.resetCore() }
        })
        body.addView(text(
            "ADBControl Remote 0.2.0\n远程客户端不包含 ADB 可执行文件；\n所有设备操作经加密 QUIC 通道由 Core 执行。",
            11f, pal.muted,
        ).apply { gravity = android.view.Gravity.CENTER })
        return scroll(body)
    }

    private fun showChangePassword() {
        val current = input("当前密码", password = true)
        val replacement = input("新密码（至少 8 个字符）", password = true)
        val fields = column(8) { addView(current); addView(replacement) }
        AlertDialog.Builder(context)
            .setTitle("修改登录密码")
            .setView(fields)
            .setNegativeButton("取消", null)
            .setPositiveButton("确认修改") { _, _ ->
                if (replacement.text.length < 8) {
                    host.notify("新密码至少需要 8 个字符")
                } else {
                    host.runRemote({ graph.repository.changePassword(current.text.toString(), replacement.text.toString()) }) { result ->
                        when (result) {
                            is com.adbcontrol.remote.model.RemoteResult.Failure ->
                                host.notify("${result.error.message}（${result.error.errorCode}）")
                            is com.adbcontrol.remote.model.RemoteResult.Success -> {
                                host.notify("密码已修改，请重新登录")
                                host.logout()
                            }
                        }
                    }
                }
            }.show()
    }
}
