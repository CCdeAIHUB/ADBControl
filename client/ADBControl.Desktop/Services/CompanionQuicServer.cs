using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace ADBControl.Desktop.Services;

public sealed record CompanionVideoMetadata(
    string SessionId,
    string Codec,
    int Width,
    int Height,
    int Bitrate,
    int FrameRate);

public sealed record CompanionVideoStream(
    string DeviceId,
    CompanionVideoMetadata Metadata,
    QuicStream Stream);

public sealed class CompanionQuicServer : IAsyncDisposable
{
    public const string TlsServerName = "adbcontrol.local";
    private const string AlpnText = "adbcontrol-companion/1";
    private const int MaxControlBytes = 4 * 1024 * 1024;
    private static readonly byte[] WireMagic = "ACQ1"u8.ToArray();
    private static readonly SslApplicationProtocol Alpn = new(AlpnText);

    private readonly int _port;
    private int _boundPort;
    private readonly X509Certificate2 _certificate;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly ConcurrentDictionary<string, CompanionQuicDeviceSession> _devices = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CompanionVideoStream>> _videoWaiters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CompanionVideoStream> _pendingVideos = new(StringComparer.Ordinal);
    private readonly ConcurrentBag<Task> _connectionTasks = [];
    private QuicListener? _listener;
    private Task? _acceptTask;
    private bool _disposed;

    public CompanionQuicServer(int port)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        _port = port;
        _certificate = LoadOrCreateCertificate();
        CertificateDer = _certificate.Export(X509ContentType.Cert);
        CertificateFingerprintSha256 = Convert.ToHexString(SHA256.HashData(CertificateDer)).ToLowerInvariant();
    }

    public int Port => Volatile.Read(ref _boundPort) is var bound && bound > 0 ? bound : _port;
    public byte[] CertificateDer { get; }
    public string CertificateDerBase64 => Convert.ToBase64String(CertificateDer);
    public string CertificateFingerprintSha256 { get; }
    public bool IsRunning => _listener is not null && !_shutdown.IsCancellationRequested;
    public event Action<string, bool>? DeviceConnectionChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_listener is not null)
            return;
        await _startLock.WaitAsync(cancellationToken);
        try
        {
            if (_listener is not null)
                return;
            if (!QuicListener.IsSupported)
                throw new PlatformNotSupportedException("当前 Windows/.NET 运行环境不支持 System.Net.Quic/MsQuic。");

            try
            {
                _listener = await ListenAsync(_port, cancellationToken);
            }
            catch (Exception ex) when (IsAddressInUse(ex))
            {
                Log("listener.port_fallback", $"configured_port={_port}; reason={ex.Message}");
                _listener = await ListenAsync(0, cancellationToken);
            }
            Volatile.Write(ref _boundPort, ((IPEndPoint)_listener.LocalEndPoint).Port);
            _acceptTask = AcceptLoopAsync(_shutdown.Token);
            Log("listener.started", $"port={Port}; configured_port={_port}; fingerprint={CertificateFingerprintSha256}");
        }
        finally
        {
            _startLock.Release();
        }
    }

    private ValueTask<QuicListener> ListenAsync(int port, CancellationToken cancellationToken)
    {
        return QuicListener.ListenAsync(
            new QuicListenerOptions
            {
                ListenEndPoint = new IPEndPoint(IPAddress.Any, port),
                ApplicationProtocols = [Alpn],
                ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(
                    new QuicServerConnectionOptions
                    {
                        DefaultStreamErrorCode = 0x10,
                        DefaultCloseErrorCode = 0x11,
                        ServerAuthenticationOptions = new SslServerAuthenticationOptions
                        {
                            ApplicationProtocols = [Alpn],
                            ServerCertificate = _certificate,
                            EnabledSslProtocols = SslProtocols.Tls13,
                        },
                    }),
            },
            cancellationToken);
    }

    private static bool IsAddressInUse(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
                return true;
            if (current.HResult == unchecked((int)0x80072740))
                return true;
            if (current.InnerException is null)
                break;
        }
        return false;
    }

    public bool IsDeviceConnected(string deviceId)
        => _devices.TryGetValue(deviceId, out var session) && session.IsConnected;

    public bool TryGetDevice(string deviceId, out CompanionQuicDeviceSession? session)
    {
        if (_devices.TryGetValue(deviceId, out var candidate) && candidate.IsConnected)
        {
            session = candidate;
            return true;
        }
        session = null;
        return false;
    }

    public async Task<CompanionQuicDeviceSession> WaitForDeviceAsync(
        string deviceId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                if (TryGetDevice(deviceId, out var session))
                    return session!;
                await Task.Delay(50, linked.Token);
            }
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"未发现设备 {deviceId} 的伴侣 App QUIC 会话。");
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException($"未发现设备 {deviceId} 的伴侣 App QUIC 会话。");
    }

    public async Task<CompanionVideoStream> WaitForVideoStreamAsync(
        string deviceId,
        string sessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var key = VideoKey(deviceId, sessionId);
        if (_pendingVideos.TryRemove(key, out var pending))
            return pending;
        var waiter = new TaskCompletionSource<CompanionVideoStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_videoWaiters.TryAdd(key, waiter))
            throw new InvalidOperationException($"视频会话 {sessionId} 已经在等待连接。");
        try
        {
            return await waiter.Task.WaitAsync(timeout, cancellationToken);
        }
        finally
        {
            _videoWaiters.TryRemove(key, out _);
        }
    }

    internal void RemoveSession(CompanionQuicDeviceSession session)
    {
        if (_devices.TryGetValue(session.DeviceId, out var active) && ReferenceEquals(active, session))
        {
            _devices.TryRemove(session.DeviceId, out _);
            DeviceConnectionChanged?.Invoke(session.DeviceId, false);
            Log("device.disconnected", $"device={session.DeviceId}");
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var listener = _listener;
                if (listener is null)
                    break;
                var connection = await listener.AcceptConnectionAsync(cancellationToken);
                _connectionTasks.Add(HandleConnectionAsync(connection, cancellationToken));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log("listener.failed", ex.ToString());
        }
    }

    private async Task HandleConnectionAsync(QuicConnection connection, CancellationToken serverCancellation)
    {
        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(serverCancellation);
        var context = new ConnectionContext(connection, connectionCancellation);
        var streamTasks = new List<Task>();
        Log("connection.accepted", connection.RemoteEndPoint?.ToString() ?? "unknown");
        try
        {
            while (!connectionCancellation.IsCancellationRequested)
            {
                var stream = await connection.AcceptInboundStreamAsync(connectionCancellation.Token);
                streamTasks.Add(HandleStreamAsync(context, stream, connectionCancellation.Token));
            }
        }
        catch (OperationCanceledException) when (connectionCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log("connection.closed", ex.Message);
        }
        finally
        {
            connectionCancellation.Cancel();
            using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await connection.CloseAsync(0x12, closeTimeout.Token); } catch { }
            if (streamTasks.Count > 0)
            {
                try { await Task.WhenAll(streamTasks); } catch { }
            }
            if (context.Session is { } session)
            {
                session.MarkDisconnected();
                RemoveSession(session);
            }
            if (context.HeartbeatTask is not null)
            {
                try { await context.HeartbeatTask; } catch { }
            }
            await connection.DisposeAsync();
        }
    }

    private async Task HandleStreamAsync(ConnectionContext context, QuicStream stream, CancellationToken cancellationToken)
    {
        var handedOff = false;
        var controlStream = false;
        try
        {
            var preface = new byte[5];
            await ReadExactlyAsync(stream, preface, cancellationToken);
            if (!preface.AsSpan(0, 4).SequenceEqual(WireMagic))
                throw new InvalidDataException("QUIC application stream magic is invalid.");
            switch (preface[4])
            {
                case 1 when stream.CanWrite:
                    controlStream = true;
                    await ReadControlLoopAsync(context, stream, cancellationToken);
                    break;
                case 2 when !stream.CanWrite:
                    handedOff = await OfferVideoStreamAsync(context, stream, cancellationToken);
                    break;
                default:
                    throw new InvalidDataException($"Unsupported QUIC application stream type {preface[4]}.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log("stream.failed", ex.ToString());
        }
        finally
        {
            if (controlStream && context.Session is { } session)
            {
                session.MarkDisconnected();
                RemoveSession(session);
                context.RequestClose();
            }
            if (!handedOff)
                await stream.DisposeAsync();
        }
    }

    private async Task ReadControlLoopAsync(
        ConnectionContext context,
        QuicStream stream,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var lengthBytes = new byte[4];
            await ReadExactlyAsync(stream, lengthBytes, cancellationToken);
            var length = BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);
            if (length is 0 or > MaxControlBytes)
                throw new InvalidDataException($"Invalid QUIC control envelope length {length}.");
            var json = GC.AllocateUninitializedArray<byte>((int)length);
            await ReadExactlyAsync(stream, json, cancellationToken);
            using var document = JsonDocument.Parse(json);
            var envelope = document.RootElement.Clone();
            var kind = envelope.GetProperty("kind").GetString();
            if (kind == "hello")
            {
                var payload = envelope.GetProperty("payload");
                var deviceId = payload.GetProperty("deviceId").GetString()
                    ?? throw new InvalidDataException("Companion hello has no deviceId.");
                var session = new CompanionQuicDeviceSession(this, context.Connection, stream, deviceId);
                context.Session = session;
                _devices.TryGetValue(deviceId, out var previous);
                _devices[deviceId] = session;
                if (previous is not null && !ReferenceEquals(previous, session))
                    previous.MarkDisconnected();
                await session.SendEnvelopeAsync(new
                {
                    protocol = "adbcontrol-companion-quic",
                    version = 1,
                    messageId = $"hello-ack-{Guid.NewGuid():N}",
                    traceId = envelope.TryGetProperty("traceId", out var trace) ? trace.GetString() : null,
                    deviceId,
                    channel = "control",
                    kind = "helloAck",
                    payload = new { selectedProtocolVersion = 1, serverTimeUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                }, cancellationToken);
                session.MarkReady();
                Log("device.ready", $"device={deviceId}");
                DeviceConnectionChanged?.Invoke(deviceId, true);
                context.HeartbeatTask ??= RunHeartbeatLoopAsync(session, cancellationToken);
            }
            else
            {
                context.Session?.HandleEnvelope(envelope);
            }
        }
    }

    private async Task<bool> OfferVideoStreamAsync(
        ConnectionContext context,
        QuicStream stream,
        CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[4];
        await ReadExactlyAsync(stream, lengthBytes, cancellationToken);
        var metadataLength = BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);
        if (metadataLength is 0 or > 64 * 1024)
            throw new InvalidDataException($"Invalid video metadata length {metadataLength}.");
        var metadataBytes = GC.AllocateUninitializedArray<byte>((int)metadataLength);
        await ReadExactlyAsync(stream, metadataBytes, cancellationToken);
        var metadata = JsonSerializer.Deserialize<CompanionVideoMetadata>(metadataBytes, JsonOptions)
            ?? throw new InvalidDataException("Video metadata JSON is empty.");
        if (!string.Equals(metadata.Codec, "h264", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unsupported companion video codec {metadata.Codec}.");

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
        while (context.Session is null && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20, cancellationToken);
        var session = context.Session ?? throw new InvalidDataException("Video stream arrived before companion hello.");
        var video = new CompanionVideoStream(session.DeviceId, metadata, stream);
        var key = VideoKey(session.DeviceId, metadata.SessionId);
        if (_videoWaiters.TryRemove(key, out var waiter))
            waiter.TrySetResult(video);
        else
            _pendingVideos[key] = video;
        Log("video.open", $"device={session.DeviceId}; session={metadata.SessionId}; size={metadata.Width}x{metadata.Height}; bitrate={metadata.Bitrate}; fps={metadata.FrameRate}");
        return true;
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
                throw new EndOfStreamException("QUIC stream closed unexpectedly.");
            offset += read;
        }
    }

    private static async Task RunHeartbeatLoopAsync(
        CompanionQuicDeviceSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                await session.SendEnvelopeAsync(new
                {
                    protocol = "adbcontrol-companion-quic",
                    version = 1,
                    messageId = $"heartbeat-{Guid.NewGuid():N}",
                    traceId = (string?)null,
                    deviceId = session.DeviceId,
                    channel = "control",
                    kind = "heartbeat",
                    payload = new { timestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                }, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log("heartbeat.failed", $"device={session.DeviceId}; {ex}");
        }
    }

    private static string VideoKey(string deviceId, string sessionId) => $"{deviceId}\n{sessionId}";

    private static X509Certificate2 LoadOrCreateCertificate()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ADBControl");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "companion-quic-server.pfx");
        if (File.Exists(path))
            return new X509Certificate2(path, (string?)null, X509KeyStorageFlags.Exportable);

        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={TlsServerName}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var usages = new OidCollection { new("1.3.6.1.5.5.7.3.1") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(TlsServerName);
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(5));
        var pfx = generated.Export(X509ContentType.Pfx);
        File.WriteAllBytes(path, pfx);
        return new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.Exportable);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _shutdown.Cancel();
        if (_listener is not null)
            await _listener.DisposeAsync();
        if (_acceptTask is not null)
        {
            try { await _acceptTask; } catch (OperationCanceledException) { }
        }
        var connections = _connectionTasks.ToArray();
        if (connections.Length > 0)
        {
            try { await Task.WhenAll(connections); } catch { }
        }
        foreach (var session in _devices.Values)
            session.MarkDisconnected();
        _devices.Clear();
        _certificate.Dispose();
        _startLock.Dispose();
        _shutdown.Dispose();
    }

    internal static void Log(string stage, string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ADBControl",
                "logs");
            Directory.CreateDirectory(directory);
            var entry = $"[{DateTimeOffset.Now:O}] {stage} {message}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(directory, "companion-quic.log"), entry);
        }
        catch { }
    }

    private sealed class ConnectionContext(
        QuicConnection connection,
        CancellationTokenSource connectionCancellation)
    {
        public QuicConnection Connection { get; } = connection;
        public CompanionQuicDeviceSession? Session { get; set; }
        public Task? HeartbeatTask { get; set; }
        public void RequestClose() => connectionCancellation.Cancel();
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

public sealed class CompanionQuicDeviceSession
{
    private readonly CompanionQuicServer _owner;
    private readonly QuicConnection _connection;
    private readonly QuicStream _control;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new(StringComparer.Ordinal);
    private volatile bool _ready;
    private volatile bool _disconnected;

    internal CompanionQuicDeviceSession(
        CompanionQuicServer owner,
        QuicConnection connection,
        QuicStream control,
        string deviceId)
    {
        _owner = owner;
        _connection = connection;
        _control = control;
        DeviceId = deviceId;
    }

    public string DeviceId { get; }
    public bool IsConnected => _ready && !_disconnected;
    public event Action<CompanionProjectionEvent>? ProjectionEventReceived;

    internal void MarkReady() => _ready = true;

    internal void MarkDisconnected()
    {
        if (_disconnected)
            return;
        _disconnected = true;
        foreach (var pending in _pending.Values)
            pending.TrySetException(new IOException("伴侣 App QUIC 控制连接已断开。"));
        _pending.Clear();
    }

    internal void HandleEnvelope(JsonElement envelope)
    {
        var kind = envelope.GetProperty("kind").GetString();
        if (envelope.TryGetProperty("payload", out var eventPayload) &&
            eventPayload.TryGetProperty("sessionId", out var sessionIdValue) &&
            eventPayload.TryGetProperty("state", out var stateValue))
        {
            var sessionId = sessionIdValue.GetString();
            var state = stateValue.GetString();
            if (!string.IsNullOrWhiteSpace(sessionId) && !string.IsNullOrWhiteSpace(state))
            {
                var message = eventPayload.TryGetProperty("message", out var messageValue)
                    ? messageValue.GetString()
                    : eventPayload.TryGetProperty("reason", out var reasonValue)
                        ? reasonValue.GetString()
                        : null;
                ProjectionEventReceived?.Invoke(new CompanionProjectionEvent(
                    sessionId,
                    state,
                    message,
                    kind == "error"));
            }
        }
        if (kind is not ("commandResponse" or "error"))
            return;
        if (!envelope.TryGetProperty("payload", out var payload) ||
            !payload.TryGetProperty("requestId", out var requestIdValue))
            return;
        var requestId = requestIdValue.GetString();
        if (requestId is not null && _pending.TryRemove(requestId, out var pending))
            pending.TrySetResult(envelope.Clone());
    }

    public async Task<JsonElement> SendCommandAsync(
        string capabilityId,
        string operation,
        IReadOnlyDictionary<string, object?> args,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, completion))
            throw new InvalidOperationException("无法注册伴侣 App 命令请求。");
        try
        {
            await SendCommandEnvelopeAsync(requestId, capabilityId, operation, args, cancellationToken);
            return await completion.Task.WaitAsync(timeout, cancellationToken);
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    public Task SendCommandNoWaitAsync(
        string capabilityId,
        string operation,
        IReadOnlyDictionary<string, object?> args,
        CancellationToken cancellationToken = default)
        => SendCommandEnvelopeAsync(Guid.NewGuid().ToString("N"), capabilityId, operation, args, cancellationToken);

    private Task SendCommandEnvelopeAsync(
        string requestId,
        string capabilityId,
        string operation,
        IReadOnlyDictionary<string, object?> args,
        CancellationToken cancellationToken)
    {
        return SendEnvelopeAsync(new
        {
            protocol = "adbcontrol-companion-quic",
            version = 1,
            messageId = $"command-{requestId}",
            traceId = requestId,
            deviceId = DeviceId,
            channel = "control",
            kind = "commandRequest",
            payload = new { requestId, capabilityId, operation, args },
        }, cancellationToken);
    }

    internal async Task SendEnvelopeAsync(object envelope, CancellationToken cancellationToken)
    {
        if (_disconnected)
            throw new IOException("伴侣 App QUIC 控制连接已断开。");
        var json = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)json.Length));
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _control.WriteAsync(length, cancellationToken);
            await _control.WriteAsync(json, cancellationToken);
            await _control.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

public sealed record CompanionProjectionEvent(
    string SessionId,
    string State,
    string? Message,
    bool Failed);
