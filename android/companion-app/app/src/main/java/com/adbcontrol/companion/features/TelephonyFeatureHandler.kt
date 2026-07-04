package com.adbcontrol.companion.features

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.provider.Telephony
import android.telephony.SmsManager
import com.adbcontrol.companion.core.CompanionCommandContext
import com.adbcontrol.companion.core.CompanionCommandResult

class TelephonyFeatureHandler(private val context: Context) : FeatureCommandHandler {
    override val capabilityIds: Set<String> = setOf(
        "android.phone.call",
        "android.sms.read",
        "android.sms.send",
    )
    override val operations: Set<String> = setOf("phone.call", "sms.read", "sms.send")

    override fun handle(context: CompanionCommandContext): CompanionCommandResult {
        return when (context.operation) {
            "phone.call" -> startPhoneCall(context)
            "sms.read" -> readSms(context)
            "sms.send" -> sendSms(context)
            else -> CompanionCommandResult.failure(
                requestId = context.requestId,
                errorCode = "COMPANION_OPERATION_NOT_SUPPORTED",
                message = "不支持的电话/短信操作：${context.operation}",
                module = "companion.telephony",
                recoverable = false,
            )
        }
    }

    private fun startPhoneCall(command: CompanionCommandContext): CompanionCommandResult {
        val number = command.args.stringArg("number")
            ?: return CompanionCommandResult.failure(
                requestId = command.requestId,
                errorCode = "COMPANION_PARAMS_INVALID",
                message = "phone.call 需要 args.number 参数。",
                module = "companion.telephony",
                recoverable = false,
            )
        val sanitized = number.filter { it.isDigit() || it == '+' || it == '*' || it == '#' }
        if (sanitized.isBlank()) {
            return CompanionCommandResult.failure(
                requestId = command.requestId,
                errorCode = "COMPANION_PHONE_NUMBER_INVALID",
                message = "电话号码中没有可拨号字符。",
                module = "companion.telephony",
                recoverable = false,
            )
        }

        val intent = Intent(Intent.ACTION_CALL, Uri.parse("tel:$sanitized")).apply {
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        }
        context.startActivity(intent)

        return CompanionCommandResult.success(
            requestId = command.requestId,
            result = mapOf(
                "started" to true,
                "number" to sanitized,
            ),
        )
    }

    private fun readSms(command: CompanionCommandContext): CompanionCommandResult {
        val limit = (command.args.intArg("limit") ?: 20).coerceIn(1, 100)
        val messages = mutableListOf<Map<String, Any?>>()
        val projection = arrayOf(
            Telephony.Sms._ID,
            Telephony.Sms.ADDRESS,
            Telephony.Sms.BODY,
            Telephony.Sms.DATE,
            Telephony.Sms.TYPE,
        )

        context.contentResolver.query(
            Telephony.Sms.CONTENT_URI,
            projection,
            null,
            null,
            "${Telephony.Sms.DATE} DESC",
        )?.use { cursor ->
            val idIndex = cursor.getColumnIndexOrThrow(Telephony.Sms._ID)
            val addressIndex = cursor.getColumnIndexOrThrow(Telephony.Sms.ADDRESS)
            val bodyIndex = cursor.getColumnIndexOrThrow(Telephony.Sms.BODY)
            val dateIndex = cursor.getColumnIndexOrThrow(Telephony.Sms.DATE)
            val typeIndex = cursor.getColumnIndexOrThrow(Telephony.Sms.TYPE)

            while (cursor.moveToNext() && messages.size < limit) {
                messages += mapOf(
                    "id" to cursor.getLong(idIndex),
                    "address" to cursor.getString(addressIndex),
                    "body" to cursor.getString(bodyIndex),
                    "dateEpochMs" to cursor.getLong(dateIndex),
                    "type" to cursor.getInt(typeIndex),
                )
            }
        }

        return CompanionCommandResult.success(
            requestId = command.requestId,
            result = mapOf(
                "messages" to messages,
                "count" to messages.size,
                "limit" to limit,
            ),
        )
    }

    private fun sendSms(command: CompanionCommandContext): CompanionCommandResult {
        val number = command.args.stringArg("number")
        val text = command.args.stringArg("text")
        val direct = command.args.booleanArg("direct")

        if (number == null || text == null) {
            return CompanionCommandResult.failure(
                requestId = command.requestId,
                errorCode = "COMPANION_PARAMS_INVALID",
                message = "sms.send 需要 args.number 和 args.text 参数。",
                module = "companion.telephony",
                recoverable = false,
            )
        }

        return if (direct) {
            val smsManager = context.getSystemService(SmsManager::class.java)
            val parts = smsManager.divideMessage(text)
            smsManager.sendMultipartTextMessage(number, null, parts, null, null)
            CompanionCommandResult.success(
                requestId = command.requestId,
                result = mapOf(
                    "mode" to "direct",
                    "sent" to true,
                    "parts" to parts.size,
                ),
            )
        } else {
            val intent = Intent(Intent.ACTION_SENDTO, Uri.parse("smsto:$number")).apply {
                putExtra("sms_body", text)
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            }
            context.startActivity(intent)
            CompanionCommandResult.success(
                requestId = command.requestId,
                result = mapOf(
                    "mode" to "compose",
                    "started" to true,
                ),
            )
        }
    }
}
