using System.Reflection;
using Microsoft.Extensions.AI;

namespace CEAISuite.Application;

public sealed record AiChatMessage(string Role, string Content, DateTimeOffset Timestamp);

public sealed record AiActionLogEntry(
    string ToolName,
    string Arguments,
    string Result,
    DateTimeOffset Timestamp);

public sealed class AiOperatorService
{
    private readonly IChatClient _chatClient;
    private readonly List<ChatMessage> _conversationHistory = new();
    private readonly List<AiChatMessage> _displayHistory = new();
    private readonly List<AiActionLogEntry> _actionLog = new();
    private readonly ChatOptions _chatOptions;

    public IReadOnlyList<AiChatMessage> DisplayHistory => _displayHistory;
    public IReadOnlyList<AiActionLogEntry> ActionLog => _actionLog;
    public bool IsConfigured { get; }

    public AiOperatorService(IChatClient? chatClient, AiToolFunctions toolFunctions)
    {
        IsConfigured = chatClient is not null;
        var baseClient = chatClient ?? new StubChatClient();

        // Build AIFunction list from the tool functions instance using reflection
        var methods = typeof(AiToolFunctions).GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        var tools = methods
            .Where(m => !m.IsSpecialName)
            .Select(m => AIFunctionFactory.Create(m, toolFunctions))
            .Cast<AITool>()
            .ToList();

        _chatOptions = new ChatOptions { Tools = tools };

        // Wrap the client with function invocation middleware so tool calls
        // are automatically executed and results fed back to the model
        _chatClient = new ChatClientBuilder(baseClient)
            .UseFunctionInvocation()
            .Build();

        var systemPrompt = new ChatMessage(ChatRole.System, """
            You are the AI Operator for CE AI Suite, a memory analysis and reverse-engineering tool.
            You help users find and modify values in running processes.

            Your capabilities via tools:
            - List and inspect running processes
            - Read and write process memory (typed values)
            - Scan memory for values (exact, unknown, increased, decreased)
            - Refine scan results iteratively
            - Disassemble code at addresses
            - Manage an address table of tracked addresses

            Guidelines:
            - Always confirm before writing to memory
            - Explain what you're doing and why
            - When helping find a value (like health/score), guide the user through the scan-refine workflow
            - Show addresses in hex format (0x...)
            - Be concise but informative
            - After calling tools, summarize the results for the user in natural language
            """);

        _conversationHistory.Add(systemPrompt);
    }

    public async Task<string> SendMessageAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        _displayHistory.Add(new AiChatMessage("user", userMessage, DateTimeOffset.UtcNow));
        _conversationHistory.Add(new ChatMessage(ChatRole.User, userMessage));

        try
        {
            var response = await _chatClient.GetResponseAsync(
                _conversationHistory,
                _chatOptions,
                cancellationToken);

            // Extract tool calls and final text from all response messages
            var assistantText = "";
            foreach (var message in response.Messages)
            {
                foreach (var content in message.Contents)
                {
                    if (content is TextContent textContent && !string.IsNullOrWhiteSpace(textContent.Text))
                    {
                        assistantText = textContent.Text;
                    }
                    else if (content is FunctionCallContent functionCall)
                    {
                        var argsStr = functionCall.Arguments is not null
                            ? string.Join(", ", functionCall.Arguments.Select(kv => $"{kv.Key}={kv.Value}"))
                            : "";
                        _actionLog.Add(new AiActionLogEntry(
                            functionCall.Name ?? "unknown",
                            argsStr,
                            "invoked",
                            DateTimeOffset.UtcNow));
                    }
                    else if (content is FunctionResultContent functionResult)
                    {
                        var resultStr = functionResult.Result?.ToString() ?? "";
                        var truncated = resultStr.Length > 200 ? resultStr[..200] + "..." : resultStr;
                        // Update the matching action log entry with result
                        for (int i = _actionLog.Count - 1; i >= 0; i--)
                        {
                            if (_actionLog[i].Result == "invoked")
                            {
                                _actionLog[i] = _actionLog[i] with { Result = truncated };
                                break;
                            }
                        }
                    }
                }
            }

            // Add all response messages to conversation history
            foreach (var message in response.Messages)
            {
                _conversationHistory.Add(message);
            }

            if (string.IsNullOrWhiteSpace(assistantText))
            {
                assistantText = "(Tool calls executed — see action log for details)";
            }

            _displayHistory.Add(new AiChatMessage("assistant", assistantText, DateTimeOffset.UtcNow));
            return assistantText;
        }
        catch (Exception ex)
        {
            var errorMessage = $"AI error: {ex.Message}";
            _displayHistory.Add(new AiChatMessage("assistant", errorMessage, DateTimeOffset.UtcNow));
            return errorMessage;
        }
    }

    public void ClearHistory()
    {
        var systemPrompt = _conversationHistory[0];
        _conversationHistory.Clear();
        _conversationHistory.Add(systemPrompt);
        _displayHistory.Clear();
        _actionLog.Clear();
    }

    private sealed class StubChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("stub");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant,
                "AI operator is not configured. Set your OpenAI API key in the OPENAI_API_KEY environment variable and restart the application."));
            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
