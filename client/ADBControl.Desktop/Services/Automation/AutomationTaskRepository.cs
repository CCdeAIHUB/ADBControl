using System.Globalization;
using ADBControl.Desktop.Models;
using Microsoft.Data.Sqlite;

namespace ADBControl.Desktop.Services.Automation;

public sealed class AutomationTaskRepository : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AutomationTaskRepository(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("自动化数据库路径不能为空。", nameof(databasePath));
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA foreign_keys=ON;
                CREATE TABLE IF NOT EXISTS automation_tasks (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    enabled INTEGER NOT NULL,
                    definition_json TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS automation_runs (
                    id TEXT PRIMARY KEY,
                    task_id TEXT NOT NULL,
                    task_name TEXT NOT NULL,
                    trigger_name TEXT NOT NULL,
                    status TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    started_at TEXT NULL,
                    completed_at TEXT NULL,
                    total_steps INTEGER NOT NULL,
                    completed_steps INTEGER NOT NULL,
                    progress REAL NOT NULL,
                    current_step TEXT NOT NULL,
                    error_code TEXT NOT NULL,
                    error_message TEXT NOT NULL,
                    error_module TEXT NOT NULL,
                    error_recoverable INTEGER NOT NULL,
                    error_suggestion TEXT NOT NULL,
                    trace_id TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_automation_runs_task_created ON automation_runs(task_id, created_at DESC);
                CREATE TABLE IF NOT EXISTS automation_run_steps (
                    id TEXT PRIMARY KEY,
                    run_id TEXT NOT NULL,
                    action_id TEXT NOT NULL,
                    action_type TEXT NOT NULL,
                    sequence INTEGER NOT NULL,
                    status TEXT NOT NULL,
                    started_at TEXT NOT NULL,
                    completed_at TEXT NULL,
                    output TEXT NOT NULL,
                    error_code TEXT NOT NULL,
                    error_message TEXT NOT NULL,
                    FOREIGN KEY(run_id) REFERENCES automation_runs(id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS ix_automation_steps_run_sequence ON automation_run_steps(run_id, sequence);
                CREATE TABLE IF NOT EXISTS automation_logs (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    run_id TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    level TEXT NOT NULL,
                    module TEXT NOT NULL,
                    message TEXT NOT NULL,
                    data_json TEXT NOT NULL,
                    FOREIGN KEY(run_id) REFERENCES automation_runs(id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS ix_automation_logs_run_created ON automation_logs(run_id, created_at);
                CREATE TABLE IF NOT EXISTS automation_trigger_state (
                    task_id TEXT NOT NULL,
                    trigger_id TEXT NOT NULL,
                    last_fired_at TEXT NULL,
                    last_evaluated_at TEXT NULL,
                    last_condition_value INTEGER NULL,
                    PRIMARY KEY(task_id, trigger_id),
                    FOREIGN KEY(task_id) REFERENCES automation_tasks(id) ON DELETE CASCADE
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<AutomationTaskDefinition>> LoadTasksAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT definition_json FROM automation_tasks ORDER BY created_at, id";
            var result = new List<AutomationTaskDefinition>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(AutomationTaskSerializer.Deserialize(reader.GetString(0)));
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertTaskAsync(AutomationTaskDefinition definition, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO automation_tasks(id, name, enabled, definition_json, created_at, updated_at)
                VALUES($id, $name, $enabled, $json, $created, $updated)
                ON CONFLICT(id) DO UPDATE SET
                    name=excluded.name,
                    enabled=excluded.enabled,
                    definition_json=excluded.definition_json,
                    updated_at=excluded.updated_at
                """;
            command.Parameters.AddWithValue("$id", definition.Id);
            command.Parameters.AddWithValue("$name", definition.Name);
            command.Parameters.AddWithValue("$enabled", definition.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$json", AutomationTaskSerializer.Serialize(definition));
            command.Parameters.AddWithValue("$created", Format(definition.CreatedAt));
            command.Parameters.AddWithValue("$updated", Format(definition.UpdatedAt));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            foreach (var statement in new[]
            {
                "DELETE FROM automation_logs WHERE run_id IN (SELECT id FROM automation_runs WHERE task_id=$taskId)",
                "DELETE FROM automation_run_steps WHERE run_id IN (SELECT id FROM automation_runs WHERE task_id=$taskId)",
                "DELETE FROM automation_runs WHERE task_id=$taskId",
                "DELETE FROM automation_trigger_state WHERE task_id=$taskId",
                "DELETE FROM automation_tasks WHERE id=$taskId",
            })
            {
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = statement;
                command.Parameters.AddWithValue("$taskId", taskId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertRunAsync(AutomationRunRecord run, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO automation_runs(
                    id, task_id, task_name, trigger_name, status, created_at, started_at, completed_at,
                    total_steps, completed_steps, progress, current_step, error_code, error_message,
                    error_module, error_recoverable, error_suggestion, trace_id)
                VALUES(
                    $id, $taskId, $taskName, $trigger, $status, $created, $started, $completed,
                    $total, $done, $progress, $current, $errorCode, $errorMessage,
                    $errorModule, $recoverable, $suggestion, $traceId)
                ON CONFLICT(id) DO UPDATE SET
                    status=excluded.status,
                    started_at=excluded.started_at,
                    completed_at=excluded.completed_at,
                    total_steps=excluded.total_steps,
                    completed_steps=excluded.completed_steps,
                    progress=excluded.progress,
                    current_step=excluded.current_step,
                    error_code=excluded.error_code,
                    error_message=excluded.error_message,
                    error_module=excluded.error_module,
                    error_recoverable=excluded.error_recoverable,
                    error_suggestion=excluded.error_suggestion
                """;
            AddRunParameters(command, run);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<AutomationRunRecord>> LoadRecentRunsAsync(int limit = 200, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, task_id, task_name, trigger_name, status, created_at, started_at, completed_at,
                       total_steps, completed_steps, progress, current_step, error_code, error_message,
                       error_module, error_recoverable, error_suggestion, trace_id
                FROM automation_runs ORDER BY created_at DESC LIMIT $limit
                """;
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
            var result = new List<AutomationRunRecord>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(ReadRun(reader));
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertStepAsync(AutomationRunStep step, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO automation_run_steps(
                    id, run_id, action_id, action_type, sequence, status, started_at, completed_at, output, error_code, error_message)
                VALUES($id, $runId, $actionId, $actionType, $sequence, $status, $started, $completed, $output, $errorCode, $errorMessage)
                ON CONFLICT(id) DO UPDATE SET
                    status=excluded.status,
                    completed_at=excluded.completed_at,
                    output=excluded.output,
                    error_code=excluded.error_code,
                    error_message=excluded.error_message
                """;
            command.Parameters.AddWithValue("$id", step.Id);
            command.Parameters.AddWithValue("$runId", step.RunId);
            command.Parameters.AddWithValue("$actionId", step.ActionId);
            command.Parameters.AddWithValue("$actionType", step.ActionType);
            command.Parameters.AddWithValue("$sequence", step.Sequence);
            command.Parameters.AddWithValue("$status", step.Status.ToString());
            command.Parameters.AddWithValue("$started", Format(step.StartedAt));
            command.Parameters.AddWithValue("$completed", DbValue(step.CompletedAt));
            command.Parameters.AddWithValue("$output", Limit(step.Output, 200_000));
            command.Parameters.AddWithValue("$errorCode", step.ErrorCode);
            command.Parameters.AddWithValue("$errorMessage", Limit(step.ErrorMessage, 20_000));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddLogAsync(AutomationLogEntry entry, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO automation_logs(run_id, created_at, level, module, message, data_json)
                VALUES($runId, $created, $level, $module, $message, $data)
                """;
            command.Parameters.AddWithValue("$runId", entry.RunId);
            command.Parameters.AddWithValue("$created", Format(entry.CreatedAt));
            command.Parameters.AddWithValue("$level", entry.Level);
            command.Parameters.AddWithValue("$module", entry.Module);
            command.Parameters.AddWithValue("$message", Limit(entry.Message, 20_000));
            command.Parameters.AddWithValue("$data", Limit(entry.DataJson, 200_000));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<AutomationTriggerState>> LoadTriggerStatesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT task_id, trigger_id, last_fired_at, last_evaluated_at, last_condition_value FROM automation_trigger_state";
            var result = new List<AutomationTriggerState>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(new AutomationTriggerState
                {
                    TaskId = reader.GetString(0),
                    TriggerId = reader.GetString(1),
                    LastFiredAt = ParseNullable(reader, 2),
                    LastEvaluatedAt = ParseNullable(reader, 3),
                    LastConditionValue = reader.IsDBNull(4) ? null : reader.GetInt32(4) != 0,
                });
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertTriggerStateAsync(AutomationTriggerState state, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO automation_trigger_state(task_id, trigger_id, last_fired_at, last_evaluated_at, last_condition_value)
                VALUES($taskId, $triggerId, $fired, $evaluated, $condition)
                ON CONFLICT(task_id, trigger_id) DO UPDATE SET
                    last_fired_at=excluded.last_fired_at,
                    last_evaluated_at=excluded.last_evaluated_at,
                    last_condition_value=excluded.last_condition_value
                """;
            command.Parameters.AddWithValue("$taskId", state.TaskId);
            command.Parameters.AddWithValue("$triggerId", state.TriggerId);
            command.Parameters.AddWithValue("$fired", DbValue(state.LastFiredAt));
            command.Parameters.AddWithValue("$evaluated", DbValue(state.LastEvaluatedAt));
            command.Parameters.AddWithValue("$condition", state.LastConditionValue is null ? DBNull.Value : state.LastConditionValue.Value ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecoverInterruptedRunsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE automation_runs
                SET status='Failed',
                    completed_at=$completed,
                    error_code='APP_RESTART_INTERRUPTED',
                    error_message='桌面应用退出时任务仍在运行，已在本次启动中终结。',
                    error_module='automation.runtime',
                    error_recoverable=1,
                    error_suggestion='检查上次退出原因后重新运行任务。'
                WHERE status IN ('Queued', 'Running', 'Paused')
                """;
            command.Parameters.AddWithValue("$completed", Format(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static void AddRunParameters(SqliteCommand command, AutomationRunRecord run)
    {
        command.Parameters.AddWithValue("$id", run.Id);
        command.Parameters.AddWithValue("$taskId", run.TaskId);
        command.Parameters.AddWithValue("$taskName", run.TaskName);
        command.Parameters.AddWithValue("$trigger", run.Trigger);
        command.Parameters.AddWithValue("$status", run.Status.ToString());
        command.Parameters.AddWithValue("$created", Format(run.CreatedAt));
        command.Parameters.AddWithValue("$started", DbValue(run.StartedAt));
        command.Parameters.AddWithValue("$completed", DbValue(run.CompletedAt));
        command.Parameters.AddWithValue("$total", run.TotalSteps);
        command.Parameters.AddWithValue("$done", run.CompletedSteps);
        command.Parameters.AddWithValue("$progress", run.Progress);
        command.Parameters.AddWithValue("$current", run.CurrentStep);
        command.Parameters.AddWithValue("$errorCode", run.ErrorCode);
        command.Parameters.AddWithValue("$errorMessage", Limit(run.ErrorMessage, 20_000));
        command.Parameters.AddWithValue("$errorModule", run.ErrorModule);
        command.Parameters.AddWithValue("$recoverable", run.ErrorRecoverable ? 1 : 0);
        command.Parameters.AddWithValue("$suggestion", run.ErrorSuggestion);
        command.Parameters.AddWithValue("$traceId", run.TraceId);
    }

    private static AutomationRunRecord ReadRun(SqliteDataReader reader)
    {
        return new AutomationRunRecord
        {
            Id = reader.GetString(0),
            TaskId = reader.GetString(1),
            TaskName = reader.GetString(2),
            Trigger = reader.GetString(3),
            Status = Enum.Parse<AutomationRunStatus>(reader.GetString(4), ignoreCase: true),
            CreatedAt = Parse(reader.GetString(5)),
            StartedAt = ParseNullable(reader, 6),
            CompletedAt = ParseNullable(reader, 7),
            TotalSteps = reader.GetInt32(8),
            CompletedSteps = reader.GetInt32(9),
            Progress = reader.GetDouble(10),
            CurrentStep = reader.GetString(11),
            ErrorCode = reader.GetString(12),
            ErrorMessage = reader.GetString(13),
            ErrorModule = reader.GetString(14),
            ErrorRecoverable = reader.GetInt32(15) != 0,
            ErrorSuggestion = reader.GetString(16),
            TraceId = reader.GetString(17),
        };
    }

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static object DbValue(DateTimeOffset? value) => value is null ? DBNull.Value : Format(value.Value);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static DateTimeOffset? ParseNullable(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Parse(reader.GetString(ordinal));
    private static string Limit(string? value, int maximum) => string.IsNullOrEmpty(value) || value.Length <= maximum ? value ?? string.Empty : value[..maximum];
}
