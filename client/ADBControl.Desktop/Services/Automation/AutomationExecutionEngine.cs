using System.Collections.Concurrent;
using ADBControl.Desktop.Models;

namespace ADBControl.Desktop.Services.Automation;

public sealed class AutomationExecutionEngine : IAsyncDisposable
{
    private readonly AutomationTaskRepository _repository;
    private readonly AutomationActionExecutor _executor;
    private readonly AutomationConditionEvaluator _conditions;
    private readonly ConcurrentDictionary<string, ActiveExecution> _active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _taskSlots = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();

    public AutomationExecutionEngine(
        AutomationTaskRepository repository,
        AutomationActionExecutor executor,
        AutomationConditionEvaluator conditions)
    {
        _repository = repository;
        _executor = executor;
        _conditions = conditions;
    }

    public Func<AutomationAiRequest, CancellationToken, Task<string>>? AiExecutor { get; set; }
    public event EventHandler<AutomationRunRecord>? RunChanged;
    public event EventHandler<AutomationAiOutput>? AiOutputProduced;

    public IReadOnlyList<AutomationRunRecord> ActiveRuns => _active.Values
        .Select(execution => execution.Run)
        .OrderByDescending(run => run.CreatedAt)
        .ToList();

    public async Task<AutomationRunRecord> StartAsync(
        AutomationTaskDefinition task,
        string trigger,
        CancellationToken cancellationToken = default)
    {
        var existing = _active.Values.Where(item => item.Run.TaskId == task.Id).ToList();
        if (task.ConcurrencyPolicy == AutomationConcurrencyPolicy.Skip && existing.Count > 0)
            return await RecordSkippedAsync(task, trigger, "已有同一任务正在运行，并发策略为 skip。", cancellationToken);
        if (task.ConcurrencyPolicy == AutomationConcurrencyPolicy.Restart && existing.Count > 0)
        {
            foreach (var execution in existing)
                execution.Control.Stop();
            await Task.WhenAll(existing.Select(execution => execution.Completion.Task)).WaitAsync(cancellationToken);
        }

        var run = AutomationRunRecord.Create(task, trigger, EstimateSteps(task.Actions));
        await _repository.UpsertRunAsync(run, cancellationToken);
        var active = new ActiveExecution(run, new AutomationExecutionControl());
        if (!_active.TryAdd(run.Id, active))
            throw new AutomationExecutionException("RUN_ID_COLLISION", "运行 ID 冲突。", "automation.runtime");
        RaiseChanged(run);
        _ = ExecuteAsync(task, active);
        return run;
    }

    public async Task<AutomationRunRecord> RecordSkippedAsync(
        AutomationTaskDefinition task,
        string trigger,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var run = AutomationRunRecord.Create(task, trigger, EstimateSteps(task.Actions));
        run.Status = AutomationRunStatus.Skipped;
        run.CompletedAt = DateTimeOffset.UtcNow;
        run.ErrorCode = "RUN_SKIPPED";
        run.ErrorMessage = reason;
        run.ErrorModule = "automation.scheduler";
        await _repository.UpsertRunAsync(run, cancellationToken);
        RaiseChanged(run);
        return run;
    }

    public async Task<bool> PauseAsync(string runId, CancellationToken cancellationToken = default)
    {
        if (!_active.TryGetValue(runId, out var execution) || !execution.Control.Pause())
            return false;
        execution.Run.Status = AutomationRunStatus.Paused;
        await PersistRunAsync(execution.Run, cancellationToken);
        return true;
    }

    public async Task<bool> ResumeAsync(string runId, CancellationToken cancellationToken = default)
    {
        if (!_active.TryGetValue(runId, out var execution) || !execution.Control.Resume())
            return false;
        execution.Run.Status = AutomationRunStatus.Running;
        await PersistRunAsync(execution.Run, cancellationToken);
        return true;
    }

    public async Task<bool> StopAsync(string runId, CancellationToken cancellationToken = default)
    {
        if (!_active.TryGetValue(runId, out var execution))
            return false;
        execution.Control.Stop();
        await execution.Completion.Task.WaitAsync(cancellationToken);
        return true;
    }

    public async Task StopTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        var executions = _active.Values.Where(item => item.Run.TaskId == taskId).ToList();
        foreach (var execution in executions)
            execution.Control.Stop();
        if (executions.Count > 0)
            await Task.WhenAll(executions.Select(item => item.Completion.Task)).WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        var executions = _active.Values.ToList();
        foreach (var execution in executions)
            execution.Control.Stop();
        if (executions.Count > 0)
            await Task.WhenAll(executions.Select(item => item.Completion.Task));
        foreach (var slot in _taskSlots.Values)
            slot.Dispose();
        _shutdown.Dispose();
    }

    public static int EstimateSteps(IEnumerable<AutomationActionDefinition> actions)
    {
        long total = 0;
        foreach (var action in actions)
        {
            total += action.Type.ToLowerInvariant() switch
            {
                "flow.if" => Math.Max(EstimateSteps(action.Actions), EstimateSteps(action.ElseActions)),
                "flow.repeat" => (long)Math.Max(1, AutomationParameterReader.GetInt(action.Parameters, "count", 1)) * EstimateSteps(action.Actions),
                "flow.parallel" => EstimateSteps(action.Actions),
                _ => 1,
            };
        }
        return (int)Math.Clamp(total, 1, int.MaxValue);
    }

    private async Task ExecuteAsync(AutomationTaskDefinition task, ActiveExecution execution)
    {
        SemaphoreSlim? slot = null;
        try
        {
            if (task.ConcurrencyPolicy != AutomationConcurrencyPolicy.Parallel)
            {
                slot = _taskSlots.GetOrAdd(task.Id, _ => new SemaphoreSlim(1, 1));
                await slot.WaitAsync(execution.Control.CancellationToken);
            }

            await execution.Control.WaitWhilePausedAsync();
            execution.Run.Status = AutomationRunStatus.Running;
            execution.Run.StartedAt = DateTimeOffset.UtcNow;
            await PersistRunAsync(execution.Run, execution.Control.CancellationToken);
            await AddLogAsync(execution.Run, "info", "automation.runtime", "任务开始执行。", execution.Control.CancellationToken);
            await ExecuteSequenceAsync(task, execution, task.Actions);
            lock (execution.Sync)
            {
                execution.Run.Status = AutomationRunStatus.Succeeded;
                execution.Run.CompletedAt = DateTimeOffset.UtcNow;
                execution.Run.CompletedSteps = execution.Run.TotalSteps;
                execution.Run.Progress = 1;
                execution.Run.CurrentStep = "已完成";
            }
            await PersistRunAsync(execution.Run, CancellationToken.None);
            await AddLogAsync(execution.Run, "info", "automation.runtime", "任务执行完成。", CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            execution.Run.Status = AutomationRunStatus.Stopped;
            execution.Run.CompletedAt = DateTimeOffset.UtcNow;
            execution.Run.CurrentStep = "已停止";
            execution.Run.ErrorCode = "RUN_STOPPED";
            execution.Run.ErrorMessage = "任务已由用户或应用停止。";
            execution.Run.ErrorModule = "automation.runtime";
            await PersistRunAsync(execution.Run, CancellationToken.None);
            await AddLogAsync(execution.Run, "warning", "automation.runtime", execution.Run.ErrorMessage, CancellationToken.None);
        }
        catch (AutomationExecutionException ex)
        {
            ApplyFailure(execution.Run, ex.ErrorCode, ex.Message, ex.Module, ex.Recoverable, ex.Suggestion);
            await PersistRunAsync(execution.Run, CancellationToken.None);
            await AddLogAsync(execution.Run, "error", ex.Module, ex.Message, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ApplyFailure(execution.Run, "RUN_UNEXPECTED_FAILURE", ex.Message, "automation.runtime", false, "查看任务步骤和日志后重试。");
            await PersistRunAsync(execution.Run, CancellationToken.None);
            await AddLogAsync(execution.Run, "error", "automation.runtime", ex.ToString(), CancellationToken.None);
        }
        finally
        {
            slot?.Release();
            _active.TryRemove(execution.Run.Id, out _);
            execution.Control.Dispose();
            RaiseChanged(execution.Run);
            execution.Completion.TrySetResult(true);
        }
    }

    private async Task ExecuteSequenceAsync(
        AutomationTaskDefinition task,
        ActiveExecution execution,
        IReadOnlyList<AutomationActionDefinition> actions)
    {
        foreach (var action in actions)
        {
            await execution.Control.WaitWhilePausedAsync();
            switch (action.Type.ToLowerInvariant())
            {
                case "flow.if":
                    var matched = await _conditions.EvaluateGroupAsync(task.DeviceId, action.Conditions, action.ConditionMode, execution.Control.CancellationToken);
                    await ExecuteSequenceAsync(task, execution, matched ? action.Actions : action.ElseActions);
                    break;
                case "flow.repeat":
                    var count = Math.Clamp(AutomationParameterReader.GetInt(action.Parameters, "count", 1), 1, 1000);
                    for (var index = 0; index < count; index++)
                        await ExecuteSequenceAsync(task, execution, action.Actions);
                    break;
                case "flow.parallel":
                    await ExecuteParallelAsync(task, execution, action);
                    break;
                default:
                    await ExecuteStepAsync(task, execution, action);
                    break;
            }
        }
    }

    private async Task ExecuteParallelAsync(AutomationTaskDefinition task, ActiveExecution execution, AutomationActionDefinition action)
    {
        var maximum = Math.Clamp(AutomationParameterReader.GetInt(action.Parameters, "maxConcurrency", 4), 1, 32);
        using var gate = new SemaphoreSlim(maximum, maximum);
        var tasks = action.Actions.Select(async child =>
        {
            await gate.WaitAsync(execution.Control.CancellationToken);
            try
            {
                await ExecuteSequenceAsync(task, execution, new[] { child });
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
    }

    private async Task ExecuteStepAsync(
        AutomationTaskDefinition task,
        ActiveExecution execution,
        AutomationActionDefinition action)
    {
        var sequence = Interlocked.Increment(ref execution.StepSequence);
        var step = new AutomationRunStep
        {
            RunId = execution.Run.Id,
            ActionId = action.Id,
            ActionType = action.Type,
            Sequence = sequence,
        };
        lock (execution.Sync)
            execution.Run.CurrentStep = AutomationActionExecutor.Describe(action);
        await _repository.UpsertStepAsync(step, execution.Control.CancellationToken);
        await PersistRunAsync(execution.Run, execution.Control.CancellationToken);
        try
        {
            step.Output = await _executor.ExecuteLeafAsync(
                task,
                execution.Run,
                action,
                execution.Control,
                AiExecutor,
                output => AiOutputProduced?.Invoke(this, output));
            step.Status = AutomationStepStatus.Succeeded;
            step.CompletedAt = DateTimeOffset.UtcNow;
            await _repository.UpsertStepAsync(step, execution.Control.CancellationToken);
            lock (execution.Sync)
            {
                execution.Run.CompletedSteps = Math.Min(execution.Run.TotalSteps, execution.Run.CompletedSteps + 1);
                execution.Run.Progress = Math.Clamp((double)execution.Run.CompletedSteps / execution.Run.TotalSteps, 0, 1);
            }
            await PersistRunAsync(execution.Run, execution.Control.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            step.Status = AutomationStepStatus.Stopped;
            step.CompletedAt = DateTimeOffset.UtcNow;
            step.ErrorCode = "RUN_STOPPED";
            step.ErrorMessage = "步骤已停止。";
            await _repository.UpsertStepAsync(step, CancellationToken.None);
            throw;
        }
        catch (AutomationExecutionException ex)
        {
            step.Status = AutomationStepStatus.Failed;
            step.CompletedAt = DateTimeOffset.UtcNow;
            step.ErrorCode = ex.ErrorCode;
            step.ErrorMessage = ex.Message;
            await _repository.UpsertStepAsync(step, CancellationToken.None);
            throw;
        }
    }

    private async Task PersistRunAsync(AutomationRunRecord run, CancellationToken cancellationToken)
    {
        await _repository.UpsertRunAsync(run, cancellationToken);
        RaiseChanged(run);
    }

    private async Task AddLogAsync(AutomationRunRecord run, string level, string module, string message, CancellationToken cancellationToken)
    {
        await _repository.AddLogAsync(new AutomationLogEntry
        {
            RunId = run.Id,
            Level = level,
            Module = module,
            Message = message,
            DataJson = $"{{\"traceId\":\"{run.TraceId}\"}}",
        }, cancellationToken);
    }

    private static void ApplyFailure(
        AutomationRunRecord run,
        string code,
        string message,
        string module,
        bool recoverable,
        string suggestion)
    {
        run.Status = AutomationRunStatus.Failed;
        run.CompletedAt = DateTimeOffset.UtcNow;
        run.ErrorCode = code;
        run.ErrorMessage = message;
        run.ErrorModule = module;
        run.ErrorRecoverable = recoverable;
        run.ErrorSuggestion = suggestion;
    }

    private void RaiseChanged(AutomationRunRecord run) => RunChanged?.Invoke(this, run);

    private sealed class ActiveExecution
    {
        public ActiveExecution(AutomationRunRecord run, AutomationExecutionControl control)
        {
            Run = run;
            Control = control;
        }

        public AutomationRunRecord Run { get; }
        public AutomationExecutionControl Control { get; }
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public object Sync { get; } = new();
        public int StepSequence;
    }
}
