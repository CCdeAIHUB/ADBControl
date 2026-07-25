using ADBControl.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinRT;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace ADBControl.Desktop.Controls;

public sealed class NativeVideoSwapChainRenderer : IDisposable
{
    private static readonly FeatureLevel[] s_featureLevels =
    [
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0,
    ];

    private readonly SwapChainPanel _panel;
    private readonly object _gate = new();
    private readonly TaskCompletionSource<bool> _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ID3D11Device1 _device;
    private readonly ID3D11DeviceContext1 _deviceContext;
    private readonly IDXGIFactory2 _factory;
    private readonly IDXGISwapChain3 _swapChain;
    private readonly Vortice.WinUI.ISwapChainPanelNative _panelNative;
    private bool _disposed;
    private int _rendering;
    private uint _pixelWidth;
    private uint _pixelHeight;
    private long _presentedFrameCount;
    private long _droppedFrameCount;

    private sealed record RendererResources(
        ID3D11Device1 Device,
        ID3D11DeviceContext1 DeviceContext,
        IDXGISwapChain3 SwapChain);

    public NativeVideoSwapChainRenderer(SwapChainPanel panel)
    {
        _panel = panel;
        _factory = CreateDXGIFactory1<IDXGIFactory2>();
        var panelNativeResult = ((IWinRTObject)_panel).NativeObject.TryAs(
            typeof(Vortice.WinUI.ISwapChainPanelNative).GUID,
            out var panelNativeHandle);
        Marshal.ThrowExceptionForHR(panelNativeResult);
        _panelNative = new Vortice.WinUI.ISwapChainPanelNative(panelNativeHandle);
        (_pixelWidth, _pixelHeight) = GetPanelPixelSize();
        var description = new SwapChainDescription1
        {
            Width = _pixelWidth,
            Height = _pixelHeight,
            Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = SampleDescription.Default,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Ignore,
            Flags = SwapChainFlags.None,
        };

        var failures = new List<string>();
        RendererResources? resources = null;
        string? adapterName = null;
        for (uint index = 0; ; index++)
        {
            var enumResult = _factory.EnumAdapters1(index, out var adapter);
            if (enumResult.Failure)
                break;
            using (adapter)
            {
                var adapterDescription = adapter.Description1;
                var name = string.IsNullOrWhiteSpace(adapterDescription.Description)
                    ? $"DXGI adapter {index}"
                    : adapterDescription.Description.Trim();
                var outputResult = adapter.EnumOutputs(0, out var output);
                output?.Dispose();
                if (outputResult.Failure)
                {
                    failures.Add($"{name}: skipped because it has no desktop output");
                    continue;
                }
                try
                {
                    var candidate = CreateRendererResources(_factory, adapter.NativePointer, DriverType.Unknown, description);
                    try
                    {
                        _panelNative.SetSwapChain(candidate.SwapChain).CheckError();
                        resources = candidate;
                        adapterName = name;
                        break;
                    }
                    catch
                    {
                        DisposeRendererResources(candidate);
                        throw;
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{name}: {DescribeFailure(ex)}");
                }
            }
        }

        if (resources is null)
        {
            try
            {
                var candidate = CreateRendererResources(_factory, IntPtr.Zero, DriverType.Warp, description);
                try
                {
                    _panelNative.SetSwapChain(candidate.SwapChain).CheckError();
                    resources = candidate;
                    adapterName = "Microsoft WARP";
                }
                catch
                {
                    DisposeRendererResources(candidate);
                    throw;
                }
            }
            catch (Exception ex)
            {
                failures.Add($"Microsoft WARP: {DescribeFailure(ex)}");
                _panelNative.Dispose();
                _factory.Dispose();
                throw new InvalidOperationException(
                    $"所有 DXGI 渲染适配器均无法创建 WinUI 交换链。{string.Join(" | ", failures)}",
                    ex);
            }
        }

        _device = resources.Device;
        _deviceContext = resources.DeviceContext;
        _swapChain = resources.SwapChain;
        AdapterName = adapterName ?? "Unknown";
        InitializationFailures = failures;

        try
        {
            UpdateCompositionTransform();
        }
        catch
        {
            _panelNative.SetSwapChain(null);
            DisposeRendererResources(resources);
            _panelNative.Dispose();
            _factory.Dispose();
            throw;
        }
        _panel.SizeChanged += OnPanelSizeChanged;
        _panel.CompositionScaleChanged += OnPanelCompositionScaleChanged;
    }

    public event Action<string>? Faulted;

    public string AdapterName { get; }

    public IReadOnlyList<string> InitializationFailures { get; }

    public long PresentedFrameCount => Interlocked.Read(ref _presentedFrameCount);

    public long DroppedFrameCount => Interlocked.Read(ref _droppedFrameCount);

    public (int Width, int Height) TargetPixelSize
    {
        get
        {
            lock (_gate)
                return (checked((int)_pixelWidth), checked((int)_pixelHeight));
        }
    }

    public bool RenderBgraFrame(ScrcpyDecodedFrame frame)
    {
        if (Interlocked.Exchange(ref _rendering, 1) != 0)
        {
            Interlocked.Increment(ref _droppedFrameCount);
            return false;
        }

        try
        {
            lock (_gate)
            {
                if (_disposed)
                    return false;
                if (frame.Width != checked((int)_pixelWidth) || frame.Height != checked((int)_pixelHeight))
                {
                    Interlocked.Increment(ref _droppedFrameCount);
                    return false;
                }
                var expectedLength = checked(frame.Width * frame.Height * 4);
                if (frame.Bgra.Length < expectedLength)
                    throw new InvalidDataException($"BGRA 帧长度无效：{frame.Bgra.Length}/{expectedLength}。");

                var index = _swapChain.CurrentBackBufferIndex;
                using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(index);
                _deviceContext.UpdateSubresource(
                    frame.Bgra.AsSpan(0, expectedLength),
                    backBuffer,
                    0,
                    checked((uint)(frame.Width * 4)),
                    0);
                _swapChain.Present(0, PresentFlags.None).CheckError();
                Interlocked.Increment(ref _presentedFrameCount);
                _firstFrame.TrySetResult(true);
                return true;
            }
        }
        catch (Exception ex)
        {
            _firstFrame.TrySetException(ex);
            Faulted?.Invoke(ex.Message);
            return false;
        }
        finally
        {
            Volatile.Write(ref _rendering, 0);
        }
    }

    public async Task<bool> WaitForFirstPresentedFrameAsync(TimeSpan timeout)
    {
        try
        {
            return await _firstFrame.Task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;

            _panel.SizeChanged -= OnPanelSizeChanged;
            _panel.CompositionScaleChanged -= OnPanelCompositionScaleChanged;
            _panelNative.SetSwapChain(null);
            _panelNative.Dispose();
            _swapChain.Dispose();
            _deviceContext.Flush();
            _deviceContext.Dispose();
            _device.Dispose();
            _factory.Dispose();
        }
    }

    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs args)
        => ResizeIfNeeded();

    private void OnPanelCompositionScaleChanged(SwapChainPanel sender, object args)
        => ResizeIfNeeded();

    private void ResizeIfNeeded()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            var (width, height) = GetPanelPixelSize();
            UpdateCompositionTransform();
            if (width == _pixelWidth && height == _pixelHeight)
                return;

            _deviceContext.Flush();
            _swapChain.ResizeBuffers(2, width, height, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
            _pixelWidth = width;
            _pixelHeight = height;
        }
    }

    private void UpdateCompositionTransform()
    {
        _swapChain.MatrixTransform = new Matrix3x2
        {
            M11 = 1.0f / Math.Max(0.01f, _panel.CompositionScaleX),
            M22 = 1.0f / Math.Max(0.01f, _panel.CompositionScaleY),
        };
    }

    private (uint Width, uint Height) GetPanelPixelSize()
    {
        var logicalWidth = _panel.ActualWidth > 1
            ? _panel.ActualWidth
            : double.IsFinite(_panel.Width) && _panel.Width > 1 ? _panel.Width : 1;
        var logicalHeight = _panel.ActualHeight > 1
            ? _panel.ActualHeight
            : double.IsFinite(_panel.Height) && _panel.Height > 1 ? _panel.Height : 1;
        var width = Math.Max(1.0, logicalWidth * Math.Max(0.01f, _panel.CompositionScaleX));
        var height = Math.Max(1.0, logicalHeight * Math.Max(0.01f, _panel.CompositionScaleY));
        return ((uint)Math.Ceiling(width), (uint)Math.Ceiling(height));
    }

    private static RendererResources CreateRendererResources(
        IDXGIFactory2 factory,
        IntPtr adapter,
        DriverType driverType,
        SwapChainDescription1 description)
    {
        ID3D11Device? baseDevice = null;
        ID3D11DeviceContext? baseContext = null;
        ID3D11Device1? device = null;
        ID3D11DeviceContext1? deviceContext = null;
        IDXGISwapChain3? swapChain = null;
        try
        {
            // Frames are decoded by FFmpeg and uploaded as BGRA textures. Requesting
            // D3D11 video support rejects WARP/basic display adapters even though the
            // renderer never uses the Direct3D video APIs.
            var flags = DeviceCreationFlags.BgraSupport;
            var createResult = D3D11CreateDevice(
                adapter,
                driverType,
                flags,
                s_featureLevels,
                out baseDevice,
                out _,
                out baseContext);
            createResult.CheckError();
            device = baseDevice.QueryInterface<ID3D11Device1>();
            deviceContext = baseContext.QueryInterface<ID3D11DeviceContext1>();
            using var temporarySwapChain = factory.CreateSwapChainForComposition(device, description, null);
            swapChain = temporarySwapChain.QueryInterface<IDXGISwapChain3>();
            return new RendererResources(device, deviceContext, swapChain);
        }
        catch
        {
            swapChain?.Dispose();
            deviceContext?.Dispose();
            device?.Dispose();
            throw;
        }
        finally
        {
            baseContext?.Dispose();
            baseDevice?.Dispose();
        }
    }

    private static string DescribeFailure(Exception exception)
        => $"{exception.Message.Trim()} (HRESULT 0x{unchecked((uint)exception.HResult):X8})";

    private static void DisposeRendererResources(RendererResources resources)
    {
        resources.SwapChain.Dispose();
        resources.DeviceContext.Dispose();
        resources.Device.Dispose();
    }
}
