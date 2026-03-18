using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CEAISuite.Desktop;

/// <summary>
/// Handles GitHub Copilot authentication and token management:
///   - OAuth Device Flow: user clicks "Sign in", gets a code, authorizes in browser
///   - Two-token dance: GitHub OAuth token → Copilot session token (auto-refreshed)
///   - Model list fetching from the Copilot API
/// </summary>
internal sealed class CopilotTokenService : IDisposable
{
    private const string TokenUrl = "https://api.github.com/copilot_internal/v2/token";
    private const string EditorVersion = "vscode/1.95.3";
    private const string EditorPluginVersion = "copilot/1.246.0";
    private const string UserAgent = "GitHubCopilotChat/0.22.4";

    // GitHub OAuth App client ID used by Copilot editor plugins
    private const string DeviceFlowClientId = "Iv1.b507a08c87ecfe98";

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

        // Parse expiry — can be ISO-8601 string or Unix timestamp (number)
        if (data.TryGetProperty("expires_at", out var expiresEl))
        {
            if (expiresEl.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(expiresEl.GetString(), out var expiresAt))
            {
                _expiresAt = expiresAt;
            }
            else if (expiresEl.ValueKind == JsonValueKind.Number)
            {
                _expiresAt = DateTimeOffset.FromUnixTimeSeconds(expiresEl.GetInt64());
            }
            else
            {
                _expiresAt = DateTimeOffset.UtcNow.AddHours(1);
            }
        }
        else
        {
            _expiresAt = DateTimeOffset.UtcNow.AddHours(1);
        }

        return _sessionToken;
    }

    // ─── Usage / Quota ────────────────────────────────────────────

    /// <summary>
    /// Fetches the current Copilot usage/quota from the GitHub API.
    /// Uses the GitHub OAuth token (NOT the Copilot session token).
    /// </summary>
    public async Task<CopilotUsageInfo> GetUsageAsync(string githubToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/copilot_internal/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var data = JsonSerializer.Deserialize<JsonElement>(json);

        var plan = data.TryGetProperty("copilot_plan", out var planEl) ? planEl.GetString() ?? "unknown" : "unknown";
        var resetDate = data.TryGetProperty("quota_reset_date", out var resetEl) ? resetEl.GetString() ?? "" : "";

        CopilotQuota ParseQuota(JsonElement parent, string key)
        {
            if (!parent.TryGetProperty("quota_snapshots", out var snapshots) ||
                !snapshots.TryGetProperty(key, out var q))
                return new CopilotQuota(0, 0, 0, false);

            var entitlement = q.TryGetProperty("entitlement", out var e) ? e.GetInt32() : 0;
            var remaining = q.TryGetProperty("remaining", out var r) ? r.GetInt32() : 0;
            var pct = q.TryGetProperty("percent_remaining", out var p) ? p.GetDouble() : 0;
            var unlimited = q.TryGetProperty("unlimited", out var u) && u.GetBoolean();
            return new CopilotQuota(entitlement, remaining, pct, unlimited);
        }

        return new CopilotUsageInfo(
            plan,
            resetDate,
            ParseQuota(data, "premium_interactions"),
            ParseQuota(data, "chat"),
            ParseQuota(data, "completions"));
    }

    public void Dispose()
    {
        _http.Dispose();
        _lock.Dispose();
    }

    // ─── OAuth Device Flow ──────────────────────────────────────────

    /// <summary>Result of starting the device flow.</summary>
    public sealed record DeviceFlowStart(
        string DeviceCode,
        string UserCode,
        string VerificationUri,
        int ExpiresInSeconds,
        int PollIntervalSeconds);

    /// <summary>
    /// Step 1: Request a device code from GitHub.
    /// Returns the user code to display and the verification URL to open.
    /// </summary>
    public async Task<DeviceFlowStart> StartDeviceFlowAsync()
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = DeviceFlowClientId,
            ["scope"] = "read:user",
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/device/code");
        request.Content = content;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var data = JsonSerializer.Deserialize<JsonElement>(json);

        return new DeviceFlowStart(
            DeviceCode: data.GetProperty("device_code").GetString()!,
            UserCode: data.GetProperty("user_code").GetString()!,
            VerificationUri: data.GetProperty("verification_uri").GetString()!,
            ExpiresInSeconds: data.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 900,
            PollIntervalSeconds: data.TryGetProperty("interval", out var intv) ? intv.GetInt32() : 5);
    }

    /// <summary>
    /// Step 2: Poll GitHub until the user authorizes (or timeout/cancel).
    /// Returns the OAuth access token on success.
    /// </summary>
    public async Task<string> PollDeviceFlowAsync(
        string deviceCode, int pollInterval, int expiresIn, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(expiresIn);

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(pollInterval), ct);

            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = DeviceFlowClientId,
                ["device_code"] = deviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token");
            request.Content = content;
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _http.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            var data = JsonSerializer.Deserialize<JsonElement>(json);

            if (data.TryGetProperty("access_token", out var tokenEl))
            {
                var token = tokenEl.GetString();
                if (!string.IsNullOrEmpty(token))
                    return token;
            }

            // Check for terminal errors
            if (data.TryGetProperty("error", out var errorEl))
            {
                var error = errorEl.GetString();
                if (error == "authorization_pending" || error == "slow_down")
                {
                    if (error == "slow_down") pollInterval += 5;
                    continue;
                }
                // expired_token, access_denied, etc.
                var desc = data.TryGetProperty("error_description", out var descEl)
                    ? descEl.GetString() : error;
                throw new InvalidOperationException($"GitHub authorization failed: {desc}");
            }
        }

        throw new TimeoutException("GitHub device authorization timed out.");
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

        // Primary: Copilot's own model list (only returns models actually usable via Copilot)
        string? sessionToken = null;
        try { sessionToken = await GetSessionTokenAsync(githubToken); } catch { }
        if (sessionToken is not null)
        {
            var models = await TryFetchModels(
                "https://api.githubcopilot.com/models",
                $"Bearer {sessionToken}",
                stripPublisher: false);

            models ??= await TryFetchModels(
                "https://api.githubcopilot.com/v1/models",
                $"Bearer {sessionToken}",
                stripPublisher: false);

            if (models is not null && models.Count > 0)
            {
                models.Sort((a, b) =>
                {
                    var cmp = string.Compare(a.Vendor, b.Vendor, StringComparison.OrdinalIgnoreCase);
                    return cmp != 0 ? cmp : string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
                });
                _cachedModels = models;
                return models;
            }
        }

        // Fallback: GitHub Models Catalog (broader, may include models not on Copilot)
        var catalogModels = await TryFetchModels(
            "https://models.github.ai/catalog/models",
            $"Bearer {githubToken}",
            stripPublisher: true);

        if (catalogModels is null || catalogModels.Count == 0)
            catalogModels = FallbackModels();

        catalogModels.Sort((a, b) =>
        {
            var cmp = string.Compare(a.Vendor, b.Vendor, StringComparison.OrdinalIgnoreCase);
            return cmp != 0 ? cmp : string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
        });

        _cachedModels = catalogModels;
        return catalogModels;
    }

    private async Task<List<CopilotModelInfo>?> TryFetchModels(string url, string authHeader, bool stripPublisher = false)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", authHeader);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            // Required headers for Copilot API endpoints
            request.Headers.TryAddWithoutValidation("Editor-Version", EditorVersion);
            request.Headers.TryAddWithoutValidation("Editor-Plugin-Version", EditorPluginVersion);
            request.Headers.TryAddWithoutValidation("Copilot-Integration-Id", "vscode-chat");

            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            var root = JsonSerializer.Deserialize<JsonElement>(json);

            var models = new List<CopilotModelInfo>();

            // Handle multiple response shapes: bare array, { data: [] }, { models: [] }
            var items = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                : root.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array
                    ? dataArr.EnumerateArray()
                    : root.TryGetProperty("models", out var modelsArr) && modelsArr.ValueKind == JsonValueKind.Array
                        ? modelsArr.EnumerateArray()
                        : default;

            foreach (var item in items)
            {
                var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(id)) continue;

                // Catalog IDs are "publisher/model" — strip prefix for API use
                var shortId = stripPublisher && id.Contains('/') ? id[(id.LastIndexOf('/') + 1)..] : id;

                var name = item.TryGetProperty("friendly_name", out var fn) ? fn.GetString() ?? shortId
                         : item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? shortId
                         : shortId;

                var vendor = item.TryGetProperty("publisher", out var pubEl) ? pubEl.GetString() ?? ""
                           : item.TryGetProperty("vendor", out var vendorEl) ? vendorEl.GetString() ?? ""
                           : item.TryGetProperty("owned_by", out var ownerEl) ? ownerEl.GetString() ?? ""
                           : "";

                models.Add(new CopilotModelInfo(shortId, name, vendor, ""));
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

    /// <summary>Curated fallback when API model list isn't available.</summary>
    private static List<CopilotModelInfo> FallbackModels() =>
    [
        new("claude-sonnet-4-6",  "Claude Sonnet 4.6",  "Anthropic", "chat"),
        new("claude-opus-4-6",    "Claude Opus 4.6",    "Anthropic", "chat"),
        new("claude-haiku-4-5",   "Claude Haiku 4.5",   "Anthropic", "chat"),
        new("gpt-5.4",            "GPT-5.4",            "OpenAI",    "chat"),
        new("gpt-4.1",            "GPT-4.1",            "OpenAI",    "chat"),
        new("o3",                 "o3",                  "OpenAI",    "chat"),
        new("o4-mini",            "o4-mini",             "OpenAI",    "chat"),
        new("gpt-4o",             "GPT-4o",              "OpenAI",    "chat"),
        new("gpt-4o-mini",        "GPT-4o mini",         "OpenAI",    "chat"),
        new("gemini-2.0-flash",   "Gemini 2.0 Flash",   "Google",    "chat"),
    ];
}

/// <summary>Model info returned by the Copilot /models endpoint.</summary>
internal sealed record CopilotModelInfo(string Id, string Name, string Vendor, string Capabilities);

/// <summary>Quota snapshot for a single Copilot resource category.</summary>
internal sealed record CopilotQuota(int Entitlement, int Remaining, double PercentRemaining, bool Unlimited);

/// <summary>Copilot subscription usage/quota info.</summary>
internal sealed record CopilotUsageInfo(
    string Plan,
    string ResetDate,
    CopilotQuota PremiumInteractions,
    CopilotQuota Chat,
    CopilotQuota Completions);
