using System.Collections.Concurrent;
using System.Diagnostics;
using ADBControl.Desktop.Models;

namespace ADBControl.Desktop.Services.Automation;

public sealed class AutomationSchedulerService : IAsyncDisposable
{
    private static readonly TimeSpan MissedTolerance = TimeSpan.FromMinutes(2);
    private readonly AutomationTaskRepository _repository;
    private readonly AutomationExecutionEngine _engine;
    private readonly AutomationConditionEvaluator _conditions;
    private readonly Func<IReadOnlyList<AutomationTaskDefinition>> _tasksProvider;
    private readonly ConcurrentDictionary<string, AutomationTriggerState> _states = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _loop;

    public AutomationSchedulerService(
        AutomationTaskRepository repository,
        AutomationExecutionEngine engine,
        AutomationConditionEvaluator conditions,
        Func<IReadOnlyList<AutomationTaskDefinition>> tasksProvider)
    {
        _repository = repository;
        _engine = engine;
        _conditions = conditions;
        _tasksProvider = tasksProvider;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        foreach (var state in await _repository.LoadTriggerStatesAsync(cancellationToken))
            _states[Key(state.TaskId, state.TriggerId)] = state;
    }

    public void Start()
    {
        if (_loop is not null)
            return;
        _loop = Task.Run(RunLoopAsync);
    }

    public DateTimeOffset? GetNextRun(AutomationTaskDefinition task, DateTimeOffset? now = null)
    {
        if (!task.Enabled)
            return null;
        DateTimeOffset? next = null;
        foreach (var trigger in task.Triggers)
        {
            if (trigger.Type.Equals("condition", StringComparison.OrdinalIgnoreCase) ||
                trigger.Type.Equals("manual", StringComparison.OrdinalIgnoreCase))
                continue;
            var state = GetState(task.Id, trigger.Id);
            var after = state.LastFiredAt ?? task.CreatedAt.AddTicks(-1);
            var candidate = AutomationScheduleCalculator.GetNextOccurrence(task, trigger, after);
            if (candidate is not null && (next is null || candidate < next))
                next = candidate;
        }
        return next;
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }
        }
        _shutdown.Dispose();
    }

    internal async Task TickAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var task in _tasksProvider().Where(task => task.Enabled))
        {
            foreach (var trigger in task.Triggers)
            {
                if (trigger.Type.Equals("manual", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    if (trigger.Type.Equals("condition", StringComparison.OrdinalIgnoreCase))
                        await EvaluateConditionTriggerAsync(task, trigger, now, cancellationToken);
                    else
                        await EvaluateTimeTriggerAsync(task, trigger, now, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    await RecordTriggerFailureAsync(task, trigger, now, ex, cancellationToken);
                }
            }
        }
    }

    private async Task RunLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(_shutdown.Token))
        {
            try
            {
                await TickAsync(DateTimeOffset.UtcNow, _shutdown.Token);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The scheduler must survive one malformed device response; the task run records the actionable failure when triggered.
                Debug.WriteLine($"Automation scheduler tick failed: {ex}");
            }
        }
    }

    private async Task EvaluateTimeTriggerAsync(
        AutomationTaskDefinition task,
        AutomationTriggerDefinition trigger,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var state = GetState(task.Id, trigger.Id);
        var after = state.LastFiredAt ?? task.CreatedAt.AddTicks(-1);
        var due = AutomationScheduleCalculator.GetNextOccurrence(task, trigger, after);
        if (due is null || due > now)
            return;

        if (!trigger.CatchUp && now - due > MissedTolerance)
        {
            state.LastFiredAt = now;
            await _repository.UpsertTriggerStateAsync(state, cancellationToken);
            await _engine.RecordSkippedAsync(task, trigger.Type, $"错过计划时间 {due.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}，catchUp=false。", cancellationToken);
            return;
        }

        var conditionsMatched = await _conditions.EvaluateGroupAsync(task.DeviceId, trigger.Conditions, trigger.ConditionMode, cancellationToken);
        if (conditionsMatched)
            await _engine.StartAsync(task, trigger.Type, cancellationToken);
        else
            await _engine.RecordSkippedAsync(task, trigger.Type, "计划时间到达，但附加设备条件不满足。", cancellationToken);
        state.LastFiredAt = now;
        await _repository.UpsertTriggerStateAsync(state, cancellationToken);
    }

    private async Task EvaluateConditionTriggerAsync(
        AutomationTaskDefinition task,
        AutomationTriggerDefinition trigger,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var state = GetState(task.Id, trigger.Id);
        if (state.LastEvaluatedAt is { } lastEvaluation &&
            now - lastEvaluation < TimeSpan.FromSeconds(Math.Max(1, trigger.PollIntervalSeconds)))
            return;

        var matched = await _conditions.EvaluateGroupAsync(task.DeviceId, trigger.Conditions, trigger.ConditionMode, cancellationToken);
        var edgeMatches = !trigger.EdgeOnly || state.LastConditionValue != true;
        var cooldownComplete = state.LastFiredAt is null || now - state.LastFiredAt >= TimeSpan.FromSeconds(Math.Max(0, trigger.CooldownSeconds));
        state.LastEvaluatedAt = now;
        state.LastConditionValue = matched;
        if (matched && edgeMatches && cooldownComplete)
        {
            await _engine.StartAsync(task, "condition", cancellationToken);
            state.LastFiredAt = now;
        }
        await _repository.UpsertTriggerStateAsync(state, cancellationToken);
    }

    private AutomationTriggerState GetState(string taskId, string triggerId)
    {
        return _states.GetOrAdd(Key(taskId, triggerId), _ => new AutomationTriggerState
        {
            TaskId = taskId,
            TriggerId = triggerId,
        });
    }

    private async Task RecordTriggerFailureAsync(
        AutomationTaskDefinition task,
        AutomationTriggerDefinition trigger,
        DateTimeOffset now,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var state = GetState(task.Id, trigger.Id);
        if (trigger.Type.Equals("condition", StringComparison.OrdinalIgnoreCase))
            state.LastEvaluatedAt = now;
        else
            state.LastFiredAt = now;
        await _repository.UpsertTriggerStateAsync(state, cancellationToken);
        await _engine.RecordSkippedAsync(task, trigger.Type, $"触发器求值失败：{exception.Message}", cancellationToken);
        Debug.WriteLine($"Automation trigger failed ({task.Id}/{trigger.Id}): {exception}");
    }

    private static string Key(string taskId, string triggerId) => $"{taskId}\n{triggerId}";
}
