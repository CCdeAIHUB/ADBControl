package com.adbcontrol.remote.data.automation

import com.adbcontrol.remote.model.*
import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID

/**
 * 任务 JSON DSL 序列化：字段命名与桌面端 `AutomationTaskSerializer.cs` 一致（camelCase），
 * 保证同一份任务 JSON 在两端可读。序列化层薄封装，核心语义由 Validator/Engine 锁定。
 */
object AutomationSerializer {

    fun serialize(task: AutomationTaskDefinition): String = JSONObject().apply {
        put("schemaVersion", task.schemaVersion)
        put("id", task.id)
        put("name", task.name)
        put("description", task.description)
        put("deviceId", task.deviceId)
        put("enabled", task.enabled)
        put("concurrencyPolicy", task.concurrencyPolicy.name.lowercase())
        put("permissions", serializePermissions(task.permissions))
        put("triggers", JSONArray().apply { task.triggers.forEach { put(serializeTrigger(it)) } })
        put("actions", JSONArray().apply { task.actions.forEach { put(serializeAction(it)) } })
        put("createdAt", task.createdAtEpochMs)
        put("updatedAt", task.updatedAtEpochMs)
    }.toString()

    fun deserialize(text: String): AutomationTaskDefinition {
        val json = JSONObject(text)
        return AutomationTaskDefinition(
            schemaVersion = json.optInt("schemaVersion", 1),
            id = json.optString("id", UUID.randomUUID().toString().replace("-", "")),
            name = json.optString("name"),
            description = json.optString("description"),
            deviceId = json.optString("deviceId"),
            enabled = json.optBoolean("enabled", true),
            concurrencyPolicy = parseConcurrency(json.optString("concurrencyPolicy", "skip")),
            permissions = parsePermissions(json.optJSONObject("permissions") ?: JSONObject()),
            triggers = json.optJSONArray("triggers").mapArray(::parseTrigger),
            actions = json.optJSONArray("actions").mapArray(::parseAction),
            createdAtEpochMs = json.optLong("createdAt", System.currentTimeMillis()),
            updatedAtEpochMs = json.optLong("updatedAt", System.currentTimeMillis()),
        )
    }

    private fun serializePermissions(permissions: AutomationPermissionSet) = JSONObject().apply {
        put("allowAdb", permissions.allowAdb)
        put("allowShell", permissions.allowShell)
        put("allowCompanion", permissions.allowCompanion)
        put("allowAi", permissions.allowAi)
        put("allowAiDeviceTools", permissions.allowAiDeviceTools)
        put("allowTaskMutation", permissions.allowTaskMutation)
        put("allowUnlock", permissions.allowUnlock)
    }

    private fun parsePermissions(json: JSONObject) = AutomationPermissionSet(
        allowAdb = json.optBoolean("allowAdb"),
        allowShell = json.optBoolean("allowShell"),
        allowCompanion = json.optBoolean("allowCompanion"),
        allowAi = json.optBoolean("allowAi"),
        allowAiDeviceTools = json.optBoolean("allowAiDeviceTools"),
        allowTaskMutation = json.optBoolean("allowTaskMutation"),
        allowUnlock = json.optBoolean("allowUnlock"),
    )

    private fun serializeTrigger(trigger: AutomationTriggerDefinition) = JSONObject().apply {
        put("id", trigger.id)
        put("type", trigger.type)
        if (trigger.timeZoneId.isNotBlank()) put("timeZoneId", trigger.timeZoneId)
        trigger.at?.let { put("at", it) }
        if (trigger.days.isNotEmpty()) put("days", JSONArray(trigger.days.map { it - 1 })) // 桌面 DayOfWeek: 0=周日
        put("intervalSeconds", trigger.intervalSeconds)
        trigger.runAtEpochMs?.let { put("runAt", it) }
        trigger.cron?.let { put("cron", it) }
        put("catchUp", trigger.catchUp)
        put("pollIntervalSeconds", trigger.pollIntervalSeconds)
        put("edgeOnly", trigger.edgeOnly)
        put("cooldownSeconds", trigger.cooldownSeconds)
        put("conditionMode", trigger.conditionMode.name.lowercase())
        put("conditions", serializeConditions(trigger.conditions))
    }

    private fun parseTrigger(json: JSONObject): AutomationTriggerDefinition = AutomationTriggerDefinition(
        id = json.optString("id", UUID.randomUUID().toString().replace("-", "")),
        type = json.optString("type", "manual"),
        timeZoneId = json.optString("timeZoneId"),
        at = json.optStringOrNull("at"),
        days = json.optJSONArray("days").let { array ->
            if (array == null) emptyList()
            else (0 until array.length()).mapNotNull { index -> (array.opt(index) as? Number)?.toInt()?.plus(1) }
        },
        intervalSeconds = json.optInt("intervalSeconds", 3600),
        runAtEpochMs = if (json.has("runAt")) json.optLong("runAt") else null,
        cron = json.optStringOrNull("cron"),
        catchUp = json.optBoolean("catchUp"),
        pollIntervalSeconds = json.optInt("pollIntervalSeconds", 2),
        edgeOnly = json.optBoolean("edgeOnly", true),
        cooldownSeconds = json.optInt("cooldownSeconds", 60),
        conditionMode = parseConditionMode(json.optString("conditionMode", "all")),
        conditions = parseConditions(json.optJSONArray("conditions")),
    )

    private fun serializeAction(action: AutomationActionDefinition): JSONObject = JSONObject().apply {
        put("id", action.id)
        put("type", action.type)
        put("parameters", JSONObject(action.parameters))
        put("conditionMode", action.conditionMode.name.lowercase())
        put("conditions", serializeConditions(action.conditions))
        put("actions", JSONArray().apply { action.actions.forEach { put(serializeAction(it)) } })
        put("elseActions", JSONArray().apply { action.elseActions.forEach { put(serializeAction(it)) } })
    }

    private fun parseAction(json: JSONObject): AutomationActionDefinition = AutomationActionDefinition(
        id = json.optString("id", UUID.randomUUID().toString().replace("-", "")),
        type = json.optString("type"),
        parameters = json.optJSONObject("parameters").toStringMap(),
        conditionMode = parseConditionMode(json.optString("conditionMode", "all")),
        conditions = parseConditions(json.optJSONArray("conditions")),
        actions = json.optJSONArray("actions").mapArray { parseAction(it) },
        elseActions = json.optJSONArray("elseActions").mapArray { parseAction(it) },
    )

    private fun serializeConditions(conditions: List<AutomationConditionDefinition>): JSONArray = JSONArray().apply {
        conditions.forEach { condition ->
            put(JSONObject().apply {
                put("type", condition.type)
                put("operator", condition.operator)
                put("caseSensitive", condition.caseSensitive)
                put("negate", condition.negate)
                put("parameters", JSONObject(condition.parameters))
            })
        }
    }

    private fun parseConditions(array: JSONArray?): List<AutomationConditionDefinition> = array.mapArray { json ->
        AutomationConditionDefinition(
            type = json.optString("type"),
            operator = json.optString("operator", "equals"),
            caseSensitive = json.optBoolean("caseSensitive"),
            negate = json.optBoolean("negate"),
            parameters = json.optJSONObject("parameters").toStringMap(),
        )
    }

    private fun parseConcurrency(value: String) = AutomationConcurrencyPolicy.entries
        .firstOrNull { it.name.lowercase() == value.lowercase() } ?: AutomationConcurrencyPolicy.SKIP

    private fun parseConditionMode(value: String) = if (value.lowercase() == "any") {
        AutomationConditionMode.ANY
    } else {
        AutomationConditionMode.ALL
    }

    private fun JSONObject.optStringOrNull(key: String): String? =
        if (has(key) && !isNull(key)) optString(key) else null

    private fun JSONObject?.toStringMap(): Map<String, String> {
        val json = this ?: return emptyMap()
        val result = mutableMapOf<String, String>()
        json.keys().forEach { key ->
            val value = json.opt(key) ?: return@forEach
            // extras/args 需要保留原始类型语义，统一存原文（布尔/数字/字符串），执行层再解析。
            result[key] = when (value) {
                is JSONObject, is JSONArray -> value.toString()
                else -> value.toString()
            }
        }
        return result
    }

    private inline fun <T> JSONArray?.mapArray(transform: (JSONObject) -> T): List<T> {
        val array = this ?: return emptyList()
        return (0 until array.length()).mapNotNull { index -> array.optJSONObject(index)?.let(transform) }
    }
}
