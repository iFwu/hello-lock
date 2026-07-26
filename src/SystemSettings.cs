using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace HelloLock;

public sealed record SystemSettingsSnapshot(int IdleMinutes, bool StartTrayAtLogin);

public static class SystemSettings
{
    private const string DesktopKeyPath = @"Control Panel\Desktop";
    private const string TrayTaskDescription = "HelloLock tray launcher managed by iFwu/hello-lock";

    private static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        "HelloLock");

    private static string InstalledExe => Path.Combine(InstallDirectory, "HelloLock.exe");

    private static string TrayTaskName
    {
        get
        {
            string sid = WindowsIdentity.GetCurrent().User?.Value
                ?? throw new InvalidOperationException("Could not determine the current user SID.");
            return $"HelloLock Tray (iFwu, {sid})";
        }
    }

    public static SystemSettingsSnapshot Load()
    {
        using RegistryKey desktop = Registry.CurrentUser.OpenSubKey(DesktopKeyPath)
            ?? throw new InvalidOperationException(Localization.Get("Settings.NotInstalled"));

        bool active = string.Equals(desktop.GetValue("ScreenSaveActive")?.ToString(), "1", StringComparison.Ordinal);
        int seconds = int.TryParse(desktop.GetValue("ScreenSaveTimeOut")?.ToString(), out int parsed)
            ? parsed
            : 0;
        int idleMinutes = active && seconds > 0 ? Math.Max(1, seconds / 60) : 0;

        dynamic? task = TryGetTrayTask();
        bool startTray = task is not null && task.Enabled;
        return new SystemSettingsSnapshot(idleMinutes, startTray);
    }

    public static void Save(int idleMinutes, bool startTrayAtLogin)
    {
        if (!File.Exists(InstalledExe))
            throw new InvalidOperationException(Localization.Get("Settings.NotInstalled"));

        using RegistryKey desktop = Registry.CurrentUser.CreateSubKey(DesktopKeyPath, writable: true);
        desktop.SetValue("ScreenSaveActive", idleMinutes > 0 ? "1" : "0", RegistryValueKind.String);
        desktop.SetValue("ScreenSaveTimeOut", checked(idleMinutes * 60).ToString(), RegistryValueKind.String);

        dynamic task = TryGetTrayTask()
            ?? throw new InvalidOperationException(Localization.Get("Settings.TaskMissing"));
        task.Enabled = startTrayAtLogin;

        UpdateInstallState(idleMinutes);
        BroadcastDesktopSettingsChanged();
    }

    private static dynamic? TryGetTrayTask()
    {
        Type schedulerType = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException(Localization.Get("Settings.TaskSchedulerMissing"));
        dynamic scheduler = Activator.CreateInstance(schedulerType)
            ?? throw new InvalidOperationException(Localization.Get("Settings.TaskSchedulerConnectFailed"));
        scheduler.Connect();
        dynamic root = scheduler.GetFolder("\\");
        try
        {
            dynamic task = root.GetTask(TrayTaskName);
            if (!string.Equals((string)task.Definition.RegistrationInfo.Description, TrayTaskDescription, StringComparison.Ordinal))
                throw new InvalidOperationException(Localization.Get("Settings.TaskNotOwned"));
            return task;
        }
        catch (COMException ex) when ((uint)ex.HResult == 0x80070002)
        {
            return null;
        }
    }

    private static void UpdateInstallState(int idleMinutes)
    {
        string statePath = Path.Combine(InstallDirectory, "install-state.json");
        if (!File.Exists(statePath)) return;

        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(statePath));
        var root = document.RootElement;
        var applied = root.GetProperty("Applied");
        var state = new
        {
            SchemaVersion = root.GetProperty("SchemaVersion").GetInt32(),
            Applied = new System.Collections.Generic.Dictionary<string, string>
            {
                ["ScreenSaveActive"] = idleMinutes > 0 ? "1" : "0",
                ["ScreenSaveTimeOut"] = (idleMinutes * 60).ToString(),
                ["ScreenSaverIsSecure"] = applied.GetProperty("ScreenSaverIsSecure").GetString() ?? "0",
                ["SCRNSAVE.EXE"] = applied.GetProperty("SCRNSAVE.EXE").GetString() ?? Path.Combine(InstallDirectory, "HelloLock.scr"),
            },
            TrayTaskName = root.GetProperty("TrayTaskName").GetString(),
        };
        string temporaryPath = statePath + ".tmp";
        File.WriteAllText(temporaryPath, System.Text.Json.JsonSerializer.Serialize(state, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, statePath, overwrite: true);
    }

    private static void BroadcastDesktopSettingsChanged()
    {
        SendMessageTimeout(
            new IntPtr(0xffff),
            0x001A,
            IntPtr.Zero,
            "Control Panel\\Desktop",
            0x0002,
            5000,
            out _);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window,
        uint message,
        IntPtr wParam,
        string lParam,
        uint flags,
        uint timeout,
        out IntPtr result);
}
