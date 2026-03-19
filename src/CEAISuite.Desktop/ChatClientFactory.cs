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
        /// The .NET MEAI/OpenAI SDK double-JSON-encodes FunctionResultContent.Result
        /// when it's a string: the content field becomes "\"actual text\"" instead of
        /// "actual text". The Copilot API rejects this. This method rewrites the
        /// request body to unwrap double-encoded tool message content.
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

                // Quick check: skip rewrite if no tool messages
                if (!body.Contains("\"role\":\"tool\"") && !body.Contains("\"role\": \"tool\""))
                    return;

                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (!root.TryGetProperty("messages", out var messages))
                    return;

                bool needsRewrite = false;
                foreach (var msg in messages.EnumerateArray())
                {
                    if (msg.TryGetProperty("role", out var role) && role.GetString() == "tool"
                        && msg.TryGetProperty("content", out var content) && content.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var val = content.GetString();
                        if (val is not null && val.Length >= 2 && val[0] == '"' && val[^1] == '"')
                        {
                            // Content is double-encoded — needs rewrite
                            needsRewrite = true;
                            break;
                        }
                    }
                }

                if (!needsRewrite) return;

                // Rebuild the JSON with fixed tool content
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
                                bool isToolMsg = msg.TryGetProperty("role", out var r) && r.GetString() == "tool";
                                if (isToolMsg)
                                {
                                    writer.WriteStartObject();
                                    foreach (var field in msg.EnumerateObject())
                                    {
                                        if (field.Name == "content" && field.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                                        {
                                            var val = field.Value.GetString();
                                            if (val is not null && val.Length >= 2 && val[0] == '"' && val[^1] == '"')
                                            {
                                                // Unwrap: try JSON-deserializing the inner string
                                                try
                                                {
                                                    var unwrapped = System.Text.Json.JsonSerializer.Deserialize<string>(val);
                                                    writer.WriteString("content", unwrapped);
                                                }
                                                catch
                                                {
                                                    // Not valid double-encoding; keep as-is
                                                    field.WriteTo(writer);
                                                }
                                            }
                                            else
                                            {
                                                field.WriteTo(writer);
                                            }
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
                        else
                        {
                            prop.WriteTo(writer);
                        }
                    }
                    writer.WriteEndObject();
                }

                var fixedBytes = output.ToArray();
                message.Request.Content = System.ClientModel.BinaryContent.Create(new BinaryData(fixedBytes));
                LogDiag($"REWRITE: fixed double-encoded tool content ({bodyBytes.Length}B → {fixedBytes.Length}B)");
            }
            catch (Exception ex)
            {
                LogDiag($"REWRITE_ERR: {ex.Message}");
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
            catch { return null; }
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

            // On any error, dump the full request body to a file for analysis
            if (status >= 400 && requestBody is not null)
            {
                try
                {
                    var logDir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "CEAISuite", "logs");
                    System.IO.Directory.CreateDirectory(logDir);
                    var ts = DateTime.Now.ToString("HHmmss-fff");
                    var dumpPath = System.IO.Path.Combine(logDir, $"failed-request-{ts}.json");
                    System.IO.File.WriteAllText(dumpPath, requestBody);
                    LogDiag($"DUMP: {dumpPath} ({requestBody.Length}B)");

                    // Also try to read error response body
                    if (message.Response?.Content is not null)
                    {
                        var errBody = message.Response.Content.ToString();
                        LogDiag($"ERR_BODY: {errBody}");
                    }
                }
                catch { }
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
            catch { }
        }

        private static void ApplyHeaders(System.ClientModel.Primitives.PipelineMessage message, string sessionToken)
        {
            message.Request.Headers.Set("Authorization", $"Bearer {sessionToken}");
            foreach (var (key, value) in CopilotTokenService.RequiredHeaders)
                message.Request.Headers.Set(key, value);
        }
    }
}
