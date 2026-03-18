using Anthropic;
using CEAISuite.Application;
using Microsoft.Extensions.AI;
using OpenAI;

namespace CEAISuite.Desktop;

/// <summary>
/// Creates IChatClient instances for different AI providers.
/// Supports OpenAI (native), Anthropic (official SDK), and OpenAI-compatible endpoints.
/// </summary>
internal static class ChatClientFactory
{
    public static IChatClient? Create(AppSettings settings)
    {
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
}
