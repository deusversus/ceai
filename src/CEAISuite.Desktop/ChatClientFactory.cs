using Anthropic;
using CEAISuite.Application;
using GenerativeAI.Microsoft;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;

namespace CEAISuite.Desktop;

/// <summary>
/// Creates IChatClient instances for different AI providers.
/// Supports OpenAI (native), Anthropic (official SDK), GitHub Copilot (two-token dance),
/// and OpenAI-compatible endpoints.
/// </summary>
internal static class ChatClientFactory
{
    private static ILogger? _logger;
    private static CopilotTokenService? _copilotTokenService;
    private static GeminiOAuthService? _geminiOAuthService;

    internal static CopilotTokenService CopilotService
    {
        get => _copilotTokenService ??= new CopilotTokenService();
    }

    /// <summary>Gemini OAuth service. Must be initialized via SetGeminiOAuth() with credentials from settings.</summary>
    internal static GeminiOAuthService? GeminiOAuth => _geminiOAuthService;

    internal static void SetGeminiOAuth(string clientId, string clientSecret)
    {
        _geminiOAuthService?.Dispose();
        _geminiOAuthService = new GeminiOAuthService(clientId, clientSecret);
    }

    /// <summary>
    /// Sets the logger used for diagnostic warnings. Call once during DI setup.
    /// </summary>
    internal static void SetLogger(ILoggerFactory loggerFactory) =>
        _logger = loggerFactory.CreateLogger(typeof(ChatClientFactory));

    /// <summary>
    /// Creates an IChatClient for an explicit provider and model combination.
    /// Used by the multi-provider model selector to switch provider+model in one step.
    /// </summary>
    public static async Task<IChatClient?> CreateAsync(AppSettings settings, string provider, string model)
    {
        return provider.ToLowerInvariant() switch
        {
            "copilot" => CreateIfKey(settings.GitHubToken, t => CreateCopilot(t, model)),
            "anthropic" => CreateIfKey(settings.AnthropicApiKey ?? settings.OpenAiApiKey, k => CreateAnthropic(k, model)),
            "openai-compatible" => CreateIfKey(settings.CompatibleApiKey ?? settings.OpenAiApiKey, k => CreateOpenAICompatible(k, model, settings.CustomEndpoint)),
            "openrouter" => CreateIfKey(settings.OpenRouterApiKey, k => CreateOpenRouter(k, model)),
            "gemini" => await CreateGeminiAutoAsync(settings, model).ConfigureAwait(false),
            _ => CreateIfKey(settings.OpenAiApiKey, k => CreateOpenAI(k, model)),
        };
    }

    public static async Task<IChatClient?> CreateAsync(AppSettings settings)
    {
        return settings.Provider.ToLowerInvariant() switch
        {
            "copilot" => CreateIfKey(settings.GitHubToken, t => CreateCopilot(t, settings.Model)),
            "anthropic" => CreateIfKey(settings.AnthropicApiKey ?? settings.OpenAiApiKey, k => CreateAnthropic(k, settings.Model)),
            "openai-compatible" => CreateIfKey(settings.CompatibleApiKey ?? settings.OpenAiApiKey, k => CreateOpenAICompatible(k, settings.Model, settings.CustomEndpoint)),
            "openrouter" => CreateIfKey(settings.OpenRouterApiKey, k => CreateOpenRouter(k, settings.Model)),
            "gemini" => await CreateGeminiAutoAsync(settings, settings.Model).ConfigureAwait(false),
            _ => CreateIfKey(settings.OpenAiApiKey, k => CreateOpenAI(k, settings.Model)),
        };
    }

    /// <summary>Create Gemini client using either API key or OAuth token based on auth method.</summary>
    private static async Task<IChatClient?> CreateGeminiAutoAsync(AppSettings settings, string model)
    {
        // Warn if refresh token is stale
        if (settings.GeminiRefreshTokenAgeDays > 90)
        {
            var ageDays = settings.GeminiRefreshTokenAgeDays;
            _logger?.LogWarning("Gemini refresh token is {AgeDays} days old. Consider re-authenticating.", ageDays);
        }

        if (settings.GeminiAuthMethod == "oauth" && !string.IsNullOrWhiteSpace(settings.GeminiRefreshToken))
        {
            // OAuth: get access token asynchronously (auto-refreshes if expired)
            if (GeminiOAuth is not { } geminiOAuth) return null;
            var accessToken = await geminiOAuth.GetAccessTokenAsync(settings.GeminiRefreshToken)
                .ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(accessToken) ? null : CreateGemini(accessToken, model);
        }

        return CreateIfKey(settings.GeminiApiKey, k => CreateGemini(k, model));
    }

    private static IChatClient? CreateIfKey(string? key, Func<string, IChatClient> factory)
        => string.IsNullOrWhiteSpace(key) ? null : factory(key);

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

    private static GenerativeAIChatClient CreateGemini(string apiKey, string model)
    {
        return new GenerativeAIChatClient(apiKey, model);
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

    private static IChatClient CreateOpenRouter(string apiKey, string model)
    {
        var options = new OpenAIClientOptions { Endpoint = new Uri("https://openrouter.ai/api/v1") };
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

        // Sync override required by PipelinePolicy contract — SDK only calls ProcessAsync
        // in practice, but the base class demands this. The async path (ProcessAsync) is preferred.
        public override void Process(
            System.ClientModel.Primitives.PipelineMessage message,
            IReadOnlyList<System.ClientModel.Primitives.PipelinePolicy> pipeline,
            int currentIndex)
        {
            var token = _tokenService.GetSessionTokenAsync(_githubToken).GetAwaiter().GetResult();
            ApplyHeaders(message, token);
            FixToolContentEncoding(message);
            var bodySnapshot = CaptureRequestBody(message);
            LogRequest(bodySnapshot);
            ProcessNext(message, pipeline, currentIndex);
            LogResponse(message, bodySnapshot);

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
            FixToolContentEncoding(message);
            var bodySnapshot = CaptureRequestBody(message);
            LogRequest(bodySnapshot);
            await ProcessNextAsync(message, pipeline, currentIndex);
            LogResponse(message, bodySnapshot);

            var status = message.Response?.Status ?? 0;
            if (status == 401 || status == 403)
            {
                token = await _tokenService.ForceRefreshAsync(_githubToken);
                ApplyHeaders(message, token);
                await ProcessNextAsync(message, pipeline, currentIndex);
            }
        }

        /// <summary>
        /// Fix .NET SDK serialization quirks that the Copilot API rejects:
        /// 1. Double-encoded tool message content ("\"text\"" → "text")
        /// 2. Tool call arguments "null" → "{}" (parameterless functions)
        /// 3. Duplicate tool names (from multiple skills providers)
        /// </summary>
        private static void FixToolContentEncoding(System.ClientModel.Primitives.PipelineMessage message)
        {
            try
            {
                if (message.Request.Content is null) return;

                using var ms = new System.IO.MemoryStream();
                message.Request.Content.WriteTo(ms, default);
                var bodyBytes = ms.ToArray();
                var body = System.Text.Encoding.UTF8.GetString(bodyBytes);

                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var root = doc.RootElement;

                bool needsRewrite = false;

                // Check messages for issues
                if (root.TryGetProperty("messages", out var messages))
                {
                    foreach (var msg in messages.EnumerateArray())
                    {
                        if (!msg.TryGetProperty("role", out var role)) continue;
                        var roleStr = role.GetString();

                        if (roleStr == "tool" && msg.TryGetProperty("content", out var content)
                            && content.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            var val = content.GetString();
                            if (val is not null && val.Length >= 2 && val[0] == '"' && val[^1] == '"')
                                needsRewrite = true;
                        }

                        if (roleStr == "assistant" && msg.TryGetProperty("tool_calls", out var tcs))
                        {
                            foreach (var tc in tcs.EnumerateArray())
                            {
                                if (tc.TryGetProperty("function", out var fn)
                                    && fn.TryGetProperty("arguments", out var args)
                                    && args.GetString() == "null")
                                    needsRewrite = true;
                            }
                        }
                    }
                }

                // Check for duplicate tool names
                if (root.TryGetProperty("tools", out var tools))
                {
                    var seen = new HashSet<string>();
                    foreach (var tool in tools.EnumerateArray())
                    {
                        if (tool.TryGetProperty("function", out var fn)
                            && fn.TryGetProperty("name", out var name)
                            && !seen.Add(name.GetString()!))
                            needsRewrite = true;
                    }
                }

                if (!needsRewrite) return;

                // Rebuild JSON with all fixes
                using var output = new System.IO.MemoryStream();
                using (var writer = new System.Text.Json.Utf8JsonWriter(output))
                {
                    writer.WriteStartObject();
                    foreach (var prop in root.EnumerateObject())
                    {
                        if (prop.Name == "messages")
                        {
                            writer.WritePropertyName("messages");
                            writer.WriteStartArray();
                            foreach (var msg in prop.Value.EnumerateArray())
                            {
                                var roleStr = msg.TryGetProperty("role", out var r) ? r.GetString() : null;
                                bool isToolMsg = roleStr == "tool";
                                bool isAssistantMsg = roleStr == "assistant";

                                if (isToolMsg || isAssistantMsg)
                                {
                                    writer.WriteStartObject();
                                    foreach (var field in msg.EnumerateObject())
                                    {
                                        // Fix 1: unwrap double-encoded tool content
                                        if (isToolMsg && field.Name == "content"
                                            && field.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                                        {
                                            var val = field.Value.GetString();
                                            if (val is not null && val.Length >= 2 && val[0] == '"' && val[^1] == '"')
                                            {
                                                try
                                                {
                                                    var unwrapped = System.Text.Json.JsonSerializer.Deserialize<string>(val);
                                                    writer.WriteString("content", unwrapped);
                                                    continue;
                                                }
                                                catch (Exception ex)
                                                {
                                                    _logger?.LogWarning(ex, "Failed to unwrap double-encoded tool content during Copilot request rewrite");
                                                }
                                            }
                                            field.WriteTo(writer);
                                        }
                                        // Fix 2: rewrite "null" arguments to "{}"
                                        else if (isAssistantMsg && field.Name == "tool_calls")
                                        {
                                            writer.WritePropertyName("tool_calls");
                                            writer.WriteStartArray();
                                            foreach (var tc in field.Value.EnumerateArray())
                                            {
                                                writer.WriteStartObject();
                                                foreach (var tcField in tc.EnumerateObject())
                                                {
                                                    if (tcField.Name == "function")
                                                    {
                                                        writer.WritePropertyName("function");
                                                        writer.WriteStartObject();
                                                        foreach (var fnField in tcField.Value.EnumerateObject())
                                                        {
                                                            if (fnField.Name == "arguments" && fnField.Value.GetString() == "null")
                                                                writer.WriteString("arguments", "{}");
                                                            else
                                                                fnField.WriteTo(writer);
                                                        }
                                                        writer.WriteEndObject();
                                                    }
                                                    else
                                                    {
                                                        tcField.WriteTo(writer);
                                                    }
                                                }
                                                writer.WriteEndObject();
                                            }
                                            writer.WriteEndArray();
                                        }
                                        else
                                        {
                                            field.WriteTo(writer);
                                        }
                                    }
                                    writer.WriteEndObject();
                                }
                                else
                                {
                                    msg.WriteTo(writer);
                                }
                            }
                            writer.WriteEndArray();
                        }
                        // Fix 3: deduplicate tools by function name
                        else if (prop.Name == "tools")
                        {
                            writer.WritePropertyName("tools");
                            writer.WriteStartArray();
                            var seenNames = new HashSet<string>();
                            foreach (var tool in prop.Value.EnumerateArray())
                            {
                                if (tool.TryGetProperty("function", out var fn)
                                    && fn.TryGetProperty("name", out var name)
                                    && !seenNames.Add(name.GetString()!))
                                    continue;
                                tool.WriteTo(writer);
                            }
                            writer.WriteEndArray();
                        }
                        else
                        {
                            prop.WriteTo(writer);
                        }
                    }
                    writer.WriteEndObject();
                }

                var fixedBytes = output.ToArray();
                message.Request.Content = System.ClientModel.BinaryContent.Create(new BinaryData(fixedBytes));
                LogDiag($"REWRITE: fixed request ({bodyBytes.Length}B → {fixedBytes.Length}B)");
            }
            catch (Exception ex)
            {
                LogDiag($"REWRITE_ERR: {ex}");
            }
        }

        // Read the request body bytes and summarize roles + flags for logging
        private static string? CaptureRequestBody(System.ClientModel.Primitives.PipelineMessage message)
        {
            try
            {
                if (message.Request.Content is null) return null;
                using var ms = new System.IO.MemoryStream();
                message.Request.Content.WriteTo(ms, default);
                return System.Text.Encoding.UTF8.GetString(ms.ToArray());
            }
            catch (Exception ex) { _logger?.LogWarning(ex, "Failed to capture Copilot request body for diagnostics"); return null; }
        }

        private static void LogRequest(string? body)
        {
            if (body is null) { LogDiag("REQ: (no body)"); return; }

            // Extract key facts from the JSON without parsing (fast)
            bool hasStream = body.Contains("\"stream\":true", StringComparison.OrdinalIgnoreCase)
                          || body.Contains("\"stream\": true", StringComparison.OrdinalIgnoreCase);
            bool hasToolRole = body.Contains("\"role\":\"tool\"", StringComparison.Ordinal)
                            || body.Contains("\"role\": \"tool\"", StringComparison.Ordinal);

            // Extract unique roles
            var roles = new System.Collections.Generic.List<string>();
            foreach (var r in new[] { "system", "user", "assistant", "tool" })
            {
                if (body.Contains($"\"role\":\"{r}\"", StringComparison.Ordinal)
                 || body.Contains($"\"role\": \"{r}\"", StringComparison.Ordinal))
                    roles.Add(r);
            }

            LogDiag($"REQ: {body.Length}B stream={hasStream} toolRole={hasToolRole} roles=[{string.Join(",", roles)}]");
        }

        private static void LogResponse(System.ClientModel.Primitives.PipelineMessage message, string? requestBody)
        {
            var status = message.Response?.Status ?? 0;
            LogDiag($"RSP: {status}");

            // Log error metadata only — never dump full request body (contains conversation history)
            if (status >= 400)
            {
                try
                {
                    LogDiag($"ERR: status={status} requestBodyLength={requestBody?.Length ?? 0}B");

                    if (message.Response?.Content is not null)
                    {
                        var errBody = message.Response.Content.ToString();
                        var truncated = errBody.Length > 200 ? errBody[..200] + "..." : errBody;
                        LogDiag($"ERR_BODY: {truncated}");
                    }
                }
                catch (Exception ex)
                {
                    if (_logger is not null && _logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning(ex, "Failed to log Copilot API error response (status {Status})", status);
                }
            }
        }

        private static void LogDiag(string msg)
        {
            try
            {
                var logDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CEAISuite", "logs");
                System.IO.Directory.CreateDirectory(logDir);
                var logPath = System.IO.Path.Combine(logDir, $"http-diag-{DateTime.Now:yyyy-MM-dd}.log");
                System.IO.File.AppendAllText(logPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to write Copilot HTTP diagnostic log entry");
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
