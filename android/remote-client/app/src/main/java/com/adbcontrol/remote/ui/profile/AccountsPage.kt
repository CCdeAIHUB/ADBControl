package com.adbcontrol.remote.ui.profile

import android.app.AlertDialog
import com.adbcontrol.remote.ui.common.themedDialogBuilder
import android.content.Context
import android.view.View
import android.widget.LinearLayout
import com.adbcontrol.remote.core.AppGraph
import com.adbcontrol.remote.model.RemoteAccount
import com.adbcontrol.remote.model.RemoteResult
import com.adbcontrol.remote.ui.common.BasePage
import com.adbcontrol.remote.ui.common.PageHost

/**
 * 账号管理（对应桌面端本地管理台的成员管理）。
 * 明确边界：Core 安全策略规定内置 admin 仅限 Core 本机登录，远程会话无法获得 Admin 角色，
 * 因此远程调用 admin.* 会被服务端以 REMOTE_AUTH_FORBIDDEN 拒绝——本页如实展示服务端错误，
 * 不伪造成功；真实的管理入口在 Core 本机 stdio（remote.admin.*）。
 */
class AccountsPage(
    context: Context,
    host: PageHost,
    private val graph: AppGraph,
) : BasePage(context, host) {

    private val listContainer = column(10)

    override fun build(): View {
        val body = column(12)
        body.addView(card(column(6) {
            addView(text("服务端限制", 14f, pal.warning, true))
            addView(text(
                "远程协议名义上提供 admin.* 方法，但当前只有内置管理员拥有管理员角色，" +
                    "而该账号受安全策略限制只能在 Core 本机登录。下面的操作会被服务端拒绝并显示错误码，" +
                    "真实管理请使用 Core 本机控制台（remote.admin.*）。",
                12f, pal.secondary,
            ))
        }))
        body.addView(listContainer)
        refresh()
        return subPage("账号管理", body)
    }

    private fun refresh() {
        listContainer.removeAllViews()
        listContainer.addView(loadingView("正在读取账号…"))
        host.runRemote({ graph.repository.accounts() }) { result ->
            listContainer.removeAllViews()
            when (result) {
                is RemoteResult.Failure -> listContainer.addView(
                    errorCard("读取失败", result.error.message, result.error.errorCode, result.error.suggestion) { refresh() },
                )
                is RemoteResult.Success -> if (result.value.isEmpty()) {
                    listContainer.addView(emptyView("无账号数据", "服务端拒绝了请求或没有可显示的账号", "👥"))
                } else {
                    result.value.forEach { account -> listContainer.addView(accountCard(account)) }
                }
            }
        }
    }

    private fun accountCard(account: RemoteAccount): View = card(column(6) {
        addView(row {
            addView(column(4) {
                addView(text(account.username, 15f, pal.text, true))
                addView(text(
                    if (account.builtIn) "内置管理员" else "成员 · ${account.devices.size} 台设备",
                    11f, pal.secondary,
                ))
            }, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
            if (account.passwordChangeRequired) addView(badge("待改密", pal.warning))
        })
        if (!account.builtIn) {
            addView(row {
                addView(secondaryButton("重置密码") { resetForm(account) }, LinearLayout.LayoutParams(0, dp(42), 1f))
                addView(secondaryButton("删除") {
                    host.confirm("删除账号", "将删除 ${account.username} 并立即撤销其全部会话。", danger = true) {
                        host.runRemote({ graph.repository.deleteAccount(account.username) }) { notifyAndRefresh(it) }
                    }
                }, LinearLayout.LayoutParams(0, dp(42), 1f).apply { leftMargin = dp(6) })
            })
        }
    }, 14)

    private fun resetForm(account: RemoteAccount) {
        val password = input("新密码（至少 8 个字符）", password = true)
        context.themedDialogBuilder()
            .setTitle("重置 ${account.username} 的密码")
            .setView(password)
            .setNegativeButton("取消", null)
            .setPositiveButton("确认重置") { _, _ ->
                if (password.text.length < 8) {
                    host.notify("密码至少 8 个字符")
                } else {
                    host.runRemote({ graph.repository.resetPassword(account.username, password.text.toString()) }) { notifyAndRefresh(it) }
                }
            }.show()
    }

    private fun notifyAndRefresh(result: RemoteResult<Any>) {
        host.notify(
            when (result) {
                is RemoteResult.Failure -> "${result.error.message}（${result.error.errorCode}）"
                is RemoteResult.Success -> "操作完成"
            },
        )
        refresh()
    }
}
