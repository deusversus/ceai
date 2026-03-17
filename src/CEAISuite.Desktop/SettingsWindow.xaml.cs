using System.Windows;
using System.Windows.Controls;
using CEAISuite.Application;

namespace CEAISuite.Desktop;

public partial class SettingsWindow : Window
{
    private readonly AppSettingsService _settingsService;
    private bool _keyVisible;

    public SettingsWindow(AppSettingsService settingsService)
    {
        InitializeComponent();
        _settingsService = settingsService;
        LoadCurrentSettings();
    }

    private void LoadCurrentSettings()
    {
        var s = _settingsService.Settings;

        // API Key
        if (!string.IsNullOrWhiteSpace(s.OpenAiApiKey))
        {
            ApiKeyBox.Password = s.OpenAiApiKey;
            ApiKeyTextBox.Text = s.OpenAiApiKey;
            ApiKeyStatus.Text = "API key is configured";
            ApiKeyStatus.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "SuccessForeground");
        }
        else
        {
            ApiKeyStatus.Text = "No API key configured — AI operator is disabled";
            ApiKeyStatus.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "WarningForeground");
        }

        // Model
        ModelCombo.Text = s.Model;

        // General
        RefreshIntervalBox.Text = s.RefreshIntervalMs.ToString();
        RefreshSlider.Value = s.RefreshIntervalMs;
        ShowUnresolvedCheck.IsChecked = s.ShowUnresolvedAsQuestionMarks;

        // Theme
        var theme = Enum.TryParse<AppTheme>(s.Theme, true, out var t) ? t : AppTheme.System;
        ThemeSystem.IsChecked = theme == AppTheme.System;
        ThemeDark.IsChecked = theme == AppTheme.Dark;
        ThemeLight.IsChecked = theme == AppTheme.Light;
    }

    private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
    {
        // Live preview
        var theme = ThemeLight?.IsChecked == true ? AppTheme.Light
                  : ThemeDark?.IsChecked == true ? AppTheme.Dark
                  : AppTheme.System;
        ThemeManager.ApplyTheme(theme);
    }

    private void ToggleApiKeyVisibility(object sender, RoutedEventArgs e)
    {
        _keyVisible = !_keyVisible;
        if (_keyVisible)
        {
            ApiKeyTextBox.Text = ApiKeyBox.Password;
            ApiKeyTextBox.Visibility = Visibility.Visible;
            ApiKeyBox.Visibility = Visibility.Collapsed;
            ShowKeyBtn.Content = "Hide";
        }
        else
        {
            ApiKeyBox.Password = ApiKeyTextBox.Text;
            ApiKeyBox.Visibility = Visibility.Visible;
            ApiKeyTextBox.Visibility = Visibility.Collapsed;
            ShowKeyBtn.Content = "Show";
        }
    }

    private void RefreshSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RefreshIntervalBox is not null)
            RefreshIntervalBox.Text = ((int)e.NewValue).ToString();
    }

    private void SaveSettings(object sender, RoutedEventArgs e)
    {
        var s = _settingsService.Settings;

        // Get the key from whichever control is visible
        s.OpenAiApiKey = _keyVisible ? ApiKeyTextBox.Text : ApiKeyBox.Password;
        s.Model = ModelCombo.Text;

        if (int.TryParse(RefreshIntervalBox.Text, out var interval) && interval >= 100)
            s.RefreshIntervalMs = interval;

        s.ShowUnresolvedAsQuestionMarks = ShowUnresolvedCheck.IsChecked == true;

        // Theme
        s.Theme = ThemeLight.IsChecked == true ? "Light"
                : ThemeDark.IsChecked == true ? "Dark"
                : "System";

        _settingsService.Save();

        MessageBox.Show("Settings saved. API key and model changes require an app restart.",
            "Settings Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        DialogResult = true;
        Close();
    }

    private void CancelSettings(object sender, RoutedEventArgs e)
    {
        // Revert theme preview if user cancels
        var savedTheme = Enum.TryParse<AppTheme>(_settingsService.Settings.Theme, true, out var t) ? t : AppTheme.System;
        ThemeManager.ApplyTheme(savedTheme);

        DialogResult = false;
        Close();
    }
}
