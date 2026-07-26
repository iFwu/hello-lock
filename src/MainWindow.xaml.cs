using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Forms;

namespace HelloLock;

public partial class MainWindow : Window
{
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectReorder = 0x8004;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;
    private const int ObjIdWindow = 0;
    private const int ChildIdSelf = 0;

    private readonly KeyboardHook _keyboardHook = new();
    private readonly MouseHook _mouseHook = new();
    private readonly DispatcherTimer _credentialForegroundTimer;
    private readonly WinEventDelegate _winEventDelegate;
    private IntPtr _foregroundEventHook;
    private IntPtr _objectEventHook;
    private IntPtr _overlayHandle;
    private IntPtr _credentialUiWindow;
    private int _zOrderReassertPending;
    private bool _unlocking;
    private bool _unlocked;
    private bool _credentialUiForeground;

    public MainWindow()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Closing += OnClosing;
        MouseDown += (_, _) => TryUnlock();
        _keyboardHook.KeyPressed += () => Dispatcher.BeginInvoke(TryUnlock);
        _keyboardHook.CanPassAuthenticationInput = () => Volatile.Read(ref _credentialUiForeground);
        _mouseHook.PointerPressed += () => Dispatcher.BeginInvoke(TryUnlock);
        _winEventDelegate = OnWinEvent;

        _credentialForegroundTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _credentialForegroundTimer.Tick += (_, _) =>
        {
            IntPtr credentialUiWindow = IntPtr.Zero;
            bool credentialUiForeground =
                _unlocking && CredUiAuthenticator.TryGetCredentialUiForegroundWindow(
                    out credentialUiWindow);
            bool wasForeground = Volatile.Read(ref _credentialUiForeground);
            if (credentialUiForeground && !wasForeground)
            {
                Volatile.Write(ref _credentialUiForeground, true);
                StartZOrderEventGuard(credentialUiWindow);
                ReassertOverlayTopmost();
            }
            else if (credentialUiForeground)
            {
                _credentialUiWindow = credentialUiWindow;
            }
            else if (!credentialUiForeground && wasForeground)
            {
                Volatile.Write(ref _credentialUiForeground, false);
                StopZOrderEventGuard();
            }
        };
        ApplyText();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _overlayHandle = new WindowInteropHelper(this).Handle;
        // 覆盖整个虚拟桌面（所有显示器）
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        Topmost = true;
        Activate();
        Dispatcher.BeginInvoke(
            PositionCardOnPrimaryScreen,
            DispatcherPriority.Loaded);
        try
        {
            _keyboardHook.Install();
            _mouseHook.Install();
        }
        catch (Exception ex)
        {
            _unlocked = true;
            _mouseHook.Dispose();
            _keyboardHook.Dispose();
            Close();
            System.Windows.MessageBox.Show(
                Localization.Format("Lock.HookFailed", ex.Message),
                "HelloLock",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // 没解锁前拦掉 Alt+F4 / 程序化关闭
        if (!_unlocked)
        {
            e.Cancel = true;
            TryUnlock();
        }
    }

    private void OnUnlockClick(object sender, RoutedEventArgs e) => TryUnlock();

    private async void TryUnlock()
    {
        if (_unlocking || _unlocked) return;
        _unlocking = true;
        _credentialUiForeground = false;
        _credentialForegroundTimer.Start();
        StatusText.Text = "";

        var lockHandle = new WindowInteropHelper(this).Handle;

        try
        {
            var (outcome, detail) =
                await CredUiAuthenticator.TryVerifyCurrentUserAsync(lockHandle);
            switch (outcome)
            {
                case AuthOutcome.Verified:
                    Unlock();
                    return;

                case AuthOutcome.HelloUnavailable:
                    StatusText.Text = detail;
                    Relock();
                    return;

                case AuthOutcome.Failed:
                default:
                    StatusText.Text = detail;
                    Relock();
                    return;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = Localization.Format("Lock.Error", ex.Message);
            Relock();
        }
        finally
        {
            _credentialForegroundTimer.Stop();
            Volatile.Write(ref _credentialUiForeground, false);
            StopZOrderEventGuard();
            _unlocking = false;
        }
    }

    private void Unlock()
    {
        _unlocked = true;
        _credentialForegroundTimer.Stop();
        StopZOrderEventGuard();
        _mouseHook.Dispose();
        _keyboardHook.Dispose();
        Close();
    }

    private void Relock()
    {
        StopZOrderEventGuard();
        Topmost = true;
        Activate();
    }

    private void StartZOrderEventGuard(IntPtr credentialUiWindow)
    {
        StopZOrderEventGuard();
        _credentialUiWindow = credentialUiWindow;
        uint flags = WineventOutOfContext | WineventSkipOwnProcess;
        _foregroundEventHook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            IntPtr.Zero,
            _winEventDelegate,
            0,
            0,
            flags);
        _objectEventHook = SetWinEventHook(
            EventObjectShow,
            EventObjectReorder,
            IntPtr.Zero,
            _winEventDelegate,
            0,
            0,
            flags);
    }

    private void StopZOrderEventGuard()
    {
        if (_foregroundEventHook != IntPtr.Zero)
        {
            UnhookWinEvent(_foregroundEventHook);
            _foregroundEventHook = IntPtr.Zero;
        }
        if (_objectEventHook != IntPtr.Zero)
        {
            UnhookWinEvent(_objectEventHook);
            _objectEventHook = IntPtr.Zero;
        }
        _credentialUiWindow = IntPtr.Zero;
        Interlocked.Exchange(ref _zOrderReassertPending, 0);
    }

    private void OnWinEvent(
        IntPtr hook,
        uint eventType,
        IntPtr window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        if (window == IntPtr.Zero || window == _overlayHandle || window == _credentialUiWindow)
            return;
        if (!Volatile.Read(ref _credentialUiForeground)) return;
        bool relevant = eventType == EventSystemForeground ||
            ((eventType == EventObjectShow || eventType == EventObjectReorder) &&
                objectId == ObjIdWindow && childId == ChildIdSelf);
        if (!relevant) return;
        if (Interlocked.Exchange(ref _zOrderReassertPending, 1) != 0) return;

        Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _zOrderReassertPending, 0);
            if (Volatile.Read(ref _credentialUiForeground))
                ReassertOverlayTopmost();
        }, DispatcherPriority.Send);
    }

    private void ReassertOverlayTopmost()
    {
        if (_overlayHandle == IntPtr.Zero) return;
        SetWindowPos(
            _overlayHandle,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private void ApplyText()
    {
        TitleText.Text = Localization.Get("Lock.Title");
        HintText.Text = Localization.Get("Lock.Hint");
        UnlockButton.Content = Localization.Get("Lock.Verify");
    }

    private void PositionCardOnPrimaryScreen()
    {
        var primary = Screen.PrimaryScreen?.Bounds;
        if (primary is null)
        {
            LockCard.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            LockCard.VerticalAlignment = System.Windows.VerticalAlignment.Center;
            return;
        }

        var topLeft = PointFromScreen(new System.Windows.Point(
            primary.Value.Left,
            primary.Value.Top));
        var bottomRight = PointFromScreen(new System.Windows.Point(
            primary.Value.Right,
            primary.Value.Bottom));
        double primaryWidth = bottomRight.X - topLeft.X;
        double primaryHeight = bottomRight.Y - topLeft.Y;

        LockCard.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        double cardWidth = LockCard.DesiredSize.Width;
        double cardHeight = LockCard.DesiredSize.Height;
        double left = topLeft.X + Math.Max(24, (primaryWidth - cardWidth) / 2);
        double top = topLeft.Y + Math.Max(24, (primaryHeight - cardHeight) / 2);
        LockCard.Margin = new Thickness(left, top, 0, 0);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    private delegate void WinEventDelegate(
        IntPtr hook,
        uint eventType,
        IntPtr window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr module,
        WinEventDelegate callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);
}
