using Anthropic;
using CEAISuite.Application;
using Microsoft.Extensions.AI;
using OpenAI;

namespace CEAISuite.Desktop;

/// <summary>
/// Creates IChatClient instances for different AI providers.
/// Supports OpenAI (native), Anthropic (official SDK), GitHub Copilot (two-token dance),
/// and OpenAI-compatible endpoints.
/// </summary>
internal static class ChatClientFactory
{
    private static CopilotTokenService? _copilotTokenService;

    internal static CopilotTokenService CopilotService
    {
        get => _copilotTokenService ??= new CopilotTokenService();
    }

    public static IChatClient? Create(AppSettings settings)
    {
        if (settings.Provider.Equals("copilot", StringComparison.OrdinalIgnoreCase))
        {
            var githubToken = settings.GitHubToken;
            if (string.IsNullOrWhiteSpace(githubToken)) return null;
            return CreateCopilot(githubToken, settings.Model);
        }

        var apiKey = settings.OpenAiApiKey;
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        return settings.Provider.ToLowerInvariant() switch
        {
            "openai" => CreateOpenAI(apiKey, settings.Model),
            "anthropic" => CreateAnthropic(apiKey, settings.Model),
            "openai-compatible" => CreateOpenAICompatible(apiKey, settings.Model, settings.CustomEndpoint),
            _ => CreateOpenAI(apiKey, settings.Model),
        };
    }

    private static IChatClient CreateOpenAI(string apiKey, string model)
    {
        return new OpenAIClient(apiKey)
            .GetChatClient(model)
            .AsIChatClient();
    }

    private static IChatClient CreateAnthropic(string apiKey, string model)
    {
        return new AnthropicClient { ApiKey = apiKey }
            .AsIChatClient(model);
    }

    private static IChatClient CreateOpenAICompatible(string apiKey, string model, string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new InvalidOperationException("Custom endpoint is required for openai-compatible provider.");

        var options = new OpenAIClientOptions { Endpoint = new Uri(endpoint) };
        return new OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey), options)
            .GetChatClient(model)
            .AsIChatClient();
    }

    private static IChatClient CreateCopilot(string githubToken, string model)
    {
        // No blocking network call — use a placeholder API key.
        // The CopilotAuthPolicy lazily exchanges the GitHub token for a session
        // token on the first actual API request.
        var options = new OpenAIClientOptions { Endpoint = CopilotTokenService.BaseUrl };
        options.AddPolicy(
            new CopilotAuthPolicy(CopilotService, githubToken),
            System.ClientModel.Primitives.PipelinePosition.BeforeTransport);

        return new OpenAIClient(new System.ClientModel.ApiKeyCredential("copilot-pending"), options)
            .GetChatClient(model)
            .AsIChatClient();
    }

    /// <summary>
    /// Pipeline policy that lazily exchanges the GitHub token for a Copilot session token
    /// and injects it + required headers on every request. No blocking constructor call.
    /// </summary>
    private sealed class CopilotAuthPolicy : System.ClientModel.Primitives.PipelinePolicy
    {
        private readonly CopilotTokenService _tokenService;
        private readonly string _githubToken;

        public CopilotAuthPolicy(CopilotTokenService tokenService, string githubToken)
        {
            _tokenService = tokenService;
            _githubToken = githubToken;
        }

        public override void Process(
            System.ClientModel.Primitives.PipelineMessage message,
            IReadOnlyList<System.ClientModel.Primitives.PipelinePolicy> pipeline,
            int currentIndex)
        {
            var token = _tokenService.GetSessionTokenAsync(_githubToken).GetAwaiter().GetResult();
            ApplyHeaders(message, token);
            ProcessNext(message, pipeline, currentIndex);

            // Retry once with a fresh token on auth failures (not 400 — that's a payload issue)
            var status = message.Response?.Status ?? 0;
            if (status == 401 || status == 403)
            {
                token = _tokenService.ForceRefreshAsync(_githubToken).GetAwaiter().GetResult();
                ApplyHeaders(message, token);
                ProcessNext(message, pipeline, currentIndex);
            }
        }

        public override async ValueTask ProcessAsync(
            System.ClientModel.Primitives.PipelineMessage message,
            IReadOnlyList<System.ClientModel.Primitives.PipelinePolicy> pipeline,
            int currentIndex)
        {
            var token = await _tokenService.GetSessionTokenAsync(_githubToken);
            ApplyHeaders(message, token);
            await ProcessNextAsync(message, pipeline, currentIndex);

            // Retry once with a fresh token on auth failures (not 400 — that's a payload issue)
            var status = message.Response?.Status ?? 0;
            if (status == 401 || status == 403)
            {
                token = await _tokenService.ForceRefreshAsync(_githubToken);
                ApplyHeaders(message, token);
                await ProcessNextAsync(message, pipeline, currentIndex);
            }
        }

        private static void ApplyHeaders(System.ClientModel.Primitives.PipelineMessage message, string sessionToken)
        {
            message.Request.Headers.Set("Authorization", $"Bearer {sessionToken}");
            foreach (var (key, value) in CopilotTokenService.RequiredHeaders)
                message.Request.Headers.Set(key, value);
        }
    }
}
