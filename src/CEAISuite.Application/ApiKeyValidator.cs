using System.Net.Http;
using System.Net.Http.Headers;

namespace CEAISuite.Application;

/// <summary>
/// Validates API keys by making lightweight requests to each provider's API.
/// </summary>
public static class ApiKeyValidator
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task<(bool IsValid, string? Error)> ValidateAsync(
        string provider, string apiKey, string? endpoint = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return (false, "API key is empty.");

        try
        {
            return provider.ToLowerInvariant() switch
            {
                "openai" => await ValidateOpenAI(apiKey, ct).ConfigureAwait(false),
                "anthropic" => await ValidateAnthropic(apiKey, ct).ConfigureAwait(false),
                "gemini" => await ValidateGemini(apiKey, ct).ConfigureAwait(false),
                "openai-compatible" => await ValidateCompatible(apiKey, endpoint, ct).ConfigureAwait(false),
                "openrouter" => await ValidateOpenRouter(apiKey, ct).ConfigureAwait(false),
                "copilot" => await ValidateCopilot(apiKey, ct).ConfigureAwait(false),
                _ => (false, $"Unknown provider: {provider}"),
            };
        }
        catch (TaskCanceledException)
        {
            return (false, "Validation timed out.");
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Network error: {ex.Message}");
        }
    }

    private static async Task<(bool, string?)> ValidateOpenAI(string apiKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var res = await Http.SendAsync(req, ct).ConfigureAwait(false);
        return res.IsSuccessStatusCode ? (true, null) : (false, $"HTTP {(int)res.StatusCode}: Invalid API key.");
    }

    private static async Task<(bool, string?)> ValidateAnthropic(string apiKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models");
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        using var res = await Http.SendAsync(req, ct).ConfigureAwait(false);
        return res.IsSuccessStatusCode ? (true, null) : (false, $"HTTP {(int)res.StatusCode}: Invalid API key.");
    }

    private static async Task<(bool, string?)> ValidateGemini(string apiKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://generativelanguage.googleapis.com/v1/models?key={apiKey}");
        using var res = await Http.SendAsync(req, ct).ConfigureAwait(false);
        return res.IsSuccessStatusCode ? (true, null) : (false, $"HTTP {(int)res.StatusCode}: Invalid API key.");
    }

    private static async Task<(bool, string?)> ValidateCompatible(string apiKey, string? endpoint, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return (false, "Custom endpoint URL is required.");

        // Warn if not HTTPS — keys would be transmitted insecurely
        if (!endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !endpoint.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase)
            && !endpoint.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase))
            return (false, "Custom endpoint should use HTTPS (except localhost).");

        var baseUrl = endpoint.TrimEnd('/');
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var res = await Http.SendAsync(req, ct).ConfigureAwait(false);
        // Some compatible endpoints don't support /models -- accept any 2xx or 404
        return res.IsSuccessStatusCode || res.StatusCode == System.Net.HttpStatusCode.NotFound
            ? (true, null) : (false, $"HTTP {(int)res.StatusCode}: Connection failed.");
    }

    private static async Task<(bool, string?)> ValidateOpenRouter(string apiKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/models");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var res = await Http.SendAsync(req, ct).ConfigureAwait(false);
        return res.IsSuccessStatusCode ? (true, null) : (false, $"HTTP {(int)res.StatusCode}: Invalid OpenRouter API key.");
    }

    private static async Task<(bool, string?)> ValidateCopilot(string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        req.Headers.Authorization = new AuthenticationHeaderValue("token", token);
        req.Headers.Add("User-Agent", "CEAISuite");
        using var res = await Http.SendAsync(req, ct).ConfigureAwait(false);
        return res.IsSuccessStatusCode ? (true, null) : (false, $"HTTP {(int)res.StatusCode}: Invalid GitHub token.");
    }
}
