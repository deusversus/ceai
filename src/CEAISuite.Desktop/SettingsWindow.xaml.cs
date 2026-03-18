using System.Windows;
using System.Windows.Controls;
using CEAISuite.Application;

namespace CEAISuite.Desktop;

public partial class SettingsWindow : Window
{
    private readonly AppSettingsService _settingsService;
    private bool _keyVisible;
    private bool _suppressModelListChange;

    private record ModelInfo(string Id, string Name, string Description);

    private static readonly ModelInfo[] OpenAIModels =
    [
        new("gpt-5.4",    "GPT-5.4",    "Latest flagship"),
        new("gpt-4.1",    "GPT-4.1",    "Fast & affordable"),
        new("o3",         "o3",          "Deep reasoning"),
        new("o4-mini",    "o4-mini",     "Fast reasoning"),
        new("gpt-4o",     "GPT-4o",      "Multimodal"),
        new("gpt-4o-mini","GPT-4o mini", "Cheapest"),
    ];

    private static readonly ModelInfo[] AnthropicModels =
    [
        new("claude-sonnet-4-6", "Claude Sonnet 4.6", "Best balance"),
        new("claude-opus-4-6",   "Claude Opus 4.6",   "Most capable"),
        new("claude-haiku-4-5",  "Claude Haiku 4.5",  "Fast & cheap"),
    ];

    public SettingsWindow(AppSettingsService settingsService)
    {
        InitializeComponent();
        _settingsService = settingsService;
        LoadCurrentSettings();
    }

    private void LoadCurrentSettings()
    {
        var s = _settingsService.Settings;
        var provider = (s.Provider ?? "openai").ToLowerInvariant();

        // Select provider card
        var providerRadio = provider switch
        {
            "anthropic" => ProviderAnthropic,
            "openai-compatible" => ProviderCompatible,
            _ => ProviderOpenAI,
        };
        providerRadio.IsChecked = true;

        // Custom endpoint
        if (!string.IsNullOrWhiteSpace(s.CustomEndpoint))
            EndpointBox.Text = s.CustomEndpoint;

        // API Key
        if (!string.IsNullOrWhiteSpace(s.OpenAiApiKey))
        {
            ApiKeyBox.Password = s.OpenAiApiKey;
            ApiKeyTextBox.Text = s.OpenAiApiKey;
            ApiKeyStatus.Text = "✓ API key configured";
            ApiKeyStatus.SetResourceReference(TextBlock.ForegroundProperty, "SuccessForeground");
        }
        else
        {
            ApiKeyStatus.Text = "No API key — AI operator disabled";
            ApiKeyStatus.SetResourceReference(TextBlock.ForegroundProperty, "WarningForeground");
        }

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

    private string GetSelectedProvider()
    {
        if (ProviderAnthropic.IsChecked == true) return "anthropic";
        if (ProviderCompatible.IsChecked == true) return "openai-compatible";
        return "openai";
    }

    private void ProviderCard_Checked(object sender, RoutedEventArgs e)
    {
        var provider = GetSelectedProvider();

        if (CustomEndpointPanel is not null)
            CustomEndpointPanel.Visibility = provider == "openai-compatible" ? Visibility.Visible : Visibility.Collapsed;

        if (ApiKeySubtitle is not null)
        {
            ApiKeySubtitle.Text = provider switch
            {
                "anthropic" => "Get yours at console.anthropic.com",
                "openai-compatible" => "API key for the compatible endpoint",
                _ => "Get yours at platform.openai.com",
            };
        }

        PopulateModelList(provider);
    }

    private void PopulateModelList(string provider)
    {
        if (ModelList is null) return;

        _suppressModelListChange = true;
        ModelList.Items.Clear();

        var models = provider switch
        {
            "anthropic" => AnthropicModels,
            "openai-compatible" => Array.Empty<ModelInfo>(),
            _ => OpenAIModels,
        };

        var currentModel = _settingsService.Settings.Model;

        foreach (var model in models)
        {
            var panel = new DockPanel { Margin = new Thickness(0) };
            var name = new TextBlock
            {
                Text = model.Name,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryForeground");

            var desc = new TextBlock
            {
                Text = model.Description,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(12, 0, 0, 0),
            };
            desc.SetResourceReference(TextBlock.ForegroundProperty, "TertiaryForeground");

            DockPanel.SetDock(desc, Dock.Right);
            panel.Children.Add(desc);
            panel.Children.Add(name);

            ModelList.Items.Add(new ListBoxItem { Content = panel, Tag = model.Id });
        }

        // Select the current model, or first available
        int selectedIdx = -1;
        for (int i = 0; i < ModelList.Items.Count; i++)
        {
            if (ModelList.Items[i] is ListBoxItem item && (string)item.Tag == currentModel)
            {
                selectedIdx = i;
                break;
            }
        }

        if (selectedIdx >= 0)
            ModelList.SelectedIndex = selectedIdx;
        else if (models.Length == 0 && CustomModelBox is not null)
            CustomModelBox.Text = currentModel;
        else if (ModelList.Items.Count > 0)
            ModelList.SelectedIndex = 0;

        _suppressModelListChange = false;
    }

    private void ModelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressModelListChange) return;
        if (ModelList.SelectedItem is ListBoxItem item && CustomModelBox is not null)
            CustomModelBox.Text = ""; // clear custom when selecting from list
    }

    private void CustomModelBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // When user types a custom model, deselect the list
        if (!string.IsNullOrWhiteSpace(CustomModelBox?.Text))
        {
            _suppressModelListChange = true;
            ModelList.SelectedIndex = -1;
            _suppressModelListChange = false;
        }
    }

    private string GetSelectedModel()
    {
        // Custom model takes priority if typed
        if (!string.IsNullOrWhiteSpace(CustomModelBox?.Text))
            return CustomModelBox.Text.Trim();

        // Otherwise use list selection
        if (ModelList.SelectedItem is ListBoxItem item)
            return (string)item.Tag;

        return _settingsService.Settings.Model;
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

        s.Provider = GetSelectedProvider();
        s.CustomEndpoint = s.Provider == "openai-compatible" ? EndpointBox.Text : null;
        s.OpenAiApiKey = _keyVisible ? ApiKeyTextBox.Text : ApiKeyBox.Password;
        s.Model = GetSelectedModel();

        if (int.TryParse(RefreshIntervalBox.Text, out var interval) && interval >= 100)
            s.RefreshIntervalMs = interval;

        s.ShowUnresolvedAsQuestionMarks = ShowUnresolvedCheck.IsChecked == true;

        s.Theme = ThemeLight.IsChecked == true ? "Light"
                : ThemeDark.IsChecked == true ? "Dark"
                : "System";

        _settingsService.Save();

        MessageBox.Show("Settings saved. Provider and model changes take effect on next message.",
            "Settings Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        DialogResult = true;
        Close();
    }

    private void CancelSettings(object sender, RoutedEventArgs e)
    {
        var savedTheme = Enum.TryParse<AppTheme>(_settingsService.Settings.Theme, true, out var t) ? t : AppTheme.System;
        ThemeManager.ApplyTheme(savedTheme);

        DialogResult = false;
        Close();
    }
}
