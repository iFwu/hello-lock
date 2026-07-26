using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace HelloLock;

public sealed class TrayController : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _lockItem;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly ToolStripMenuItem _exitItem;
    private readonly Icon _trayIcon;

    public TrayController(Action lockNow, Action openSettings, Action exit)
    {
        _menu = new ContextMenuStrip();
        _lockItem = new ToolStripMenuItem(null, null, (_, _) => TryLock(lockNow));
        _settingsItem = new ToolStripMenuItem(null, null, (_, _) => openSettings());
        _exitItem = new ToolStripMenuItem(null, null, (_, _) => exit());
        _menu.Items.Add(_lockItem);
        _menu.Items.Add(_settingsItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_exitItem);

        _trayIcon = LoadTrayIcon();
        _notifyIcon = new NotifyIcon
        {
            Icon = _trayIcon,
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) TryLock(lockNow);
        };
        Localization.LanguageChanged += OnLanguageChanged;
        ApplyText();
    }

    private static Icon LoadTrayIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/HelloLockTray.ico"));
        if (resource?.Stream is null)
            return (Icon)SystemIcons.Application.Clone();

        using (resource.Stream)
        using (var sourceIcon = new Icon(resource.Stream))
        {
            return (Icon)sourceIcon.Clone();
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => ApplyText();

    private void ApplyText()
    {
        _lockItem.Text = Localization.Get("Tray.LockNow");
        _settingsItem.Text = Localization.Get("Tray.Settings");
        _exitItem.Text = Localization.Get("Tray.Exit");
        _notifyIcon.Text = Localization.Get("Tray.Tooltip");
    }

    private static void TryLock(Action lockNow)
    {
        try
        {
            lockNow();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Localization.Format("Tray.StartFailed", ex.Message),
                "HelloLock",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    public static void StartLockProcess()
    {
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine the HelloLock executable path.");

        // The tray runs from HelloLock.exe. Keep screen-saver activation in a
        // separate process so closing the lock window never exits the tray.
        Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = "/s",
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        });
    }

    public void Dispose()
    {
        Localization.LanguageChanged -= OnLanguageChanged;
        _notifyIcon.Visible = false;
        _menu.Dispose();
        _notifyIcon.Dispose();
        _trayIcon.Dispose();
    }
}
