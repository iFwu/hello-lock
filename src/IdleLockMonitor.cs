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
    private readonly Action _onGuardDisabled;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly string _logPath;
    private DateTime _graceUntilUtc;
    private DateTime _settingsWriteTimeUtc;
    private int _idleMinutes;
    private uint _baselineTick;
    private bool _sessionStateKnown;
    private bool _sessionStateUnavailableLogged;
    private bool _guardEnabled = true;
    private bool _guardShutdownRequested;
    private bool _sessionLocked;
    private bool _thresholdReached;
    private bool _disposed;

    public IdleLockMonitor(Action lockNow, Action onGuardDisabled)
    {
        _lockNow = lockNow;
        _onGuardDisabled = onGuardDisabled;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _logPath = Path.Combine(UserSettingsStore.SettingsDirectory, "idle-monitor.log");
        _baselineTick = unchecked((uint)Environment.TickCount);
        ReloadSettings(force: true);
        _graceUntilUtc = DateTime.UtcNow.AddSeconds(GraceSeconds);
        SystemEvents.SessionSwitch += OnSessionSwitch;
        RefreshSessionState();
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

        // 守护被关闭（设置页取消勾选）：托盘守护进程自己干净退出。
        if (!_guardEnabled)
        {
            if (!_guardShutdownRequested)
            {
                _guardShutdownRequested = true;
                WriteLog("Guard disabled via settings; stopping tray agent.");
                _onGuardDisabled();
            }
            return;
        }

        RefreshSessionState();
        if (!_sessionStateKnown ||
            _sessionLocked ||
            _idleMinutes <= 0 ||
            DateTime.UtcNow < _graceUntilUtc)
            return;

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
        uint idleMilliseconds = IdleLockPolicy.GetEffectiveIdleMilliseconds(
            now,
            info.Time,
            _baselineTick);
        if (!IdleLockPolicy.ShouldStartLock(
                _sessionStateKnown,
                _sessionLocked,
                _idleMinutes,
                now,
                info.Time,
                _baselineTick))
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
        var settings = UserSettingsStore.Load();
        _idleMinutes = settings.IdleMinutes;
        _guardEnabled = settings.GuardEnabled;
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
            if (e.Reason is SessionSwitchReason.SessionLock or
                SessionSwitchReason.SessionLogoff or
                SessionSwitchReason.ConsoleDisconnect or
                SessionSwitchReason.RemoteDisconnect)
            {
                ApplySessionState(locked: true);
            }
            else if (e.Reason is SessionSwitchReason.SessionUnlock or
                     SessionSwitchReason.SessionLogon or
                     SessionSwitchReason.ConsoleConnect or
                     SessionSwitchReason.RemoteConnect)
            {
                ApplySessionState(locked: false);
            }
        });
    }

    private void RefreshSessionState()
    {
        if (!WindowsSessionState.TryGetCurrentSessionLocked(out bool locked))
        {
            if (!_sessionStateUnavailableLogged)
            {
                _sessionStateUnavailableLogged = true;
                WriteLog("Windows session state unavailable; idle locking paused.");
            }
            return;
        }

        if (_sessionStateUnavailableLogged)
        {
            _sessionStateUnavailableLogged = false;
            WriteLog("Windows session state available; idle locking resumed.");
        }
        ApplySessionState(locked);
    }

    private void ApplySessionState(bool locked)
    {
        bool stateChanged = !_sessionStateKnown || _sessionLocked != locked;
        bool resetBaseline = !_sessionStateKnown || (_sessionLocked && !locked);
        _sessionStateKnown = true;
        _sessionLocked = locked;
        if (stateChanged)
            WriteLog($"Windows session state: {(locked ? "locked" : "unlocked")}");
        if (locked)
        {
            _thresholdReached = true;
            return;
        }

        if (!resetBaseline) return;
        _baselineTick = unchecked((uint)Environment.TickCount);
        _thresholdReached = false;
        _graceUntilUtc = DateTime.UtcNow.AddSeconds(GraceSeconds);
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
