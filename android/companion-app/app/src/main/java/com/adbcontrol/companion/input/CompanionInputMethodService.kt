package com.adbcontrol.companion.input

import android.inputmethodservice.InputMethodService
import android.view.KeyEvent
import android.view.View
import android.widget.TextView
import java.lang.ref.WeakReference

class CompanionInputMethodService : InputMethodService() {
    override fun onCreate() {
        super.onCreate()
        activeService = WeakReference(this)
    }

    override fun onDestroy() {
        if (activeService?.get() === this) {
            activeService = null
        }
        super.onDestroy()
    }

    override fun onCreateInputView(): View {
        return TextView(this).apply {
            text = "ADBControl IME active"
            textSize = 16f
            setPadding(24, 16, 24, 16)
        }
    }

    private fun commitTextInternal(text: String): Boolean {
        return currentInputConnection?.commitText(text, 1) == true
    }

    private fun sendKeyInternal(keyCode: Int): Boolean {
        val connection = currentInputConnection ?: return false
        val down = KeyEvent(KeyEvent.ACTION_DOWN, keyCode)
        val up = KeyEvent(KeyEvent.ACTION_UP, keyCode)
        return connection.sendKeyEvent(down) && connection.sendKeyEvent(up)
    }

    companion object {
        private var activeService: WeakReference<CompanionInputMethodService>? = null

        fun commitText(text: String): Boolean {
            return activeService?.get()?.commitTextInternal(text) == true
        }

        fun sendKey(keyCode: Int): Boolean {
            return activeService?.get()?.sendKeyInternal(keyCode) == true
        }
    }
}
