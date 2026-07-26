using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace HelloLock;

public sealed record SystemSettingsSnapshot(int IdleMinutes, bool GuardEnabled);

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
        var settings = UserSettingsStore.Load();
        return new SystemSettingsSnapshot(settings.IdleMinutes, settings.GuardEnabled);
    }

    public static void Save(int idleMinutes, bool guardEnabled)
    {
        if (!File.Exists(InstalledExe))
            throw new InvalidOperationException(Localization.Get("Settings.NotInstalled"));

        // 1) settings.json 是行为真相源：托盘进程读它、监视它。
        var settings = UserSettingsStore.Load();
        settings.IdleMinutes = idleMinutes;
        settings.GuardEnabled = guardEnabled;
        UserSettingsStore.Save(settings);

        // 2) 同步开机自启计划任务（best-effort：任务缺失不阻断行为设置）。
        dynamic? task = TryGetTrayTask();
        if (task is not null) task.Enabled = guardEnabled;

        // 3) 对齐运行态：开 → 立刻拉起守护；关 → 交给托盘自我退出。
        if (guardEnabled)
            TrayController.StartAgentIfNotRunning();
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
