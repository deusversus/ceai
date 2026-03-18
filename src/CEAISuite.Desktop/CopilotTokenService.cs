using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CEAISuite.Desktop;

/// <summary>
/// Handles the GitHub Copilot two-token dance:
///   1. GitHub OAuth token (long-lived, from user settings)
///   2. Copilot session token (expires ~1h, refreshed transparently)
///
/// The Copilot API at api.githubcopilot.com is wire-compatible with OpenAI's
/// Chat Completions API, so we only need to exchange the token and set headers.
/// </summary>
internal sealed class CopilotTokenService : IDisposable
{
    private const string TokenUrl = "https://api.github.com/copilot_internal/v2/token";
    private const string EditorVersion = "vscode/1.95.3";
    private const string EditorPluginVersion = "copilot/1.246.0";
    private const string UserAgent = "GitHubCopilotChat/0.22.4";

    private readonly HttpClient _http = new();
    private string? _sessionToken;
    private DateTimeOffset _expiresAt;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Base URL for the Copilot API (OpenAI-compatible).</summary>
    public static readonly Uri BaseUrl = new("https://api.githubcopilot.com");

    /// <summary>
    /// Headers required on every Copilot API call (set on the OpenAI client).
    /// </summary>
    public static readonly Dictionary<string, string> RequiredHeaders = new()
    {
        ["Editor-Version"] = EditorVersion,
        ["Editor-Plugin-Version"] = EditorPluginVersion,
        ["Copilot-Integration-Id"] = "vscode-chat",
        ["User-Agent"] = UserAgent,
    };

    /// <summary>
    /// Exchanges a GitHub OAuth token for a Copilot session token.
    /// Returns the session token (bearer) that can be used as an OpenAI API key.
    /// Thread-safe; caches and auto-refreshes.
    /// </summary>
    public async Task<string> GetSessionTokenAsync(string githubToken)
    {
        // Fast path: token still valid (with 60s buffer)
        if (_sessionToken is not null && DateTimeOffset.UtcNow < _expiresAt - TimeSpan.FromSeconds(60))
            return _sessionToken;

        await _lock.WaitAsync();
        try
        {
            // Double-check after lock
            if (_sessionToken is not null && DateTimeOffset.UtcNow < _expiresAt - TimeSpan.FromSeconds(60))
                return _sessionToken;

            return await RefreshTokenAsync(githubToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Forces a token refresh (e.g., after a 401 error).
    /// </summary>
    public async Task<string> ForceRefreshAsync(string githubToken)
    {
        await _lock.WaitAsync();
        try
        {
            _sessionToken = null;
            return await RefreshTokenAsync(githubToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Invalidates the cached token.</summary>
    public void Invalidate()
    {
        _sessionToken = null;
        _expiresAt = default;
    }

    private async Task<string> RefreshTokenAsync(string githubToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, TokenUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("token", githubToken);
        request.Headers.TryAddWithoutValidation("Editor-Version", EditorVersion);
        request.Headers.TryAddWithoutValidation("Editor-Plugin-Version", EditorPluginVersion);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        using var response = await _http.SendAsync(request);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException(
                "GitHub authorization failed — your token may have expired. " +
                "Please update it in Settings → AI Provider.");

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var data = JsonSerializer.Deserialize<JsonElement>(json);

        _sessionToken = data.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("Copilot token response missing 'token' field.");

        // Parse expiry — ISO-8601 string like "2025-01-01T12:00:00Z"
        if (data.TryGetProperty("expires_at", out var expiresEl) &&
            DateTimeOffset.TryParse(expiresEl.GetString(), out var expiresAt))
        {
            _expiresAt = expiresAt;
        }
        else
        {
            _expiresAt = DateTimeOffset.UtcNow.AddHours(1); // fallback
        }

        return _sessionToken;
    }

    public void Dispose()
    {
        _http.Dispose();
        _lock.Dispose();
    }

    // ─── Model list fetching ────────────────────────────────────────

    /// <summary>Cached model list (fetched once per session).</summary>
    private List<CopilotModelInfo>? _cachedModels;

    /// <summary>
    /// Fetches available models from the GitHub Copilot API.
    /// Tries multiple endpoints: GitHub internal API (OAuth token), then
    /// Copilot API (session token), then falls back to a curated list.
    /// Results are cached after first successful fetch.
    /// </summary>
    public async Task<List<CopilotModelInfo>> FetchModelsAsync(string githubToken)
    {
        if (_cachedModels is not null) return _cachedModels;

        // Attempt 1: GitHub internal models endpoint (uses OAuth token directly)
        var models = await TryFetchModels(
            "https://api.github.com/copilot_internal/v2/models",
            $"token {githubToken}");

        // Attempt 2: Copilot API /models (uses session token)
        if (models is null)
        {
            var sessionToken = await GetSessionTokenAsync(githubToken);
            models = await TryFetchModels(
                $"{BaseUrl}models",
                $"Bearer {sessionToken}");
        }

        // Attempt 3: Copilot API /v1/models
        if (models is null)
        {
            var sessionToken = await GetSessionTokenAsync(githubToken);
            models = await TryFetchModels(
                $"{BaseUrl}v1/models",
                $"Bearer {sessionToken}");
        }

        // All attempts failed — return empty (caller shows error)
        if (models is null || models.Count == 0)
            throw new InvalidOperationException(
                "Could not fetch model list from Copilot API. You can still type a model ID manually.");

        models.Sort((a, b) =>
        {
            var cmp = string.Compare(a.Vendor, b.Vendor, StringComparison.OrdinalIgnoreCase);
            return cmp != 0 ? cmp : string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
        });

        _cachedModels = models;
        return models;
    }

    private async Task<List<CopilotModelInfo>?> TryFetchModels(string url, string authHeader)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", authHeader);
            request.Headers.TryAddWithoutValidation("Editor-Version", EditorVersion);
            request.Headers.TryAddWithoutValidation("Editor-Plugin-Version", EditorPluginVersion);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Copilot-Integration-Id", "vscode-chat");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            var root = JsonSerializer.Deserialize<JsonElement>(json);

            var models = new List<CopilotModelInfo>();

            // Handle OpenAI-style { "data": [...] } response
            var items = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                : root.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array
                    ? dataArr.EnumerateArray()
                    : root.TryGetProperty("models", out var modelsArr) && modelsArr.ValueKind == JsonValueKind.Array
                        ? modelsArr.EnumerateArray()
                        : default;

            foreach (var item in items)
            {
                var id = "";
                if (item.TryGetProperty("id", out var idEl))
                    id = idEl.GetString() ?? "";
                else if (item.TryGetProperty("name", out var n))
                    id = n.GetString() ?? "";
                if (string.IsNullOrEmpty(id)) continue;

                var name = id;
                var vendor = "";
                if (item.TryGetProperty("name", out var nameEl) && nameEl.GetString() != id)
                    name = nameEl.GetString() ?? id;
                if (item.TryGetProperty("vendor", out var vendorEl))
                    vendor = vendorEl.GetString() ?? "";
                else if (item.TryGetProperty("owned_by", out var ownerEl))
                    vendor = ownerEl.GetString() ?? "";

                var capabilities = "";
                if (item.TryGetProperty("capabilities", out var capEl) &&
                    capEl.ValueKind == JsonValueKind.Object &&
                    capEl.TryGetProperty("type", out var typeEl))
                    capabilities = typeEl.GetString() ?? "";

                models.Add(new CopilotModelInfo(id, name, vendor, capabilities));
            }

            return models.Count > 0 ? models : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Clears the cached model list (e.g. when token changes).</summary>
    public void InvalidateModels() => _cachedModels = null;
}

/// <summary>Model info returned by the Copilot /models endpoint.</summary>
internal sealed record CopilotModelInfo(string Id, string Name, string Vendor, string Capabilities);
