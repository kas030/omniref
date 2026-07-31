using System.Windows;
using Microsoft.Win32;
using OmniRef.Infrastructure.Windows.Settings;

namespace OmniRef.App.Services;

public sealed class ThemeManager : IDisposable
{
    private AppTheme _configuredTheme;

    public AppTheme ConfiguredTheme => _configuredTheme;

    public void Apply(AppTheme theme)
    {
        _configuredTheme = theme;
        var effectiveTheme = theme == AppTheme.System
            ? IsSystemLightTheme() ? AppTheme.Light : AppTheme.Dark
            : theme;
        var source = new Uri(
            effectiveTheme == AppTheme.Light ? "Themes/Light.xaml" : "Themes/Dark.xaml",
            UriKind.Relative);

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(
            dictionary => dictionary.Source?.OriginalString.Contains(
                "Themes/Light.xaml",
                StringComparison.OrdinalIgnoreCase) == true ||
                          dictionary.Source?.OriginalString.Contains(
                              "Themes/Dark.xaml",
                              StringComparison.OrdinalIgnoreCase) == true);
        var replacement = new ResourceDictionary { Source = source };
        if (existing is null)
        {
            dictionaries.Insert(0, replacement);
        }
        else
        {
            var index = dictionaries.IndexOf(existing);
            dictionaries[index] = replacement;
        }

        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        if (theme == AppTheme.System)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
    }

    public AppTheme Cycle()
    {
        var next = _configuredTheme switch
        {
            AppTheme.System => AppTheme.Light,
            AppTheme.Light => AppTheme.Dark,
            _ => AppTheme.System
        };
        Apply(next);
        return next;
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs eventArgs)
    {
        if (_configuredTheme == AppTheme.System)
        {
            Application.Current.Dispatcher.Invoke(() => Apply(AppTheme.System));
        }
    }

    private static bool IsSystemLightTheme()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                0);
            return value is int intValue && intValue != 0;
        }
        catch (Exception exception) when (
            exception is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
