package com.adbcontrol.remote.ui.common

import android.view.View
import com.adbcontrol.remote.model.RemoteDevice
import com.adbcontrol.remote.model.RemoteResult

/**
 * 页面契约：所有页面只依赖 [PageHost] 与自己的数据层依赖，
 * 不直接接触 Activity / 传输层，保证 UI 与业务解耦。
 */
interface Page {
    fun build(): View

    /** 页面离开时清理轮询定时器等资源（由页面栈调用）。 */
    fun onDetach() {}
}

interface PageHost {
    /** 在工作线程执行远程调用，回调切回主线程；会话失效时自动回到登录页。 */
    fun <T> runRemote(action: () -> RemoteResult<T>, done: (RemoteResult<T>) -> Unit)

    fun notify(message: String)

    fun confirm(title: String, message: String, danger: Boolean = false, action: () -> Unit)

    /** 压入二级页（设备详情、各工具页等），返回键逐级回退。 */
    fun pushPage(page: Page)

    fun popPage()

    fun openDevice(device: RemoteDevice)

    /** 打开 AI 助手；deviceId 非空时绑定当前设备上下文。 */
    fun openAiChat(deviceId: String?)

    /** 选择本地图片（AI 附件）。 */
    fun pickImageFile(onPicked: (bytes: ByteArray) -> Unit)

    /** 选择本地文件（上传到设备）。 */
    fun pickUploadFile(onPicked: (name: String, bytes: ByteArray) -> Unit)

    /** 通用选择弹窗（应用操作菜单等）。 */
    fun showChoiceDialog(title: String, options: List<String>, onSelected: (String) -> Unit)

    /** 通用文本查看弹窗（终端结果、软件信息等）。 */
    fun showTextDialog(title: String, message: String)

    /** 文本输入弹窗（新建目录等）。 */
    fun showPromptDialog(title: String, hint: String, onConfirm: (String) -> Unit)

    /** 图片查看弹窗（文件预览）。 */
    fun showImageDialog(title: String, bitmap: android.graphics.Bitmap)

    /** 退出登录（回到登录页）。 */
    fun logout()

    /** 移除当前 Core 配置（回到初始化页）。 */
    fun resetCore()

    /** 主题变更后重建界面。 */
    fun recreateForTheme()
}
