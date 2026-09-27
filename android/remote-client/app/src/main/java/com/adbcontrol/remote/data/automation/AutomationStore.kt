package com.adbcontrol.remote.data.automation

import android.content.ContentValues
import android.content.Context
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteOpenHelper
import com.adbcontrol.remote.model.AutomationRunRecord
import com.adbcontrol.remote.model.AutomationRunStatus
import com.adbcontrol.remote.model.AutomationStepStatus
import com.adbcontrol.remote.model.AutomationTaskDefinition
import com.adbcontrol.remote.model.AutomationTriggerState
import org.json.JSONObject

/**
 * 自动化本地存储：SQLite（数据库位于应用私有目录）。
 * 表结构与桌面端 `AutomationTaskRepository.cs` 的运行记录语义一致：任务 JSON 整体存储，
 * 运行/步骤记录单列，便于列表页与运行详情页直接查询。
 */
class AutomationStore(context: Context) : SQLiteOpenHelper(context, "automation.db", null, 1) {

    override fun onCreate(db: SQLiteDatabase) {
        db.execSQL(
            "CREATE TABLE tasks (" +
                "id TEXT PRIMARY KEY, name TEXT NOT NULL, enabled INTEGER NOT NULL, " +
                "updated_at INTEGER NOT NULL, definition TEXT NOT NULL)",
        )
        db.execSQL(
            "CREATE TABLE runs (" +
                "id TEXT PRIMARY KEY, task_id TEXT NOT NULL, task_name TEXT NOT NULL, trigger TEXT NOT NULL, " +
                "status TEXT NOT NULL, created_at INTEGER NOT NULL, started_at INTEGER, completed_at INTEGER, " +
                "total_steps INTEGER NOT NULL DEFAULT 1, completed_steps INTEGER NOT NULL DEFAULT 0, " +
                "progress REAL NOT NULL DEFAULT 0, current_step TEXT NOT NULL DEFAULT '', " +
                "error_code TEXT NOT NULL DEFAULT '', error_message TEXT NOT NULL DEFAULT '', " +
                "error_module TEXT NOT NULL DEFAULT '', error_recoverable INTEGER NOT NULL DEFAULT 0, " +
                "error_suggestion TEXT NOT NULL DEFAULT '', trace_id TEXT NOT NULL DEFAULT '', payload TEXT NOT NULL DEFAULT '{}')",
        )
        db.execSQL("CREATE INDEX runs_task_created ON runs(task_id, created_at DESC)")
        db.execSQL(
            "CREATE TABLE trigger_state (" +
                "task_id TEXT NOT NULL, trigger_id TEXT NOT NULL, last_fired_at INTEGER, " +
                "last_evaluated_at INTEGER, last_condition_value INTEGER, " +
                "PRIMARY KEY (task_id, trigger_id))",
        )
    }

    override fun onUpgrade(db: SQLiteDatabase, oldVersion: Int, newVersion: Int) = Unit

    fun upsertTask(task: AutomationTaskDefinition) {
        val values = ContentValues().apply {
            put("id", task.id)
            put("name", task.name)
            put("enabled", if (task.enabled) 1 else 0)
            put("updated_at", task.updatedAtEpochMs)
            put("definition", AutomationSerializer.serialize(task))
        }
        writableDatabase.insertWithOnConflict("tasks", null, values, SQLiteDatabase.CONFLICT_REPLACE)
    }

    fun deleteTask(taskId: String) {
        val db = writableDatabase
        db.delete("runs", "task_id = ?", arrayOf(taskId))
        db.delete("trigger_state", "task_id = ?", arrayOf(taskId))
        db.delete("tasks", "id = ?", arrayOf(taskId))
    }

    fun listTasks(): List<AutomationTaskDefinition> = readableDatabase.rawQuery(
        "SELECT definition FROM tasks ORDER BY updated_at DESC", null,
    ).use { cursor ->
        buildList {
            while (cursor.moveToNext()) {
                runCatching { AutomationSerializer.deserialize(cursor.getString(0)) }
                    .onSuccess { add(it) }
                    .onFailure { /* 单条任务损坏不允许拖垮整个列表；记录后在读取方呈现 */ }
            }
        }
    }

    fun getTask(taskId: String): AutomationTaskDefinition? = readableDatabase.rawQuery(
        "SELECT definition FROM tasks WHERE id = ?", arrayOf(taskId),
    ).use { cursor ->
        if (cursor.moveToFirst()) {
            runCatching { AutomationSerializer.deserialize(cursor.getString(0)) }.getOrNull()
        } else null
    }

    fun setTaskEnabled(taskId: String, enabled: Boolean) {
        getTask(taskId)?.let { task ->
            upsertTask(task.copy(enabled = enabled, updatedAtEpochMs = System.currentTimeMillis()))
        }
    }

    fun getTriggerState(taskId: String, triggerId: String): AutomationTriggerState? = readableDatabase.rawQuery(
        "SELECT last_fired_at, last_evaluated_at, last_condition_value FROM trigger_state WHERE task_id = ? AND trigger_id = ?",
        arrayOf(taskId, triggerId),
    ).use { cursor ->
        if (cursor.moveToFirst()) {
            AutomationTriggerState(
                taskId = taskId,
                triggerId = triggerId,
                lastFiredAtEpochMs = cursor.longOrNull(0),
                lastEvaluatedAtEpochMs = cursor.longOrNull(1),
                lastConditionValue = cursor.intOrNull(2)?.let { it == 1 },
            )
        } else null
    }

    fun saveTriggerState(state: AutomationTriggerState) {
        val values = ContentValues().apply {
            put("task_id", state.taskId)
            put("trigger_id", state.triggerId)
            put("last_fired_at", state.lastFiredAtEpochMs)
            put("last_evaluated_at", state.lastEvaluatedAtEpochMs)
            put("last_condition_value", state.lastConditionValue?.let { if (it) 1 else 0 })
        }
        writableDatabase.insertWithOnConflict("trigger_state", null, values, SQLiteDatabase.CONFLICT_REPLACE)
    }

    fun insertRun(run: AutomationRunRecord, payload: JSONObject = JSONObject()) {
        val values = ContentValues().apply {
            put("id", run.id)
            put("task_id", run.taskId)
            put("task_name", run.taskName)
            put("trigger", run.trigger)
            put("status", run.status.name)
            put("created_at", run.createdAtEpochMs)
            put("started_at", run.startedAtEpochMs)
            put("completed_at", run.completedAtEpochMs)
            put("total_steps", run.totalSteps)
            put("completed_steps", run.completedSteps)
            put("progress", run.progress)
            put("current_step", run.currentStep)
            put("error_code", run.errorCode)
            put("error_message", run.errorMessage)
            put("error_module", run.errorModule)
            put("error_recoverable", if (run.errorRecoverable) 1 else 0)
            put("error_suggestion", run.errorSuggestion)
            put("trace_id", run.traceId)
            put("payload", payload.toString())
        }
        writableDatabase.insertWithOnConflict("runs", null, values, SQLiteDatabase.CONFLICT_REPLACE)
    }

    fun updateRunStatus(
        runId: String,
        status: AutomationRunStatus,
        startedAt: Long? = null,
        completedSteps: Int? = null,
        progress: Double? = null,
        currentStep: String? = null,
        completedAt: Long? = null,
        errorCode: String? = null,
        errorMessage: String? = null,
        errorModule: String? = null,
        errorRecoverable: Boolean? = null,
        errorSuggestion: String? = null,
    ) {
        val values = ContentValues().apply {
            put("status", status.name)
            startedAt?.let { put("started_at", it) }
            completedSteps?.let { put("completed_steps", it) }
            progress?.let { put("progress", it) }
            currentStep?.let { put("current_step", it) }
            completedAt?.let { put("completed_at", it) }
            errorCode?.let { put("error_code", it) }
            errorMessage?.let { put("error_message", it) }
            errorModule?.let { put("error_module", it) }
            errorRecoverable?.let { put("error_recoverable", if (it) 1 else 0) }
            errorSuggestion?.let { put("error_suggestion", it) }
        }
        writableDatabase.update("runs", values, "id = ?", arrayOf(runId))
    }

    fun listRuns(taskId: String? = null, limit: Int = 100): List<AutomationRunRecord> {
        val selection = if (taskId == null) "" else "WHERE task_id = ?"
        val args = if (taskId == null) emptyArray() else arrayOf(taskId)
        return readableDatabase.rawQuery(
            "SELECT id, task_id, task_name, trigger, status, created_at, started_at, completed_at, " +
                "total_steps, completed_steps, progress, current_step, error_code, error_message, " +
                "error_module, error_recoverable, error_suggestion, trace_id FROM runs $selection " +
                "ORDER BY created_at DESC LIMIT $limit",
            args,
        ).use { cursor ->
            buildList {
                while (cursor.moveToNext()) {
                    add(
                        AutomationRunRecord(
                            id = cursor.getString(0),
                            taskId = cursor.getString(1),
                            taskName = cursor.getString(2),
                            trigger = cursor.getString(3),
                            status = runCatching { AutomationRunStatus.valueOf(cursor.getString(4)) }
                                .getOrDefault(AutomationRunStatus.FAILED),
                            createdAtEpochMs = cursor.getLong(5),
                            startedAtEpochMs = cursor.longOrNull(6),
                            completedAtEpochMs = cursor.longOrNull(7),
                            totalSteps = cursor.getInt(8),
                            completedSteps = cursor.getInt(9),
                            progress = cursor.getDouble(10),
                            currentStep = cursor.getString(11),
                            errorCode = cursor.getString(12),
                            errorMessage = cursor.getString(13),
                            errorModule = cursor.getString(14),
                            errorRecoverable = cursor.getInt(15) == 1,
                            errorSuggestion = cursor.getString(16),
                            traceId = cursor.getString(17),
                        ),
                    )
                }
            }
        }
    }

    fun deleteRun(runId: String) {
        writableDatabase.delete("runs", "id = ?", arrayOf(runId))
    }

    /** 运行中/排队中的记录（用于并发策略判断与任务页“运行中”指标）。 */
    fun activeRun(taskId: String): AutomationRunRecord? = readableDatabase.rawQuery(
        "SELECT id FROM runs WHERE task_id = ? AND status IN ('QUEUED','RUNNING','PAUSED') ORDER BY created_at DESC LIMIT 1",
        arrayOf(taskId),
    ).use { cursor ->
        if (cursor.moveToFirst()) listRuns(taskId).firstOrNull { it.id == cursor.getString(0) } else null
    }

    private fun android.database.Cursor.longOrNull(index: Int): Long? =
        if (isNull(index)) null else getLong(index)

    private fun android.database.Cursor.intOrNull(index: Int): Int? =
        if (isNull(index)) null else getInt(index)
}
