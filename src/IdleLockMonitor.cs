using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows.Threading;
using Microsoft.Win32;

namespace HelloLock;

public sealed class IdleLockMonitor : IDisposable
{
    private const int GraceSeconds = 5;
    private readonly Action _lockNow;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly string _logPath;
    private DateTime _graceUntilUtc;
    private DateTime _settingsWriteTimeUtc;
    private int _idleMinutes;
    private bool _sessionLocked;
    private bool _thresholdReached;
    private bool _disposed;

    public IdleLockMonitor(Action lockNow)
    {
        _lockNow = lockNow;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _logPath = Path.Combine(UserSettingsStore.SettingsDirectory, "idle-monitor.log");
        ReloadSettings(force: true);
        _graceUntilUtc = DateTime.UtcNow.AddSeconds(GraceSeconds);
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _timer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            OnTick,
            _dispatcher);
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed) return;

        ReloadSettings(force: false);
        if (_sessionLocked || _idleMinutes <= 0 || DateTime.UtcNow < _graceUntilUtc) return;

        var info = new LastInputInfo
        {
            Size = (uint)Marshal.SizeOf<LastInputInfo>(),
        };
        if (!GetLastInputInfo(ref info))
        {
            WriteLog($"GetLastInputInfo failed: {Marshal.GetLastWin32Error()}");
            return;
        }

        uint now = unchecked((uint)Environment.TickCount);
        uint idleMilliseconds = unchecked(now - info.Time);
        uint thresholdMilliseconds = checked((uint)(_idleMinutes * 60_000));
        if (idleMilliseconds < thresholdMilliseconds)
        {
            _thresholdReached = false;
            return;
        }

        if (_thresholdReached) return;
        _thresholdReached = true;
        if (IsOverlayLockActive()) return;
        WriteLog($"Idle threshold reached: idle={idleMilliseconds / 1000}s threshold={_idleMinutes * 60}s");
        _lockNow();
    }

    private static bool IsOverlayLockActive()
    {
        string userSid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        try
        {
            if (!Mutex.TryOpenExisting($"Local\\HelloLock-{userSid}", out Mutex? mutex))
                return false;
            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private void ReloadSettings(bool force)
    {
        DateTime writeTime = File.Exists(UserSettingsStore.SettingsPath)
            ? File.GetLastWriteTimeUtc(UserSettingsStore.SettingsPath)
            : DateTime.MinValue;
        if (!force && writeTime == _settingsWriteTimeUtc) return;

        int previous = _idleMinutes;
        _idleMinutes = UserSettingsStore.Load().IdleMinutes;
        _settingsWriteTimeUtc = writeTime;
        _thresholdReached = false;
        _graceUntilUtc = DateTime.UtcNow.AddSeconds(GraceSeconds);
        if (force || previous != _idleMinutes)
            WriteLog($"Idle monitor configured: {_idleMinutes} minute(s)");
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            if (e.Reason == SessionSwitchReason.SessionLock)
            {
                _sessionLocked = true;
                _thresholdReached = true;
            }
            else if (e.Reason == SessionSwitchReason.SessionUnlock)
            {
                _sessionLocked = false;
                _thresholdReached = false;
                _graceUntilUtc = DateTime.UtcNow.AddSeconds(GraceSeconds);
            }
        });
    }

    private void WriteLog(string message)
    {
        try
        {
            Directory.CreateDirectory(UserSettingsStore.SettingsDirectory);
            File.AppendAllText(_logPath, $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never interrupt locking.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
}
