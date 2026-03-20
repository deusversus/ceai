using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CEAISuite.Application;

/// <summary>
/// Persisted settings. The API key is stored encrypted on disk via DPAPI;
/// the <see cref="OpenAiApiKey"/> property holds the *plaintext* at runtime,
/// while <see cref="EncryptedApiKey"/> holds the Base64-encoded ciphertext for serialization.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Runtime-only plaintext key (not serialized).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? OpenAiApiKey { get; set; }

    /// <summary>DPAPI-encrypted, Base64-encoded API key (written to disk).</summary>
    public string? EncryptedApiKey { get; set; }

    /// <summary>AI provider: "openai", "anthropic", "openai-compatible", "copilot".</summary>
    public string Provider { get; set; } = "openai";

    /// <summary>For OpenAI-compatible endpoints (e.g., local LLMs, Azure, etc.).</summary>
    public string? CustomEndpoint { get; set; }

    /// <summary>Runtime-only plaintext GitHub token for Copilot provider (not serialized).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? GitHubToken { get; set; }

    /// <summary>DPAPI-encrypted, Base64-encoded GitHub token (written to disk).</summary>
    public string? EncryptedGitHubToken { get; set; }

    public string Model { get; set; } = "gpt-5.4";
    public int RefreshIntervalMs { get; set; } = 500;
    public bool ShowUnresolvedAsQuestionMarks { get; set; } = true;
    public bool AutoHideMenuBar { get; set; } = false;
    public string Theme { get; set; } = "System";

    /// <summary>UI density preset: "Clean", "Balanced", "Dense". Controls default panel visibility.</summary>
    public string DensityPreset { get; set; } = "Balanced";

    /// <summary>Automatically open Memory Browser tab when attaching to a process.</summary>
    public bool AutoOpenMemoryBrowser { get; set; } = true;

    /// <summary>Maximum conversation messages (excluding system prompt) sent to the API. 0 = unlimited.</summary>
    public int MaxConversationMessages { get; set; } = 40;

    /// <summary>Minimum seconds between AI requests. 0 = disabled.</summary>
    public int RateLimitSeconds { get; set; } = 0;

    /// <summary>If true, queue and wait for cooldown; if false, reject with error.</summary>
    public bool RateLimitWait { get; set; } = true;

    /// <summary>Stream AI responses token-by-token. Disable for batch responses.</summary>
    public bool UseStreaming { get; set; } = true;

    // ── Token Limits ──

    /// <summary>Token profile preset: "saving", "balanced", "performance".</summary>
    public string TokenProfile { get; set; } = "balanced";

    // Per-field overrides (null = use profile default)
    public int? LimitMaxOutputTokens { get; set; }
    public int? LimitMaxImagesPerTurn { get; set; }
    public int? LimitMaxApprovalRounds { get; set; }
    public int? LimitMaxReplayMessages { get; set; }
    public int? LimitMaxToolResultChars { get; set; }
    public int? LimitMaxStackFrames { get; set; }
    public int? LimitMaxBrowseMemoryBytes { get; set; }
    public int? LimitMaxHitLogEntries { get; set; }
    public int? LimitMaxSearchResults { get; set; }
    public int? LimitMaxChatSearchResults { get; set; }
    public bool? LimitFilterRegisters { get; set; }
    public bool? LimitDereferenceHookRegisters { get; set; }
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class AppSettingsService
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CEAISuite");
    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    private AppSettings _settings = new();

    public AppSettings Settings => _settings;

    /// <summary>Event raised when settings change (after Save).</summary>
    public event Action? SettingsChanged;

    public void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                _settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new();
            }
        }
        catch { _settings = new(); }

        // Decrypt stored API key
        if (!string.IsNullOrWhiteSpace(_settings.EncryptedApiKey))
        {
            try
            {
                _settings.OpenAiApiKey = DecryptString(_settings.EncryptedApiKey);
            }
            catch { _settings.OpenAiApiKey = null; }
        }

        // Decrypt stored GitHub token (for Copilot provider)
        if (!string.IsNullOrWhiteSpace(_settings.EncryptedGitHubToken))
        {
            try
            {
                _settings.GitHubToken = DecryptString(_settings.EncryptedGitHubToken);
            }
            catch { _settings.GitHubToken = null; }
        }

        // Environment variable overrides stored key if present
        var envKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                  ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey) && string.IsNullOrWhiteSpace(_settings.OpenAiApiKey))
            _settings.OpenAiApiKey = envKey;

        var envGitHub = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(envGitHub) && string.IsNullOrWhiteSpace(_settings.GitHubToken))
            _settings.GitHubToken = envGitHub;

        var envModel = Environment.GetEnvironmentVariable("CEAI_MODEL");
        if (!string.IsNullOrWhiteSpace(envModel))
            _settings.Model = envModel;

        var envProvider = Environment.GetEnvironmentVariable("CEAI_PROVIDER");
        if (!string.IsNullOrWhiteSpace(envProvider))
            _settings.Provider = envProvider;

        var envEndpoint = Environment.GetEnvironmentVariable("CEAI_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(envEndpoint))
            _settings.CustomEndpoint = envEndpoint;
    }

    public void Save()
    {
        // Encrypt API key before writing
        if (!string.IsNullOrWhiteSpace(_settings.OpenAiApiKey))
            _settings.EncryptedApiKey = EncryptString(_settings.OpenAiApiKey);
        else
            _settings.EncryptedApiKey = null;

        // Encrypt GitHub token before writing
        if (!string.IsNullOrWhiteSpace(_settings.GitHubToken))
            _settings.EncryptedGitHubToken = EncryptString(_settings.GitHubToken);
        else
            _settings.EncryptedGitHubToken = null;

        Directory.CreateDirectory(SettingsDir);
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings, options));
        SettingsChanged?.Invoke();
    }

    /// <summary>Encrypt a string using Windows DPAPI (CurrentUser scope).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string EncryptString(string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    /// <summary>Decrypt a DPAPI-protected Base64 string.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string DecryptString(string encrypted)
    {
        var bytes = Convert.FromBase64String(encrypted);
        var decrypted = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(decrypted);
    }
}
