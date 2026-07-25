package com.adbcontrol.companion.commands

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.util.Log
import com.adbcontrol.companion.core.CompanionCommandContext
import com.adbcontrol.companion.core.PermissionGuard
import com.adbcontrol.companion.features.AndroidFeatureDispatcher
import org.json.JSONObject
import java.io.File
import java.util.UUID

class CompanionCommandReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != ACTION_EXECUTE_COMMAND) {
            return
        }

        val pendingResult = goAsync()
        val applicationContext = context.applicationContext
        Thread(
            {
                try {
                    executeCommand(applicationContext, intent, pendingResult)
                } finally {
                    pendingResult.finish()
                }
            },
            "adbcontrol-command",
        ).apply {
            isDaemon = true
            start()
        }
    }

    private fun executeCommand(context: Context, intent: Intent, pendingResult: PendingResult) {
        val requestId = intent.getStringExtra(EXTRA_REQUEST_ID) ?: UUID.randomUUID().toString()
        val capabilityId = intent.getStringExtra(EXTRA_CAPABILITY_ID).orEmpty()
        val operation = intent.getStringExtra(EXTRA_OPERATION).orEmpty()
        val args = parseArgs(intent.getStringExtra(EXTRA_ARGS_JSON))
        val dispatcher = AndroidFeatureDispatcher(context, PermissionGuard(context))
        val result = dispatcher.dispatch(
            CompanionCommandContext(
                requestId = requestId,
                capabilityId = capabilityId,
                operation = operation,
                args = args,
            ),
        )
        val payload = JSONObject(result.toPayload()).toString()
        persistCommandResult(context, requestId, payload)
        pendingResult.resultCode = 0
        pendingResult.resultData = payload
    }

    private fun persistCommandResult(context: Context, requestId: String, payload: String) {
        // `am broadcast` does not reliably print resultData on OEM Android builds. Persisting
        // this request-scoped snapshot gives the desktop's ADB adapter a deterministic result
        // channel without weakening the QUIC/IPC command contract.
        try {
            val directory = context.getExternalFilesDir(RESULT_DIRECTORY) ?: return
            directory.mkdirs()
            val target = File(directory, "$requestId.json")
            val temporary = File(directory, "$requestId.tmp")
            temporary.writeText(payload, Charsets.UTF_8)
            if (!temporary.renameTo(target)) {
                temporary.copyTo(target, overwrite = true)
                temporary.delete()
            }
        } catch (error: Exception) {
            Log.e(TAG, "Unable to persist companion command result for $requestId", error)
        }
    }

    private fun parseArgs(json: String?): Map<String, Any?> {
        if (json.isNullOrBlank()) {
            return emptyMap()
        }

        val source = JSONObject(json)
        return source.keys().asSequence().associateWith { key ->
            val value = source.get(key)
            if (value == JSONObject.NULL) null else value
        }
    }

    companion object {
        const val ACTION_EXECUTE_COMMAND = "com.adbcontrol.companion.EXECUTE_COMMAND"
        const val EXTRA_REQUEST_ID = "requestId"
        const val EXTRA_CAPABILITY_ID = "capabilityId"
        const val EXTRA_OPERATION = "operation"
        const val EXTRA_ARGS_JSON = "argsJson"
        private const val RESULT_DIRECTORY = "command-results"
        private const val TAG = "ADBControlCommand"
    }
}
