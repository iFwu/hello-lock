using System;
using System.IO;
using System.Text.Json;

namespace HelloLock;

public enum AppLanguage
{
    System,
    English,
    ChineseSimplified,
}

public sealed class UserSettings
{
    public AppLanguage Language { get; set; } = AppLanguage.System;
    public int IdleMinutes { get; set; } = 30;
}

public static class UserSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HelloLock");

    public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public static UserSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new UserSettings();
            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(SettingsPath))
                ?? new UserSettings();
            settings.IdleMinutes = Math.Clamp(settings.IdleMinutes, 0, 1440);
            return settings;
        }
        catch (JsonException)
        {
            return new UserSettings();
        }
    }

    public static void Save(UserSettings settings)
    {
        settings.IdleMinutes = Math.Clamp(settings.IdleMinutes, 0, 1440);
        Directory.CreateDirectory(SettingsDirectory);
        string temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }
}
