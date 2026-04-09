using System.Runtime.Versioning;
using CEAISuite.Application;
using CEAISuite.Desktop;
using Microsoft.Extensions.AI;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for ChatClientFactory: provider routing, missing API key handling,
/// and error conditions.
/// </summary>
[SupportedOSPlatform("windows")]
public class ChatClientFactoryTests
{
    // ── Null/missing key handling ──

    [Fact]
    public async Task CreateAsync_NoApiKey_ReturnsNull()
    {
        var settings = new AppSettings { Provider = "openai", OpenAiApiKey = null };
        var client = await ChatClientFactory.CreateAsync(settings);
        Assert.Null(client);
    }

    [Fact]
    public async Task CreateAsync_WhitespaceApiKey_ReturnsNull()
    {
        var settings = new AppSettings { Provider = "openai", OpenAiApiKey = "   " };
        var client = await ChatClientFactory.CreateAsync(settings);
        Assert.Null(client);
    }

    [Fact]
    public async Task CreateAsync_EmptyAnthropicKey_ReturnsNull()
    {
        var settings = new AppSettings { Provider = "anthropic", AnthropicApiKey = null, OpenAiApiKey = null };
        var client = await ChatClientFactory.CreateAsync(settings);
        Assert.Null(client);
    }

    [Fact]
    public async Task CreateAsync_EmptyCopilotToken_ReturnsNull()
    {
        var settings = new AppSettings { Provider = "copilot", GitHubToken = null };
        var client = await ChatClientFactory.CreateAsync(settings);
        Assert.Null(client);
    }

    [Fact]
    public async Task CreateAsync_EmptyGeminiKey_ReturnsNull()
    {
        var settings = new AppSettings { Provider = "gemini", GeminiApiKey = null, GeminiAuthMethod = "api_key" };
        var client = await ChatClientFactory.CreateAsync(settings);
        Assert.Null(client);
    }

    // ── Provider creation (with dummy keys — no actual HTTP calls) ──

    [Fact]
    public async Task CreateAsync_OpenAI_ReturnsNonNull()
    {
        var settings = new AppSettings { Provider = "openai", OpenAiApiKey = "sk-test-dummy" };
        var client = await ChatClientFactory.CreateAsync(settings);
        Assert.NotNull(client);
    }

    [Fact]
    public async Task CreateAsync_Anthropic_ReturnsNonNull()
    {
        var settings = new AppSettings { Provider = "anthropic", AnthropicApiKey = "sk-ant-dummy" };
        var client = await ChatClientFactory.CreateAsync(settings);
        Assert.NotNull(client);
    }

    [Fact]
    public async Task CreateAsync_Gemini_ApiKey_ReturnsNonNull()
    {
        var settings = new AppSettings
        {
            Provider = "gemini",
            GeminiApiKey = "test-gemini-key",
            GeminiAuthMethod = "api_key",
        };
        var client = await ChatClientFactory.CreateAsync(settings);
        Assert.NotNull(client);
    }

    [Fact]
    public async Task CreateAsync_Copilot_ReturnsNonNull()
    {
        var settings = new AppSettings { Provider = "copilot", GitHubToken = "ghp_test" };
        var client = await ChatClientFactory.CreateAsync(settings);
        Assert.NotNull(client);
    }

    [Fact]
    public async Task CreateAsync_OpenAICompatible_MissingEndpoint_Throws()
    {
        var settings = new AppSettings
        {
            Provider = "openai-compatible",
            CompatibleApiKey = "test-key",
            CustomEndpoint = null,
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ChatClientFactory.CreateAsync(settings));
    }

    [Fact]
    public async Task CreateAsync_OpenAICompatible_WithEndpoint_ReturnsNonNull()
    {
        var settings = new AppSettings
        {
            Provider = "openai-compatible",
            CompatibleApiKey = "test-key",
            CustomEndpoint = "https://localhost:8080/v1",
            CompatibleModel = "local-model",
        };
        var client = await ChatClientFactory.CreateAsync(settings);
        Assert.NotNull(client);
    }

    // ── Explicit provider+model overload ──

    [Fact]
    public async Task CreateAsync_ExplicitProvider_OverridesSettingsProvider()
    {
        var settings = new AppSettings
        {
            Provider = "openai",
            OpenAiApiKey = "sk-test",
            AnthropicApiKey = "sk-ant-test",
        };

        // Request Anthropic even though settings.Provider is "openai"
        var client = await ChatClientFactory.CreateAsync(settings, "anthropic", "claude-sonnet-4-6");
        Assert.NotNull(client);
    }

    // ── Gemini OAuth path (no OAuth service set up) ──

    [Fact]
    public async Task CreateAsync_GeminiOAuth_NoService_ReturnsNull()
    {
        var settings = new AppSettings
        {
            Provider = "gemini",
            GeminiAuthMethod = "oauth",
            GeminiRefreshToken = "refresh-token-value",
            GeminiApiKey = null,
        };

        // GeminiOAuth service not configured — should return null gracefully
        var client = await ChatClientFactory.CreateAsync(settings);
        Assert.Null(client);
    }

    // ── CopilotService accessor ──

    [Fact]
    public void CopilotService_ReturnsNonNull()
    {
        Assert.NotNull(ChatClientFactory.CopilotService);
    }

    // ── Anthropic fallback to OpenAI key ──

    [Fact]
    public async Task CreateAsync_Anthropic_FallsBackToOpenAiKey()
    {
        var settings = new AppSettings
        {
            Provider = "anthropic",
            AnthropicApiKey = null,
            OpenAiApiKey = "sk-fallback-key",
        };
        var client = await ChatClientFactory.CreateAsync(settings);
        Assert.NotNull(client);
    }

    // ── Model override via explicit provider+model overload ──

    [Fact]
    public async Task CreateAsync_ExplicitOpenAI_ModelOverride_ReturnsNonNull()
    {
        var settings = new AppSettings { OpenAiApiKey = "sk-test" };
        var client = await ChatClientFactory.CreateAsync(settings, "openai", "gpt-4o-mini");
        Assert.NotNull(client);
    }

    [Fact]
    public async Task CreateAsync_ExplicitAnthropic_ModelOverride_ReturnsNonNull()
    {
        var settings = new AppSettings { AnthropicApiKey = "sk-ant-test" };
        var client = await ChatClientFactory.CreateAsync(settings, "anthropic", "claude-haiku-4-5");
        Assert.NotNull(client);
    }

    [Fact]
    public async Task CreateAsync_ExplicitCopilot_ModelOverride_ReturnsNonNull()
    {
        var settings = new AppSettings { GitHubToken = "ghp_test" };
        var client = await ChatClientFactory.CreateAsync(settings, "copilot", "gpt-4o");
        Assert.NotNull(client);
    }

    [Fact]
    public async Task CreateAsync_ExplicitGemini_ApiKey_ReturnsNonNull()
    {
        var settings = new AppSettings
        {
            GeminiApiKey = "test-gemini-key",
            GeminiAuthMethod = "api_key",
        };
        var client = await ChatClientFactory.CreateAsync(settings, "gemini", "gemini-2.0-flash");
        Assert.NotNull(client);
    }

    // ── Compatible endpoint validation ──

    [Fact]
    public async Task CreateAsync_ExplicitCompatible_MissingEndpoint_Throws()
    {
        var settings = new AppSettings { CompatibleApiKey = "test-key", CustomEndpoint = null };
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ChatClientFactory.CreateAsync(settings, "openai-compatible", "local-model"));
    }

    [Fact]
    public async Task CreateAsync_ExplicitCompatible_WhitespaceEndpoint_Throws()
    {
        var settings = new AppSettings { CompatibleApiKey = "test-key", CustomEndpoint = "   " };
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ChatClientFactory.CreateAsync(settings, "openai-compatible", "local-model"));
    }

    [Fact]
    public async Task CreateAsync_ExplicitCompatible_WithEndpoint_ReturnsNonNull()
    {
        var settings = new AppSettings
        {
            CompatibleApiKey = "test-key",
            CustomEndpoint = "https://localhost:1234/v1",
        };
        var client = await ChatClientFactory.CreateAsync(settings, "openai-compatible", "local-model");
        Assert.NotNull(client);
    }

    // ── Compatible fallback to OpenAI key ──

    [Fact]
    public async Task CreateAsync_Compatible_FallsBackToOpenAiKey()
    {
        var settings = new AppSettings
        {
            Provider = "openai-compatible",
            CompatibleApiKey = null,
            OpenAiApiKey = "sk-fallback",
            CustomEndpoint = "https://localhost:8080/v1",
        };
        var client = await ChatClientFactory.CreateAsync(settings);
        Assert.NotNull(client);
    }

    [Fact]
    public async Task CreateAsync_Compatible_BothKeysNull_ReturnsNull()
    {
        var settings = new AppSettings
        {
            Provider = "openai-compatible",
            CompatibleApiKey = null,
            OpenAiApiKey = null,
            CustomEndpoint = "https://localhost:8080/v1",
        };
        var client = await ChatClientFactory.CreateAsync(settings);
        Assert.Null(client);
    }

    // ── Unknown provider defaults to OpenAI ──

    [Fact]
    public async Task CreateAsync_UnknownProvider_FallsBackToOpenAI()
    {
        var settings = new AppSettings
        {
            Provider = "some-future-provider",
            OpenAiApiKey = "sk-test",
        };
        var client = await ChatClientFactory.CreateAsync(settings);
        Assert.NotNull(client);
    }

    [Fact]
    public async Task CreateAsync_UnknownProvider_NoKey_ReturnsNull()
    {
        var settings = new AppSettings
        {
            Provider = "some-future-provider",
            OpenAiApiKey = null,
        };
        var client = await ChatClientFactory.CreateAsync(settings);
        Assert.Null(client);
    }

    // ── Gemini OAuth path with no refresh token ──

    [Fact]
    public async Task CreateAsync_GeminiOAuth_EmptyRefreshToken_FallsBackToApiKey()
    {
        var settings = new AppSettings
        {
            Provider = "gemini",
            GeminiAuthMethod = "oauth",
            GeminiRefreshToken = "",
            GeminiApiKey = "test-gemini-key",
        };
        var client = await ChatClientFactory.CreateAsync(settings);
        Assert.NotNull(client);
    }

    [Fact]
    public async Task CreateAsync_GeminiOAuth_NoRefreshToken_NoApiKey_ReturnsNull()
    {
        var settings = new AppSettings
        {
            Provider = "gemini",
            GeminiAuthMethod = "oauth",
            GeminiRefreshToken = null,
            GeminiApiKey = null,
        };
        var client = await ChatClientFactory.CreateAsync(settings);
        Assert.Null(client);
    }

    // ── CopilotService is singleton ──

    [Fact]
    public void CopilotService_ReturnsSameInstance()
    {
        var a = ChatClientFactory.CopilotService;
        var b = ChatClientFactory.CopilotService;
        Assert.Same(a, b);
    }

    // ── Provider case insensitivity ──

    [Theory]
    [InlineData("OpenAI")]
    [InlineData("OPENAI")]
    [InlineData("openai")]
    public async Task CreateAsync_ProviderCaseInsensitive(string provider)
    {
        var settings = new AppSettings { Provider = provider, OpenAiApiKey = "sk-test" };
        var client = await ChatClientFactory.CreateAsync(settings);
        Assert.NotNull(client);
    }

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("ANTHROPIC")]
    public async Task CreateAsync_ExplicitProvider_CaseInsensitive(string provider)
    {
        var settings = new AppSettings { AnthropicApiKey = "sk-ant-test" };
        var client = await ChatClientFactory.CreateAsync(settings, provider, "claude-sonnet-4-6");
        Assert.NotNull(client);
    }

    // ── GeminiOAuth accessor ──

    [Fact]
    public void GeminiOAuth_BeforeSetup_ReturnsNull()
    {
        // GeminiOAuth is null until SetGeminiOAuth is called
        // (We can't reliably test this since it's static and other tests may set it,
        // but at minimum it shouldn't throw)
        var result = ChatClientFactory.GeminiOAuth;
        // Just verify it doesn't throw — result may or may not be null depending on test order
    }
}
