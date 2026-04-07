using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

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

    // ── Per-provider API keys (each pair: runtime plaintext + disk-encrypted) ──

    /// <summary>Runtime-only plaintext Anthropic key (not serialized).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? AnthropicApiKey { get; set; }
    public string? EncryptedAnthropicApiKey { get; set; }

    /// <summary>Runtime-only plaintext Gemini key (not serialized).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? GeminiApiKey { get; set; }
    public string? EncryptedGeminiApiKey { get; set; }

    /// <summary>Runtime-only plaintext Gemini OAuth token (not serialized).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? GeminiOAuthToken { get; set; }
    public string? EncryptedGeminiOAuthToken { get; set; }

    /// <summary>Runtime-only plaintext Gemini refresh token (not serialized).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? GeminiRefreshToken { get; set; }
    public string? EncryptedGeminiRefreshToken { get; set; }

    /// <summary>Gemini authentication method: "api_key" or "oauth".</summary>
    public string GeminiAuthMethod { get; set; } = "api_key";

    /// <summary>Google OAuth client ID for Gemini (not serialized — encrypted on disk).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? GeminiOAuthClientId { get; set; }
    public string? EncryptedGeminiOAuthClientId { get; set; }

    /// <summary>Google OAuth client secret for Gemini (not serialized — encrypted on disk).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? GeminiOAuthClientSecret { get; set; }
    public string? EncryptedGeminiOAuthClientSecret { get; set; }

    /// <summary>Runtime-only plaintext OpenAI-compatible key (not serialized).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? CompatibleApiKey { get; set; }
    public string? EncryptedCompatibleApiKey { get; set; }

    /// <summary>Settings schema version for migrations.</summary>
    public int SettingsVersion { get; set; }

    /// <summary>AI provider: "openai", "anthropic", "gemini", "openai-compatible", "copilot".</summary>
    public string Provider { get; set; } = "openai";

    /// <summary>For OpenAI-compatible endpoints (e.g., local LLMs, Azure, etc.).</summary>
    public string? CustomEndpoint { get; set; }

    /// <summary>Runtime-only plaintext GitHub token for Copilot provider (not serialized).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? GitHubToken { get; set; }

    /// <summary>DPAPI-encrypted, Base64-encoded GitHub token (written to disk).</summary>
    public string? EncryptedGitHubToken { get; set; }

    public string Model { get; set; } = "gpt-5.4";

    // Per-provider model selection (preserves custom model across provider switches)
    public string OpenAiModel { get; set; } = "gpt-5.4";
    public string AnthropicModel { get; set; } = "claude-sonnet-4-6";
    public string GeminiModel { get; set; } = "gemini-3.1-flash-lite-preview";
    public string CopilotModel { get; set; } = "gpt-4o";
    public string CompatibleModel { get; set; } = "";

    public int RefreshIntervalMs { get; set; } = 500;
    public bool ShowUnresolvedAsQuestionMarks { get; set; } = true;
    public string Theme { get; set; } = "System";

    /// <summary>UI density preset: "Clean", "Balanced", "Dense". Controls default panel visibility.</summary>
    public string DensityPreset { get; set; } = "Balanced";

    /// <summary>Automatically open Memory Browser tab when attaching to a process.</summary>
    public bool AutoOpenMemoryBrowser { get; set; } = true;

    /// <summary>Maximum conversation messages (excluding system prompt) sent to the API. 0 = unlimited.</summary>
    public int MaxConversationMessages { get; set; } = 40;

    /// <summary>Minimum seconds between AI requests. 0 = disabled.</summary>
    public int RateLimitSeconds { get; set; }

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

    // ── MCP Servers ──

    /// <summary>
    /// Configured MCP (Model Context Protocol) servers. Each server provides
    /// external tools that the agent can use alongside built-in tools.
    /// </summary>
    public List<McpServerSettingsEntry> McpServers { get; set; } = [];

    // ── Token Budget ──

    /// <summary>Maximum session cost in USD. 0 = unlimited.</summary>
    public decimal MaxSessionCostDollars { get; set; } = 0;

    /// <summary>Input token price per million (for cost estimation).</summary>
    public decimal InputPricePerMillion { get; set; } = 3.00m;

    /// <summary>Output token price per million (for cost estimation).</summary>
    public decimal OutputPricePerMillion { get; set; } = 15.00m;

    /// <summary>Cached input token price per million.</summary>
    public decimal CachedInputPricePerMillion { get; set; } = 0.30m;

    // ── Memory ──

    /// <summary>Enable persistent cross-session agent memory. Default: true.</summary>
    public bool EnableAgentMemory { get; set; } = true;

    /// <summary>Maximum memory entries to keep (older entries are pruned).</summary>
    public int MaxMemoryEntries { get; set; } = 500;

    // ── Plan Mode ──

    /// <summary>Require plan mode for requests that involve destructive operations.</summary>
    public bool RequirePlanForDestructive { get; set; }
    public List<string> FallbackModels { get; set; } = [];
    public string PermissionMode { get; set; } = "Normal";

    /// <summary>
    /// Enable speculative early tool execution during LLM streaming.
    /// Read-only tools start executing before the full response arrives.
    /// Default: false (conservative; enable for lower latency on tool-heavy conversations).
    /// </summary>
    public bool EnableEarlyToolExecution { get; set; }

    // ── General ──

    /// <summary>Auto-save interval in minutes for address table and session data.</summary>
    public int AutoSaveIntervalMinutes { get; set; } = 5;

    /// <summary>Number of days to retain log files before cleanup.</summary>
    public int LogRetentionDays { get; set; } = 14;

    // ── Scanning ──

    /// <summary>Number of threads to use for memory scans.</summary>
    public int ScanThreadCount { get; set; } = Environment.ProcessorCount;

    /// <summary>Default data type for new scans (Byte, Int16, Int32, Int64, Float, Double, String).</summary>
    public string DefaultScanDataType { get; set; } = "Int32";

    // ── Lua ──

    /// <summary>Maximum execution time in seconds for Lua scripts before timeout.</summary>
    public int LuaExecutionTimeoutSeconds { get; set; } = 30;

    // ── Memory Browser ──

    /// <summary>Number of bytes displayed per row in the Memory Browser hex view.</summary>
    public int MemoryBrowserBytesPerRow { get; set; } = 16;

    /// <summary>Set to true after the first-run welcome dialog has been completed.</summary>
    public bool FirstRunCompleted { get; set; }

    /// <summary>Returns the API key for the given provider name.</summary>
    public string? GetApiKeyForProvider(string? provider) => provider?.ToLowerInvariant() switch
    {
        "openai" => OpenAiApiKey,
        "anthropic" => AnthropicApiKey,
        "gemini" => GeminiApiKey,
        "openai-compatible" => CompatibleApiKey,
        "copilot" => GitHubToken,
        _ => OpenAiApiKey,
    };

    public string GetModelForProvider(string? provider) => provider?.ToLowerInvariant() switch
    {
        "openai" => OpenAiModel,
        "anthropic" => AnthropicModel,
        "gemini" => GeminiModel,
        "copilot" => CopilotModel,
        "openai-compatible" => CompatibleModel,
        _ => Model,
    };

    public void SetModelForProvider(string? provider, string model)
    {
        switch (provider?.ToLowerInvariant())
        {
            case "openai": OpenAiModel = model; break;
            case "anthropic": AnthropicModel = model; break;
            case "gemini": GeminiModel = model; break;
            case "copilot": CopilotModel = model; break;
            case "openai-compatible": CompatibleModel = model; break;
        }
        Model = model; // Also update the active model
    }

    // ── Auto-Update ──

    /// <summary>Check GitHub for updates when the application starts.</summary>
    public bool CheckForUpdatesOnStartup { get; set; } = true;

    /// <summary>Opt-in: write submission-ready crash telemetry JSON alongside local crash logs.</summary>
    public bool EnableCrashTelemetry { get; set; }

    /// <summary>Version string the user chose to skip (e.g., "0.3.0"). Null = no skip.</summary>
    public string? SkippedVersion { get; set; }
}

/// <summary>Settings entry for a configured MCP server.</summary>
public sealed class McpServerSettingsEntry
{
    /// <summary>Human-readable server name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Command to execute (e.g., "npx", "python", path to binary).</summary>
    public string Command { get; set; } = "";

    /// <summary>Command-line arguments.</summary>
    public string? Arguments { get; set; }

    /// <summary>Runtime-only plaintext environment variables (not serialized).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<string, string>? Environment { get; set; }

    /// <summary>DPAPI-encrypted environment variable values (written to disk).</summary>
    public Dictionary<string, string>? EncryptedEnvironment { get; set; }

    /// <summary>Whether to auto-connect on startup.</summary>
    public bool AutoConnect { get; set; } = true;

    /// <summary>Whether this server is enabled.</summary>
    public bool Enabled { get; set; } = true;
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class AppSettingsService
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CEAISuite");
    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    private readonly ILogger<AppSettingsService>? _logger;
    private AppSettings _settings = new();

    public AppSettingsService(ILogger<AppSettingsService>? logger = null)
    {
        _logger = logger;
    }

    public AppSettings Settings => _settings;

    /// <summary>True when the welcome dialog has never been completed.</summary>
    public bool IsFirstRun => !_settings.FirstRunCompleted;

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
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to deserialize settings from {Path}, using defaults", SettingsPath);
            _settings = new();
        }

        // Decrypt stored secrets
        _settings.OpenAiApiKey = TryDecryptField(_settings.EncryptedApiKey, _logger, "OpenAI API key");
        _settings.GitHubToken = TryDecryptField(_settings.EncryptedGitHubToken, _logger, "GitHub token");

        // Decrypt per-provider API keys
        _settings.AnthropicApiKey = TryDecryptField(_settings.EncryptedAnthropicApiKey, _logger, "Anthropic API key");
        _settings.GeminiApiKey = TryDecryptField(_settings.EncryptedGeminiApiKey, _logger, "Gemini API key");
        _settings.GeminiOAuthToken = TryDecryptField(_settings.EncryptedGeminiOAuthToken, _logger, "Gemini OAuth token");
        _settings.GeminiRefreshToken = TryDecryptField(_settings.EncryptedGeminiRefreshToken, _logger, "Gemini refresh token");
        _settings.GeminiOAuthClientId = TryDecryptField(_settings.EncryptedGeminiOAuthClientId, _logger, "Gemini OAuth client ID");
        _settings.GeminiOAuthClientSecret = TryDecryptField(_settings.EncryptedGeminiOAuthClientSecret, _logger, "Gemini OAuth client secret");
        _settings.CompatibleApiKey = TryDecryptField(_settings.EncryptedCompatibleApiKey, _logger, "Compatible API key");

        // Decrypt MCP server environment variables
        foreach (var mcp in _settings.McpServers)
        {
            if (mcp.EncryptedEnvironment is { Count: > 0 })
            {
                try
                {
                    mcp.Environment = mcp.EncryptedEnvironment.ToDictionary(
                        kv => kv.Key,
                        kv => DecryptString(kv.Value));
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to decrypt MCP server '{Name}' environment variables", mcp.Name);
                    mcp.Environment = null;
                }
            }
        }

        // Migrate from shared OpenAiApiKey to per-provider keys (one-time)
        if (_settings.SettingsVersion < 1 && !string.IsNullOrWhiteSpace(_settings.OpenAiApiKey))
        {
            switch (_settings.Provider?.ToLowerInvariant())
            {
                case "anthropic":
                    _settings.AnthropicApiKey ??= _settings.OpenAiApiKey;
                    break;
                case "openai-compatible":
                    _settings.CompatibleApiKey ??= _settings.OpenAiApiKey;
                    break;
                // "openai" stays in OpenAiApiKey — no migration needed
            }
            _settings.SettingsVersion = 1;
            Save(); // Persist migration
        }

        // Environment variable overrides stored key if present
        var envAnthropicKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!string.IsNullOrWhiteSpace(envAnthropicKey) && string.IsNullOrWhiteSpace(_settings.AnthropicApiKey))
            _settings.AnthropicApiKey = envAnthropicKey;

        var envOpenAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(envOpenAiKey) && string.IsNullOrWhiteSpace(_settings.OpenAiApiKey))
            _settings.OpenAiApiKey = envOpenAiKey;

        var envGeminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (!string.IsNullOrWhiteSpace(envGeminiKey) && string.IsNullOrWhiteSpace(_settings.GeminiApiKey))
            _settings.GeminiApiKey = envGeminiKey;

        var envGeminiClientId = Environment.GetEnvironmentVariable("GEMINI_OAUTH_CLIENT_ID");
        if (!string.IsNullOrWhiteSpace(envGeminiClientId) && string.IsNullOrWhiteSpace(_settings.GeminiOAuthClientId))
            _settings.GeminiOAuthClientId = envGeminiClientId;
        var envGeminiClientSecret = Environment.GetEnvironmentVariable("GEMINI_OAUTH_CLIENT_SECRET");
        if (!string.IsNullOrWhiteSpace(envGeminiClientSecret) && string.IsNullOrWhiteSpace(_settings.GeminiOAuthClientSecret))
            _settings.GeminiOAuthClientSecret = envGeminiClientSecret;

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
        // Encrypt secrets before writing
        _settings.EncryptedApiKey = EncryptField(_settings.OpenAiApiKey);
        _settings.EncryptedGitHubToken = EncryptField(_settings.GitHubToken);

        // Encrypt per-provider API keys before writing
        _settings.EncryptedAnthropicApiKey = EncryptField(_settings.AnthropicApiKey);
        _settings.EncryptedGeminiApiKey = EncryptField(_settings.GeminiApiKey);
        _settings.EncryptedGeminiOAuthToken = EncryptField(_settings.GeminiOAuthToken);
        _settings.EncryptedGeminiRefreshToken = EncryptField(_settings.GeminiRefreshToken);
        _settings.EncryptedGeminiOAuthClientId = EncryptField(_settings.GeminiOAuthClientId);
        _settings.EncryptedGeminiOAuthClientSecret = EncryptField(_settings.GeminiOAuthClientSecret);
        _settings.EncryptedCompatibleApiKey = EncryptField(_settings.CompatibleApiKey);

        // Encrypt MCP server environment variables before writing
        foreach (var mcp in _settings.McpServers)
        {
            if (mcp.Environment is { Count: > 0 })
            {
                mcp.EncryptedEnvironment = mcp.Environment.ToDictionary(
                    kv => kv.Key,
                    kv => EncryptString(kv.Value));
            }
            else
            {
                mcp.EncryptedEnvironment = null;
            }
        }

        Directory.CreateDirectory(SettingsDir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings, IndentedJsonOptions));
        SettingsChanged?.Invoke();
    }

    /// <summary>Decrypt a stored field, returning null on failure or if input is blank.</summary>
    private static string? TryDecryptField(string? encrypted, ILogger? logger, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(encrypted)) return null;
        try { return DecryptString(encrypted); }
        catch (Exception ex) { logger?.LogWarning(ex, "Failed to decrypt {Field}", fieldName); return null; }
    }

    /// <summary>Encrypt a field for storage, returning null if plaintext is blank.</summary>
    private static string? EncryptField(string? plaintext)
        => string.IsNullOrWhiteSpace(plaintext) ? null : EncryptString(plaintext);

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
