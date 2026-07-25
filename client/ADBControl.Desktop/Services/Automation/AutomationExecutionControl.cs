namespace ADBControl.Desktop.Services.Automation;

public sealed class AutomationExecutionControl : IDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cancellation = new();
    private TaskCompletionSource<bool> _resumeSource = CompletedSource();
    private bool _paused;

    public CancellationToken CancellationToken => _cancellation.Token;
    public bool IsPaused
    {
        get
        {
            lock (_sync)
                return _paused;
        }
    }

    public bool Pause()
    {
        lock (_sync)
        {
            if (_paused || _cancellation.IsCancellationRequested)
                return false;
            _paused = true;
            _resumeSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }

    public bool Resume()
    {
        TaskCompletionSource<bool>? source;
        lock (_sync)
        {
            if (!_paused)
                return false;
            _paused = false;
            source = _resumeSource;
        }
        source.TrySetResult(true);
        return true;
    }

    public void Stop()
    {
        _cancellation.Cancel();
        lock (_sync)
            _resumeSource.TrySetCanceled(_cancellation.Token);
    }

    public async Task WaitWhilePausedAsync()
    {
        Task wait;
        lock (_sync)
            wait = _resumeSource.Task;
        await wait.WaitAsync(_cancellation.Token);
        _cancellation.Token.ThrowIfCancellationRequested();
    }

    public async Task DelayAsync(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            return;
        var remaining = duration;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (remaining > TimeSpan.Zero)
        {
            await WaitWhilePausedAsync();
            var slice = remaining < TimeSpan.FromMilliseconds(200) ? remaining : TimeSpan.FromMilliseconds(200);
            var before = stopwatch.Elapsed;
            await Task.Delay(slice, _cancellation.Token);
            remaining -= stopwatch.Elapsed - before;
        }
    }

    public void Dispose() => _cancellation.Dispose();

    private static TaskCompletionSource<bool> CompletedSource()
    {
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult(true);
        return source;
    }
}
