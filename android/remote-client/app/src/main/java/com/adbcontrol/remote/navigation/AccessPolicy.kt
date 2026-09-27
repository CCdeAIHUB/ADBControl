package com.adbcontrol.remote.navigation

import com.adbcontrol.remote.model.UserRole

/**
 * 导航与功能访问门控（纯 Kotlin，可被契约测试锁定）：
 * - 任务模块仅管理员角色可见（与桌面端任务页一致）；
 * - 账号管理仅管理员角色可见；
 * - 设备与个人页对所有已登录角色开放。
 */
object AccessPolicy {
    fun canSeeTasks(role: UserRole): Boolean = role == UserRole.ADMIN
    fun canSeeAccounts(role: UserRole): Boolean = role == UserRole.ADMIN
    fun canSeeDevices(role: UserRole): Boolean = true
    fun canSeeProfile(role: UserRole): Boolean = true
}
