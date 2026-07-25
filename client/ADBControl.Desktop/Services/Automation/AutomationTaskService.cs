using System.Collections.Concurrent;
using System.Text.Json;
using ADBControl.Desktop.Models;

namespace ADBControl.Desktop.Services.Automation;

public interface IAutomationTaskManager
{
    Task<string> CreateFromJsonAsync(string json, CancellationToken cancellationToken = default);
    Task<string> UpdateFromJsonAsync(string taskId, string json, CancellationToken cancellationToken = default);
    Task<string> ListAsJsonAsync(CancellationToken cancellationToken = default);
    Task<string> GetAsJsonAsync(string taskId, CancellationToken cancellationToken = default);
    Task<string> RunFromAiAsync(string taskId, CancellationToken cancellationToken = default);
    Task<string> SetEnabledFromAiAsync(string taskId, bool enabled, CancellationToken cancellationToken = default);
    Task<string> DeleteFromAiAsync(string taskId, CancellationToken cancellationToken = default);
}

public sealed class AutomationTaskService : IAutomationTaskManager, IAsyncDisposable
{
    private readonly AutomationTaskRepository _repository;
    private readonly AutomationExecutionEngine _engine;
    private readonly AutomationSchedulerService _scheduler;
    private readonly ConcurrentDictionary<string, AutomationTaskDefinition> _definitions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AutomationRunRecord> _lastRuns = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    public AutomationTaskService(string databasePath, IAutomationDeviceGateway gateway)
    {
        _repository = new AutomationTaskRepository(databasePath);
        var conditions = new AutomationConditionEvaluator(gateway);
        var executor = new AutomationActionExecutor(gateway, conditions);
        _engine = new AutomationExecutionEngine(_repository, executor, conditions);
        _scheduler = new AutomationSchedulerService(_repository, _engine, conditions, GetDefinitions);
        _engine.RunChanged += OnRunChanged;
        _engine.AiOutputProduced += (_, output) => AiOutputProduced?.Invoke(this, output);
    }

    public Func<AutomationAiRequest, CancellationToken, Task<string>>? AiExecutor
    {
        get => _engine.AiExecutor;
        set => _engine.AiExecutor = value;
    }

    public bool IsInitialized => _initialized;
    public event EventHandler? Changed;
    public event EventHandler<AutomationAiOutput>? AiOutputProduced;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _initializeGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
                return;
            await _repository.InitializeAsync(cancellationToken);
            await _repository.RecoverInterruptedRunsAsync(cancellationToken);
            foreach (var definition in await _repository.LoadTasksAsync(cancellationToken))
                _definitions[definition.Id] = definition;
            foreach (var run in await _repository.LoadRecentRunsAsync(cancellationToken: cancellationToken))
                _lastRuns.TryAdd(run.TaskId, run);
            await _scheduler.InitializeAsync(cancellationToken);
            _initialized = true;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        _scheduler.Start();
    }

    public IReadOnlyList<AutomationTaskSnapshot> GetSnapshots()
    {
        var activeRuns = _engine.ActiveRuns;
        return _definitions.Values
            .OrderByDescending(definition => activeRuns.Any(run => run.TaskId == definition.Id))
            .ThenBy(definition => definition.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(definition => new AutomationTaskSnapshot(
                AutomationTaskSerializer.Clone(definition),
                activeRuns.FirstOrDefault(run => run.TaskId == definition.Id),
                _lastRuns.GetValueOrDefault(definition.Id),
                _scheduler.GetNextRun(definition)))
            .ToList();
    }

    public async Task<AutomationTaskDefinition> CreateOrUpdateAsync(
        AutomationTaskDefinition definition,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var copy = AutomationTaskSerializer.Clone(definition);
        var validation = AutomationTaskValidator.Validate(copy);
        if (!validation.IsValid)
        {
            throw new AutomationExecutionException(
                "TASK_VALIDATION_FAILED",
                string.Join(Environment.NewLine, validation.Errors),
                "automation.schema",
                suggestion: "根据错误列表修正任务 JSON 后重试。");
        }

        if (_definitions.TryGetValue(copy.Id, out var existing))
            copy.CreatedAt = existing.CreatedAt;
        else if (copy.CreatedAt == default)
            copy.CreatedAt = DateTimeOffset.UtcNow;
        copy.UpdatedAt = DateTimeOffset.UtcNow;
        await _repository.UpsertTaskAsync(copy, cancellationToken);
        _definitions[copy.Id] = copy;
        Changed?.Invoke(this, EventArgs.Empty);
        return AutomationTaskSerializer.Clone(copy);
    }

    public async Task<AutomationRunRecord?> RunNowAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (!_definitions.TryGetValue(taskId, out var definition))
            throw new AutomationExecutionException("TASK_NOT_FOUND", $"找不到任务：{taskId}。", "automation.service");
        return await _engine.StartAsync(definition, "manual", cancellationToken);
    }

    public Task<bool> PauseAsync(string runId, CancellationToken cancellationToken = default) => _engine.PauseAsync(runId, cancellationToken);
    public Task<bool> ResumeAsync(string runId, CancellationToken cancellationToken = default) => _engine.ResumeAsync(runId, cancellationToken);
    public Task<bool> StopAsync(string runId, CancellationToken cancellationToken = default) => _engine.StopAsync(runId, cancellationToken);

    public async Task SetEnabledAsync(string taskId, bool enabled, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (!_definitions.TryGetValue(taskId, out var definition))
            throw new AutomationExecutionException("TASK_NOT_FOUND", $"找不到任务：{taskId}。", "automation.service");
        var copy = AutomationTaskSerializer.Clone(definition);
        copy.Enabled = enabled;
        await CreateOrUpdateAsync(copy, cancellationToken);
    }

    public async Task DeleteAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _engine.StopTaskAsync(taskId, cancellationToken);
        if (!_definitions.TryRemove(taskId, out _))
            throw new AutomationExecutionException("TASK_NOT_FOUND", $"找不到任务：{taskId}。", "automation.service");
        _lastRuns.TryRemove(taskId, out _);
        await _repository.DeleteTaskAsync(taskId, cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<string> CreateFromJsonAsync(string json, CancellationToken cancellationToken = default)
    {
        var definition = AutomationTaskSerializer.Deserialize(json);
        definition.Id = Guid.NewGuid().ToString("N");
        definition.CreatedAt = DateTimeOffset.UtcNow;
        definition.UpdatedAt = definition.CreatedAt;
        var saved = await CreateOrUpdateAsync(definition, cancellationToken);
        return AutomationTaskSerializer.Serialize(saved);
    }

    public async Task<string> UpdateFromJsonAsync(string taskId, string json, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (!_definitions.TryGetValue(taskId, out var existing))
            throw new AutomationExecutionException("TASK_NOT_FOUND", $"找不到任务：{taskId}。", "automation.service");
        var definition = AutomationTaskSerializer.Deserialize(json);
        definition.Id = taskId;
        definition.CreatedAt = existing.CreatedAt;
        var saved = await CreateOrUpdateAsync(definition, cancellationToken);
        return AutomationTaskSerializer.Serialize(saved);
    }

    public async Task<string> ListAsJsonAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var summaries = GetSnapshots().Select(snapshot => new
        {
            id = snapshot.Definition.Id,
            name = snapshot.Definition.Name,
            enabled = snapshot.Definition.Enabled,
            deviceId = snapshot.Definition.DeviceId,
            triggers = snapshot.Definition.Triggers.Select(trigger => trigger.Type),
            activeRun = snapshot.ActiveRun is null ? null : new { id = snapshot.ActiveRun.Id, status = snapshot.ActiveRun.Status.ToString(), snapshot.ActiveRun.Progress },
            lastRun = snapshot.LastRun is null ? null : new { id = snapshot.LastRun.Id, status = snapshot.LastRun.Status.ToString(), snapshot.LastRun.ErrorCode, snapshot.LastRun.ErrorMessage },
            nextRunAt = snapshot.NextRunAt,
        });
        return JsonSerializer.Serialize(summaries, AutomationTaskSerializer.JsonOptions);
    }

    public async Task<string> GetAsJsonAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (!_definitions.TryGetValue(taskId, out var definition))
            throw new AutomationExecutionException("TASK_NOT_FOUND", $"找不到任务：{taskId}。", "automation.service");
        return AutomationTaskSerializer.Serialize(definition);
    }

    public async Task<string> RunFromAiAsync(string taskId, CancellationToken cancellationToken = default)
    {
        var run = await RunNowAsync(taskId, cancellationToken)
            ?? throw new AutomationExecutionException("RUN_NOT_CREATED", "没有创建运行记录。", "automation.service");
        return JsonSerializer.Serialize(run, AutomationTaskSerializer.JsonOptions);
    }

    public async Task<string> SetEnabledFromAiAsync(string taskId, bool enabled, CancellationToken cancellationToken = default)
    {
        await SetEnabledAsync(taskId, enabled, cancellationToken);
        return JsonSerializer.Serialize(new { taskId, enabled }, AutomationTaskSerializer.JsonOptions);
    }

    public async Task<string> DeleteFromAiAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await DeleteAsync(taskId, cancellationToken);
        return JsonSerializer.Serialize(new { taskId, deleted = true }, AutomationTaskSerializer.JsonOptions);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await _scheduler.DisposeAsync();
        await _engine.DisposeAsync();
        await _repository.DisposeAsync();
        _initializeGate.Dispose();
    }

    private IReadOnlyList<AutomationTaskDefinition> GetDefinitions() => _definitions.Values.ToList();

    private void OnRunChanged(object? sender, AutomationRunRecord run)
    {
        if (run.Status is AutomationRunStatus.Succeeded or AutomationRunStatus.Failed or AutomationRunStatus.Stopped or AutomationRunStatus.Skipped)
            _lastRuns[run.TaskId] = run;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
