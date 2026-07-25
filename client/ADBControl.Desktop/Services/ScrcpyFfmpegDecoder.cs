using FFmpeg.AutoGen;
using System.Runtime.InteropServices;

namespace ADBControl.Desktop.Services;

public sealed record ScrcpyDecodedFrame(byte[] Bgra, int Width, int Height, long TimestampUs);

public sealed unsafe class ScrcpyFfmpegDecoder : IDisposable
{
    private static readonly object s_initializationLock = new();
    private static bool s_initialized;

    private AVCodecContext* _codecContext;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private SwsContext* _scaleContext;
    private bool _disposed;

    public ScrcpyFfmpegDecoder()
    {
        EnsureInitialized();
        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
        if (codec == null)
            throw new InvalidOperationException("FFmpeg 未提供 H.264 解码器。");

        _codecContext = ffmpeg.avcodec_alloc_context3(codec);
        _packet = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
        if (_codecContext == null || _packet == null || _frame == null)
            throw new OutOfMemoryException("无法分配 FFmpeg H.264 解码资源。");

        _codecContext->thread_count = 1;
        var openResult = ffmpeg.avcodec_open2(_codecContext, codec, null);
        if (openResult < 0)
            throw CreateException("打开 H.264 解码器", openResult);
    }

    public IReadOnlyList<ScrcpyDecodedFrame> Decode(
        byte[] encodedData,
        long timestampUs,
        int outputWidth,
        int outputHeight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (encodedData.Length == 0 || outputWidth <= 0 || outputHeight <= 0)
            return [];

        ffmpeg.av_packet_unref(_packet);
        var allocateResult = ffmpeg.av_new_packet(_packet, encodedData.Length);
        if (allocateResult < 0)
            throw CreateException("分配 H.264 输入包", allocateResult);
        Marshal.Copy(encodedData, 0, (IntPtr)_packet->data, encodedData.Length);
        _packet->pts = timestampUs;
        _packet->dts = timestampUs;

        var sendResult = ffmpeg.avcodec_send_packet(_codecContext, _packet);
        if (sendResult < 0)
            throw CreateException("提交 H.264 输入包", sendResult);

        var decoded = new List<ScrcpyDecodedFrame>(1);
        while (true)
        {
            var receiveResult = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
            if (receiveResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receiveResult == ffmpeg.AVERROR_EOF)
                break;
            if (receiveResult < 0)
                throw CreateException("接收 H.264 解码帧", receiveResult);

            decoded.Add(ConvertFrame(timestampUs, outputWidth, outputHeight));
            ffmpeg.av_frame_unref(_frame);
        }
        return decoded;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_scaleContext != null)
        {
            ffmpeg.sws_freeContext(_scaleContext);
            _scaleContext = null;
        }
        if (_frame != null)
        {
            var frame = _frame;
            ffmpeg.av_frame_free(&frame);
            _frame = null;
        }
        if (_packet != null)
        {
            var packet = _packet;
            ffmpeg.av_packet_free(&packet);
            _packet = null;
        }
        if (_codecContext != null)
        {
            var codecContext = _codecContext;
            ffmpeg.avcodec_free_context(&codecContext);
            _codecContext = null;
        }
    }

    private ScrcpyDecodedFrame ConvertFrame(long timestampUs, int outputWidth, int outputHeight)
    {
        _scaleContext = ffmpeg.sws_getCachedContext(
            _scaleContext,
            _frame->width,
            _frame->height,
            (AVPixelFormat)_frame->format,
            outputWidth,
            outputHeight,
            AVPixelFormat.AV_PIX_FMT_BGRA,
            ffmpeg.SWS_FAST_BILINEAR,
            null,
            null,
            null);
        if (_scaleContext == null)
            throw new InvalidOperationException("FFmpeg 无法创建 BGRA 颜色转换器。");

        var bgra = GC.AllocateUninitializedArray<byte>(checked(outputWidth * outputHeight * 4));
        fixed (byte* destination = bgra)
        {
            var sourceData = new byte*[]
            {
                _frame->data[0],
                _frame->data[1],
                _frame->data[2],
                _frame->data[3],
            };
            var sourceLines = new[]
            {
                _frame->linesize[0],
                _frame->linesize[1],
                _frame->linesize[2],
                _frame->linesize[3],
            };
            var destinationData = new byte*[] { destination, null, null, null };
            var destinationLines = new[] { outputWidth * 4, 0, 0, 0 };
            var convertedRows = ffmpeg.sws_scale(
                _scaleContext,
                sourceData,
                sourceLines,
                0,
                _frame->height,
                destinationData,
                destinationLines);
            if (convertedRows != outputHeight)
                throw new InvalidOperationException($"FFmpeg 仅转换了 {convertedRows}/{outputHeight} 行视频数据。");
        }
        return new ScrcpyDecodedFrame(bgra, outputWidth, outputHeight, timestampUs);
    }

    private static void EnsureInitialized()
    {
        lock (s_initializationLock)
        {
            if (s_initialized)
                return;
            ffmpeg.RootPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
            _ = ffmpeg.avcodec_version();
            s_initialized = true;
        }
    }

    private static InvalidOperationException CreateException(string operation, int errorCode)
    {
        var buffer = stackalloc byte[1024];
        ffmpeg.av_strerror(errorCode, buffer, 1024);
        var message = Marshal.PtrToStringAnsi((IntPtr)buffer) ?? $"FFmpeg error {errorCode}";
        return new InvalidOperationException($"{operation}失败：{message}（{errorCode}）。");
    }
}
