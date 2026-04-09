using CEAISuite.Desktop;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for CopilotTokenService: offline-testable logic including
/// token caching, URL constants, required headers, model info, and invalidation.
/// Does NOT test actual HTTP calls.
/// </summary>
public class CopilotTokenServiceTests : IDisposable
{
    private readonly CopilotTokenService _service = new();

    public void Dispose() => _service.Dispose();

    // ── Static constants ──

    [Fact]
    public void BaseUrl_IsCorrectCopilotEndpoint()
    {
        Assert.Equal("https://api.githubcopilot.com/", CopilotTokenService.BaseUrl.ToString());
    }

    [Fact]
    public void RequiredHeaders_ContainsExpectedKeys()
    {
        Assert.True(CopilotTokenService.RequiredHeaders.ContainsKey("Editor-Version"));
        Assert.True(CopilotTokenService.RequiredHeaders.ContainsKey("Editor-Plugin-Version"));
        Assert.True(CopilotTokenService.RequiredHeaders.ContainsKey("Copilot-Integration-Id"));
        Assert.True(CopilotTokenService.RequiredHeaders.ContainsKey("User-Agent"));
    }

    [Fact]
    public void RequiredHeaders_ValuesAreNonEmpty()
    {
        foreach (var (key, value) in CopilotTokenService.RequiredHeaders)
        {
            Assert.False(string.IsNullOrWhiteSpace(value), $"Header '{key}' should have a non-empty value");
        }
    }

    // ── Invalidation ──

    [Fact]
    public void Invalidate_DoesNotThrow()
    {
        _service.Invalidate();
        // After invalidation, the next GetSessionTokenAsync should try to refresh
    }

    [Fact]
    public void InvalidateModels_DoesNotThrow()
    {
        _service.InvalidateModels();
    }

    // ── CopilotModelInfo ──

    [Fact]
    public void CopilotModelInfo_GetRate_WithExplicitRate_ReturnsIt()
    {
        var model = new CopilotModelInfo("test-model", "Test Model", "TestVendor", "chat", "2x");
        Assert.Equal("2x", model.GetRate());
    }

    [Fact]
    public void CopilotModelInfo_GetRate_KnownModel_ReturnsKnownRate()
    {
        var model = new CopilotModelInfo("claude-opus-4-6", "Claude Opus 4.6", "Anthropic", "chat");
        Assert.Equal("3x", model.GetRate());
    }

    [Fact]
    public void CopilotModelInfo_GetRate_IncludedModel_ReturnsIncluded()
    {
        var model = new CopilotModelInfo("gpt-4o", "GPT-4o", "OpenAI", "chat");
        Assert.Equal("Included", model.GetRate());
    }

    [Fact]
    public void CopilotModelInfo_GetRate_UnknownModel_ReturnsDefault1x()
    {
        var model = new CopilotModelInfo("totally-new-model", "New", "Unknown", "chat");
        Assert.Equal("1x", model.GetRate());
    }

    [Fact]
    public void CopilotModelInfo_GetRate_EmptyExplicitRate_FallsBackToKnown()
    {
        var model = new CopilotModelInfo("gpt-4o-mini", "GPT-4o mini", "OpenAI", "chat", "");
        Assert.Equal("Included", model.GetRate());
    }

    // ── CopilotQuota ──

    [Fact]
    public void CopilotQuota_RecordProperties()
    {
        var quota = new CopilotQuota(1000, 750, 75.0, false);
        Assert.Equal(1000, quota.Entitlement);
        Assert.Equal(750, quota.Remaining);
        Assert.Equal(75.0, quota.PercentRemaining);
        Assert.False(quota.Unlimited);
    }

    [Fact]
    public void CopilotQuota_Unlimited()
    {
        var quota = new CopilotQuota(0, 0, 0, true);
        Assert.True(quota.Unlimited);
    }

    // ── CopilotUsageInfo ──

    [Fact]
    public void CopilotUsageInfo_RecordProperties()
    {
        var usage = new CopilotUsageInfo(
            "pro", "2026-05-01",
            new CopilotQuota(100, 80, 80.0, false),
            new CopilotQuota(500, 400, 80.0, false),
            new CopilotQuota(1000, 900, 90.0, false));

        Assert.Equal("pro", usage.Plan);
        Assert.Equal("2026-05-01", usage.ResetDate);
        Assert.Equal(100, usage.PremiumInteractions.Entitlement);
        Assert.Equal(500, usage.Chat.Entitlement);
        Assert.Equal(1000, usage.Completions.Entitlement);
    }

    // ── DeviceFlowStart ──

    [Fact]
    public void DeviceFlowStart_RecordProperties()
    {
        var start = new CopilotTokenService.DeviceFlowStart(
            "device-code-123", "ABCD-1234", "https://github.com/login/device", 900, 5);

        Assert.Equal("device-code-123", start.DeviceCode);
        Assert.Equal("ABCD-1234", start.UserCode);
        Assert.Equal("https://github.com/login/device", start.VerificationUri);
        Assert.Equal(900, start.ExpiresInSeconds);
        Assert.Equal(5, start.PollIntervalSeconds);
    }

    // ── Known rates coverage ──

    [Theory]
    [InlineData("gpt-4.1", "Included")]
    [InlineData("gpt-4o-mini", "Included")]
    [InlineData("claude-haiku-4-5", "0.33x")]
    [InlineData("claude-sonnet-4-6", "1x")]
    [InlineData("o3", "1x")]
    [InlineData("claude-opus-4-5", "3x")]
    public void CopilotModelInfo_GetRate_KnownRates(string modelId, string expectedRate)
    {
        var model = new CopilotModelInfo(modelId, modelId, "", "chat");
        Assert.Equal(expectedRate, model.GetRate());
    }

    // ── Comprehensive known rate coverage ──

    [Theory]
    [InlineData("gpt-4o", "Included")]
    [InlineData("gpt-5-mini", "Included")]
    [InlineData("raptor-mini", "Included")]
    [InlineData("gpt-5.1-codex-mini", "0.33x")]
    [InlineData("gpt-5.4-mini", "0.33x")]
    [InlineData("gemini-3-flash", "0.33x")]
    [InlineData("gemini-3-flash-preview", "0.33x")]
    [InlineData("grok-code-fast-1", "0.25x")]
    [InlineData("claude-sonnet-4", "1x")]
    [InlineData("claude-sonnet-4-5", "1x")]
    [InlineData("gpt-5.1", "1x")]
    [InlineData("gpt-5.2", "1x")]
    [InlineData("gpt-5.4", "1x")]
    [InlineData("o4-mini", "1x")]
    [InlineData("gemini-2.5-pro", "1x")]
    [InlineData("gemini-3-pro", "1x")]
    [InlineData("gemini-3.1-pro", "1x")]
    [InlineData("claude-opus-4-6", "3x")]
    public void CopilotModelInfo_GetRate_AllKnownRates(string modelId, string expectedRate)
    {
        var model = new CopilotModelInfo(modelId, modelId, "", "chat");
        Assert.Equal(expectedRate, model.GetRate());
    }

    // ── GetRate case insensitivity ──

    [Fact]
    public void CopilotModelInfo_GetRate_CaseInsensitiveLookup()
    {
        var model = new CopilotModelInfo("GPT-4O", "GPT-4o", "OpenAI", "chat");
        Assert.Equal("Included", model.GetRate());
    }

    // ── CopilotModelInfo record equality ──

    [Fact]
    public void CopilotModelInfo_RecordEquality()
    {
        var a = new CopilotModelInfo("gpt-4o", "GPT-4o", "OpenAI", "chat");
        var b = new CopilotModelInfo("gpt-4o", "GPT-4o", "OpenAI", "chat");
        Assert.Equal(a, b);
    }

    [Fact]
    public void CopilotModelInfo_RecordInequality_DifferentId()
    {
        var a = new CopilotModelInfo("gpt-4o", "GPT-4o", "OpenAI", "chat");
        var b = new CopilotModelInfo("gpt-4o-mini", "GPT-4o mini", "OpenAI", "chat");
        Assert.NotEqual(a, b);
    }

    // ── CopilotQuota record equality ──

    [Fact]
    public void CopilotQuota_RecordEquality()
    {
        var a = new CopilotQuota(100, 80, 80.0, false);
        var b = new CopilotQuota(100, 80, 80.0, false);
        Assert.Equal(a, b);
    }

    [Fact]
    public void CopilotQuota_RecordInequality()
    {
        var a = new CopilotQuota(100, 80, 80.0, false);
        var b = new CopilotQuota(100, 50, 50.0, false);
        Assert.NotEqual(a, b);
    }

    // ── CopilotUsageInfo record equality ──

    [Fact]
    public void CopilotUsageInfo_RecordEquality()
    {
        var q = new CopilotQuota(100, 80, 80.0, false);
        var a = new CopilotUsageInfo("pro", "2026-05-01", q, q, q);
        var b = new CopilotUsageInfo("pro", "2026-05-01", q, q, q);
        Assert.Equal(a, b);
    }

    // ── DeviceFlowStart record equality ──

    [Fact]
    public void DeviceFlowStart_RecordEquality()
    {
        var a = new CopilotTokenService.DeviceFlowStart("dc", "UC", "https://gh.com/device", 900, 5);
        var b = new CopilotTokenService.DeviceFlowStart("dc", "UC", "https://gh.com/device", 900, 5);
        Assert.Equal(a, b);
    }

    // ── Multiple invalidations don't throw ──

    [Fact]
    public void Invalidate_MultipleCalls_DoNotThrow()
    {
        _service.Invalidate();
        _service.Invalidate();
        _service.Invalidate();
    }

    [Fact]
    public void InvalidateModels_MultipleCalls_DoNotThrow()
    {
        _service.InvalidateModels();
        _service.InvalidateModels();
    }

    // ── Dispose is idempotent ──

    [Fact]
    public void Dispose_MultipleCalls_DoNotThrow()
    {
        var service = new CopilotTokenService();
        service.Dispose();
        // Second dispose should not throw
        service.Dispose();
    }

    // ── RequiredHeaders has exactly 4 entries ──

    [Fact]
    public void RequiredHeaders_HasExactly4Entries()
    {
        Assert.Equal(4, CopilotTokenService.RequiredHeaders.Count);
    }

    // ── RequiredHeaders Editor-Version format ──

    [Fact]
    public void RequiredHeaders_EditorVersion_MatchesVSCodeFormat()
    {
        var version = CopilotTokenService.RequiredHeaders["Editor-Version"];
        Assert.StartsWith("vscode/", version);
    }

    // ── BaseUrl uses https ──

    [Fact]
    public void BaseUrl_UsesHttps()
    {
        Assert.Equal("https", CopilotTokenService.BaseUrl.Scheme);
    }

    // ── GetRate precedence: explicit > known > default ──

    [Fact]
    public void CopilotModelInfo_GetRate_ExplicitOverridesKnown()
    {
        // gpt-4o is "Included" in known table, but explicit "5x" takes precedence
        var model = new CopilotModelInfo("gpt-4o", "GPT-4o", "OpenAI", "chat", "5x");
        Assert.Equal("5x", model.GetRate());
    }
}
