using System.Runtime.InteropServices;

namespace ADBControl.Desktop.Services;

public sealed class LowLevelMouseWheelEventArgs(int screenX, int screenY, int delta) : EventArgs
{
    public int ScreenX { get; } = screenX;
    public int ScreenY { get; } = screenY;
    public int Delta { get; } = delta;
}

public sealed class LowLevelMouseWheelInput : IDisposable
{
    private const int WhMouseLowLevel = 14;
    private readonly uint _processId = unchecked((uint)Environment.ProcessId);
    private LowLevelMouseProc? _hookProc;
    private IntPtr _hookHandle;

    public event EventHandler<LowLevelMouseWheelEventArgs>? Wheel;

    public bool Start()
    {
        if (_hookHandle != IntPtr.Zero)
            return true;

        _hookProc ??= HookCallback;
        _hookHandle = SetWindowsHookEx(WhMouseLowLevel, _hookProc, GetModuleHandle(null), 0);
        if (_hookHandle != IntPtr.Zero)
        {
            MouseWheelDiagnostics.Write("low-level-hook-installed", 0, "process-window", null, null, true);
            return true;
        }

        MouseWheelDiagnostics.Write(
            "low-level-hook-install-failed",
            0,
            "process-window",
            null,
            null,
            false,
            "MOUSE_WHEEL_LOW_LEVEL_HOOK_FAILED",
            $"Win32Error: {Marshal.GetLastWin32Error()}");
        return false;
    }

    public void Dispose()
    {
        if (_hookHandle == IntPtr.Zero)
            return;

        if (!UnhookWindowsHookEx(_hookHandle))
        {
            MouseWheelDiagnostics.Write(
                "low-level-hook-remove-failed",
                0,
                "process-window",
                null,
                null,
                false,
                "MOUSE_WHEEL_LOW_LEVEL_UNHOOK_FAILED",
                $"Win32Error: {Marshal.GetLastWin32Error()}");
        }
        _hookHandle = IntPtr.Zero;
    }

    private IntPtr HookCallback(int hookCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            var message = unchecked((uint)wParam.ToInt64());
            if (hookCode >= 0 && message == MouseWheelScrollPolicy.MouseWheelMessage && lParam != IntPtr.Zero)
            {
                var data = Marshal.PtrToStructure<MouseLowLevelHookData>(lParam);
                var delta = unchecked((short)((data.MouseData >> 16) & 0xffff));
                var targetWindow = WindowFromPoint(data.Point);
                _ = GetWindowThreadProcessId(targetWindow, out var targetProcessId);
                if (MouseWheelScrollPolicy.ShouldObserveLowLevelWheel(
                        hookCode,
                        message,
                        delta,
                        targetProcessId,
                        _processId))
                {
                    Wheel?.Invoke(this, new LowLevelMouseWheelEventArgs(data.Point.X, data.Point.Y, delta));
                }
            }
        }
        catch (Exception ex)
        {
            MouseWheelDiagnostics.Write(
                "low-level-callback-failed",
                0,
                "process-window",
                null,
                null,
                false,
                "MOUSE_WHEEL_LOW_LEVEL_CALLBACK_FAILED",
                ex.Message);
        }

        return CallNextHookEx(_hookHandle, hookCode, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseLowLevelHookData
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    private delegate IntPtr LowLevelMouseProc(int hookCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookId,
        LowLevelMouseProc hookProc,
        IntPtr moduleHandle,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hookHandle,
        int hookCode,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
