package com.adbcontrol.remote.ui.device

import android.app.Dialog
import android.content.Context
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.view.Window
import android.widget.LinearLayout
import com.adbcontrol.remote.core.AppGraph
import com.adbcontrol.remote.data.adb.LockStateParser
import com.adbcontrol.remote.data.adb.SafeKeyboard
import com.adbcontrol.remote.model.RemoteDevice
import com.adbcontrol.remote.model.RemoteResult
import com.adbcontrol.remote.ui.common.PageHost
import com.adbcontrol.remote.ui.common.column
import com.adbcontrol.remote.ui.common.dp
import com.adbcontrol.remote.ui.common.pal
import com.adbcontrol.remote.ui.common.primaryButton
import com.adbcontrol.remote.ui.common.row
import com.adbcontrol.remote.ui.common.secondaryButton
import com.adbcontrol.remote.ui.common.shape
import com.adbcontrol.remote.ui.common.text
import java.util.concurrent.atomic.AtomicBoolean

/**
 * 安全键盘（对应桌面端 DeviceKeyboard flyout）：
 * - 每个按键单独发送：可打印 ASCII 逐字符 `input text '<c>'`，特殊键发 keyevent；
 * - 互斥发送：busy 时拒绝（DEVICE_KEYBOARD_BUSY），锁屏 Unknown/锁定 时拒绝发送；
 * - 不经过系统输入法，也不拼长文本，规避注入与剪贴板泄漏。
 */
class SafeKeyboardSheet(
    private val context: Context,
    private val host: PageHost,
    private val graph: AppGraph,
    private val device: RemoteDevice,
    private val onDismissRefresh: () -> Unit,
) {
    private val busy = AtomicBoolean(false)
    private var lockKnown = false
    private val lockHint by lazy { context.text("锁屏检测中…", 11f, context.pal.muted) }

    fun show() {
        checkLockState()
        val dialog = Dialog(context)
        dialog.requestWindowFeature(Window.FEATURE_NO_TITLE)
        dialog.setContentView(buildContent(dialog))
        dialog.window?.apply {
            setLayout(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT)
            setGravity(Gravity.BOTTOM)
            setBackgroundDrawableResource(android.R.color.transparent)
        }
        dialog.setOnDismissListener { onDismissRefresh() }
        dialog.show()
    }

    private fun buildContent(dialog: Dialog): View {
        val root = context.column(8) {
            setBackgroundColor(context.pal.surface)
            setPadding(context.dp(12), context.dp(14), context.dp(12), context.dp(20))
            addView(context.row {
                addView(context.text("安全键盘", 15f, context.pal.text, true), LinearLayout.LayoutParams(
                    0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f,
                ))
                addView(lockHint)
                addView(context.text("关闭", 14f, context.pal.brand, true).apply {
                    setOnClickListener { dialog.dismiss() }
                    setPadding(context.dp(10), 0, context.dp(4), 0)
                })
            })
            SafeKeyboard.keyLabels.forEach { rowKeys ->
                addView(context.row {
                    rowKeys.forEach { key ->
                        addView(keyButton(key), LinearLayout.LayoutParams(0, context.dp(46), 1f).apply { leftMargin = context.dp(2) })
                    }
                })
            }
            addView(context.row {
                addView(keyButton(" ", display = "␣"), LinearLayout.LayoutParams(0, context.dp(46), 2f).apply { leftMargin = context.dp(2) })
                addView(keyButton("@", display = "@"), LinearLayout.LayoutParams(0, context.dp(46), 1f).apply { leftMargin = context.dp(2) })
                addView(keyButton(".", display = "."), LinearLayout.LayoutParams(0, context.dp(46), 1f).apply { leftMargin = context.dp(2) })
                addView(keyButton("-", display = "-"), LinearLayout.LayoutParams(0, context.dp(46), 1f).apply { leftMargin = context.dp(2) })
            })
            addView(context.row {
                addView(specialButton("退格", "backspace"), LinearLayout.LayoutParams(0, context.dp(46), 1f).apply { leftMargin = context.dp(2) })
                addView(specialButton("回车", "enter"), LinearLayout.LayoutParams(0, context.dp(46), 1f).apply { leftMargin = context.dp(2) })
                addView(specialButton("←", "left"), LinearLayout.LayoutParams(0, context.dp(46), 1f).apply { leftMargin = context.dp(2) })
                addView(specialButton("→", "right"), LinearLayout.LayoutParams(0, context.dp(46), 1f).apply { leftMargin = context.dp(2) })
            })
        }
        return root
    }

    private fun keyButton(key: String, display: String = key): View = context.secondaryButton(display) { sendKey(key) }

    private fun specialButton(label: String, key: String): View = context.secondaryButton(label) { sendSpecial(key) }

    private fun checkLockState() {
        host.runRemote({ graph.commands.queryLockState(device.id) }) { result ->
            when (result) {
                is RemoteResult.Failure -> {
                    lockKnown = false
                    lockHint.text = "状态未知"
                }
                is RemoteResult.Success -> when (result.value.state) {
                    LockStateParser.LockState.Locked -> {
                        lockKnown = false
                        lockHint.text = "设备已锁屏"
                    }
                    LockStateParser.LockState.Unlocked -> {
                        lockKnown = true
                        lockHint.text = "可以输入"
                    }
                    LockStateParser.LockState.Unknown -> {
                        lockKnown = false
                        lockHint.text = "状态未知"
                    }
                }
            }
        }
    }

    private fun sendKey(key: String) {
        if (!ensureSendable()) return
        val textArg = SafeKeyboard.printableCommand(key.firstOrNull() ?: ' ')
            ?: run {
                busy.set(false)
                host.notify("不支持的字符（DEVICE_KEYBOARD_INVALID_KEY）")
                return
            }
        send("input text '$textArg'")
    }

    private fun sendSpecial(key: String) {
        if (!ensureSendable()) return
        val code = SafeKeyboard.keyeventCommand(key)
            ?: run {
                busy.set(false)
                host.notify("未知按键（DEVICE_KEYBOARD_INVALID_KEY）")
                return
            }
        send("input keyevent $code")
    }

    /** 桌面端同款门槛：锁屏 Unknown/锁定 时拒绝，busy 时拒绝（错误码对齐）。 */
    private fun ensureSendable(): Boolean {
        if (!busy.compareAndSet(false, true)) {
            host.notify("正在发送上一个按键（DEVICE_KEYBOARD_BUSY）")
            return false
        }
        if (!lockKnown) {
            busy.set(false)
            host.notify("设备锁屏状态未知或已锁定，拒绝发送（DEVICE_KEYBOARD_LOCK_UNKNOWN）")
            return false
        }
        return true
    }

    private fun send(shellCommand: String) {
        host.runRemote({ graph.commands.shell(device.id, shellCommand, timeoutMs = 3_000) }) { result ->
            busy.set(false)
            when (result) {
                is RemoteResult.Failure -> host.notify("${result.error.message}（${result.error.errorCode}）")
                is RemoteResult.Success -> if (result.value.exitCode != 0) {
                    host.notify("按键发送失败（DEVICE_KEYBOARD_SEND_FAILED）")
                }
            }
        }
    }
}
