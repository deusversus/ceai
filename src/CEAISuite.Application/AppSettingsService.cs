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
    public bool AutoHideMenuBar { get; set; }
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

    // ── Auto-Update ──

    /// <summary>Check GitHub for updates when the application starts.</summary>
    public bool CheckForUpdatesOnStartup { get; set; } = true;

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
    private static readonly JsonSerializerOptions s_indentedJsonOptions = new() { WriteIndented = true };

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

        // Decrypt stored API key
        if (!string.IsNullOrWhiteSpace(_settings.EncryptedApiKey))
        {
            try
            {
                _settings.OpenAiApiKey = DecryptString(_settings.EncryptedApiKey);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to decrypt stored API key — clearing to null");
                _settings.OpenAiApiKey = null;
            }
        }

        // Decrypt stored GitHub token (for Copilot provider)
        if (!string.IsNullOrWhiteSpace(_settings.EncryptedGitHubToken))
        {
            try
            {
                _settings.GitHubToken = DecryptString(_settings.EncryptedGitHubToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to decrypt stored GitHub token — clearing to null");
                _settings.GitHubToken = null;
            }
        }

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
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings, s_indentedJsonOptions));
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
