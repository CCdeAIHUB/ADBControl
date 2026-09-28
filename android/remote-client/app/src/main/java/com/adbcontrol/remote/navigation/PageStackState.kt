package com.adbcontrol.remote.navigation

/** 二级页存在时主壳必须隐藏；返回只能弹出一层，不能穿透并退出 Activity。 */
class PageStackState {
    var depth: Int = 0
        private set
    val showsMainShell: Boolean get() = depth == 0
    fun push() { depth += 1 }
    fun pop(): Boolean {
        if (depth == 0) return false
        depth -= 1
        return true
    }
    fun clear() { depth = 0 }
}
