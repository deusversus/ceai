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
        _copilotTokenService ??= new CopilotTokenService();

        // Exchange GitHub OAuth token for a Copilot session token (blocking for init)
        var sessionToken = _copilotTokenService
            .GetSessionTokenAsync(githubToken)
            .GetAwaiter().GetResult();

        // Copilot API is OpenAI-compatible — use OpenAI client pointed at Copilot endpoint
        var options = new OpenAIClientOptions { Endpoint = CopilotTokenService.BaseUrl };

        // Add required Copilot headers via pipeline policy
        options.AddPolicy(new CopilotHeaderPolicy(), System.ClientModel.Primitives.PipelinePosition.PerCall);

        return new OpenAIClient(new System.ClientModel.ApiKeyCredential(sessionToken), options)
            .GetChatClient(model)
            .AsIChatClient();
    }

    /// <summary>Pipeline policy that adds Copilot-required headers to every request.</summary>
    private sealed class CopilotHeaderPolicy : System.ClientModel.Primitives.PipelinePolicy
    {
        public override void Process(
            System.ClientModel.Primitives.PipelineMessage message,
            IReadOnlyList<System.ClientModel.Primitives.PipelinePolicy> pipeline,
            int currentIndex)
        {
            SetHeaders(message);
            ProcessNext(message, pipeline, currentIndex);
        }

        public override ValueTask ProcessAsync(
            System.ClientModel.Primitives.PipelineMessage message,
            IReadOnlyList<System.ClientModel.Primitives.PipelinePolicy> pipeline,
            int currentIndex)
        {
            SetHeaders(message);
            return ProcessNextAsync(message, pipeline, currentIndex);
        }

        private static void SetHeaders(System.ClientModel.Primitives.PipelineMessage message)
        {
            foreach (var (key, value) in CopilotTokenService.RequiredHeaders)
                message.Request.Headers.Set(key, value);
        }
    }
}
