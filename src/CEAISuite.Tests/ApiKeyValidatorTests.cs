using CEAISuite.Application;

namespace CEAISuite.Tests;

public class ApiKeyValidatorTests
{
    [Fact]
    public async Task ValidateAsync_EmptyKey_ReturnsFalse()
    {
        var (isValid, error) = await ApiKeyValidator.ValidateAsync("openai", "");
        Assert.False(isValid);
        Assert.Equal("API key is empty.", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ValidateAsync_WhitespaceKey_ReturnsFalse(string? key)
    {
        var (isValid, error) = await ApiKeyValidator.ValidateAsync("anthropic", key!);
        Assert.False(isValid);
        Assert.Equal("API key is empty.", error);
    }

    [Fact]
    public async Task ValidateAsync_UnknownProvider_ReturnsFalse()
    {
        var (isValid, error) = await ApiKeyValidator.ValidateAsync("not-a-provider", "sk-test123");
        Assert.False(isValid);
        Assert.Contains("Unknown provider", error);
    }

    [Fact]
    public async Task ValidateAsync_Compatible_NoEndpoint_ReturnsFalse()
    {
        var (isValid, error) = await ApiKeyValidator.ValidateAsync("openai-compatible", "sk-test123", endpoint: null);
        Assert.False(isValid);
        Assert.Equal("Custom endpoint URL is required.", error);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("copilot")]
    public async Task ValidateAsync_ValidProviderStrings_DoNotThrow(string provider)
    {
        // These will attempt real HTTP calls and fail (invalid key), but should NOT throw.
        // They should return (false, <some error>) rather than an unhandled exception.
        var (isValid, error) = await ApiKeyValidator.ValidateAsync(provider, "sk-definitely-not-valid-key-12345");
        Assert.False(isValid);
        Assert.NotNull(error);
    }
}
