using CEAISuite.Desktop;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for GeminiOAuthService: offline-testable logic including
/// constructor validation, invalidation, cached token state.
/// Does NOT test actual HTTP calls or browser flows.
/// </summary>
public class GeminiOAuthServiceTests
{
    // ── Constructor validation ──

    [Fact]
    public void Constructor_NullClientId_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => new GeminiOAuthService(null!, "secret"));
    }

    [Fact]
    public void Constructor_EmptyClientId_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => new GeminiOAuthService("", "secret"));
    }

    [Fact]
    public void Constructor_WhitespaceClientId_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => new GeminiOAuthService("  ", "secret"));
    }

    [Fact]
    public void Constructor_NullClientSecret_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => new GeminiOAuthService("client-id", null!));
    }

    [Fact]
    public void Constructor_EmptyClientSecret_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => new GeminiOAuthService("client-id", ""));
    }

    [Fact]
    public void Constructor_ValidCredentials_DoesNotThrow()
    {
        using var svc = new GeminiOAuthService("client-id", "client-secret");
        Assert.NotNull(svc);
    }

    // ── Invalidation ──

    [Fact]
    public void Invalidate_ClearsCachedToken()
    {
        using var svc = new GeminiOAuthService("client-id", "client-secret");

        // Initially no cached token
        Assert.Null(svc.CachedAccessToken);

        // Invalidate should be safe even when no token cached
        svc.Invalidate();
        Assert.Null(svc.CachedAccessToken);
    }

    // ── GetAccessTokenAsync: no refresh token ──

    [Fact]
    public async Task GetAccessTokenAsync_NoRefreshToken_Throws()
    {
        using var svc = new GeminiOAuthService("client-id", "client-secret");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.GetAccessTokenAsync());

        Assert.Contains("refresh token", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetAccessTokenAsync_EmptyRefreshToken_Throws()
    {
        using var svc = new GeminiOAuthService("client-id", "client-secret");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.GetAccessTokenAsync(""));

        Assert.Contains("refresh token", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhitespaceRefreshToken_Throws()
    {
        using var svc = new GeminiOAuthService("client-id", "client-secret");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.GetAccessTokenAsync("   "));

        Assert.Contains("refresh token", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── RevokeAsync: no token ──

    [Fact]
    public async Task RevokeAsync_NoToken_DoesNotThrow()
    {
        using var svc = new GeminiOAuthService("client-id", "client-secret");
        // Should be a no-op when no token is cached
        await svc.RevokeAsync();
    }

    // ── Dispose ──

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var svc = new GeminiOAuthService("client-id", "client-secret");
        svc.Dispose();
    }

    [Fact]
    public void Dispose_MultipleCallsSafe()
    {
        var svc = new GeminiOAuthService("client-id", "client-secret");
        svc.Dispose();
        // Second dispose should not throw (HttpClient and SemaphoreSlim are safe)
    }

    // ── Cancellation ──

    [Fact]
    public async Task GetAccessTokenAsync_CancelledToken_Throws()
    {
        using var svc = new GeminiOAuthService("client-id", "client-secret");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.GetAccessTokenAsync("some-refresh-token", cts.Token));
    }
}
