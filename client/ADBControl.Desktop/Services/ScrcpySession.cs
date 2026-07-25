using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace ADBControl.Desktop.Services;

public sealed class ScrcpySession : IAsyncDisposable
{
    public const string UpstreamVersion = ScrcpyIntegration.UpstreamVersion;
    private const string DeviceServerPathPrefix = "/data/local/tmp/adbcontrol-scrcpy-server";
    private const uint H264CodecId = 0x68323634;
    private const ulong SessionPacketFlag = 1UL << 63;
    private const ulong ConfigPacketFlag = 1UL << 62;
    private const ulong KeyFramePacketFlag = 1UL << 61;
    private const ulong PacketFlagsMask = SessionPacketFlag | ConfigPacketFlag | KeyFramePacketFlag;
    private const ulong GenericFingerPointerId = ulong.MaxValue - 1;
    private static readonly object LogSync = new();

    private readonly AdbService _adb;
    private readonly SemaphoreSlim _controlWriteLock = new(1, 1);
    private CancellationTokenSource? _cancellation;
    private Process? _serverProcess;
    private TcpClient? _videoClient;
    private TcpClient? _controlClient;
    private NetworkStream? _videoStream;
    private NetworkStream? _controlStream;
    private Channel<ScrcpyVideoPacket>? _frames;
    private Task? _readerTask;
    private Task? _decoderTask;
    private Task<string>? _serverStdoutTask;
    private Task<string>? _serverStderrTask;
    private int? _forwardedPort;
    private string? _deviceId;
    private string? _deviceServerPath;
    private string? _sessionId;
    private byte[]? _codecConfig;
    private long? _firstPresentationTimestampUs;
    private bool _disposed;
    private int _receivedFrames;
    private long _receivedBytes;
    private int _decodedFrames;

    private sealed record ScrcpyVideoPacket(byte[] Data, long TimestampUs, bool IsKeyFrame);

    public ScrcpySession(AdbService adb)
    {
        _adb = adb;
    }

    public event Action<ScrcpyFrameSize>? FrameSizeChanged;
    public event Action<string>? Faulted;

    public bool IsRunning =>
        _cancellation is { IsCancellationRequested: false } &&
        _serverProcess is { HasExited: false } &&
        _readerTask is { IsCompleted: false };

    public string? DeviceId => _deviceId;
    public ScrcpyFrameSize FrameSize { get; private set; }
    public int ReceivedFrames => Volatile.Read(ref _receivedFrames);
    public long ReceivedBytes => Interlocked.Read(ref _receivedBytes);
    public int DecodedFrames => Volatile.Read(ref _decodedFrames);

    public void WriteDiagnostic(string stage, string details)
        => Log(_sessionId, stage, details);

    public async Task<AdbCommandResult> StartAsync(
        string deviceId,
        ScrcpyVideoOptions options,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await StopAsync();

        var scid = RandomNumberGenerator.GetInt32(0x10000000, int.MaxValue);
        var sessionId = scid.ToString("x8");
        var installedCompanionPath = await ResolveInstalledCompanionApkPathAsync(deviceId);
        var deviceServerPath = installedCompanionPath;
        var temporaryServerPath = false;
        if (deviceServerPath is null)
        {
            var serverPath = ResolveServerPath();
            if (serverPath is null)
            {
                return new AdbCommandResult(
                    1,
                    string.Empty,
                    "设备未安装伴侣 App，且未找到独立 scrcpy-server 构建产物。" );
            }
            deviceServerPath = $"{DeviceServerPathPrefix}-{sessionId}.jar";
            var push = await _adb.PushAsync(deviceId, serverPath, deviceServerPath);
            if (!push.Success)
            {
                Log(sessionId, "push.failed", $"exit={push.ExitCode}; stderr={push.Stderr}");
                return push;
            }
            temporaryServerPath = true;
        }
        Log(
            sessionId,
            "start",
            $"device={deviceId}; source={(temporaryServerPath ? "pushed-server" : "companion-apk")}; classpath={deviceServerPath}; requested={options.Width}x{options.Height}@{options.FrameRate}; bitrate={options.BitRate}");

        _deviceId = deviceId;
        _deviceServerPath = temporaryServerPath ? deviceServerPath : null;
        _sessionId = sessionId;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cancellation.Token;
        try
        {
            var socketName = $"scrcpy_{scid:x8}";
            var forward = await RunAdbAsync(
                TimeSpan.FromSeconds(10),
                token,
                "-s", deviceId, "forward", "tcp:0", $"localabstract:{socketName}");
            if (!forward.Success || !int.TryParse(forward.Stdout.Trim(), out var localPort))
            {
                Log(sessionId, "forward.failed", $"exit={forward.ExitCode}; stdout={forward.Stdout}; stderr={forward.Stderr}");
                return forward.Success
                    ? new AdbCommandResult(1, forward.Stdout, "ADB 未返回 scrcpy 转发端口。")
                    : forward;
            }
            _forwardedPort = localPort;
            Log(sessionId, "forward.ready", $"tcp:{localPort} -> localabstract:{socketName}");

            _serverProcess = StartServerProcess(deviceId, scid, deviceServerPath, options);
            _serverStdoutTask = _serverProcess.StandardOutput.ReadToEndAsync();
            _serverStderrTask = _serverProcess.StandardError.ReadToEndAsync();

            _videoClient = await ConnectFirstSocketWithRetryAsync(localPort, _serverProcess, token);
            _controlClient = await ConnectWithRetryAsync(localPort, _serverProcess, token);
            Log(sessionId, "sockets.connected", "video=true; control=true");
            _videoClient.NoDelay = true;
            _controlClient.NoDelay = true;
            _videoStream = _videoClient.GetStream();
            _controlStream = _controlClient.GetStream();

            var codecBytes = new byte[4];
            await ReadExactlyAsync(_videoStream, codecBytes, token);
            var codecId = BinaryPrimitives.ReadUInt32BigEndian(codecBytes);
            if (codecId != H264CodecId)
                throw new InvalidDataException($"scrcpy 返回了不支持的视频编码 0x{codecId:X8}，当前仅接收 H.264。");

            FrameSize = await ReadInitialSessionMetaAsync(_videoStream, token);
            FrameSizeChanged?.Invoke(FrameSize);
            Log(sessionId, "stream.ready", $"codec=h264; size={FrameSize.Width}x{FrameSize.Height}");

            _codecConfig = await ReadInitialCodecConfigAsync(_videoStream, token);
            Log(
                sessionId,
                "codec.ready",
                $"bytes={_codecConfig.Length}; prefix={Convert.ToHexString(_codecConfig.AsSpan(0, Math.Min(16, _codecConfig.Length)))}");

            _frames = Channel.CreateBounded<ScrcpyVideoPacket>(new BoundedChannelOptions(12)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
            _readerTask = ReadVideoPacketsAsync(_videoStream, _frames.Writer, token);
            return new AdbCommandResult(0, $"scrcpy {UpstreamVersion} {FrameSize.Width}x{FrameSize.Height}", string.Empty);
        }
        catch (Exception ex)
        {
            var serverError = await ReadServerOutputAsync(TimeSpan.FromMilliseconds(350));
            Log(sessionId, "start.failed", string.IsNullOrWhiteSpace(serverError) ? ex.ToString() : $"{ex}\n{serverError}");
            await StopAsync();
            return new AdbCommandResult(1, string.Empty, string.IsNullOrWhiteSpace(serverError) ? ex.Message : $"{ex.Message}\n{serverError}");
        }
    }

    public void StartDecoding(
        Func<(int Width, int Height)> outputSizeProvider,
        Action<ScrcpyDecodedFrame> frameReady)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(outputSizeProvider);
        ArgumentNullException.ThrowIfNull(frameReady);
        if (_decoderTask is not null)
            throw new InvalidOperationException("scrcpy 解码任务已经启动。");
        if (_frames is null || _cancellation is null)
            throw new InvalidOperationException("scrcpy 视频连接尚未建立。");

        _decoderTask = DecodePacketsAsync(
            _frames.Reader,
            outputSizeProvider,
            frameReady,
            _cancellation.Token);
    }

    public async Task SendTouchAsync(int action, int x, int y, uint pointerId, CancellationToken cancellationToken = default)
    {
        var stream = _controlStream;
        var size = FrameSize;
        if (stream is null || size.Width <= 0 || size.Height <= 0)
            return;

        var message = new byte[32];
        message[0] = 2;
        message[1] = checked((byte)action);
        BinaryPrimitives.WriteUInt64BigEndian(message.AsSpan(2, 8), GenericFingerPointerId - pointerId);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(10, 4), Math.Clamp(x, 0, size.Width - 1));
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(14, 4), Math.Clamp(y, 0, size.Height - 1));
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(18, 2), checked((ushort)size.Width));
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(20, 2), checked((ushort)size.Height));
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(22, 2), action == 1 ? (ushort)0 : ushort.MaxValue);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(24, 4), 0);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(28, 4), 0);
        await WriteControlMessageAsync(message, cancellationToken);
        if (action != 2)
            Log(_sessionId, action == 0 ? "control.touch.down" : "control.touch.up", $"x={x}; y={y}; pointer={pointerId}");
    }

    public async Task SendKeycodeAsync(int action, int keycode, CancellationToken cancellationToken = default)
    {
        var message = new byte[14];
        message[0] = 0;
        message[1] = checked((byte)action);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(2, 4), keycode);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(6, 4), 0);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(10, 4), 0);
        await WriteControlMessageAsync(message, cancellationToken);
    }

    public async Task StopAsync()
    {
        var deviceId = _deviceId;
        var forwardedPort = _forwardedPort;
        var deviceServerPath = _deviceServerPath;
        var sessionId = _sessionId;
        _deviceId = null;
        _forwardedPort = null;
        _deviceServerPath = null;
        _sessionId = null;

        var cancellation = _cancellation;
        _cancellation = null;
        cancellation?.Cancel();

        _frames?.Writer.TryComplete();
        _frames = null;

        _videoStream?.Dispose();
        _videoStream = null;
        _controlStream?.Dispose();
        _controlStream = null;
        _videoClient?.Dispose();
        _videoClient = null;
        _controlClient?.Dispose();
        _controlClient = null;

        if (_serverProcess is { } process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            catch
            {
            }
            var serverOutput = await ReadServerOutputAsync(TimeSpan.FromMilliseconds(250));
            if (!string.IsNullOrWhiteSpace(serverOutput))
                Log(sessionId, "server.output", serverOutput);
            process.Dispose();
        }
        _serverProcess = null;

        if (_readerTask is { } readerTask)
        {
            try
            {
                await readerTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        }
        _readerTask = null;

        if (_decoderTask is { } decoderTask)
        {
            try
            {
                await decoderTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        }
        _decoderTask = null;

        if (deviceId is not null && forwardedPort is not null)
        {
            await RunAdbAsync(
                TimeSpan.FromSeconds(5),
                CancellationToken.None,
                "-s", deviceId, "forward", "--remove", $"tcp:{forwardedPort.Value}");
        }
        if (deviceId is not null && deviceServerPath is not null)
        {
            await RunAdbAsync(
                TimeSpan.FromSeconds(5),
                CancellationToken.None,
                "-s", deviceId, "shell", "rm", "-f", deviceServerPath);
        }

        cancellation?.Dispose();
        _codecConfig = null;
        _firstPresentationTimestampUs = null;
        FrameSize = default;
        if (sessionId is not null)
        {
            Log(
                sessionId,
                "stopped",
                $"resources released; received_frames={ReceivedFrames}; decoded_frames={DecodedFrames}; bytes={ReceivedBytes}");
        }
        Interlocked.Exchange(ref _receivedFrames, 0);
        Interlocked.Exchange(ref _receivedBytes, 0);
        Interlocked.Exchange(ref _decodedFrames, 0);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await StopAsync();
        _controlWriteLock.Dispose();
    }

    private async Task DecodePacketsAsync(
        ChannelReader<ScrcpyVideoPacket> reader,
        Func<(int Width, int Height)> outputSizeProvider,
        Action<ScrcpyDecodedFrame> frameReady,
        CancellationToken cancellationToken)
    {
        try
        {
            using var decoder = new ScrcpyFfmpegDecoder();
            Log(_sessionId, "decoder.ready", "codec=ffmpeg/libavcodec h264; output=bgra");
            await foreach (var packet in reader.ReadAllAsync(cancellationToken))
            {
                var target = outputSizeProvider();
                if (target.Width <= 0 || target.Height <= 0)
                    continue;
                foreach (var frame in decoder.Decode(packet.Data, packet.TimestampUs, target.Width, target.Height))
                {
                    var decoded = Interlocked.Increment(ref _decodedFrames);
                    if (decoded == 1)
                    {
                        Log(
                            _sessionId,
                            "decoder.first_frame",
                            $"input_bytes={packet.Data.Length}; key={packet.IsKeyFrame}; output={frame.Width}x{frame.Height}; pts_us={packet.TimestampUs}");
                    }
                    frameReady(frame);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log(_sessionId, "decoder.failed", ex.ToString());
            Faulted?.Invoke($"scrcpy H.264 解码失败：{ex.Message}");
        }
    }

    private async Task<byte[]> ReadInitialCodecConfigAsync(Stream stream, CancellationToken cancellationToken)
    {
        var metadata = new byte[12];
        while (true)
        {
            await ReadExactlyAsync(stream, metadata, cancellationToken);
            var ptsAndFlags = BinaryPrimitives.ReadUInt64BigEndian(metadata);
            if ((ptsAndFlags & SessionPacketFlag) != 0)
            {
                var size = ValidateFrameSize(
                    BinaryPrimitives.ReadInt32BigEndian(metadata.AsSpan(4, 4)),
                    BinaryPrimitives.ReadInt32BigEndian(metadata.AsSpan(8, 4)));
                FrameSize = size;
                FrameSizeChanged?.Invoke(size);
                continue;
            }

            var packetSize = BinaryPrimitives.ReadInt32BigEndian(metadata.AsSpan(8, 4));
            if (packetSize <= 0 || packetSize > 1024 * 1024)
                throw new InvalidDataException($"scrcpy H.264 配置包大小无效：{packetSize}。");

            var data = GC.AllocateUninitializedArray<byte>(packetSize);
            await ReadExactlyAsync(stream, data, cancellationToken);
            if ((ptsAndFlags & ConfigPacketFlag) == 0)
                throw new InvalidDataException("scrcpy 未在首个视频帧前发送 H.264 SPS/PPS 配置。");
            if (!HasAnnexBStartCode(data))
                throw new InvalidDataException("scrcpy H.264 配置不是 Annex B 格式。");
            return data;
        }
    }

    private static bool HasAnnexBStartCode(ReadOnlySpan<byte> data)
        => data.Length >= 3 && data[0] == 0 && data[1] == 0 &&
           (data[2] == 1 || (data.Length >= 4 && data[2] == 0 && data[3] == 1));

    private async Task<ScrcpyFrameSize> ReadInitialSessionMetaAsync(Stream stream, CancellationToken cancellationToken)
    {
        var metadata = new byte[12];
        await ReadExactlyAsync(stream, metadata, cancellationToken);
        var flags = BinaryPrimitives.ReadUInt64BigEndian(metadata);
        if ((flags & SessionPacketFlag) == 0)
            throw new InvalidDataException("scrcpy 视频流缺少初始尺寸元数据。");
        var width = BinaryPrimitives.ReadInt32BigEndian(metadata.AsSpan(4, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(metadata.AsSpan(8, 4));
        return ValidateFrameSize(width, height);
    }

    private async Task ReadVideoPacketsAsync(
        Stream stream,
        ChannelWriter<ScrcpyVideoPacket> writer,
        CancellationToken cancellationToken)
    {
        var metadata = new byte[12];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await ReadExactlyAsync(stream, metadata, cancellationToken);
                var ptsAndFlags = BinaryPrimitives.ReadUInt64BigEndian(metadata);
                if ((ptsAndFlags & SessionPacketFlag) != 0)
                {
                    var size = ValidateFrameSize(
                        BinaryPrimitives.ReadInt32BigEndian(metadata.AsSpan(4, 4)),
                        BinaryPrimitives.ReadInt32BigEndian(metadata.AsSpan(8, 4)));
                    FrameSize = size;
                    FrameSizeChanged?.Invoke(size);
                    continue;
                }

                var packetSize = BinaryPrimitives.ReadInt32BigEndian(metadata.AsSpan(8, 4));
                if (packetSize <= 0 || packetSize > 16 * 1024 * 1024)
                    throw new InvalidDataException($"scrcpy 视频包大小无效：{packetSize}。");
                var data = GC.AllocateUninitializedArray<byte>(packetSize);
                await ReadExactlyAsync(stream, data, cancellationToken);
                Interlocked.Add(ref _receivedBytes, packetSize);

                if ((ptsAndFlags & ConfigPacketFlag) != 0)
                {
                    _codecConfig = data;
                    continue;
                }

                var keyFrame = (ptsAndFlags & KeyFramePacketFlag) != 0;
                var ptsUs = checked((long)(ptsAndFlags & ~PacketFlagsMask));
                _firstPresentationTimestampUs ??= ptsUs;
                var normalizedUs = Math.Max(0, ptsUs - _firstPresentationTimestampUs.Value);
                if (keyFrame && _codecConfig is { Length: > 0 } config)
                {
                    var combined = GC.AllocateUninitializedArray<byte>(config.Length + data.Length);
                    Buffer.BlockCopy(config, 0, combined, 0, config.Length);
                    Buffer.BlockCopy(data, 0, combined, config.Length, data.Length);
                    data = combined;
                }

                Interlocked.Increment(ref _receivedFrames);
                if (ReceivedFrames == 1)
                    Log(_sessionId, "frame.first", $"bytes={data.Length}; key={keyFrame}; pts_us={ptsUs}");
                await writer.WriteAsync(
                    new ScrcpyVideoPacket(data, normalizedUs, keyFrame),
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log(_sessionId, "stream.fault", ex.ToString());
            writer.TryComplete(ex);
            Faulted?.Invoke(ex.Message);
            return;
        }
        writer.TryComplete();
    }

    private async Task WriteControlMessageAsync(byte[] message, CancellationToken cancellationToken)
    {
        var stream = _controlStream;
        if (stream is null)
            return;
        await _controlWriteLock.WaitAsync(cancellationToken);
        try
        {
            await stream.WriteAsync(message, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _controlWriteLock.Release();
        }
    }

    private static Process StartServerProcess(string deviceId, int scid, string deviceServerPath, ScrcpyVideoOptions options)
    {
        var startInfo = CreateAdbStartInfo();
        foreach (var argument in new[]
        {
            "-s", deviceId, "shell",
            $"CLASSPATH={deviceServerPath}", "app_process", "/", "com.genymobile.scrcpy.Server", UpstreamVersion,
            $"scid={scid:x8}", "log_level=info", "audio=false", "video=true", "control=true",
            "video_codec=h264", $"max_size={options.MaxSize}", $"video_bit_rate={options.BitRate}",
            $"max_fps={options.FrameRate}", "tunnel_forward=true", "send_device_meta=false",
            "send_dummy_byte=true", "send_stream_meta=true", "send_frame_meta=true",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }
        return Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 scrcpy Android 服务端。");
    }

    private static async Task<TcpClient> ConnectWithRetryAsync(int port, Process serverProcess, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        Exception? lastError = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(8))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (serverProcess.HasExited)
                throw new InvalidOperationException($"scrcpy 服务端提前退出，退出码 {serverProcess.ExitCode}。");
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync("127.0.0.1", port, cancellationToken);
                return client;
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                lastError = ex;
                client.Dispose();
                await Task.Delay(80, cancellationToken);
            }
        }
        throw new IOException("8 秒内未连接到 scrcpy 本地 socket。", lastError);
    }

    private static async Task<TcpClient> ConnectFirstSocketWithRetryAsync(
        int port,
        Process serverProcess,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        Exception? lastError = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(8))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (serverProcess.HasExited)
                throw new InvalidOperationException($"scrcpy 服务端提前退出，退出码 {serverProcess.ExitCode}。");

            var client = new TcpClient { NoDelay = true };
            try
            {
                await client.ConnectAsync("127.0.0.1", port, cancellationToken);
                using var handshakeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                handshakeCancellation.CancelAfter(TimeSpan.FromSeconds(1));
                var dummy = new byte[1];
                var read = await client.GetStream().ReadAsync(dummy, handshakeCancellation.Token);
                if (read == 1 && dummy[0] == 0)
                    return client;
                lastError = new EndOfStreamException("scrcpy 首连接在握手前被 ADB 关闭。");
            }
            catch (Exception ex) when (
                ex is SocketException or IOException ||
                ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                lastError = ex;
            }

            client.Dispose();
            await Task.Delay(80, cancellationToken);
        }

        throw new IOException("8 秒内未完成 scrcpy 首连接握手。", lastError);
    }

    private static ScrcpyFrameSize ValidateFrameSize(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > ushort.MaxValue || height > ushort.MaxValue)
            throw new InvalidDataException($"scrcpy 返回了无效画面尺寸：{width}×{height}。");
        return new ScrcpyFrameSize(width, height);
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
                throw new EndOfStreamException("scrcpy 视频连接已关闭。");
            offset += read;
        }
    }

    private async Task<string> ReadServerOutputAsync(TimeSpan wait)
    {
        try
        {
            var tasks = new[] { _serverStdoutTask, _serverStderrTask }.Where(task => task is not null).Cast<Task<string>>().ToArray();
            if (tasks.Length == 0)
                return string.Empty;

            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(wait));
            var output = new List<string>(2);
            foreach (var task in tasks)
            {
                if (task.IsCompletedSuccessfully)
                    output.Add(await task);
            }
            return string.Join(Environment.NewLine, output.Where(value => !string.IsNullOrWhiteSpace(value)));
        }
        catch
        {
        }
        return string.Empty;
    }

    private static void Log(string? sessionId, string stage, string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ADBControl",
                "logs");
            Directory.CreateDirectory(directory);
            var normalized = message.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            var entry = $"[{DateTimeOffset.Now:O}] [{sessionId ?? "none"}] {stage}{Environment.NewLine}{normalized}{Environment.NewLine}";
            lock (LogSync)
                File.AppendAllText(Path.Combine(directory, "scrcpy.log"), entry);
        }
        catch
        {
        }
    }

    private static string? ResolveServerPath()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "scrcpy", "scrcpy-server-v4.0"),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "..", "third_party", "scrcpy", "server", "build", "outputs", "apk", "release", "server-release-unsigned.apk")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "third_party", "scrcpy", "server", "build", "outputs", "apk", "release", "server-release-unsigned.apk")),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private async Task<string?> ResolveInstalledCompanionApkPathAsync(string deviceId)
    {
        var result = await _adb.ShellAsync(deviceId, $"pm path {CompanionAppService.PackageName}");
        if (!result.Success)
            return null;
        return result.Stdout
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("package:", StringComparison.Ordinal))
            ?["package:".Length..];
    }

    private static ProcessStartInfo CreateAdbStartInfo()
        => new()
        {
            FileName = "adb",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

    private static async Task<AdbCommandResult> RunAdbAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        var startInfo = CreateAdbStartInfo();
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 adb。");
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedSource.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(linkedSource.Token);
        try
        {
            await process.WaitForExitAsync(linkedSource.Token);
            return new AdbCommandResult(process.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            return new AdbCommandResult(-1, string.Empty, "adb 命令执行超时。");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
