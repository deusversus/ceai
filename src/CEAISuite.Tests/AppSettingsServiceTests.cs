using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CEAISuite.Application;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for AppSettings model and AppSettingsService: DPAPI encryption,
/// defaults, key health, and provider-keyed accessors.
/// File I/O tests use DPAPI directly (same code path as the service).
/// </summary>
[SupportedOSPlatform("windows")]
public class AppSettingsServiceTests
{
    // ── AppSettings defaults ──

    [Fact]
    public void AppSettings_Defaults_AreCorrect()
    {
        var settings = new AppSettings();
        Assert.Equal("openai", settings.Provider);
        Assert.Equal("gpt-5.4", settings.Model);
        Assert.Equal(500, settings.RefreshIntervalMs);
        Assert.True(settings.UseStreaming);
        Assert.True(settings.ShowUnresolvedAsQuestionMarks);
        Assert.Equal("balanced", settings.TokenProfile);
        Assert.Equal(40, settings.MaxConversationMessages);
        Assert.True(settings.EnableAgentMemory);
        Assert.Equal("System", settings.Theme);
    }

    // ── Provider key routing ──

    [Fact]
    public void GetApiKeyForProvider_ReturnsCorrectKey()
    {
        var settings = new AppSettings
        {
            OpenAiApiKey = "openai-key",
            AnthropicApiKey = "anthropic-key",
            GeminiApiKey = "gemini-key",
            CompatibleApiKey = "compat-key",
            GitHubToken = "gh-token",
        };

        Assert.Equal("openai-key", settings.GetApiKeyForProvider("openai"));
        Assert.Equal("anthropic-key", settings.GetApiKeyForProvider("anthropic"));
        Assert.Equal("gemini-key", settings.GetApiKeyForProvider("gemini"));
        Assert.Equal("compat-key", settings.GetApiKeyForProvider("openai-compatible"));
        Assert.Equal("gh-token", settings.GetApiKeyForProvider("copilot"));
        Assert.Equal("openai-key", settings.GetApiKeyForProvider("unknown"));
    }

    [Fact]
    public void GetApiKeyForProvider_NullProvider_ReturnsOpenAiKey()
    {
        var settings = new AppSettings { OpenAiApiKey = "fallback" };
        Assert.Equal("fallback", settings.GetApiKeyForProvider(null));
    }

    // ── Provider model routing ──

    [Fact]
    public void GetModelForProvider_ReturnsCorrectModel()
    {
        var settings = new AppSettings
        {
            OpenAiModel = "gpt-4o",
            AnthropicModel = "claude-sonnet-4-6",
            GeminiModel = "gemini-2.0-flash",
            CopilotModel = "gpt-4o",
            CompatibleModel = "local-llm",
        };

        Assert.Equal("gpt-4o", settings.GetModelForProvider("openai"));
        Assert.Equal("claude-sonnet-4-6", settings.GetModelForProvider("anthropic"));
        Assert.Equal("gemini-2.0-flash", settings.GetModelForProvider("gemini"));
        Assert.Equal("gpt-4o", settings.GetModelForProvider("copilot"));
        Assert.Equal("local-llm", settings.GetModelForProvider("openai-compatible"));
    }

    [Fact]
    public void SetModelForProvider_UpdatesBothProviderAndActiveModel()
    {
        var settings = new AppSettings();
        settings.SetModelForProvider("anthropic", "claude-opus-4-6");

        Assert.Equal("claude-opus-4-6", settings.AnthropicModel);
        Assert.Equal("claude-opus-4-6", settings.Model);
    }

    // ── Gemini refresh token age ──

    [Fact]
    public void GeminiRefreshTokenAgeDays_NoIssueDate_ReturnsNegativeOne()
    {
        var settings = new AppSettings();
        Assert.Equal(-1, settings.GeminiRefreshTokenAgeDays);
    }

    [Fact]
    public void GeminiRefreshTokenAgeDays_WithIssueDate_ReturnsAge()
    {
        var settings = new AppSettings
        {
            GeminiRefreshTokenIssuedUtc = DateTimeOffset.UtcNow.AddDays(-10)
        };
        Assert.InRange(settings.GeminiRefreshTokenAgeDays, 9, 11);
    }

    // ── Key health ──

    [Fact]
    public void GetKeyHealth_ReturnsHealthForConfiguredKeys()
    {
        var settings = new AppSettings
        {
            OpenAiApiKey = "sk-key",
            OpenAiKeyIssuedUtc = DateTimeOffset.UtcNow.AddDays(-5),
        };

        var health = settings.GetKeyHealth();
        Assert.Single(health.Entries);
        Assert.Equal("openai", health.Entries[0].Provider);
        Assert.False(health.Entries[0].IsStale);
        Assert.False(health.HasStaleKeys);
    }

    [Fact]
    public void GetKeyHealth_StaleKey_Flagged()
    {
        var settings = new AppSettings
        {
            OpenAiApiKey = "sk-key",
            OpenAiKeyIssuedUtc = DateTimeOffset.UtcNow.AddDays(-100),
        };

        var health = settings.GetKeyHealth();
        Assert.True(health.HasStaleKeys);
        Assert.True(health.Entries[0].IsStale);
    }

    [Fact]
    public void GetKeyHealth_EmptyKeys_ReturnsNoEntries()
    {
        var settings = new AppSettings();
        var health = settings.GetKeyHealth();
        Assert.Empty(health.Entries);
    }

    [Fact]
    public void GetKeyHealth_MultipleProviders_ReturnsAll()
    {
        var settings = new AppSettings
        {
            OpenAiApiKey = "k1", OpenAiKeyIssuedUtc = DateTimeOffset.UtcNow,
            AnthropicApiKey = "k2", AnthropicKeyIssuedUtc = DateTimeOffset.UtcNow,
            GitHubToken = "k3", GitHubTokenIssuedUtc = DateTimeOffset.UtcNow,
        };

        var health = settings.GetKeyHealth();
        Assert.Equal(3, health.Entries.Count);
    }

    // ── Key rotation tracking ──

    [Fact]
    public void TrackKeyRotation_DetectsChangedKey()
    {
        var previous = new AppSettings { OpenAiApiKey = "old-key" };
        var current = new AppSettings { OpenAiApiKey = "new-key" };

        current.TrackKeyRotation(previous);
        Assert.NotNull(current.OpenAiKeyIssuedUtc);
    }

    [Fact]
    public void TrackKeyRotation_NullPrevious_NoOp()
    {
        var current = new AppSettings { OpenAiApiKey = "key" };
        current.TrackKeyRotation(null); // Should not throw
        Assert.Null(current.OpenAiKeyIssuedUtc);
    }

    [Fact]
    public void TrackKeyRotation_SameKey_NoTimestampUpdate()
    {
        var previous = new AppSettings { OpenAiApiKey = "same-key" };
        var current = new AppSettings { OpenAiApiKey = "same-key" };

        current.TrackKeyRotation(previous);
        Assert.Null(current.OpenAiKeyIssuedUtc);
    }

    // ── DPAPI encryption round-trip (tests the same EncryptString/DecryptString used by service) ──

    [Fact]
    public void DPAPI_EncryptDecrypt_RoundTrips()
    {
        var plaintext = "sk-test-secret-key-12345";
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        var encoded = Convert.ToBase64String(encrypted);

        // Decrypt
        var decoded = Convert.FromBase64String(encoded);
        var decrypted = ProtectedData.Unprotect(decoded, null, DataProtectionScope.CurrentUser);
        var result = Encoding.UTF8.GetString(decrypted);

        Assert.Equal(plaintext, result);
    }

    [Fact]
    public void DPAPI_EncryptDecrypt_EmptyString_RoundTrips()
    {
        var plaintext = "";
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        Assert.Equal(plaintext, Encoding.UTF8.GetString(decrypted));
    }

    // ── AppSettingsService integration (using actual path) ──

    [Fact]
    public void AppSettingsService_Load_DefaultState()
    {
        using var svc = new AppSettingsService();
        svc.Load();

        // Should load without error — settings may come from existing file or defaults
        Assert.NotNull(svc.Settings);
    }

    [Fact]
    public void AppSettingsService_SettingsChangedEvent_FiresOnSave()
    {
        using var svc = new AppSettingsService();
        svc.Load();

        bool eventFired = false;
        svc.SettingsChanged += () => eventFired = true;
        svc.Save();

        Assert.True(eventFired);
    }

    // ── SensitiveString initialization ──

    [Fact]
    public void InitializeSensitiveKeys_CreatesAndDisposesCorrectly()
    {
        var settings = new AppSettings
        {
            OpenAiApiKey = "test-key",
            AnthropicApiKey = "ant-key",
        };

        settings.InitializeSensitiveKeys();

        Assert.NotNull(settings.GetSensitiveKey("openai"));
        Assert.NotNull(settings.GetSensitiveKey("anthropic"));
        Assert.Null(settings.GetSensitiveKey("gemini")); // Not set

        settings.Dispose(); // Should dispose SensitiveString instances
    }

    // ── McpServerSettingsEntry ──

    [Fact]
    public void McpServerSettingsEntry_Defaults()
    {
        var entry = new McpServerSettingsEntry();
        Assert.Equal("", entry.Name);
        Assert.Equal("", entry.Command);
        Assert.Null(entry.Arguments);
        Assert.True(entry.AutoConnect);
        Assert.True(entry.Enabled);
    }

    // ── JSON serialization ──

    [Fact]
    public void AppSettings_JsonIgnore_PlaintextKeysNotSerialized()
    {
        var settings = new AppSettings
        {
            OpenAiApiKey = "should-not-appear",
            AnthropicApiKey = "also-hidden",
            EncryptedApiKey = "this-appears",
        };

        var json = JsonSerializer.Serialize(settings);
        Assert.DoesNotContain("should-not-appear", json);
        Assert.DoesNotContain("also-hidden", json);
        Assert.Contains("this-appears", json);
    }
}
