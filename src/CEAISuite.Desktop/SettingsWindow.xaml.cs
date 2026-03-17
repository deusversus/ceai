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
            ApiKeyStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4E, 0xC9, 0xB0));
        }
        else
        {
            ApiKeyStatus.Text = "No API key configured — AI operator is disabled";
            ApiKeyStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDC, 0xDC, 0xAA));
        }

        // Model
        ModelCombo.Text = s.Model;

        // General
        RefreshIntervalBox.Text = s.RefreshIntervalMs.ToString();
        RefreshSlider.Value = s.RefreshIntervalMs;
        ShowUnresolvedCheck.IsChecked = s.ShowUnresolvedAsQuestionMarks;
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

        _settingsService.Save();

        MessageBox.Show("Settings saved. API key and model changes require an app restart.",
            "Settings Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        DialogResult = true;
        Close();
    }

    private void CancelSettings(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
