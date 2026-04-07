using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Web;

namespace CEAISuite.Desktop;

/// <summary>
/// Google OAuth 2.0 service for Gemini API access (installed/desktop app flow).
/// Uses localhost redirect with HttpListener to capture the authorization code.
/// Handles token exchange, caching, auto-refresh, and revocation.
/// </summary>
internal sealed class GeminiOAuthService : IDisposable
{
    private const string AuthUrl = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenUrl = "https://oauth2.googleapis.com/token";
    private const string RevokeUrl = "https://oauth2.googleapis.com/revoke";
    private const string Scope = "https://www.googleapis.com/auth/generative-language.retriever";

    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly HttpClient _http = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Create a Gemini OAuth service. Client credentials must be provided externally
    /// (from encrypted AppSettings or environment variables — never hardcoded).
    /// </summary>
    public GeminiOAuthService(string clientId, string clientSecret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);
        _clientId = clientId;
        _clientSecret = clientSecret;
    }

    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

    /// <summary>Current cached access token (if any).</summary>
    internal string? CachedAccessToken => _accessToken;

    /// <summary>
    /// Launch the full OAuth flow: open browser → localhost callback → exchange code for tokens.
    /// Returns (accessToken, refreshToken) on success.
    /// </summary>
    public async Task<(string AccessToken, string RefreshToken)> AuthorizeAsync(CancellationToken ct = default)
    {
        // Find an available port
        var listener = new HttpListener();
        var port = FindAvailablePort();
        var redirectUri = $"http://localhost:{port}/";
        listener.Prefixes.Add(redirectUri);
        listener.Start();

        try
        {
            // Build authorization URL
            var state = Guid.NewGuid().ToString("N");
            var authUri = $"{AuthUrl}?client_id={Uri.EscapeDataString(_clientId)}"
                + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
                + $"&response_type=code"
                + $"&scope={Uri.EscapeDataString(Scope)}"
                + $"&access_type=offline"
                + $"&prompt=consent"
                + $"&state={state}";

            // Open browser
            Process.Start(new ProcessStartInfo(authUri) { UseShellExecute = true });

            // Wait for callback
            var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(5), ct);
            var query = context.Request.Url?.Query ?? "";
            var queryParams = HttpUtility.ParseQueryString(query);

            // Serve "you can close this" page
            var responseHtml = """
                <html><body style="font-family:sans-serif;text-align:center;padding-top:60px;">
                <h2>Signed in to Google Gemini</h2>
                <p>You can close this window and return to CE AI Suite.</p>
                </body></html>
                """;
            var responseBytes = System.Text.Encoding.UTF8.GetBytes(responseHtml);
            context.Response.ContentType = "text/html";
            context.Response.ContentLength64 = responseBytes.Length;
            await context.Response.OutputStream.WriteAsync(responseBytes, ct);
            context.Response.Close();

            // Validate state
            var returnedState = queryParams["state"];
            if (returnedState != state)
                throw new InvalidOperationException("OAuth state mismatch — possible CSRF attack.");

            // Check for error
            var error = queryParams["error"];
            if (!string.IsNullOrEmpty(error))
                throw new InvalidOperationException($"Google authorization failed: {error}");

            var code = queryParams["code"]
                ?? throw new InvalidOperationException("No authorization code received from Google.");

            // Exchange code for tokens
            return await ExchangeCodeAsync(code, redirectUri, ct);
        }
        finally
        {
            listener.Stop();
            listener.Close();
        }
    }

    /// <summary>Exchange authorization code for access + refresh tokens.</summary>
    private async Task<(string AccessToken, string RefreshToken)> ExchangeCodeAsync(
        string code, string redirectUri, CancellationToken ct)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = _clientId,
            ["client_secret"] = _clientSecret,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
        });

        using var response = await _http.PostAsync(TokenUrl, content, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token exchange failed (HTTP {(int)response.StatusCode}): {json}");

        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var accessToken = root.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("No access_token in response.");
        var refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "";
        var expiresIn = root.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600;

        // Cache
        _accessToken = accessToken;
        _refreshToken = refreshToken;
        _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60); // 60s buffer

        return (accessToken, refreshToken);
    }

    /// <summary>
    /// Get a valid access token — returns cached if still valid, otherwise refreshes.
    /// </summary>
    public async Task<string> GetAccessTokenAsync(string? refreshToken = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // Use the provided refresh token or the cached one
            var rt = refreshToken ?? _refreshToken;

            // Fast path: cached token still valid
            if (_accessToken is not null && DateTimeOffset.UtcNow < _tokenExpiry)
                return _accessToken;

            if (string.IsNullOrWhiteSpace(rt))
                throw new InvalidOperationException("No refresh token available. Please sign in again.");

            // Refresh
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["client_secret"] = _clientSecret,
                ["refresh_token"] = rt,
                ["grant_type"] = "refresh_token",
            });

            using var response = await _http.PostAsync(TokenUrl, content, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Token refresh failed (HTTP {(int)response.StatusCode}). Please sign in again.");

            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            _accessToken = root.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("No access_token in refresh response.");
            var expiresIn = root.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600;
            _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60);

            // Refresh tokens can rotate — update if a new one is returned
            if (root.TryGetProperty("refresh_token", out var newRt) && newRt.GetString() is { } newRefresh)
                _refreshToken = newRefresh;

            return _accessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Revoke the token (for sign-out).</summary>
    public async Task RevokeAsync(string? token = null, CancellationToken ct = default)
    {
        var t = token ?? _accessToken ?? _refreshToken;
        if (string.IsNullOrWhiteSpace(t)) return;

        try
        {
            var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = t });
            await _http.PostAsync(RevokeUrl, content, ct);
        }
        catch
        {
            // Best-effort revocation — don't fail sign-out if Google is unreachable
        }

        Invalidate();
    }

    /// <summary>Clear cached tokens.</summary>
    public void Invalidate()
    {
        _accessToken = null;
        _refreshToken = null;
        _tokenExpiry = DateTimeOffset.MinValue;
    }

    private static int FindAvailablePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
