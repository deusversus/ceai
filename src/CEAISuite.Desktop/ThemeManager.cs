using System.Windows;
using Microsoft.Win32;

namespace CEAISuite.Desktop;

public enum AppTheme
{
    System,
    Dark,
    Light
}

/// <summary>
/// Manages theme switching by swapping merged resource dictionaries at runtime.
/// </summary>
public static class ThemeManager
{
    private static ResourceDictionary? _currentThemeDictionary;

    private static readonly Uri DarkThemeUri =
        new("pack://application:,,,/Themes/DarkTheme.xaml", UriKind.Absolute);
    private static readonly Uri LightThemeUri =
        new("pack://application:,,,/Themes/LightTheme.xaml", UriKind.Absolute);

    /// <summary>Apply the given theme to the application.</summary>
    public static void ApplyTheme(AppTheme theme)
    {
        var resolved = theme == AppTheme.System ? DetectSystemTheme() : theme;
        var uri = resolved == AppTheme.Light ? LightThemeUri : DarkThemeUri;
        var dict = new ResourceDictionary { Source = uri };

        var mergedDictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;

        if (_currentThemeDictionary is not null)
            mergedDictionaries.Remove(_currentThemeDictionary);

        mergedDictionaries.Insert(0, dict);
        _currentThemeDictionary = dict;

        CurrentTheme = theme;
        ResolvedTheme = resolved;
    }

    /// <summary>The user-selected theme preference.</summary>
    public static AppTheme CurrentTheme { get; private set; } = AppTheme.Dark;

    /// <summary>The actual resolved theme (System → Dark or Light).</summary>
    public static AppTheme ResolvedTheme { get; private set; } = AppTheme.Dark;

    /// <summary>Detect whether Windows is using light or dark mode.</summary>
    public static AppTheme DetectSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int i)
                return i == 1 ? AppTheme.Light : AppTheme.Dark;
        }
        catch { /* fallback */ }
        return AppTheme.Dark;
    }
}
