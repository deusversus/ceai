using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CEAISuite.Application;

namespace CEAISuite.Desktop;

public partial class SettingsWindow : Window, IDisposable
{
    private readonly AppSettingsService _settingsService;
    private bool _keyVisible;
    private bool _suppressModelListChange;
    private CancellationTokenSource? _deviceFlowCts;
    private DispatcherTimer? _apiKeyRevealTimer;
    private DispatcherTimer? _ghTokenRevealTimer;

    private sealed record ModelInfo(string Id, string Name, string Description);

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

    private static readonly ModelInfo[] GeminiModels =
    [
        new("gemini-3.1-flash-lite-preview", "Gemini 3.1 Flash Lite", "Fastest & cheapest"),
        new("gemini-3-flash-preview",      "Gemini 3.0 Flash",     "Fast & efficient"),
        new("gemini-3.1-pro-preview",      "Gemini 3.1 Pro",       "Most capable"),
    ];

    private static readonly ModelInfo[] CopilotModels = []; // populated dynamically from API

    private bool _copilotModelsLoading;

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
            "copilot" => ProviderCopilot,
            "gemini" => ProviderGemini,
            "openai-compatible" => ProviderCompatible,
            _ => ProviderOpenAI,
        };
        providerRadio.IsChecked = true;

        // Custom endpoint
        if (!string.IsNullOrWhiteSpace(s.CustomEndpoint))
            EndpointBox.Text = s.CustomEndpoint;

        // API Key (for non-Copilot providers) — load per-provider key
        var currentKey = s.Provider?.ToLowerInvariant() switch
        {
            "anthropic" => s.AnthropicApiKey,
            "gemini" => s.GeminiApiKey,
            "openai-compatible" => s.CompatibleApiKey,
            _ => s.OpenAiApiKey,
        };
        if (!string.IsNullOrWhiteSpace(currentKey))
        {
            ApiKeyBox.Password = currentKey;
            // Don't load ApiKeyTextBox.Text — only populate when user clicks Show
            ApiKeyStatus.Text = "✓ API key configured";
            ApiKeyStatus.SetResourceReference(TextBlock.ForegroundProperty, "SuccessForeground");
        }
        else if (s.Provider is "copilot")
        {
            // Copilot uses GitHub token, not API key — don't show misleading message
            ApiKeyStatus.Text = "";
        }
        else
        {
            ApiKeyStatus.Text = "No API key for this provider";
            ApiKeyStatus.SetResourceReference(TextBlock.ForegroundProperty, "WarningForeground");
        }

        // GitHub Token (for Copilot provider)
        if (!string.IsNullOrWhiteSpace(s.GitHubToken))
        {
            GitHubTokenBox.Password = s.GitHubToken;
            // Don't load GitHubTokenTextBox.Text — only populate when user clicks Show
            GitHubTokenStatus.Text = "✓ Signed in to GitHub Copilot";
            GitHubTokenStatus.SetResourceReference(TextBlock.ForegroundProperty, "SuccessForeground");
            GitHubSignInBtn.Content = "✓  Signed in — sign in again?";
            CopilotSignOutBtn.Visibility = Visibility.Visible;
        }
        else
        {
            GitHubTokenStatus.Text = "Sign in to enable Copilot";
            GitHubTokenStatus.SetResourceReference(TextBlock.ForegroundProperty, "WarningForeground");
            CopilotSignOutBtn.Visibility = Visibility.Collapsed;
        }

        // General
        RefreshIntervalBox.Text = s.RefreshIntervalMs.ToString(CultureInfo.InvariantCulture);
        RefreshSlider.Value = s.RefreshIntervalMs;
        ShowUnresolvedCheck.IsChecked = s.ShowUnresolvedAsQuestionMarks;
        StreamingCheck.IsChecked = s.UseStreaming;
        AutoOpenMemoryBrowserCheck.IsChecked = s.AutoOpenMemoryBrowser;
        CheckForUpdatesCheck.IsChecked = s.CheckForUpdatesOnStartup;

        // Rate limiting
        RateLimitBox.Text = s.RateLimitSeconds.ToString(CultureInfo.InvariantCulture);
        RateLimitSlider.Value = s.RateLimitSeconds;
        RateLimitWaitCheck.IsChecked = s.RateLimitWait;

        // Theme
        var theme = Enum.TryParse<AppTheme>(s.Theme, true, out var t) ? t : AppTheme.System;
        ThemeSystem.IsChecked = theme == AppTheme.System;
        ThemeDark.IsChecked = theme == AppTheme.Dark;
        ThemeLight.IsChecked = theme == AppTheme.Light;

        // Density preset
        var density = (s.DensityPreset ?? "Balanced").ToLowerInvariant();
        DensityClean.IsChecked = density == "clean";
        DensityBalanced.IsChecked = density == "balanced";
        DensityDense.IsChecked = density == "dense";

        // Performance / Token Limits
        var profile = (s.TokenProfile ?? "balanced").ToLowerInvariant();
        ProfileSaving.IsChecked = profile == "saving";
        ProfileBalanced.IsChecked = profile == "balanced";
        ProfilePerformance.IsChecked = profile == "performance";
        UpdateProfileDescription(profile);

        // Advanced overrides (blank = use profile default)
        LoadLimitBox(LimitMaxOutputTokens, s.LimitMaxOutputTokens);
        LoadLimitBox(LimitMaxImagesPerTurn, s.LimitMaxImagesPerTurn);
        LoadLimitBox(LimitMaxApprovalRounds, s.LimitMaxApprovalRounds);
        LoadLimitBox(LimitMaxReplayMessages, s.LimitMaxReplayMessages);
        LoadLimitBox(LimitMaxToolResultChars, s.LimitMaxToolResultChars);
        LoadLimitBox(LimitMaxStackFrames, s.LimitMaxStackFrames);
        LoadLimitBox(LimitMaxBrowseMemoryBytes, s.LimitMaxBrowseMemoryBytes);
        LoadLimitBox(LimitMaxHitLogEntries, s.LimitMaxHitLogEntries);
        LoadLimitBox(LimitMaxSearchResults, s.LimitMaxSearchResults);
        LoadLimitBox(LimitMaxChatSearchResults, s.LimitMaxChatSearchResults);
        LimitFilterRegisters.IsChecked = s.LimitFilterRegisters;
        LimitDereferenceHookRegisters.IsChecked = s.LimitDereferenceHookRegisters;

        // Agent tab
        var permMode = (s.PermissionMode ?? "Normal").ToLowerInvariant();
        PermModeNormal.IsChecked = permMode == "normal";
        PermModeReadOnly.IsChecked = permMode == "readonly";
        PermModePlanOnly.IsChecked = permMode == "planonly";
        PermModeUnrestricted.IsChecked = permMode == "unrestricted";

        MaxSessionCostBox.Text = s.MaxSessionCostDollars > 0 ? s.MaxSessionCostDollars.ToString("F2", CultureInfo.InvariantCulture) : "";
        InputPriceBox.Text = s.InputPricePerMillion.ToString("F2", CultureInfo.InvariantCulture);
        OutputPriceBox.Text = s.OutputPricePerMillion.ToString("F2", CultureInfo.InvariantCulture);
        CachedInputPriceBox.Text = s.CachedInputPricePerMillion.ToString("F2", CultureInfo.InvariantCulture);

        FallbackModelsBox.Text = s.FallbackModels.Count > 0 ? string.Join(", ", s.FallbackModels) : "";

        EnableAgentMemoryCheck.IsChecked = s.EnableAgentMemory;
        MaxMemoryEntriesBox.Text = s.MaxMemoryEntries.ToString(CultureInfo.InvariantCulture);

        RequirePlanCheck.IsChecked = s.RequirePlanForDestructive;
        EarlyToolExecutionCheck.IsChecked = s.EnableEarlyToolExecution;

        // Additional General settings
        MaxConversationMessagesBox.Text = s.MaxConversationMessages.ToString(CultureInfo.InvariantCulture);
        AutoSaveIntervalBox.Text = s.AutoSaveIntervalMinutes.ToString(CultureInfo.InvariantCulture);
        LogRetentionBox.Text = s.LogRetentionDays.ToString(CultureInfo.InvariantCulture);

        // Scanning
        ScanThreadCountBox.Text = s.ScanThreadCount.ToString(CultureInfo.InvariantCulture);
        foreach (ComboBoxItem item in DefaultScanDataTypeCombo.Items)
        {
            if ((string)item.Content == s.DefaultScanDataType)
            {
                DefaultScanDataTypeCombo.SelectedItem = item;
                break;
            }
        }

        // Lua
        LuaTimeoutBox.Text = s.LuaExecutionTimeoutSeconds.ToString(CultureInfo.InvariantCulture);

        // Memory Browser
        var bytesPerRow = s.MemoryBrowserBytesPerRow.ToString(CultureInfo.InvariantCulture);
        foreach (ComboBoxItem item in MemBrowserBytesCombo.Items)
        {
            if ((string)item.Content == bytesPerRow)
            {
                MemBrowserBytesCombo.SelectedItem = item;
                break;
            }
        }
    }

    private string GetSelectedProvider()
    {
        if (ProviderAnthropic.IsChecked == true) return "anthropic";
        if (ProviderCopilot.IsChecked == true) return "copilot";
        if (ProviderGemini.IsChecked == true) return "gemini";
        if (ProviderCompatible.IsChecked == true) return "openai-compatible";
        return "openai";
    }

    private void ProviderCard_Checked(object sender, RoutedEventArgs e)
    {
        var provider = GetSelectedProvider();
        var isCopilot = provider == "copilot";

        if (CustomEndpointPanel is not null)
            CustomEndpointPanel.Visibility = provider == "openai-compatible" ? Visibility.Visible : Visibility.Collapsed;

        // Toggle between API Key panel and GitHub Token panel
        if (ApiKeyPanel is not null)
            ApiKeyPanel.Visibility = isCopilot ? Visibility.Collapsed : Visibility.Visible;
        if (GitHubTokenPanel is not null)
            GitHubTokenPanel.Visibility = isCopilot ? Visibility.Visible : Visibility.Collapsed;

        if (ApiKeySubtitle is not null)
        {
            ApiKeySubtitle.Text = provider switch
            {
                "anthropic" => "Get yours at console.anthropic.com",
                "gemini" => "Get yours at ai.google.dev",
                "openai-compatible" => "API key for the compatible endpoint",
                _ => "Get yours at platform.openai.com",
            };
        }

        // Swap API key display to the per-provider key
        var s = _settingsService.Settings;
        var newKey = provider switch
        {
            "anthropic" => s.AnthropicApiKey,
            "gemini" => s.GeminiApiKey,
            "openai-compatible" => s.CompatibleApiKey,
            "openai" => s.OpenAiApiKey,
            _ => null,
        };
        if (ApiKeyBox is not null)
        {
            ApiKeyBox.Password = newKey ?? "";
            if (_keyVisible && ApiKeyTextBox is not null)
                ApiKeyTextBox.Text = newKey ?? "";
        }

        // Update API key status for the new provider
        if (ApiKeyStatus is not null && provider != "copilot")
        {
            if (!string.IsNullOrWhiteSpace(newKey))
            {
                ApiKeyStatus.Text = "✓ API key configured";
                ApiKeyStatus.SetResourceReference(TextBlock.ForegroundProperty, "SuccessForeground");
            }
            else
            {
                ApiKeyStatus.Text = "No API key for this provider";
                ApiKeyStatus.SetResourceReference(TextBlock.ForegroundProperty, "WarningForeground");
            }
        }

        // Clear validation status when switching providers
        if (ApiKeyValidationStatus is not null)
            ApiKeyValidationStatus.Text = "";

        PopulateModelList(provider);

        // Auto-set default token pricing for the selected provider
        UpdateDefaultPricing(provider);

        // Auto-fetch usage when Copilot is selected
        if (CopilotUsagePanel is not null)
            TryAutoFetchUsage();
    }

    /// <summary>
    /// Update the token pricing fields to sensible defaults for the selected provider.
    /// Only updates if the current values match the previous provider's defaults
    /// (i.e., the user hasn't customized them).
    /// </summary>
    private void UpdateDefaultPricing(string provider)
    {
        if (InputPriceBox is null) return; // Agent tab not yet loaded

        // Known default pricing per provider (per million tokens)
        var (input, output, cached) = provider switch
        {
            "anthropic" => (3.00m, 15.00m, 0.30m),    // Claude Sonnet 4 pricing
            "openai" => (2.50m, 10.00m, 1.25m),       // GPT-4o pricing
            "copilot" => (0m, 0m, 0m),                 // Copilot: included in subscription
            "gemini" => (0.15m, 0.60m, 0.04m),         // Gemini 2.5 Flash pricing
            _ => (3.00m, 15.00m, 0.30m),               // Default fallback
        };

        // Only auto-set if the fields look like they haven't been customized
        // (empty, or match a known provider default within tolerance)
        if (decimal.TryParse(InputPriceBox.Text, out var currentInput))
        {
            var knownDefaults = new[] { 0m, 0.15m, 2.50m, 3.00m, 15.00m };
            if (!knownDefaults.Any(d => Math.Abs(d - currentInput) < 0.01m))
                return; // User has customized — don't overwrite
        }

        InputPriceBox.Text = input.ToString("F2", CultureInfo.InvariantCulture);
        OutputPriceBox.Text = output.ToString("F2", CultureInfo.InvariantCulture);
        CachedInputPriceBox.Text = cached.ToString("F2", CultureInfo.InvariantCulture);
    }

    private void PopulateModelList(string provider)
    {
        if (ModelList is null) return;

        _suppressModelListChange = true;
        ModelList.Items.Clear();

        if (provider == "copilot")
        {
            // Async fetch from API — show loading placeholder
            var currentModel = _settingsService.Settings.GetModelForProvider(provider);
            var loadingItem = new ListBoxItem
            {
                Content = new TextBlock { Text = "Loading models from Copilot API…", FontStyle = FontStyles.Italic },
                IsEnabled = false,
            };
            loadingItem.SetResourceReference(ListBoxItem.ForegroundProperty, "TertiaryForeground");
            ModelList.Items.Add(loadingItem);
            _suppressModelListChange = false;

            // Show current model in custom box while loading
            if (CustomModelBox is not null && !string.IsNullOrWhiteSpace(currentModel))
                CustomModelBox.Text = currentModel;

            _ = FetchAndPopulateCopilotModelsAsync(currentModel);
            return;
        }

        var models = provider switch
        {
            "anthropic" => AnthropicModels,
            "gemini" => GeminiModels,
            "openai-compatible" => Array.Empty<ModelInfo>(),
            _ => OpenAIModels,
        };

        PopulateModelListItems(models, _settingsService.Settings.GetModelForProvider(provider));
    }

    private async Task FetchAndPopulateCopilotModelsAsync(string currentModel)
    {
        if (_copilotModelsLoading) return;
        _copilotModelsLoading = true;

        try
        {
            var githubToken = _settingsService.Settings.GitHubToken;
            if (string.IsNullOrWhiteSpace(githubToken))
            {
                Dispatcher.Invoke(() =>
                {
                    ModelList.Items.Clear();
                    var noToken = new ListBoxItem
                    {
                        Content = new TextBlock { Text = "Enter a GitHub token first", FontStyle = FontStyles.Italic },
                        IsEnabled = false,
                    };
                    noToken.SetResourceReference(ListBoxItem.ForegroundProperty, "WarningForeground");
                    ModelList.Items.Add(noToken);
                });
                return;
            }

            var copilotModels = await ChatClientFactory.CopilotService.FetchModelsAsync(githubToken);

            var modelInfos = copilotModels
                .Select(m => new ModelInfo(m.Id, m.Name, m.Vendor))
                .ToArray();

            Dispatcher.Invoke(() =>
            {
                ModelList.Items.Clear();
                PopulateModelListItems(modelInfos, currentModel);
            });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
                ModelList.Items.Clear();
                var errItem = new ListBoxItem
                {
                    Content = new TextBlock { Text = $"Failed to load: {ex.Message}", FontStyle = FontStyles.Italic, TextWrapping = TextWrapping.Wrap },
                    IsEnabled = false,
                };
                errItem.SetResourceReference(ListBoxItem.ForegroundProperty, "ErrorForeground");
                ModelList.Items.Add(errItem);

                // Still allow custom model entry
                if (CustomModelBox is not null && !string.IsNullOrWhiteSpace(currentModel))
                    CustomModelBox.Text = currentModel;
            });
        }
        finally
        {
            _copilotModelsLoading = false;
        }
    }

    private void PopulateModelListItems(ModelInfo[] models, string currentModel)
    {
        _suppressModelListChange = true;

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

    private void ToggleApiKeyVisibility(object? sender, RoutedEventArgs? e)
    {
        _keyVisible = !_keyVisible;
        if (_keyVisible)
        {
            ApiKeyTextBox.Text = ApiKeyBox.Password;
            ApiKeyTextBox.Visibility = Visibility.Visible;
            ApiKeyBox.Visibility = Visibility.Collapsed;
            ShowKeyBtn.Content = "Hide";

            // Auto-hide after 30 seconds
            _apiKeyRevealTimer?.Stop();
            _apiKeyRevealTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _apiKeyRevealTimer.Tick += (_, _) =>
            {
                _apiKeyRevealTimer.Stop();
                ToggleApiKeyVisibility(null, null);
            };
            _apiKeyRevealTimer.Start();
        }
        else
        {
            _apiKeyRevealTimer?.Stop();
            ApiKeyBox.Password = ApiKeyTextBox.Text;
            ApiKeyTextBox.Text = ""; // Clear plaintext
            ApiKeyBox.Visibility = Visibility.Visible;
            ApiKeyTextBox.Visibility = Visibility.Collapsed;
            ShowKeyBtn.Content = "Show";
        }
    }

    private async void ValidateApiKey_Click(object sender, RoutedEventArgs e)
    {
        var provider = GetSelectedProvider();
        var key = _keyVisible ? ApiKeyTextBox.Text : ApiKeyBox.Password;
        var endpoint = provider == "openai-compatible" ? EndpointBox.Text : null;

        ApiKeyValidationStatus.Text = "Validating...";
        ApiKeyValidationStatus.Foreground = new SolidColorBrush(Colors.Gray);
        ValidateKeyBtn.IsEnabled = false;

        try
        {
            var (isValid, error) = await ApiKeyValidator.ValidateAsync(provider, key, endpoint);
            ApiKeyValidationStatus.Text = isValid ? "\u2713 Valid" : $"\u2717 {error}";
            ApiKeyValidationStatus.Foreground = new SolidColorBrush(isValid ? Colors.Green : Colors.Red);
        }
        catch (Exception ex)
        {
            ApiKeyValidationStatus.Text = $"\u2717 {ex.Message}";
            ApiKeyValidationStatus.Foreground = new SolidColorBrush(Colors.Red);
        }
        finally
        {
            ValidateKeyBtn.IsEnabled = true;
        }
    }

    private bool _ghTokenVisible;
    private void ToggleGitHubTokenVisibility(object? sender, RoutedEventArgs? e)
    {
        _ghTokenVisible = !_ghTokenVisible;
        if (_ghTokenVisible)
        {
            GitHubTokenTextBox.Text = GitHubTokenBox.Password;
            GitHubTokenTextBox.Visibility = Visibility.Visible;
            GitHubTokenBox.Visibility = Visibility.Collapsed;
            ShowGitHubTokenBtn.Content = "Hide";

            // Auto-hide after 30 seconds
            _ghTokenRevealTimer?.Stop();
            _ghTokenRevealTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _ghTokenRevealTimer.Tick += (_, _) =>
            {
                _ghTokenRevealTimer.Stop();
                ToggleGitHubTokenVisibility(null, null);
            };
            _ghTokenRevealTimer.Start();
        }
        else
        {
            _ghTokenRevealTimer?.Stop();
            GitHubTokenBox.Password = GitHubTokenTextBox.Text;
            GitHubTokenTextBox.Text = ""; // Clear plaintext
            GitHubTokenBox.Visibility = Visibility.Visible;
            GitHubTokenTextBox.Visibility = Visibility.Collapsed;
            ShowGitHubTokenBtn.Content = "Show";
        }
    }

    private void RefreshSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RefreshIntervalBox is not null)
            RefreshIntervalBox.Text = ((int)e.NewValue).ToString(CultureInfo.InvariantCulture);
    }

    private void RateLimitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RateLimitBox is not null)
            RateLimitBox.Text = ((int)e.NewValue).ToString(CultureInfo.InvariantCulture);
    }

    private void SaveSettings(object sender, RoutedEventArgs e)
    {
        var s = _settingsService.Settings;

        s.Provider = GetSelectedProvider();
        s.CustomEndpoint = s.Provider == "openai-compatible" ? EndpointBox.Text : null;

        // Save API key to the correct per-provider property
        var apiKeyValue = _keyVisible ? ApiKeyTextBox.Text : ApiKeyBox.Password;
        switch (GetSelectedProvider())
        {
            case "openai": s.OpenAiApiKey = apiKeyValue; break;
            case "anthropic": s.AnthropicApiKey = apiKeyValue; break;
            case "gemini": s.GeminiApiKey = apiKeyValue; break;
            case "openai-compatible": s.CompatibleApiKey = apiKeyValue; break;
            // Copilot uses GitHubToken (handled below)
        }

        s.GitHubToken = _ghTokenVisible ? GitHubTokenTextBox.Text : GitHubTokenBox.Password;

        // Save model per-provider so it persists across provider switches
        var selectedModel = GetSelectedModel();
        s.SetModelForProvider(GetSelectedProvider(), selectedModel);
        s.Model = selectedModel;

        if (int.TryParse(RefreshIntervalBox.Text, out var interval) && interval >= 100)
            s.RefreshIntervalMs = interval;

        s.ShowUnresolvedAsQuestionMarks = ShowUnresolvedCheck.IsChecked == true;
        s.UseStreaming = StreamingCheck.IsChecked == true;
        s.AutoOpenMemoryBrowser = AutoOpenMemoryBrowserCheck.IsChecked == true;
        s.CheckForUpdatesOnStartup = CheckForUpdatesCheck.IsChecked == true;

        if (int.TryParse(RateLimitBox.Text, out var rateLimit) && rateLimit >= 0)
            s.RateLimitSeconds = rateLimit;
        s.RateLimitWait = RateLimitWaitCheck.IsChecked == true;

        s.Theme = ThemeLight.IsChecked == true ? "Light"
                : ThemeDark.IsChecked == true ? "Dark"
                : "System";

        s.DensityPreset = DensityClean.IsChecked == true ? "Clean"
                        : DensityDense.IsChecked == true ? "Dense"
                        : "Balanced";

        // Token profile
        s.TokenProfile = ProfileSaving.IsChecked == true ? "saving"
                       : ProfilePerformance.IsChecked == true ? "performance"
                       : "balanced";

        // Advanced overrides (blank = null = use profile default)
        s.LimitMaxOutputTokens = ParseLimitBox(LimitMaxOutputTokens);
        s.LimitMaxImagesPerTurn = ParseLimitBox(LimitMaxImagesPerTurn);
        s.LimitMaxApprovalRounds = ParseLimitBox(LimitMaxApprovalRounds);
        s.LimitMaxReplayMessages = ParseLimitBox(LimitMaxReplayMessages);
        s.LimitMaxToolResultChars = ParseLimitBox(LimitMaxToolResultChars);
        s.LimitMaxStackFrames = ParseLimitBox(LimitMaxStackFrames);
        s.LimitMaxBrowseMemoryBytes = ParseLimitBox(LimitMaxBrowseMemoryBytes);
        s.LimitMaxHitLogEntries = ParseLimitBox(LimitMaxHitLogEntries);
        s.LimitMaxSearchResults = ParseLimitBox(LimitMaxSearchResults);
        s.LimitMaxChatSearchResults = ParseLimitBox(LimitMaxChatSearchResults);
        s.LimitFilterRegisters = LimitFilterRegisters.IsChecked; // null = use profile
        s.LimitDereferenceHookRegisters = LimitDereferenceHookRegisters.IsChecked;

        // Agent tab
        s.PermissionMode = PermModeReadOnly.IsChecked == true ? "ReadOnly"
                         : PermModePlanOnly.IsChecked == true ? "PlanOnly"
                         : PermModeUnrestricted.IsChecked == true ? "Unrestricted"
                         : "Normal";

        if (decimal.TryParse(MaxSessionCostBox.Text, out var maxCost) && maxCost >= 0)
            s.MaxSessionCostDollars = maxCost;
        else
            s.MaxSessionCostDollars = 0;

        if (decimal.TryParse(InputPriceBox.Text, out var inputPrice) && inputPrice >= 0)
            s.InputPricePerMillion = inputPrice;
        if (decimal.TryParse(OutputPriceBox.Text, out var outputPrice) && outputPrice >= 0)
            s.OutputPricePerMillion = outputPrice;
        if (decimal.TryParse(CachedInputPriceBox.Text, out var cachedPrice) && cachedPrice >= 0)
            s.CachedInputPricePerMillion = cachedPrice;

        s.FallbackModels = string.IsNullOrWhiteSpace(FallbackModelsBox.Text)
            ? []
            : FallbackModelsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        s.EnableAgentMemory = EnableAgentMemoryCheck.IsChecked == true;
        if (int.TryParse(MaxMemoryEntriesBox.Text, out var maxMem) && maxMem > 0)
            s.MaxMemoryEntries = maxMem;

        s.RequirePlanForDestructive = RequirePlanCheck.IsChecked == true;
        s.EnableEarlyToolExecution = EarlyToolExecutionCheck.IsChecked == true;

        // Additional General settings
        if (int.TryParse(MaxConversationMessagesBox.Text, out var maxMsg) && maxMsg >= 0)
            s.MaxConversationMessages = maxMsg;
        if (int.TryParse(AutoSaveIntervalBox.Text, out var autoSave) && autoSave >= 1)
            s.AutoSaveIntervalMinutes = autoSave;
        if (int.TryParse(LogRetentionBox.Text, out var logDays) && logDays >= 1)
            s.LogRetentionDays = logDays;

        // Scanning
        if (int.TryParse(ScanThreadCountBox.Text, out var threads) && threads >= 1)
            s.ScanThreadCount = Math.Min(threads, 32);
        if (DefaultScanDataTypeCombo.SelectedItem is ComboBoxItem scanTypeItem)
            s.DefaultScanDataType = (string)scanTypeItem.Content;

        // Lua
        if (int.TryParse(LuaTimeoutBox.Text, out var luaTimeout) && luaTimeout >= 1)
            s.LuaExecutionTimeoutSeconds = Math.Min(luaTimeout, 300);

        // Memory Browser
        if (MemBrowserBytesCombo.SelectedItem is ComboBoxItem memItem &&
            int.TryParse((string)memItem.Content, out var bytesPerRow) && bytesPerRow > 0)
            s.MemoryBrowserBytesPerRow = bytesPerRow;

        _settingsService.Save();

        // Background-validate the saved key (non-blocking warning)
        var savedProvider = GetSelectedProvider();
        var savedKey = s.GetApiKeyForProvider(savedProvider);
        var savedEndpoint = savedProvider == "openai-compatible" ? s.CustomEndpoint : null;
        if (!string.IsNullOrWhiteSpace(savedKey) && savedProvider != "copilot")
        {
            _ = Task.Run(async () =>
            {
                var (isValid, error) = await ApiKeyValidator.ValidateAsync(savedProvider, savedKey, savedEndpoint);
                if (!isValid)
                {
                    Dispatcher.Invoke(() =>
                        MessageBox.Show(
                            $"Warning: API key validation failed for {savedProvider}.\n{error}\n\nThe key was saved, but may not work.",
                            "Key Validation Warning", MessageBoxButton.OK, MessageBoxImage.Warning));
                }
            });
        }

        MessageBox.Show("Settings saved. Provider and model changes take effect on next message.",
            "Settings Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        DialogResult = true;
        Close();
    }

    // ─── Copilot Usage ──────────────────────────────────────────────

    private async void RefreshUsage_Click(object sender, RoutedEventArgs e)
    {
        await FetchAndDisplayUsageAsync();
    }

    private void TryAutoFetchUsage()
    {
        var githubToken = _ghTokenVisible ? GitHubTokenTextBox.Text : GitHubTokenBox.Password;
        if (GetSelectedProvider() == "copilot" && !string.IsNullOrWhiteSpace(githubToken))
        {
            CopilotUsagePanel.Visibility = Visibility.Visible;
            _ = FetchAndDisplayUsageAsync();
        }
        else
        {
            CopilotUsagePanel.Visibility = Visibility.Collapsed;
        }
    }

    private async Task FetchAndDisplayUsageAsync()
    {
        var githubToken = _ghTokenVisible ? GitHubTokenTextBox.Text : GitHubTokenBox.Password;
        if (string.IsNullOrWhiteSpace(githubToken))
        {
            UsageErrorText.Text = "No GitHub token available";
            return;
        }

        RefreshUsageBtn.IsEnabled = false;
        UsageErrorText.Text = "";

        try
        {
            var usage = await ChatClientFactory.CopilotService.GetUsageAsync(githubToken);

            Dispatcher.Invoke(() =>
            {
                CopilotUsagePanel.Visibility = Visibility.Visible;
                UsagePlanText.Text = usage.Plan;
                UsageResetText.Text = usage.ResetDate;

                // Premium interactions
                var pi = usage.PremiumInteractions;
                if (pi.Unlimited)
                {
                    PremiumProgressBar.Value = 100;
                    PremiumUsageText.Text = "Unlimited";
                }
                else
                {
                    var used = pi.Entitlement - pi.Remaining;
                    PremiumProgressBar.Maximum = pi.Entitlement > 0 ? pi.Entitlement : 1;
                    PremiumProgressBar.Value = used;
                    PremiumUsageText.Text = $"{used} / {pi.Entitlement} used";
                }

                // Chat quota
                if (usage.Chat.Unlimited)
                {
                    ChatQuotaPanel.Visibility = Visibility.Collapsed;
                }
                else
                {
                    ChatQuotaPanel.Visibility = Visibility.Visible;
                    var chatUsed = usage.Chat.Entitlement - usage.Chat.Remaining;
                    ChatQuotaText.Text = $"{chatUsed} / {usage.Chat.Entitlement} used";
                }

                // Completions quota
                if (usage.Completions.Unlimited)
                {
                    CompletionsQuotaPanel.Visibility = Visibility.Collapsed;
                }
                else
                {
                    CompletionsQuotaPanel.Visibility = Visibility.Visible;
                    var compUsed = usage.Completions.Entitlement - usage.Completions.Remaining;
                    CompletionsQuotaText.Text = $"{compUsed} / {usage.Completions.Entitlement} used";
                }

                UsageErrorText.Text = "";
            });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
                UsageErrorText.Text = $"Failed to load usage: {ex.Message}";
            });
        }
        finally
        {
            Dispatcher.Invoke(() => RefreshUsageBtn.IsEnabled = true);
        }
    }

    // ─── Token Profile Helpers ────────────────────────────────────────

    private void ProfileCard_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string profile) return;
        UpdateProfileDescription(profile);
    }

    private void UpdateProfileDescription(string profile)
    {
        if (ProfileDescription is null) return;
        ProfileDescription.Text = profile switch
        {
            "saving" => "Minimizes tokens per request. Smaller responses, fewer results, filtered registers. Best for pay-per-token APIs.",
            "performance" => "Maximizes detail and context. Larger responses, more results, all registers, auto-dereference. Higher token cost.",
            _ => "Balanced trade-off between token cost and AI capability. Recommended for most users.",
        };
    }

    private static void LoadLimitBox(TextBox box, int? value)
    {
        box.Text = value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "";
    }

    private static int? ParseLimitBox(TextBox box)
    {
        var text = box.Text.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        return int.TryParse(text, out var val) && val > 0 ? val : null;
    }

    public void Dispose()
    {
        _deviceFlowCts?.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        Dispose();
        base.OnClosed(e);
    }

    private void CancelSettings(object sender, RoutedEventArgs e)
    {
        _deviceFlowCts?.Cancel();
        var savedTheme = Enum.TryParse<AppTheme>(_settingsService.Settings.Theme, true, out var t) ? t : AppTheme.System;
        ThemeManager.ApplyTheme(savedTheme);

        DialogResult = false;
        Close();
    }

    private void ResetLayoutFromSettings(object sender, RoutedEventArgs e)
    {
        var layoutPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CEAISuite", "layout.xml");
        try { System.IO.File.Delete(layoutPath); } catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"[SettingsWindow] Failed to delete layout file: {ex.Message}"); }
        MessageBox.Show("Layout reset. Restart the application to apply the default layout.",
            "Reset Layout", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ─── GitHub Device Flow ─────────────────────────────────────────

    private async void GitHubSignIn_Click(object sender, RoutedEventArgs e)
    {
        _deviceFlowCts?.Cancel();
        _deviceFlowCts = new CancellationTokenSource();
        var ct = _deviceFlowCts.Token;

        try
        {
            GitHubSignInBtn.IsEnabled = false;
            GitHubSignInBtn.Content = "Starting…";
            DeviceFlowPanel.Visibility = Visibility.Visible;
            DeviceFlowProgress.IsIndeterminate = true;
            DeviceFlowStatusText.Text = "Contacting GitHub…";

            var service = ChatClientFactory.CopilotService;
            var start = await service.StartDeviceFlowAsync();

            // Show the code and open browser
            DeviceCodeText.Text = start.UserCode;
            DeviceFlowStatusText.Text = "Waiting for authorization…";
            GitHubSignInBtn.Content = "Waiting…";

            // Copy code to clipboard and open verification URL
            Clipboard.SetText(start.UserCode);
            Process.Start(new ProcessStartInfo(start.VerificationUri) { UseShellExecute = true });

            // Poll until authorized
            var oauthToken = await service.PollDeviceFlowAsync(
                start.DeviceCode, start.PollIntervalSeconds, start.ExpiresInSeconds, ct);

            // Success — store the token
            GitHubTokenBox.Password = oauthToken;
            GitHubTokenTextBox.Text = oauthToken;
            GitHubTokenStatus.Text = "✓ Signed in to GitHub Copilot";
            GitHubTokenStatus.SetResourceReference(TextBlock.ForegroundProperty, "SuccessForeground");
            GitHubSignInBtn.Content = "✓  Signed in — sign in again?";
            CopilotSignOutBtn.Visibility = Visibility.Visible;
            DeviceCodeText.Text = ""; // Clear device code after successful authorization
            DeviceFlowPanel.Visibility = Visibility.Collapsed;

            // Invalidate cached models so they re-fetch with new token
            service.InvalidateModels();
            service.Invalidate();

            // Auto-refresh the model list
            if (GetSelectedProvider() == "copilot")
                PopulateModelList("copilot");

            // Auto-fetch usage after sign-in
            TryAutoFetchUsage();
        }
        catch (OperationCanceledException)
        {
            DeviceFlowStatusText.Text = "Cancelled";
        }
        catch (Exception ex)
        {
            DeviceFlowStatusText.Text = $"Failed: {ex.Message}";
            GitHubTokenStatus.Text = $"Auth failed: {ex.Message}";
            GitHubTokenStatus.SetResourceReference(TextBlock.ForegroundProperty, "ErrorForeground");
        }
        finally
        {
            GitHubSignInBtn.IsEnabled = true;
            DeviceFlowProgress.IsIndeterminate = false;
            if (GitHubSignInBtn.Content is string s && s == "Waiting…")
                GitHubSignInBtn.Content = "🔗  Sign in with GitHub";
        }
    }

    private void CopyDeviceCode_Click(object sender, MouseButtonEventArgs e)
    {
        if (DeviceCodeText.Text is { Length: > 0 } code)
        {
            Clipboard.SetText(code);
            DeviceFlowStatusText.Text = "Code copied! Waiting for authorization…";
        }
    }

    private void CopilotSignOut_Click(object sender, RoutedEventArgs e)
    {
        // Clear tokens
        _settingsService.Settings.GitHubToken = null;
        _settingsService.Settings.EncryptedGitHubToken = null;

        // Invalidate cached session tokens
        ChatClientFactory.CopilotService.Invalidate();
        ChatClientFactory.CopilotService.InvalidateModels();

        // Clear UI
        GitHubTokenBox.Password = "";
        GitHubTokenTextBox.Text = "";
        DeviceCodeText.Text = "";
        GitHubTokenStatus.Text = "Not signed in";
        GitHubTokenStatus.SetResourceReference(TextBlock.ForegroundProperty, "WarningForeground");
        CopilotSignOutBtn.Visibility = Visibility.Collapsed;
        GitHubSignInBtn.Content = "🔗  Sign in with GitHub";

        // Hide usage panel
        CopilotUsagePanel.Visibility = Visibility.Collapsed;

        // Clear model list since token is gone
        if (GetSelectedProvider() == "copilot")
            PopulateModelList("copilot");

        // Save immediately
        _settingsService.Save();
    }

    private void CaptionClose_Click(object sender, RoutedEventArgs e) => Close();
}
