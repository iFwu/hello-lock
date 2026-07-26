using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace HelloLock;

public partial class SettingsWindow : Window
{
    private static readonly int[] IdleOptions = [0, 5, 10, 15, 30, 60];
    private static readonly string InformationalVersion =
        typeof(SettingsWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? typeof(SettingsWindow).Assembly.GetName().Version?.ToString()
        ?? "unknown";
    private static readonly string DisplayVersion = GetDisplayVersion();

    private SystemSettingsSnapshot _systemSettings;
    private readonly AppLanguage _originalLanguage;
    private bool _loading;
    private bool _saved;

    public SettingsWindow()
    {
        InitializeComponent();
        _systemSettings = SystemSettings.Load();
        _originalLanguage = Localization.SelectedLanguage;
        Closing += OnClosing;
        LoadControls();
        ApplyText();
    }

    private void LoadControls()
    {
        _loading = true;
        try
        {
            StartupCheck.IsChecked = _systemSettings.StartTrayAtLogin;
            ReloadLocalizedOptions();
        }
        finally
        {
            _loading = false;
        }
    }

    private void ReloadLocalizedOptions()
    {
        int selectedMinutes = IdleCombo.SelectedValue is int minutes
            ? minutes
            : _systemSettings.IdleMinutes;
        AppLanguage selectedLanguage = LanguageCombo.SelectedValue is AppLanguage language
            ? language
            : Localization.SelectedLanguage;

        IdleCombo.ItemsSource = IdleOptions
            .Select(value => new IdleOption(
                value,
                value == 0
                    ? Localization.Get("Settings.Disabled")
                    : Localization.Format("Settings.Minutes", value)))
            .ToList();
        IdleCombo.SelectedValue = IdleOptions.Contains(selectedMinutes)
            ? selectedMinutes
            : 30;

        LanguageCombo.ItemsSource = new List<LanguageOption>
        {
            new(AppLanguage.System, Localization.Get("Settings.Language.System")),
            new(AppLanguage.English, Localization.Get("Settings.Language.English")),
            new(AppLanguage.ChineseSimplified, Localization.Get("Settings.Language.Chinese")),
        };
        LanguageCombo.SelectedValue = selectedLanguage;
    }

    private void ApplyText()
    {
        Title = Localization.Get("Settings.Title");
        SubtitleText.Text = Localization.Get("Settings.General");
        IdleLabel.Text = Localization.Get("Settings.IdleTimeout");
        StartupLabel.Text = Localization.Get("Settings.StartAtLogin");
        LanguageLabel.Text = Localization.Get("Settings.Language");
        StartupCheck.Content = Localization.Get("Settings.Enabled");
        SaveButton.Content = Localization.Get("Settings.Save");
        CancelButton.Content = Localization.Get("Settings.Cancel");
        VersionText.Text = Localization.Format("Settings.Version", DisplayVersion);
        VersionText.ToolTip = InformationalVersion;
    }

    private static string GetDisplayVersion()
    {
        int separator = InformationalVersion.IndexOf('+');
        if (separator < 0) return InformationalVersion;

        string version = InformationalVersion[..separator];
        string revision = InformationalVersion[(separator + 1)..];
        if (revision.Length > 7) revision = revision[..7];
        return $"{version} ({revision})";
    }

    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageCombo.SelectedValue is not AppLanguage language) return;

        _loading = true;
        try
        {
            Localization.SetLanguage(language, persist: false);
            ApplyText();
            ReloadLocalizedOptions();
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            int idleMinutes = IdleCombo.SelectedValue is int selected ? selected : 30;
            bool startTray = StartupCheck.IsChecked == true;
            AppLanguage language = LanguageCombo.SelectedValue is AppLanguage selectedLanguage
                ? selectedLanguage
                : AppLanguage.System;

            SystemSettings.Save(idleMinutes, startTray);
            Localization.SetLanguage(language);
            _systemSettings = new SystemSettingsSnapshot(idleMinutes, startTray);
            _saved = true;
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(31, 111, 235));
            StatusText.Text = Localization.Get("Settings.Saved");
        }
        catch (Exception ex)
        {
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(207, 34, 46));
            StatusText.Text = Localization.Format("Settings.SaveFailed", ex.Message);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_saved)
        {
            Localization.SetLanguage(_originalLanguage, persist: false);
        }
    }

    private sealed record IdleOption(int Minutes, string Label);
    private sealed record LanguageOption(AppLanguage Language, string Label);
}
