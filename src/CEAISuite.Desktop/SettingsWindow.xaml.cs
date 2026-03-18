using System.Windows;
using System.Windows.Controls;
using CEAISuite.Application;

namespace CEAISuite.Desktop;

public partial class SettingsWindow : Window
{
    private readonly AppSettingsService _settingsService;
    private bool _keyVisible;
    private bool _suppressProviderChanged;

    private static readonly string[] OpenAIModels = ["gpt-5.4", "gpt-4.1", "gpt-4o", "gpt-4o-mini", "o3", "o4-mini"];
    private static readonly string[] AnthropicModels = ["claude-sonnet-4-6", "claude-opus-4-6", "claude-haiku-4-5"];

    public SettingsWindow(AppSettingsService settingsService)
    {
        InitializeComponent();
        _settingsService = settingsService;
        LoadCurrentSettings();
    }

    private void LoadCurrentSettings()
    {
        var s = _settingsService.Settings;

        // Provider (set before API key so subtitle updates)
        _suppressProviderChanged = true;
        var provider = (s.Provider ?? "openai").ToLowerInvariant();
        foreach (ComboBoxItem item in ProviderCombo.Items)
        {
            if ((string)item.Tag == provider)
            {
                ProviderCombo.SelectedItem = item;
                break;
            }
        }
        _suppressProviderChanged = false;
        ApplyProviderUI(provider);

        // Custom endpoint
        if (!string.IsNullOrWhiteSpace(s.CustomEndpoint))
            EndpointBox.Text = s.CustomEndpoint;

        // API Key
        if (!string.IsNullOrWhiteSpace(s.OpenAiApiKey))
        {
            ApiKeyBox.Password = s.OpenAiApiKey;
            ApiKeyTextBox.Text = s.OpenAiApiKey;
            ApiKeyStatus.Text = "API key is configured";
            ApiKeyStatus.SetResourceReference(TextBlock.ForegroundProperty, "SuccessForeground");
        }
        else
        {
            ApiKeyStatus.Text = "No API key configured — AI operator is disabled";
            ApiKeyStatus.SetResourceReference(TextBlock.ForegroundProperty, "WarningForeground");
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

    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProviderChanged) return;
        if (ProviderCombo.SelectedItem is ComboBoxItem item)
        {
            var provider = (string)item.Tag;
            ApplyProviderUI(provider);
        }
    }

    private void ApplyProviderUI(string provider)
    {
        // Show/hide custom endpoint panel
        if (CustomEndpointPanel is not null)
            CustomEndpointPanel.Visibility = provider == "openai-compatible" ? Visibility.Visible : Visibility.Collapsed;

        // Update subtitle text
        if (ApiKeySubtitle is not null)
        {
            ApiKeySubtitle.Text = provider switch
            {
                "anthropic" => "Get yours at console.anthropic.com",
                "openai-compatible" => "API key for the compatible endpoint",
                _ => "Get yours at platform.openai.com",
            };
        }

        // Populate model suggestions (preserve current text if user typed something custom)
        if (ModelCombo is null) return;
        var currentModel = ModelCombo.Text;
        ModelCombo.Items.Clear();

        var models = provider switch
        {
            "anthropic" => AnthropicModels,
            "openai-compatible" => Array.Empty<string>(),
            _ => OpenAIModels,
        };

        foreach (var model in models)
            ModelCombo.Items.Add(new ComboBoxItem { Content = model });

        // Restore model text (or set default for new provider)
        ModelCombo.Text = currentModel;
    }

    private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
    {
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

        // Provider
        if (ProviderCombo.SelectedItem is ComboBoxItem providerItem)
            s.Provider = (string)providerItem.Tag;

        // Custom endpoint
        s.CustomEndpoint = s.Provider == "openai-compatible" ? EndpointBox.Text : null;

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

        MessageBox.Show("Settings saved. API key, model, and provider changes require an app restart.",
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
