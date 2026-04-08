using System.Globalization;
using System.Reflection;
using CEAISuite.Application;
using CEAISuite.Domain;

namespace CEAISuite.Tests;

public class SensitiveStringTests
{
    [Fact]
    public void FromPlaintext_CreatesPinnedMemory()
    {
        using var ss = SensitiveString.FromPlaintext("secret-key-123");

        Assert.False(ss.IsDisposed);
        Assert.Equal("secret-key-123", ss.ToString());
    }

    [Fact]
    public void ToString_ReturnsCorrectPlaintextBeforeDisposal()
    {
        using var ss = SensitiveString.FromPlaintext("my-api-key");

        Assert.Equal("my-api-key", ss.ToString());
    }

    [Fact]
    public void Dispose_ZerosBackingMemory()
    {
        var ss = SensitiveString.FromPlaintext("sensitive-data");

        // Get a reference to the backing byte array via reflection
        var bytesField = typeof(SensitiveString).GetField("_bytes", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(bytesField);
        var bytes = (byte[])bytesField.GetValue(ss)!;

        // Verify it has content before disposal
        Assert.True(bytes.Length > 0);
        Assert.Contains(bytes, b => b != 0);

        ss.Dispose();

        // After disposal, all bytes must be zero
        Assert.True(ss.IsDisposed);
        Assert.All(bytes, b => Assert.Equal(0, b));
    }

    [Fact]
    public void ToString_ThrowsAfterDisposal()
    {
        var ss = SensitiveString.FromPlaintext("doomed");
        ss.Dispose();

        Assert.Throws<ObjectDisposedException>(() => ss.ToString());
    }

    [Fact]
    public void ImplicitStringConversion_Works()
    {
        using var ss = SensitiveString.FromPlaintext("convert-me");

        string result = ss;

        Assert.Equal("convert-me", result);
    }

    [Fact]
    public void Equals_ReturnsTrueForSameContent()
    {
        using var a = SensitiveString.FromPlaintext("same");
        using var b = SensitiveString.FromPlaintext("same");

        Assert.True(a.Equals(b));
    }

    [Fact]
    public void Equals_ReturnsFalseForDifferentContent()
    {
        using var a = SensitiveString.FromPlaintext("alpha");
        using var b = SensitiveString.FromPlaintext("beta");

        Assert.False(a.Equals(b));
    }

    [Fact]
    public void Equals_ReturnsFalseAfterDisposal()
    {
        var a = SensitiveString.FromPlaintext("test");
        using var b = SensitiveString.FromPlaintext("test");

        a.Dispose();

        Assert.False(a.Equals(b));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var ss = SensitiveString.FromPlaintext("double-free-safe");

        ss.Dispose();
        ss.Dispose(); // Second call should not throw

        Assert.True(ss.IsDisposed);
    }
}

public class GeminiRefreshTokenAgeTests
{
    [Fact]
    public void GeminiRefreshTokenAgeDays_ReturnsCorrectValue()
    {
        var settings = new AppSettings
        {
            GeminiRefreshTokenIssuedUtc = DateTimeOffset.UtcNow.AddDays(-45)
        };

        Assert.Equal(45, settings.GeminiRefreshTokenAgeDays);
    }

    [Fact]
    public void GeminiRefreshTokenAgeDays_ReturnsMinusOneWhenNull()
    {
        var settings = new AppSettings
        {
            GeminiRefreshTokenIssuedUtc = null
        };

        Assert.Equal(-1, settings.GeminiRefreshTokenAgeDays);
    }

    [Fact]
    public void GeminiRefreshTokenAgeDays_ReturnsZeroForToday()
    {
        var settings = new AppSettings
        {
            GeminiRefreshTokenIssuedUtc = DateTimeOffset.UtcNow
        };

        Assert.Equal(0, settings.GeminiRefreshTokenAgeDays);
    }

    [Fact]
    public void GeminiRefreshTokenAgeDays_OldToken_ExceedsNinetyDays()
    {
        var settings = new AppSettings
        {
            GeminiRefreshTokenIssuedUtc = DateTimeOffset.UtcNow.AddDays(-120)
        };

        var age = settings.GeminiRefreshTokenAgeDays;

        Assert.True(age > 90, string.Format(CultureInfo.InvariantCulture, "Expected age > 90 but got {0}", age));
    }

    [Fact]
    public void GeminiRefreshTokenAgeDays_RecentToken_UnderNinetyDays()
    {
        var settings = new AppSettings
        {
            GeminiRefreshTokenIssuedUtc = DateTimeOffset.UtcNow.AddDays(-30)
        };

        var age = settings.GeminiRefreshTokenAgeDays;

        Assert.True(age <= 90, string.Format(CultureInfo.InvariantCulture, "Expected age <= 90 but got {0}", age));
    }
}

public class SensitiveStringIntegrationTests
{
    [Fact]
    public void AppSettings_Dispose_ZeroesSensitiveStrings()
    {
        var settings = new AppSettings();
        settings.OpenAiApiKey = "sk-test-key-1234567890";
        settings.AnthropicApiKey = "sk-ant-test-key";
        settings.InitializeSensitiveKeys();

        var sensitiveOpenAi = settings.GetSensitiveKey("openai");
        var sensitiveAnthropic = settings.GetSensitiveKey("anthropic");
        Assert.NotNull(sensitiveOpenAi);
        Assert.NotNull(sensitiveAnthropic);

        settings.Dispose();

        Assert.True(sensitiveOpenAi!.IsDisposed);
        Assert.True(sensitiveAnthropic!.IsDisposed);
    }

    [Fact]
    public void GetKeyHealth_StaleKey_FlagsAsStale()
    {
        var settings = new AppSettings();
        settings.OpenAiApiKey = "sk-test";
        settings.OpenAiKeyIssuedUtc = DateTimeOffset.UtcNow.AddDays(-100);

        var report = settings.GetKeyHealth();

        Assert.Single(report.Entries);
        Assert.True(report.Entries[0].IsStale);
        Assert.Equal(100, report.Entries[0].AgeDays);
        Assert.True(report.HasStaleKeys);
    }

    [Fact]
    public void GetKeyHealth_FreshKey_NotStale()
    {
        var settings = new AppSettings();
        settings.OpenAiApiKey = "sk-test";
        settings.OpenAiKeyIssuedUtc = DateTimeOffset.UtcNow;

        var report = settings.GetKeyHealth();

        Assert.Single(report.Entries);
        Assert.False(report.Entries[0].IsStale);
        Assert.Equal(0, report.Entries[0].AgeDays);
    }

    [Fact]
    public void GetKeyHealth_NoKeys_EmptyReport()
    {
        var settings = new AppSettings();

        var report = settings.GetKeyHealth();

        Assert.Empty(report.Entries);
        Assert.False(report.HasStaleKeys);
    }
}
