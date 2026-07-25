using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace ADBControl.Desktop.Controls;

public static class WindowMinimumSize
{
    public static IDisposable Attach(Window window, int minimumWidth, int minimumHeight)
    {
        var handle = WindowNative.GetWindowHandle(window);
        if (handle == IntPtr.Zero)
            return EmptyDisposable.Instance;
        return new MinimumSizeHook(handle, minimumWidth, minimumHeight);
    }

    private sealed class MinimumSizeHook : IDisposable
    {
        private const uint WmGetMinMaxInfo = 0x0024;
        private readonly IntPtr _handle;
        private readonly int _minimumWidth;
        private readonly int _minimumHeight;
        private readonly SubclassProc _callback;
        private readonly nuint _id;
        private bool _attached;

        public MinimumSizeHook(IntPtr handle, int minimumWidth, int minimumHeight)
        {
            _handle = handle;
            _minimumWidth = minimumWidth;
            _minimumHeight = minimumHeight;
            _callback = WindowProc;
            _id = unchecked((nuint)handle.ToInt64());
            _attached = SetWindowSubclass(_handle, _callback, _id, 0);
        }

        public void Dispose()
        {
            if (!_attached)
                return;
            RemoveWindowSubclass(_handle, _callback, _id);
            _attached = false;
        }

        private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data)
        {
            var result = DefSubclassProc(hwnd, message, wParam, lParam);
            if (message != WmGetMinMaxInfo || lParam == IntPtr.Zero)
                return result;

            var dpi = GetDpiForWindow(hwnd);
            if (dpi == 0)
                dpi = 96;
            var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            info.MinimumTrackSize.X = Math.Max(info.MinimumTrackSize.X, (int)Math.Ceiling(_minimumWidth * dpi / 96d));
            info.MinimumTrackSize.Y = Math.Max(info.MinimumTrackSize.Y, (int)Math.Ceiling(_minimumHeight * dpi / 96d));
            Marshal.StructureToPtr(info, lParam, false);
            return result;
        }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static EmptyDisposable Instance { get; } = new();
        public void Dispose()
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinimumTrackSize;
        public NativePoint MaximumTrackSize;
    }

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc callback, nuint id, nuint data);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc callback, nuint id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
