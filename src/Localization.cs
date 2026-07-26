using System;
using System.Collections.Generic;
using System.Globalization;

namespace HelloLock;

public static class Localization
{
    private static readonly IReadOnlyDictionary<string, (string En, string Zh)> Strings =
        new Dictionary<string, (string En, string Zh)>(StringComparer.Ordinal)
        {
            ["Tray.LockNow"] = ("Lock now", "立即锁定"),
            ["Tray.Settings"] = ("Settings", "设置"),
            ["Tray.Exit"] = ("Exit tray", "退出托盘"),
            ["Tray.Tooltip"] = ("HelloLock - click to lock", "HelloLock - 单击立即锁定"),
            ["Tray.StartFailed"] = ("Unable to start HelloLock:\n{0}", "无法启动 HelloLock：\n{0}"),

            ["Lock.Title"] = ("Desktop locked", "桌面已锁定"),
            ["Lock.Hint"] = ("Click or press a key to verify", "点击或按任意键进行验证"),
            ["Lock.Verify"] = ("Verify with Windows credentials", "使用 Windows 凭据验证"),
            ["Lock.HookFailed"] = (
                "HelloLock could not install its input guard and did not lock the desktop.\n\n{0}",
                "HelloLock 无法安装输入保护，因此未锁定桌面。\n\n{0}"),
            ["Lock.Error"] = ("Error: {0}", "错误：{0}"),

            ["Credential.Prompt"] = ("Verify the current Windows user", "验证当前 Windows 用户"),
            ["Credential.Canceled"] = ("Windows credential verification was canceled.", "已取消 Windows 凭据验证。"),
            ["Credential.PromptFailed"] = ("Credential UI failed to start (Win32 error {0}).", "凭据界面启动失败（Win32 error {0}）。"),
            ["Credential.NoBuffer"] = ("Windows returned no credential data.", "Windows 未返回认证数据。"),
            ["Credential.Exception"] = ("Windows credential verification failed: {0}", "Windows 凭据验证异常：{0}"),
            ["Credential.LuidFailed"] = ("Could not allocate a logon identifier (Win32 error {0}).", "无法分配登录标识（Win32 error {0}）。"),
            ["Credential.LsaFailed"] = ("Credential verification failed (Win32 {0}, substatus {1}, NTSTATUS 0x{2:X8}).", "凭据验证失败（Win32 {0}，substatus {1}，NTSTATUS 0x{2:X8}）。"),
            ["Credential.OtherUser"] = ("The credential belongs to another user: {0}", "凭据属于其他用户：{0}"),
            ["Credential.OperationFailed"] = ("{0} failed (Win32 {1}, NTSTATUS 0x{2:X8}).", "{0} 失败（Win32 {1}，NTSTATUS 0x{2:X8}）。"),

            ["Settings.Title"] = ("HelloLock settings", "HelloLock 设置"),
            ["Settings.General"] = ("General", "常规"),
            ["Settings.IdleTimeout"] = ("Lock after", "空闲后锁定"),
            ["Settings.Disabled"] = ("Disabled", "关闭"),
            ["Settings.Minutes"] = ("{0} minutes", "{0} 分钟"),
            ["Settings.StartAtLogin"] = ("Start tray when I sign in", "登录后启动托盘"),
            ["Settings.Enabled"] = ("Enabled", "已启用"),
            ["Settings.Language"] = ("Language", "语言"),
            ["Settings.Language.System"] = ("Use system language", "跟随系统"),
            ["Settings.Language.English"] = ("English", "English"),
            ["Settings.Language.Chinese"] = ("简体中文", "简体中文"),
            ["Settings.Save"] = ("Save", "保存"),
            ["Settings.Cancel"] = ("Cancel", "取消"),
            ["Settings.Version"] = ("Version {0}", "版本 {0}"),
            ["Settings.Saved"] = ("Settings saved.", "设置已保存。"),
            ["Settings.SaveFailed"] = ("Unable to save settings:\n{0}", "无法保存设置：\n{0}"),
            ["Settings.NotInstalled"] = ("HelloLock is not installed. Run the installer first.", "HelloLock 尚未安装，请先运行安装脚本。"),
            ["Settings.TaskMissing"] = ("The HelloLock tray task is missing. Run the installer again.", "HelloLock 托盘任务不存在，请重新运行安装脚本。"),
            ["Settings.TaskSchedulerMissing"] = ("Windows Task Scheduler is unavailable.", "Windows 任务计划程序不可用。"),
            ["Settings.TaskSchedulerConnectFailed"] = ("Could not connect to Windows Task Scheduler.", "无法连接 Windows 任务计划程序。"),
            ["Settings.TaskNotOwned"] = ("The HelloLock tray task is not owned by this installation.", "HelloLock 托盘任务不属于当前安装。"),
        };

    private static UserSettings _settings = UserSettingsStore.Load();

    public static event EventHandler? LanguageChanged;

    public static AppLanguage SelectedLanguage => _settings.Language;

    public static bool IsChinese => _settings.Language switch
    {
        AppLanguage.ChineseSimplified => true,
        AppLanguage.English => false,
        _ => CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase),
    };

    public static string Get(string key)
    {
        if (!Strings.TryGetValue(key, out var value)) return key;
        return IsChinese ? value.Zh : value.En;
    }

    public static string Format(string key, params object[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), arguments);

    public static void SetLanguage(AppLanguage language, bool persist = true)
    {
        bool changed = _settings.Language != language;
        if (persist)
        {
            var settings = UserSettingsStore.Load();
            settings.Language = language;
            UserSettingsStore.Save(settings);
            _settings = settings;
        }
        else
        {
            _settings.Language = language;
        }
        if (changed)
        {
            LanguageChanged?.Invoke(null, EventArgs.Empty);
        }
    }
}
