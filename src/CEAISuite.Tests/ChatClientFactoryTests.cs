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
}
