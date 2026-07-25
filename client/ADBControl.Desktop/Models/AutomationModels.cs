using System.Text.Json;

namespace ADBControl.Desktop.Models;

public enum AutomationConcurrencyPolicy
{
    Skip,
    Queue,
    Restart,
    Parallel,
}

public enum AutomationConditionMode
{
    All,
    Any,
}

public enum AutomationRunStatus
{
    Queued,
    Running,
    Paused,
    Succeeded,
    Failed,
    Stopped,
    Skipped,
}

public enum AutomationStepStatus
{
    Running,
    Succeeded,
    Failed,
    Stopped,
}

public sealed class AutomationTaskDefinition
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public AutomationConcurrencyPolicy ConcurrencyPolicy { get; set; } = AutomationConcurrencyPolicy.Skip;
    public AutomationPermissionSet Permissions { get; set; } = new();
    public List<AutomationTriggerDefinition> Triggers { get; set; } = new();
    public List<AutomationActionDefinition> Actions { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AutomationPermissionSet
{
    public bool AllowAdb { get; set; }
    public bool AllowShell { get; set; }
    public bool AllowCompanion { get; set; }
    public bool AllowAi { get; set; }
    public bool AllowAiDeviceTools { get; set; }
    public bool AllowTaskMutation { get; set; }
    public bool AllowUnlock { get; set; }
}

public sealed class AutomationTriggerDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = "manual";
    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    public string? At { get; set; }
    public List<DayOfWeek> Days { get; set; } = new();
    public int IntervalSeconds { get; set; } = 3600;
    public DateTimeOffset? RunAt { get; set; }
    public string? Cron { get; set; }
    public bool CatchUp { get; set; }
    public int PollIntervalSeconds { get; set; } = 2;
    public bool EdgeOnly { get; set; } = true;
    public int CooldownSeconds { get; set; } = 60;
    public AutomationConditionMode ConditionMode { get; set; } = AutomationConditionMode.All;
    public List<AutomationConditionDefinition> Conditions { get; set; } = new();
}

public sealed class AutomationConditionDefinition
{
    public string Type { get; set; } = string.Empty;
    public string Operator { get; set; } = "equals";
    public bool CaseSensitive { get; set; }
    public bool Negate { get; set; }
    public Dictionary<string, JsonElement> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AutomationActionDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = string.Empty;
    public Dictionary<string, JsonElement> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public AutomationConditionMode ConditionMode { get; set; } = AutomationConditionMode.All;
    public List<AutomationConditionDefinition> Conditions { get; set; } = new();
    public List<AutomationActionDefinition> Actions { get; set; } = new();
    public List<AutomationActionDefinition> ElseActions { get; set; } = new();
}

public sealed class AutomationRunRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TaskId { get; set; } = string.Empty;
    public string TaskName { get; set; } = string.Empty;
    public string Trigger { get; set; } = string.Empty;
    public AutomationRunStatus Status { get; set; } = AutomationRunStatus.Queued;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int TotalSteps { get; set; }
    public int CompletedSteps { get; set; }
    public double Progress { get; set; }
    public string CurrentStep { get; set; } = string.Empty;
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public string ErrorModule { get; set; } = string.Empty;
    public bool ErrorRecoverable { get; set; }
    public string ErrorSuggestion { get; set; } = string.Empty;
    public string TraceId { get; set; } = Guid.NewGuid().ToString("N");

    public static AutomationRunRecord Create(AutomationTaskDefinition task, string trigger, int totalSteps)
    {
        return new AutomationRunRecord
        {
            TaskId = task.Id,
            TaskName = task.Name,
            Trigger = trigger,
            TotalSteps = Math.Max(1, totalSteps),
        };
    }
}

public sealed class AutomationRunStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string RunId { get; set; } = string.Empty;
    public string ActionId { get; set; } = string.Empty;
    public string ActionType { get; set; } = string.Empty;
    public int Sequence { get; set; }
    public AutomationStepStatus Status { get; set; } = AutomationStepStatus.Running;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string Output { get; set; } = string.Empty;
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
}

public sealed class AutomationLogEntry
{
    public long Id { get; set; }
    public string RunId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Level { get; set; } = "info";
    public string Module { get; set; } = "automation";
    public string Message { get; set; } = string.Empty;
    public string DataJson { get; set; } = "{}";
}

public sealed class AutomationTriggerState
{
    public string TaskId { get; set; } = string.Empty;
    public string TriggerId { get; set; } = string.Empty;
    public DateTimeOffset? LastFiredAt { get; set; }
    public DateTimeOffset? LastEvaluatedAt { get; set; }
    public bool? LastConditionValue { get; set; }
}

public sealed record AutomationTaskSnapshot(
    AutomationTaskDefinition Definition,
    AutomationRunRecord? ActiveRun,
    AutomationRunRecord? LastRun,
    DateTimeOffset? NextRunAt);

public sealed record AutomationCommandResult(bool Success, int ExitCode, string Stdout, string Stderr)
{
    public string CombinedOutput => string.Join(
        Environment.NewLine,
        new[] { Stdout.Trim(), Stderr.Trim() }.Where(value => !string.IsNullOrWhiteSpace(value)));
}

public sealed record AutomationConditionResult(bool Matched, string Actual, string Description);

public sealed record AutomationAiRequest(
    string TaskId,
    string TaskName,
    string RunId,
    string DeviceId,
    string Prompt,
    string? ModelId,
    bool AllowDeviceTools,
    bool AllowTaskMutation);

public sealed record AutomationAiOutput(
    string TaskId,
    string TaskName,
    string RunId,
    string Prompt,
    string Response);

public sealed class AutomationExecutionException : Exception
{
    public AutomationExecutionException(
        string errorCode,
        string message,
        string module,
        bool recoverable = false,
        string suggestion = "",
        Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        Module = module;
        Recoverable = recoverable;
        Suggestion = suggestion;
    }

    public string ErrorCode { get; }
    public string Module { get; }
    public bool Recoverable { get; }
    public string Suggestion { get; }
}
