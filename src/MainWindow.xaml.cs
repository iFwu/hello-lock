using System;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Forms;

namespace HelloLock;

public partial class MainWindow : Window
{
    private readonly KeyboardHook _keyboardHook = new();
    private readonly MouseHook _mouseHook = new();
    private readonly DispatcherTimer _credentialForegroundTimer;
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

        _credentialForegroundTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _credentialForegroundTimer.Tick += (_, _) =>
        {
            Volatile.Write(
                ref _credentialUiForeground,
                _unlocking && CredUiAuthenticator.IsCredentialUiForeground());
        };
        ApplyText();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
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
            _credentialUiForeground = false;
            _unlocking = false;
        }
    }

    private void Unlock()
    {
        _unlocked = true;
        _credentialForegroundTimer.Stop();
        _mouseHook.Dispose();
        _keyboardHook.Dispose();
        Close();
    }

    private void Relock()
    {
        Topmost = true;
        Activate();
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
}
