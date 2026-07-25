using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

namespace ADBControl.Desktop.Services;

public sealed class CompanionProjectionSession : IAsyncDisposable
{
    private const int CodecConfigFlag = 2;
    private const int KeyFrameFlag = 1;
    private readonly CompanionQuicServer _server;
    private readonly ConcurrentDictionary<uint, TouchStart> _touches = new();
    private CancellationTokenSource? _cancellation;
    private CompanionQuicDeviceSession? _device;
    private CompanionVideoStream? _video;
    private Channel<VideoPacket>? _packets;
    private Task? _readerTask;
    private Task? _decoderTask;
    private TaskCompletionSource<string>? _startFailure;
    private string? _sessionId;
    private bool _disposed;
    private bool _stopping;
    private int _receivedFrames;
    private int _decodedFrames;

    private sealed record VideoPacket(byte[] Data, long TimestampUs, int Flags, long ArrivalTimestamp);
    private sealed record TouchStart(int X, int Y, long Timestamp);

    public CompanionProjectionSession(CompanionQuicServer server)
    {
        _server = server;
    }

    public event Action<ScrcpyFrameSize>? FrameSizeChanged;
    public event Action<string>? Faulted;

    public bool IsRunning =>
        _cancellation is { IsCancellationRequested: false } &&
        _video is not null &&
        _readerTask is { IsCompleted: false };
    public string? DeviceId { get; private set; }
    public ScrcpyFrameSize FrameSize { get; private set; }
    public int ReceivedFrames => Volatile.Read(ref _receivedFrames);
    public int DecodedFrames => Volatile.Read(ref _decodedFrames);

    public async Task<AdbCommandResult> StartAsync(
        string deviceId,
        ScrcpyVideoOptions options,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await StopAsync();
        DeviceId = deviceId;
        _sessionId = Guid.NewGuid().ToString("N");
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cancellation.Token;
        try
        {
            _device = await _server.WaitForDeviceAsync(deviceId, TimeSpan.FromSeconds(4), token);
            _startFailure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _device.ProjectionEventReceived += OnProjectionEvent;
            var videoTask = _server.WaitForVideoStreamAsync(
                deviceId,
                _sessionId,
                TimeSpan.FromSeconds(90),
                token);
            var response = await _device.SendCommandAsync(
                "android.screen.projection",
                "projection.start",
                new Dictionary<string, object?>
                {
                    ["sessionId"] = _sessionId,
                    ["width"] = options.Width,
                    ["height"] = options.Height,
                    ["bitrate"] = options.BitRate,
                    ["frameRate"] = options.FrameRate,
                },
                TimeSpan.FromSeconds(10),
                token);
            if (!IsSuccessfulCommand(response, out var commandError))
                throw new InvalidOperationException(commandError);

            var completed = await Task.WhenAny(videoTask, _startFailure.Task);
            if (ReferenceEquals(completed, _startFailure.Task))
                throw new InvalidOperationException(await _startFailure.Task);
            _video = await videoTask;
            _startFailure = null;
            var metadata = _video.Metadata;
            FrameSize = new ScrcpyFrameSize(metadata.Width, metadata.Height);
            FrameSizeChanged?.Invoke(FrameSize);
            _packets = Channel.CreateBounded<VideoPacket>(new BoundedChannelOptions(4)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
            _readerTask = ReadVideoPacketsAsync(_video.Stream, _packets.Writer, token);
            WriteDiagnostic(
                "app.stream.ready",
                $"device={deviceId}; session={_sessionId}; size={metadata.Width}x{metadata.Height}; bitrate={metadata.Bitrate}; fps={metadata.FrameRate}");
            return new AdbCommandResult(
                0,
                $"App QUIC MediaProjection {metadata.Width}x{metadata.Height}",
                string.Empty);
        }
        catch (Exception ex)
        {
            WriteDiagnostic("app.start.failed", ex.ToString());
            await StopAsync();
            return new AdbCommandResult(1, string.Empty, ex.Message);
        }
    }

    public void StartDecoding(
        Func<(int Width, int Height)> outputSizeProvider,
        Action<ScrcpyDecodedFrame> frameReady)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_decoderTask is not null)
            throw new InvalidOperationException("伴侣 App 视频解码任务已经启动。");
        if (_packets is null || _cancellation is null)
            throw new InvalidOperationException("伴侣 App 视频流尚未建立。");
        _decoderTask = DecodePacketsAsync(
            _packets.Reader,
            outputSizeProvider,
            frameReady,
            _cancellation.Token);
    }

    public Task SendTouchAsync(
        int action,
        int x,
        int y,
        uint pointerId,
        CancellationToken cancellationToken = default)
    {
        var device = _device;
        if (device?.IsConnected != true)
            return Task.CompletedTask;
        if (action == 0)
        {
            _touches[pointerId] = new TouchStart(x, y, Stopwatch.GetTimestamp());
            return Task.CompletedTask;
        }
        if (action == 2)
            return Task.CompletedTask;
        if (action != 1 || !_touches.TryRemove(pointerId, out var start))
            return Task.CompletedTask;

        var elapsedMs = (int)Math.Clamp(
            Stopwatch.GetElapsedTime(start.Timestamp).TotalMilliseconds,
            1,
            3000);
        var distance = Math.Sqrt(Math.Pow(x - start.X, 2) + Math.Pow(y - start.Y, 2));
        if (distance < 12)
        {
            return device.SendCommandNoWaitAsync(
                "android.accessibility.control",
                "accessibility.touch.tap",
                new Dictionary<string, object?> { ["x"] = x, ["y"] = y },
                cancellationToken);
        }
        return device.SendCommandNoWaitAsync(
            "android.accessibility.control",
            "accessibility.touch.swipe",
            new Dictionary<string, object?>
            {
                ["startX"] = start.X,
                ["startY"] = start.Y,
                ["endX"] = x,
                ["endY"] = y,
                ["durationMs"] = elapsedMs,
            },
            cancellationToken);
    }

    public Task SendKeycodeAsync(int action, int keycode, CancellationToken cancellationToken = default)
    {
        if (action != 0 || _device?.IsConnected != true)
            return Task.CompletedTask;
        var operation = keycode switch
        {
            4 => "accessibility.global.back",
            3 => "accessibility.global.home",
            187 => "accessibility.global.recents",
            _ => null,
        };
        return operation is null
            ? Task.CompletedTask
            : _device.SendCommandNoWaitAsync(
                "android.accessibility.control",
                operation,
                new Dictionary<string, object?>(),
                cancellationToken);
    }

    private async Task ReadVideoPacketsAsync(
        Stream stream,
        ChannelWriter<VideoPacket> writer,
        CancellationToken cancellationToken)
    {
        var header = new byte[16];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await ReadExactlyAsync(stream, header, cancellationToken);
                var timestampUs = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(0, 8));
                var flags = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4)));
                var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12, 4));
                if (length is 0 or > 16 * 1024 * 1024)
                    throw new InvalidDataException($"伴侣 App 视频包大小无效：{length}。");
                var data = GC.AllocateUninitializedArray<byte>((int)length);
                await ReadExactlyAsync(stream, data, cancellationToken);
                var normalized = NormalizeAnnexB(data);
                await writer.WriteAsync(
                    new VideoPacket(normalized, timestampUs, flags, Stopwatch.GetTimestamp()),
                    cancellationToken);
                if ((flags & CodecConfigFlag) == 0)
                    Interlocked.Increment(ref _receivedFrames);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            WriteDiagnostic("app.stream.failed", ex.ToString());
            Faulted?.Invoke($"伴侣 App 视频流中断：{ex.Message}");
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private async Task DecodePacketsAsync(
        ChannelReader<VideoPacket> reader,
        Func<(int Width, int Height)> outputSizeProvider,
        Action<ScrcpyDecodedFrame> frameReady,
        CancellationToken cancellationToken)
    {
        try
        {
            using var decoder = new ScrcpyFfmpegDecoder();
            long? firstDeviceTimestampUs = null;
            long firstHostTimestamp = 0;
            await foreach (var packet in reader.ReadAllAsync(cancellationToken))
            {
                var target = outputSizeProvider();
                if (target.Width <= 0 || target.Height <= 0)
                    continue;
                var isConfig = (packet.Flags & CodecConfigFlag) != 0;
                if (!isConfig && firstDeviceTimestampUs is null)
                {
                    firstDeviceTimestampUs = packet.TimestampUs;
                    firstHostTimestamp = packet.ArrivalTimestamp;
                }
                foreach (var frame in decoder.Decode(packet.Data, packet.TimestampUs, target.Width, target.Height))
                {
                    Interlocked.Increment(ref _decodedFrames);
                    var late = firstDeviceTimestampUs is { } basePts &&
                        Stopwatch.GetElapsedTime(
                            firstHostTimestamp,
                            Stopwatch.GetTimestamp()).TotalMilliseconds -
                        (packet.TimestampUs - basePts) / 1000d > 350;
                    if (!late)
                        frameReady(frame);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            WriteDiagnostic("app.decoder.failed", ex.ToString());
            Faulted?.Invoke($"伴侣 App H.264 解码失败：{ex.Message}");
        }
    }

    public async Task StopAsync()
    {
        _stopping = true;
        var cancellation = _cancellation;
        _cancellation = null;
        if (_device is not null)
            _device.ProjectionEventReceived -= OnProjectionEvent;
        if (_device?.IsConnected == true)
        {
            try
            {
                await _device.SendCommandNoWaitAsync(
                    "android.screen.projection",
                    "projection.stop",
                    new Dictionary<string, object?>());
            }
            catch { }
        }
        cancellation?.Cancel();
        _packets?.Writer.TryComplete();
        if (_video is not null)
        {
            try { await _video.Stream.DisposeAsync(); } catch { }
        }
        foreach (var task in new[] { _readerTask, _decoderTask })
        {
            if (task is null) continue;
            try { await task; } catch (OperationCanceledException) { } catch { }
        }
        _readerTask = null;
        _decoderTask = null;
        _packets = null;
        _video = null;
        _device = null;
        _touches.Clear();
        cancellation?.Dispose();
        FrameSize = default;
        DeviceId = null;
        _sessionId = null;
        _startFailure = null;
        _stopping = false;
        Interlocked.Exchange(ref _receivedFrames, 0);
        Interlocked.Exchange(ref _decodedFrames, 0);
    }

    public void WriteDiagnostic(string stage, string message)
        => CompanionQuicServer.Log(stage, $"session={_sessionId ?? "none"}; {message}");

    private void OnProjectionEvent(CompanionProjectionEvent projectionEvent)
    {
        if (_stopping || !string.Equals(projectionEvent.SessionId, _sessionId, StringComparison.Ordinal))
            return;
        var terminal = projectionEvent.Failed || projectionEvent.State is
            "consentDenied" or "encoderFailed" or "projectionRevoked" or "stopped";
        if (!terminal)
            return;
        var message = projectionEvent.Message ?? projectionEvent.State switch
        {
            "consentDenied" => "手机未授予屏幕录制权限。请解锁设备后重新开启投屏。",
            "encoderFailed" => "手机 H.264 编码器启动失败。",
            "projectionRevoked" => "Android 已撤销投屏授权。",
            _ => "手机端投屏已停止。",
        };
        if (_startFailure?.TrySetResult(message) == true)
            return;
        if (_video is not null)
            Faulted?.Invoke(message);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await StopAsync();
    }

    private static bool IsSuccessfulCommand(System.Text.Json.JsonElement envelope, out string error)
    {
        var payload = envelope.GetProperty("payload");
        if (payload.TryGetProperty("ok", out var ok) && ok.GetBoolean())
        {
            error = string.Empty;
            return true;
        }
        error = payload.TryGetProperty("error", out var errorObject) &&
            errorObject.ValueKind == System.Text.Json.JsonValueKind.Object &&
            errorObject.TryGetProperty("message", out var message)
            ? message.GetString() ?? "伴侣 App 拒绝了投屏命令。"
            : "伴侣 App 拒绝了投屏命令。";
        return false;
    }

    private static byte[] NormalizeAnnexB(byte[] data)
    {
        if (HasAnnexBStartCode(data))
            return data;
        using var output = new MemoryStream(data.Length + 32);
        var offset = 0;
        while (offset + 4 <= data.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
            offset += 4;
            if (length == 0 || length > data.Length - offset)
                return data;
            output.Write([0, 0, 0, 1]);
            output.Write(data, offset, (int)length);
            offset += (int)length;
        }
        return offset == data.Length ? output.ToArray() : data;
    }

    private static bool HasAnnexBStartCode(ReadOnlySpan<byte> data)
        => data.Length >= 3 && data[0] == 0 && data[1] == 0 &&
           (data[2] == 1 || (data.Length >= 4 && data[2] == 0 && data[3] == 1));

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
                throw new EndOfStreamException("伴侣 App 视频流已关闭。");
            offset += read;
        }
    }
}
