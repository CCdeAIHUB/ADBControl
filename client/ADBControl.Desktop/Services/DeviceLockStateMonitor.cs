using ADBControl.Desktop.Models;

namespace ADBControl.Desktop.Services;

public interface IDeviceLockStateSource
{
    Task<DeviceLockState> GetStateAsync(string deviceId, CancellationToken cancellationToken = default);
}

public sealed class DeviceLockStateChangedEventArgs : EventArgs
{
    public DeviceLockStateChangedEventArgs(
        string deviceId,
        DeviceLockState previousState,
        DeviceLockState state,
        string? errorCode = null,
        string? errorMessage = null)
    {
        DeviceId = deviceId;
        PreviousState = previousState;
        State = state;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    public string DeviceId { get; }
    public DeviceLockState PreviousState { get; }
    public DeviceLockState State { get; }
    public string? ErrorCode { get; }
    public string? ErrorMessage { get; }
}

public sealed class DeviceLockStateMonitor : IAsyncDisposable
{
    private readonly IDeviceLockStateSource _source;
    private readonly TimeSpan _interval;
    private readonly object _sync = new();
    private CancellationTokenSource? _cancellation;
    private Task _monitorTask = Task.CompletedTask;
    private string? _deviceId;
    private DeviceLockState _currentState = DeviceLockState.Unknown;
    private bool _hasSample;
    private string? _lastErrorCode;

    public DeviceLockStateMonitor(IDeviceLockStateSource source, TimeSpan? interval = null)
    {
        _source = source;
        _interval = interval ?? TimeSpan.FromSeconds(1);
        if (_interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), "锁屏状态轮询间隔必须大于零。");
    }

    public event EventHandler<DeviceLockStateChangedEventArgs>? StateChanged;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
                return _cancellation is not null;
        }
    }

    public void Start(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        CancellationTokenSource? previous;
        var next = new CancellationTokenSource();
        lock (_sync)
        {
            previous = _cancellation;
            _cancellation = next;
            _deviceId = deviceId;
            _currentState = DeviceLockState.Unknown;
            _hasSample = false;
            _lastErrorCode = null;
            _monitorTask = Task.Run(() => MonitorAsync(deviceId, next));
        }
        previous?.Cancel();
    }

    public void Stop()
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            cancellation = _cancellation;
            _cancellation = null;
            _deviceId = null;
            _currentState = DeviceLockState.Unknown;
            _hasSample = false;
            _lastErrorCode = null;
        }
        cancellation?.Cancel();
    }

    public DeviceLockState GetCurrentState(string deviceId)
    {
        lock (_sync)
        {
            return string.Equals(_deviceId, deviceId, StringComparison.Ordinal) && _hasSample
                ? _currentState
                : DeviceLockState.Unknown;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task task;
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            task = _monitorTask;
            cancellation = _cancellation;
            _cancellation = null;
            _deviceId = null;
            _currentState = DeviceLockState.Unknown;
            _hasSample = false;
        }
        cancellation?.Cancel();
        await task.ConfigureAwait(false);
    }

    private async Task MonitorAsync(string deviceId, CancellationTokenSource owner)
    {
        try
        {
            while (!owner.IsCancellationRequested)
            {
                var state = DeviceLockState.Unknown;
                string? errorCode = null;
                string? errorMessage = null;
                try
                {
                    state = await _source.GetStateAsync(deviceId, owner.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (owner.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    errorCode = "DEVICE_LOCK_STATE_QUERY_FAILED";
                    errorMessage = ex.Message;
                }

                owner.Token.ThrowIfCancellationRequested();
                Publish(deviceId, owner, state, errorCode, errorMessage);
                await Task.Delay(_interval, owner.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (owner.IsCancellationRequested)
        {
        }
        finally
        {
            owner.Dispose();
        }
    }

    private void Publish(
        string deviceId,
        CancellationTokenSource owner,
        DeviceLockState state,
        string? errorCode,
        string? errorMessage)
    {
        DeviceLockState previousState;
        bool shouldPublish;
        lock (_sync)
        {
            if (!ReferenceEquals(_cancellation, owner) || !string.Equals(_deviceId, deviceId, StringComparison.Ordinal))
                return;

            previousState = _currentState;
            shouldPublish = !_hasSample ||
                previousState != state ||
                !string.Equals(_lastErrorCode, errorCode, StringComparison.Ordinal);
            _hasSample = true;
            _currentState = state;
            _lastErrorCode = errorCode;
        }

        if (shouldPublish)
        {
            StateChanged?.Invoke(
                this,
                new DeviceLockStateChangedEventArgs(deviceId, previousState, state, errorCode, errorMessage));
        }
    }
}
