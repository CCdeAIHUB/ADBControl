namespace ADBControl.Desktop.Services;

public enum ProjectionBackend
{
    None,
    AdbScrcpy,
    CompanionApp,
}

public sealed class ProjectionSession : IAsyncDisposable
{
    private readonly AdbService _adb;
    private readonly CompanionQuicServer _companionServer;
    private ScrcpySession? _adbSession;
    private CompanionProjectionSession? _appSession;
    private bool _disposed;

    public ProjectionSession(AdbService adb, CompanionQuicServer companionServer)
    {
        _adb = adb;
        _companionServer = companionServer;
    }

    public event Action<ScrcpyFrameSize>? FrameSizeChanged;
    public event Action<string>? Faulted;
    public event Action<ProjectionBackend>? BackendChanged;

    public ProjectionBackend Backend { get; private set; }
    public string BackendLabel => Backend switch
    {
        ProjectionBackend.AdbScrcpy => "scrcpy（ADB）",
        ProjectionBackend.CompanionApp => "伴侣 App（QUIC）",
        _ => "投屏",
    };
    public bool IsRunning => _adbSession?.IsRunning == true || _appSession?.IsRunning == true;
    public string? DeviceId => _adbSession?.DeviceId ?? _appSession?.DeviceId;
    public ScrcpyFrameSize FrameSize => _adbSession?.FrameSize ?? _appSession?.FrameSize ?? default;

    public async Task<AdbCommandResult> StartAsync(
        string deviceId,
        ScrcpyVideoOptions options,
        bool adbAvailable,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await StopAsync();
        if (adbAvailable)
        {
            var session = new ScrcpySession(_adb);
            session.FrameSizeChanged += OnFrameSizeChanged;
            session.Faulted += OnFaulted;
            _adbSession = session;
            SetBackend(ProjectionBackend.AdbScrcpy);
            var adbResult = await session.StartAsync(deviceId, options, cancellationToken);
            if (adbResult.Success || cancellationToken.IsCancellationRequested)
                return adbResult;

            session.FrameSizeChanged -= OnFrameSizeChanged;
            session.Faulted -= OnFaulted;
            _adbSession = null;
            await session.DisposeAsync();
            if (!_companionServer.IsDeviceConnected(deviceId))
                return adbResult;
        }

        var appSession = new CompanionProjectionSession(_companionServer);
        appSession.FrameSizeChanged += OnFrameSizeChanged;
        appSession.Faulted += OnFaulted;
        _appSession = appSession;
        SetBackend(ProjectionBackend.CompanionApp);
        return await appSession.StartAsync(deviceId, options, cancellationToken);
    }

    public void StartDecoding(
        Func<(int Width, int Height)> outputSizeProvider,
        Action<ScrcpyDecodedFrame> frameReady)
    {
        if (_adbSession is not null)
            _adbSession.StartDecoding(outputSizeProvider, frameReady);
        else if (_appSession is not null)
            _appSession.StartDecoding(outputSizeProvider, frameReady);
        else
            throw new InvalidOperationException("投屏会话尚未建立。");
    }

    public Task SendTouchAsync(int action, int x, int y, uint pointerId, CancellationToken cancellationToken = default)
        => _adbSession?.SendTouchAsync(action, x, y, pointerId, cancellationToken)
            ?? _appSession?.SendTouchAsync(action, x, y, pointerId, cancellationToken)
            ?? Task.CompletedTask;

    public Task SendKeycodeAsync(int action, int keycode, CancellationToken cancellationToken = default)
        => _adbSession?.SendKeycodeAsync(action, keycode, cancellationToken)
            ?? _appSession?.SendKeycodeAsync(action, keycode, cancellationToken)
            ?? Task.CompletedTask;

    public void WriteDiagnostic(string stage, string details)
    {
        if (_adbSession is not null)
            _adbSession.WriteDiagnostic(stage, details);
        else
            _appSession?.WriteDiagnostic(stage, details);
    }

    public async Task StopAsync()
    {
        var adb = _adbSession;
        _adbSession = null;
        if (adb is not null)
        {
            adb.FrameSizeChanged -= OnFrameSizeChanged;
            adb.Faulted -= OnFaulted;
            await adb.DisposeAsync();
        }
        var app = _appSession;
        _appSession = null;
        if (app is not null)
        {
            app.FrameSizeChanged -= OnFrameSizeChanged;
            app.Faulted -= OnFaulted;
            await app.DisposeAsync();
        }
        SetBackend(ProjectionBackend.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await StopAsync();
    }

    private void OnFrameSizeChanged(ScrcpyFrameSize size) => FrameSizeChanged?.Invoke(size);
    private void OnFaulted(string message) => Faulted?.Invoke(message);

    private void SetBackend(ProjectionBackend backend)
    {
        if (Backend == backend)
            return;
        Backend = backend;
        BackendChanged?.Invoke(backend);
    }
}
