package com.adbcontrol.remote.ui

import android.content.Context
import android.graphics.Color
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.LinearLayout
import com.adbcontrol.remote.model.AppError
import com.adbcontrol.remote.model.CoreProfile
import com.adbcontrol.remote.security.FingerprintPolicy
import com.adbcontrol.remote.ui.common.*

/**
 * 连接与登录页。用户只需要：地址 → 用户名/密码。
 * 证书信任由 App 自动处理：首次连接记录服务器证书指纹（TOFU）并在后续连接校验；
 * 高级用户仍可通过 CoreProfile 的指纹/证书字段强校验，但界面不要求。
 */
class AuthScreens(private val context: Context) {

    fun setup(onSubmit: (CoreProfile) -> Unit): View {
        val endpoint = context.input("服务器地址，例如 192.168.1.10:45921")
        val webEndpoint = context.input("投屏服务地址（可选，例如 http://192.168.1.10:18087）")
        val errorText = context.text("", 12f, context.pal.danger)
        val note = context.text(CONNECT_NOTE, 11f, context.pal.muted)
        val submit = context.primaryButton("下一步") {
            val raw = endpoint.text.toString().trim()
            val validationError = when {
                raw.isBlank() -> "请填写服务器地址（主机:端口）"
                else -> null
            }
            if (validationError != null) {
                errorText.text = validationError
                return@primaryButton
            }
            errorText.text = ""
            // 允许直接输入 host:port；自动补 quic:// 协议头（原生层只接受 quic:// 地址）。
            val endpointValue = if (raw.startsWith("quic://")) raw else "quic://$raw"
            onSubmit(CoreProfile(endpoint = endpointValue, webEndpoint = webEndpoint.text.toString().trim()))
        }
        return centered(
            "连接远程 Core",
            "输入 Core 所在主机的远程控制地址；账号由 Core 管理员创建。",
            endpoint, webEndpoint, errorText, note,
            action = submit,
        )
    }

    fun login(profile: CoreProfile, onLogin: (String, String) -> Unit, onReset: () -> Unit): View {
        val username = context.input("远程账号用户名")
        val password = context.input("密码", password = true)
        return centered(
            "欢迎回来",
            "${profile.endpoint}\n内置 admin 仅允许在 Core 本机登录，请使用管理员创建的远程账号。",
            username, password,
            action = context.primaryButton("登录") { onLogin(username.text.toString(), password.text.toString()) },
            secondary = context.secondaryButton("更换服务器") { onReset() },
        )
    }

    fun changePassword(onSubmit: (String, String) -> Unit): View {
        val current = context.input("当前密码", password = true)
        val replacement = context.input("新密码（至少 8 个字符）", password = true)
        val repeat = context.input("再次输入新密码", password = true)
        val error = context.text("", 12f, context.pal.danger)
        return centered("首次登录请修改密码", "为避免默认凭据暴露，修改完成前不能使用远程控制。",
            current, replacement, repeat, error,
            action = context.primaryButton("确认修改") {
                when {
                    replacement.text.length < 8 -> error.text = "新密码至少需要 8 个字符"
                    replacement.text.toString() != repeat.text.toString() -> error.text = "两次输入的新密码不一致"
                    else -> onSubmit(current.text.toString(), replacement.text.toString())
                }
            })
    }

    fun loading(): View = centered("正在建立加密连接", "正在校验 Core 证书并建立 QUIC 会话，请稍候…")

    fun failure(error: AppError, onRetry: () -> Unit, onReset: () -> Unit): View =
        centered("连接没有完成", error.message,
            context.text(
                "错误码：${error.errorCode}\n模块：${error.module}${error.traceId?.let { "\n追踪：$it" } ?: ""}" +
                    if (error.errorCode == "REMOTE_CERTIFICATE_FINGERPRINT_MISMATCH") {
                        "\n\n服务器证书与已记录的指纹不一致。若确认 Core 更换过证书，请用“重新配置”删除旧指纹；" +
                            "否则请检查网络中是否存在中间人。"
                    } else {
                        ""
                    },
                12f, context.pal.muted,
            ),
            action = context.primaryButton("重试") { onRetry() },
            secondary = context.secondaryButton("重新配置") { onReset() })

    /**
     * 全屏认证页：无卡片，内容直接铺在软件背景色上并整体垂直居中。
     * 输入框用 surface 填充（浅色=白、深色=浅一档），与 background 保持可辨识的对比；
     * 文字层级 text/secondary/muted 全部出自 ThemePalette，保证深浅色一致。
     */
    private fun centered(
        title: String,
        subtitle: String,
        vararg fields: View,
        action: View? = null,
        secondary: View? = null,
    ): View {
        val content = context.column(14) {
            gravity = Gravity.CENTER_HORIZONTAL
            setPadding(context.dp(28), context.dp(24), context.dp(28), context.dp(32))
            addView(context.text("AC", 22f, Color.WHITE, true).apply {
                gravity = Gravity.CENTER
                background = context.shape(context.pal.brand, 20)
            }, LinearLayout.LayoutParams(context.dp(64), context.dp(64)))
            addView(context.text("ADBControl 远程控制", 12f, context.pal.muted, true).apply {
                gravity = Gravity.CENTER
            })
            addView(context.text(title, 24f, context.pal.text, true).apply { gravity = Gravity.CENTER })
            addView(context.text(subtitle, 13f, context.pal.secondary).apply { gravity = Gravity.CENTER })
            fields.forEach { field ->
                addView(field, ViewGroup.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT,
                ))
            }
            action?.let { addView(it, ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, context.dp(50),
            )) }
            secondary?.let { addView(it, ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, context.dp(46),
            )) }
        }
        // 外层铺满视口并垂直居中内容（ScrollView fillViewport 保证背景全屏覆盖）。
        val outer = context.column {
            gravity = Gravity.CENTER
            setBackgroundColor(context.pal.background)
        }
        outer.addView(content, ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT,
        ))
        return android.widget.ScrollView(context).apply {
            isFillViewport = true
            clipToPadding = false
            setBackgroundColor(context.pal.background)
            addView(outer, ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT,
            ))
        }
    }

    private companion object {
        val CONNECT_NOTE = """
            安全说明：首次连接会自动记录服务器的证书指纹（SHA-256），
            之后每次连接都会校验，防止连接被劫持；无需手动填写任何证书信息。
        """.trimIndent()
    }
}
