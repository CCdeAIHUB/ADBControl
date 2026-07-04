package com.adbcontrol.companion.commands

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import com.adbcontrol.companion.core.CompanionCommandContext
import com.adbcontrol.companion.core.PermissionGuard
import com.adbcontrol.companion.features.AndroidFeatureDispatcher
import org.json.JSONObject
import java.util.UUID

class CompanionCommandReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != ACTION_EXECUTE_COMMAND) {
            return
        }

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
        resultCode = 0
        setResultData(JSONObject(result.toPayload()).toString())
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
    }
}
