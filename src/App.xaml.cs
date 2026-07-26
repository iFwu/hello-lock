using System;
using System.Security.Principal;
using System.Threading;
using System.Windows.Interop;
using System.Windows.Media;

namespace HelloLock;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private TrayController? _tray;
    private SettingsWindow? _settingsWindow;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        base.OnStartup(e);

        string mode = e.Args.Length == 0 ? "/lock" : e.Args[0].ToLowerInvariant();
        if (mode.StartsWith("/tray", StringComparison.Ordinal) ||
            mode.StartsWith("-tray", StringComparison.Ordinal))
        {
            StartTray();
            return;
        }

        if (mode.StartsWith("/c", StringComparison.Ordinal) ||
            mode.StartsWith("-c", StringComparison.Ordinal))
        {
            ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose;
            ShowSettings();
            return;
        }

        string userSid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        _singleInstance = new Mutex(
            initiallyOwned: true,
            name: $"Local\\HelloLock-{userSid}",
            createdNew: out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose;
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    private void StartTray()
    {
        string userSid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        _singleInstance = new Mutex(
            initiallyOwned: true,
            name: $"Local\\HelloLock-Tray-{userSid}",
            createdNew: out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        _tray = new TrayController(
            lockNow: TrayController.StartLockProcess,
            openSettings: ShowSettings,
            exit: Shutdown);
    }

    private void ShowSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        try
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            MainWindow = _settingsWindow;
            _settingsWindow.Show();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                ex.Message,
                "HelloLock",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            if (_tray is null) Shutdown();
        }
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _tray?.Dispose();
        if (_singleInstance is not null)
        {
            try
            {
                _singleInstance.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The process never acquired ownership.
            }
            _singleInstance.Dispose();
        }

        base.OnExit(e);
    }
}
