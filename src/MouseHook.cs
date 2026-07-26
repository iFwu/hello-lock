using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HelloLock;

/// <summary>
/// Low-level mouse hook that blocks ordinary pointer input while locked.
/// Mouse movement remains visible, but buttons and wheels are never passed to
/// desktop applications until the lock is removed.
/// </summary>
public sealed class MouseHook : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmLeftButtonDown = 0x0201;
    private const int WmLeftButtonUp = 0x0202;
    private const int WmLeftButtonDoubleClick = 0x0203;
    private const int WmRightButtonDown = 0x0204;
    private const int WmRightButtonUp = 0x0205;
    private const int WmRightButtonDoubleClick = 0x0206;
    private const int WmMiddleButtonDown = 0x0207;
    private const int WmMiddleButtonUp = 0x0208;
    private const int WmMiddleButtonDoubleClick = 0x0209;
    private const int WmMouseWheel = 0x020A;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;
    private const int WmXButtonDoubleClick = 0x020D;
    private const int WmMouseHorizontalWheel = 0x020E;

    private readonly LowLevelMouseProc _proc;
    private IntPtr _hookId = IntPtr.Zero;

    public bool Enabled { get; set; } = true;
    public event Action? PointerPressed;

    public MouseHook()
    {
        _proc = HookCallback;
    }

    public void Install()
    {
        if (_hookId != IntPtr.Zero) return;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule!;
        _hookId = SetWindowsHookEx(
            WhMouseLl,
            _proc,
            GetModuleHandle(module.ModuleName),
            0);
        if (_hookId == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Failed to install the low-level mouse hook.");
        }
    }

    private IntPtr HookCallback(int code, IntPtr message, IntPtr data)
    {
        if (code < 0 || !Enabled)
            return CallNextHookEx(_hookId, code, message, data);

        int value = message.ToInt32();
        if (!IsMouseMessage(value))
            return CallNextHookEx(_hookId, code, message, data);

        if (IsButtonDown(value))
            PointerPressed?.Invoke();

        return (IntPtr)1;
    }

    private static bool IsMouseMessage(int message) => message is
        WmLeftButtonDown or WmLeftButtonUp or WmLeftButtonDoubleClick or
        WmRightButtonDown or WmRightButtonUp or WmRightButtonDoubleClick or
        WmMiddleButtonDown or WmMiddleButtonUp or WmMiddleButtonDoubleClick or
        WmMouseWheel or WmMouseHorizontalWheel or
        WmXButtonDown or WmXButtonUp or WmXButtonDoubleClick;

    private static bool IsButtonDown(int message) => message is
        WmLeftButtonDown or WmRightButtonDown or
        WmMiddleButtonDown or WmXButtonDown;

    public void Dispose()
    {
        if (_hookId == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hookId);
        _hookId = IntPtr.Zero;
    }

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hook,
        LowLevelMouseProc callback,
        IntPtr module,
        uint threadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(
        IntPtr hook,
        int code,
        IntPtr message,
        IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string moduleName);
}
