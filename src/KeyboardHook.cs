using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HelloLock;

/// <summary>
/// 低级键盘钩子 (WH_KEYBOARD_LL)：锁定时吞掉所有按键，
/// 顺带屏蔽 Alt+Tab / Win / Alt+Esc / Ctrl+Esc 等切换热键。
/// Ctrl+Alt+Del (SAS) is handled by the secure desktop and cannot be intercepted
/// by a user-mode hook. See the README security model.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookId = IntPtr.Zero;

    /// <summary>false 时钩子直接放行。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The caller supplies a cached foreground decision. The low-level callback
    /// must not perform I/O or cross-process inspection because Windows removes
    /// hooks that exceed LowLevelHooksTimeout.
    /// </summary>
    public Func<bool>? CanPassAuthenticationInput { get; set; }

    /// <summary>锁定期间任意按键触发，让 UI 弹 Hello。</summary>
    public event Action? KeyPressed;

    public KeyboardHook()
    {
        _proc = HookCallback;
    }

    public void Install()
    {
        if (_hookId != IntPtr.Zero) return;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc,
            GetModuleHandle(curModule.ModuleName), 0);
        if (_hookId == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Failed to install the low-level keyboard hook.");
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Enabled)
        {
            int msg = wParam.ToInt32();
            bool isKeyDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool isKeyMessage = isKeyDown || msg == WM_KEYUP || msg == WM_SYSKEYUP;
            if (!isKeyMessage)
                return CallNextHookEx(_hookId, nCode, wParam, lParam);

            int virtualKey = Marshal.ReadInt32(lParam);
            if (CanPassAuthenticationInput?.Invoke() == true && IsSafeAuthenticationKey(virtualKey))
                return CallNextHookEx(_hookId, nCode, wParam, lParam);

            if (isKeyDown)
            {
                KeyPressed?.Invoke();
            }
            // 返回 1 = 吞掉这个按键，不再往下传
            return (IntPtr)1;
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static bool IsSafeAuthenticationKey(int key)
    {
        // Letters, top-row digits, numpad digits and OEM punctuation cover
        // numeric/alphanumeric PINs without allowing Win/Alt/Ctrl shortcuts.
        if (key is >= 0x30 and <= 0x39) return true;
        if (key is >= 0x41 and <= 0x5A) return true;
        if (key is >= 0x60 and <= 0x6F) return true;
        if (key is >= 0xBA and <= 0xE2) return true;

        return key is
            0x08 or // Backspace
            0x09 or // Tab (Alt is always blocked, so Alt+Tab cannot escape)
            0x0D or // Enter
            0x10 or // Shift
            0x14 or // Caps Lock
            0x1B or // Escape/cancel
            0x20 or // Space
            0x23 or // End
            0x24 or // Home
            0x25 or // Left
            0x27 or // Right
            0x2E;   // Delete
    }

    public void Dispose()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
}
