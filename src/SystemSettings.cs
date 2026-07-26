using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace HelloLock;

public sealed record SystemSettingsSnapshot(int IdleMinutes, bool StartTrayAtLogin);

public static class SystemSettings
{
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
        if (!File.Exists(InstalledExe))
            throw new InvalidOperationException(Localization.Get("Settings.NotInstalled"));
        int idleMinutes = UserSettingsStore.Load().IdleMinutes;
        dynamic? task = TryGetTrayTask();
        bool startTray = task is not null && task.Enabled;
        return new SystemSettingsSnapshot(idleMinutes, startTray);
    }

    public static void Save(int idleMinutes, bool startTrayAtLogin)
    {
        if (!File.Exists(InstalledExe))
            throw new InvalidOperationException(Localization.Get("Settings.NotInstalled"));

        dynamic task = TryGetTrayTask()
            ?? throw new InvalidOperationException(Localization.Get("Settings.TaskMissing"));
        task.Enabled = startTrayAtLogin;

        var settings = UserSettingsStore.Load();
        settings.IdleMinutes = idleMinutes;
        UserSettingsStore.Save(settings);
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

}
