using ADBControl.Desktop.Models;
using ADBControl.Desktop.Services;
using ADBControl.Desktop.Services.Automation;

internal static class AutomationTests
{
    public static async Task RunAsync()
    {
        ValidateDslAndSchedules();
        await ValidateRepositoryRecoveryAsync();
        await ValidateExecutionControlsAsync();
        await ValidateConditionCatalogAsync();
        Console.WriteLine("Automation task DSL, scheduler, repository, runtime, and condition tests passed.");
    }

    private static void ValidateDslAndSchedules()
    {
        var definition = AutomationTaskSerializer.Deserialize("""
        {
          "schemaVersion": 1,
          "name": "每日检查",
          "deviceId": "device-1",
          "enabled": true,
          "concurrencyPolicy": "skip",
          "permissions": { "allowAdb": true, "allowShell": true },
          "triggers": [
            { "id": "daily", "type": "daily", "at": "17:00", "timeZoneId": "UTC" },
            { "id": "weekly", "type": "weekly", "days": ["Wednesday"], "at": "11:00", "timeZoneId": "UTC" },
            { "id": "hourly", "type": "interval", "intervalSeconds": 3600 }
          ],
          "actions": [{ "type": "adb.shell", "parameters": { "command": "getprop ro.product.model" } }]
        }
        """);
        definition.CreatedAt = DateTimeOffset.Parse("2026-07-15T00:00:00Z");
        var validation = AutomationTaskValidator.Validate(definition);
        Ensure(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));

        var daily = AutomationScheduleCalculator.GetNextOccurrence(
            definition,
            definition.Triggers[0],
            DateTimeOffset.Parse("2026-07-15T16:30:00Z"));
        Ensure(daily == DateTimeOffset.Parse("2026-07-15T17:00:00Z"), "每天 17:00 必须计算到当天的下一次执行。 ");

        var weekly = AutomationScheduleCalculator.GetNextOccurrence(
            definition,
            definition.Triggers[1],
            DateTimeOffset.Parse("2026-07-15T10:30:00Z"));
        Ensure(weekly == DateTimeOffset.Parse("2026-07-15T11:00:00Z"), "周三 11:00 必须在当天命中。 ");

        var interval = AutomationScheduleCalculator.GetNextOccurrence(
            definition,
            definition.Triggers[2],
            DateTimeOffset.Parse("2026-07-15T00:01:00Z"));
        Ensure(interval == DateTimeOffset.Parse("2026-07-15T01:00:00Z"), "每小时触发必须以任务创建时间为稳定锚点。 ");

        var cronTrigger = AutomationTaskSerializer.Deserialize("""
        {
          "name":"Cron", "enabled":true,
          "triggers":[{"id":"cron","type":"cron","cron":"*/15 9-10 * * 1-5","timeZoneId":"UTC"}],
          "actions":[{"type":"log","parameters":{"message":"tick"}}]
        }
        """).Triggers[0];
        var cronTask = new AutomationTaskDefinition { Name = "Cron", CreatedAt = DateTimeOffset.Parse("2026-07-15T00:00:00Z") };
        var cron = AutomationScheduleCalculator.GetNextOccurrence(cronTask, cronTrigger, DateTimeOffset.Parse("2026-07-15T09:07:00Z"));
        Ensure(cron == DateTimeOffset.Parse("2026-07-15T09:15:00Z"), "五段 Cron 的范围和步长必须生效。 ");

        var missingConditionPermissions = AutomationTaskSerializer.Deserialize("""
        {
          "name":"设备条件权限", "deviceId":"device-1", "enabled":true,
          "triggers":[{"type":"condition","conditions":[{"type":"app.foreground","parameters":{"value":"com.example"}}]}],
          "actions":[{"type":"log","parameters":{"message":"matched"}}]
        }
        """);
        var permissionValidation = AutomationTaskValidator.Validate(missingConditionPermissions);
        Ensure(!permissionValidation.IsValid && permissionValidation.Errors.Any(error => error.Contains("allowAdb", StringComparison.Ordinal)), "设备条件必须显式声明 ADB 权限。 ");
    }

    private static async Task ValidateRepositoryRecoveryAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ADBControl", "automation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "automation.sqlite");
        await using var repository = new AutomationTaskRepository(databasePath);
        await repository.InitializeAsync();

        var task = AutomationTaskSerializer.Deserialize("""
        { "name":"持久化", "enabled":true, "triggers":[{"type":"manual"}], "actions":[{"type":"log","parameters":{"message":"ok"}}] }
        """);
        await repository.UpsertTaskAsync(task);
        var loaded = await repository.LoadTasksAsync();
        Ensure(loaded.Count == 1 && loaded[0].Name == "持久化", "SQLite 必须完整恢复任务定义。 ");

        var run = AutomationRunRecord.Create(task, "manual", 1);
        run.Status = AutomationRunStatus.Running;
        await repository.UpsertRunAsync(run);
        await repository.RecoverInterruptedRunsAsync();
        var recovered = (await repository.LoadRecentRunsAsync())[0];
        Ensure(recovered.Status == AutomationRunStatus.Failed && recovered.ErrorCode == "APP_RESTART_INTERRUPTED", "启动时必须显式终结中断运行。 ");

        await repository.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    private static async Task ValidateExecutionControlsAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ADBControl", "automation-runtime-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var gateway = new FakeAutomationDeviceGateway();
        await using var service = new AutomationTaskService(Path.Combine(directory, "automation.sqlite"), gateway);
        Ensure(await service.ListAsJsonAsync() == "[]", "AI 在显式初始化前读取任务列表时必须先完成数据库初始化。 ");

        var definition = AutomationTaskSerializer.Deserialize("""
        {
          "name":"暂停与进度", "deviceId":"device-1", "enabled":true,
          "permissions":{"allowAdb":true,"allowShell":true},
          "triggers":[{"type":"manual"}],
          "actions":[
            {"type":"log","parameters":{"message":"start"}},
            {"type":"delay","parameters":{"milliseconds":600}},
            {"type":"adb.shell","parameters":{"command":"getprop ro.product.model"}}
          ]
        }
        """);
        await service.CreateOrUpdateAsync(definition);
        var run = await service.RunNowAsync(definition.Id);
        Ensure(run is not null, "手动运行必须创建运行记录。 ");
        await WaitUntilAsync(() => service.GetSnapshots().Single().ActiveRun?.Status == AutomationRunStatus.Running, TimeSpan.FromSeconds(2));
        await service.PauseAsync(run!.Id);
        Ensure(service.GetSnapshots().Single().ActiveRun?.Status == AutomationRunStatus.Paused, "运行必须可以暂停。 ");
        await Task.Delay(150);
        await service.ResumeAsync(run.Id);
        await WaitUntilAsync(() => service.GetSnapshots().Single().LastRun?.Status == AutomationRunStatus.Succeeded, TimeSpan.FromSeconds(3));
        var completed = service.GetSnapshots().Single().LastRun!;
        Ensure(completed.Progress >= 1 && completed.CompletedSteps == completed.TotalSteps, "完成后步骤进度必须达到 100%。 ");
        Ensure(gateway.ShellCommands.Contains("getprop ro.product.model"), "运行器必须执行真实网关动作而不是静态结果。 ");

        var adb = new AdbService();
        var aiTools = new AiAgentToolService(adb, new CompanionAppService(adb), service);
        var aiCreate = await aiTools.ExecuteAsync(
            new AiAgentToolCall
            {
                Id = "tool-create",
                Name = "task_create",
                ArgumentsJson = """
                {"definition":{"name":"AI 创建","enabled":true,"triggers":[{"type":"manual"}],"actions":[{"type":"log","parameters":{"message":"created"}}]}}
                """,
            },
            "完全访问",
            currentDevice: null,
            (_, _) => Task.FromResult(false));
        Ensure(aiCreate.Success && service.GetSnapshots().Any(item => item.Definition.Name == "AI 创建"), "AI 工具必须在没有打开设备详情页时也能创建任务。 ");

        var stoppable = AutomationTaskSerializer.Deserialize("""
        { "name":"停止", "enabled":true, "triggers":[{"type":"manual"}], "actions":[{"type":"delay","parameters":{"milliseconds":5000}}] }
        """);
        await service.CreateOrUpdateAsync(stoppable);
        var stopRun = await service.RunNowAsync(stoppable.Id);
        await WaitUntilAsync(() => service.GetSnapshots().Single(item => item.Definition.Id == stoppable.Id).ActiveRun?.Status == AutomationRunStatus.Running, TimeSpan.FromSeconds(2));
        await service.StopAsync(stopRun!.Id);
        await WaitUntilAsync(() => service.GetSnapshots().Single(item => item.Definition.Id == stoppable.Id).LastRun?.Status == AutomationRunStatus.Stopped, TimeSpan.FromSeconds(2));

        await service.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    private static async Task ValidateConditionCatalogAsync()
    {
        var gateway = new FakeAutomationDeviceGateway
        {
            ShellOutput = "mResumedActivity: ActivityRecord{123 u0 com.example.browser/.MainActivity t4}",
        };
        var evaluator = new AutomationConditionEvaluator(gateway);
        var condition = AutomationTaskSerializer.Deserialize("""
        {
          "name":"condition", "deviceId":"device-1", "actions":[{"type":"log"}],
          "triggers":[{"type":"condition","conditions":[{"type":"app.foreground","operator":"equals","parameters":{"value":"com.example.browser"}}]}]
        }
        """).Triggers[0].Conditions[0];
        var result = await evaluator.EvaluateAsync("device-1", condition, CancellationToken.None);
        Ensure(result.Matched && result.Actual == "com.example.browser", "前台 App 条件必须从设备输出解析并比较包名。 ");
        Ensure(AutomationConditionEvaluator.Compare("https://example.com/path", "contains", "example.com", false), "网页条件需要可复用的 contains 比较器。 ");
        Ensure(AutomationConditionEvaluator.Compare("87", "greaterThanOrEqual", "80", false), "电量等数值条件必须支持大小比较。 ");

        gateway.ShellOutput = "window policy output without keyguard state";
        var locked = new AutomationConditionDefinition { Type = "screen.locked" };
        var lockedResult = await evaluator.EvaluateAsync("device-1", locked, CancellationToken.None);
        Ensure(!lockedResult.Matched && lockedResult.Actual == "unknown", "无法识别锁屏状态时不得误判为已锁屏。 ");

        gateway.CompanionOutput = "Broadcast completed: result=0\n data={\"requestId\":\"1\",\"ok\":true,\"result\":{\"enabled\":true}}";
        var accessibility = new AutomationConditionDefinition { Type = "companion.accessibility.ready" };
        var accessibilityResult = await evaluator.EvaluateAsync("device-1", accessibility, CancellationToken.None);
        Ensure(accessibilityResult.Matched && accessibilityResult.Actual == "true", "Companion 无障碍状态必须从返回 payload 的 enabled 字段解析。 ");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new InvalidOperationException("等待自动化状态超时。 ");
            await Task.Delay(20);
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class FakeAutomationDeviceGateway : IAutomationDeviceGateway
    {
        public List<string> ShellCommands { get; } = new();
        public string ShellOutput { get; set; } = "ok";
        public string CompanionOutput { get; set; } = "{}";

        public Task<bool> IsConnectedAsync(string deviceId, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<AutomationCommandResult> ShellAsync(string deviceId, string command, CancellationToken cancellationToken)
        {
            ShellCommands.Add(command);
            return Task.FromResult(new AutomationCommandResult(true, 0, ShellOutput, string.Empty));
        }

        public Task<AutomationCommandResult> CompanionAsync(
            string deviceId,
            string capabilityId,
            string operation,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken) => Task.FromResult(new AutomationCommandResult(true, 0, CompanionOutput, string.Empty));

        public Task<(int Width, int Height)> GetScreenSizeAsync(string deviceId, CancellationToken cancellationToken) => Task.FromResult((1080, 2400));
    }
}
