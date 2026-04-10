using Microsoft.Extensions.AI;

namespace CEAISuite.Application.AgentLoop;

/// <summary>
/// Manages the conversation message list for the agent loop.
/// Replaces MAF's <c>InMemoryChatHistoryProvider</c> with full control
/// over message manipulation, pruning, and replay.
/// </summary>
public sealed class ChatHistoryManager
{
    private readonly List<ChatMessage> _messages = new();
    private readonly object _lock = new();

    /// <summary>Current message count.</summary>
    public int Count
    {
        get { lock (_lock) return _messages.Count; }
    }

    /// <summary>Get a snapshot of all messages (for passing to IChatClient).</summary>
    public IList<ChatMessage> GetMessages()
    {
        lock (_lock) return _messages.ToList();
    }

    /// <summary>Add a user message, optionally appending dynamic context.</summary>
    public void AddUserMessage(string text, string? contextSuffix = null)
    {
        var content = contextSuffix is not null
            ? $"{text}\n\n{contextSuffix}"
            : text;

        lock (_lock)
            _messages.Add(new ChatMessage(ChatRole.User, content));
    }

    /// <summary>Add a user message with mixed content (text + images), optionally appending dynamic context.</summary>
    public void AddUserMessage(IList<AIContent> contents, string? contextSuffix = null)
    {
        if (contextSuffix is not null)
            contents.Add(new TextContent($"\n\n{contextSuffix}"));

        lock (_lock)
            _messages.Add(new ChatMessage(ChatRole.User, contents));
    }

    /// <summary>Add an assistant message (may contain text + FunctionCallContent).</summary>
    public void AddAssistantMessage(ChatMessage message)
    {
        lock (_lock) _messages.Add(message);
    }

    /// <summary>
    /// Add tool results as a user-role message containing FunctionResultContent items.
    /// This is the standard format expected by the Anthropic and OpenAI APIs.
    /// </summary>
    public void AddToolResults(IEnumerable<FunctionResultContent> results)
    {
        var contents = new List<AIContent>();
        foreach (var result in results)
            contents.Add(result);

        var message = new ChatMessage(ChatRole.Tool, contents);
        lock (_lock) _messages.Add(message);
    }

    /// <summary>Add a system-level injection message (e.g., post-compaction context restoration).</summary>
    public void AddSystemMessage(string text)
    {
        lock (_lock)
            _messages.Add(new ChatMessage(ChatRole.User,
                $"<system-reminder>\n{text}\n</system-reminder>"));
    }

    /// <summary>Clear all messages (e.g., on new chat).</summary>
    public void Clear()
    {
        lock (_lock) _messages.Clear();
    }

    /// <summary>Replace all messages (e.g., after compaction).</summary>
    public void ReplaceAll(IList<ChatMessage> compacted)
    {
        lock (_lock)
        {
            _messages.Clear();
            _messages.AddRange(compacted);
        }
    }

    /// <summary>
    /// Rough token estimate based on character count / 4.
    /// Used for compaction trigger decisions — not billing-accurate.
    /// </summary>
    public int EstimateTokens()
    {
        lock (_lock)
        {
            int chars = 0;
            foreach (var msg in _messages)
            {
                foreach (var content in msg.Contents)
                {
                    if (content is TextContent tc)
                        chars += tc.Text?.Length ?? 0;
                    else if (content is FunctionCallContent fc)
                        chars += (fc.Name?.Length ?? 0) + (fc.Arguments?.ToString()?.Length ?? 0);
                    else if (content is FunctionResultContent fr)
                        chars += fr.Result?.ToString()?.Length ?? 0;
                }
            }
            return chars / 4;
        }
    }

    /// <summary>
    /// Count user-role messages (used for sliding window compaction trigger).
    /// </summary>
    public int CountUserTurns()
    {
        lock (_lock)
            return _messages.Count(m => m.Role == ChatRole.User);
    }

    /// <summary>
    /// Prune old tool results to save tokens: keep the last <paramref name="keepRecentTurns"/>
    /// user turns intact, truncate older FunctionResultContent to short summaries.
    /// Absorbed from AiOperatorService.PruneOldToolResults().
    /// </summary>
    public int PruneOldToolResults(int keepRecentTurns = 2, int summaryMaxChars = 100)
    {
        lock (_lock)
        {
            // Find the index of the Nth-from-last user message
            int userTurnsSeen = 0;
            int cutoffIndex = _messages.Count;
            for (int i = _messages.Count - 1; i >= 0; i--)
            {
                if (_messages[i].Role == ChatRole.User)
                {
                    userTurnsSeen++;
                    if (userTurnsSeen >= keepRecentTurns)
                    {
                        cutoffIndex = i;
                        break;
                    }
                }
            }

            int pruned = 0;
            for (int i = 0; i < cutoffIndex; i++)
            {
                var msg = _messages[i];
                if (msg.Role != ChatRole.Tool) continue;

                for (int j = 0; j < msg.Contents.Count; j++)
                {
                    if (msg.Contents[j] is FunctionResultContent frc)
                    {
                        var resultStr = frc.Result?.ToString();
                        if (resultStr is not null && resultStr.Length > summaryMaxChars)
                        {
                            var summary = string.Concat(resultStr.AsSpan(0, summaryMaxChars), "... [pruned]");
                            msg.Contents[j] = new FunctionResultContent(frc.CallId ?? "", summary);
                            pruned++;
                        }
                    }
                }
            }

            return pruned;
        }
    }

    /// <summary>
    /// Replace the content of a specific FunctionResultContent by its call ID.
    /// Used by microcompaction to prune oversized results without removing messages.
    /// </summary>
    public bool ReplaceToolResult(string callId, string newContent)
    {
        lock (_lock)
        {
            foreach (var msg in _messages)
            {
                if (msg.Role != ChatRole.Tool) continue;
                for (int j = 0; j < msg.Contents.Count; j++)
                {
                    if (msg.Contents[j] is FunctionResultContent frc && frc.CallId == callId)
                    {
                        msg.Contents[j] = new FunctionResultContent(callId, newContent);
                        return true;
                    }
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Replay saved chat messages into the history for session restoration.
    /// Reconstructs FunctionCallContent and FunctionResultContent from metadata,
    /// spilling large results to the store.
    /// Absorbed from AiOperatorService.ReplayHistoryInto().
    /// </summary>
    public void ReplayFromSaved(
        IEnumerable<AiChatMessage> saved,
        int maxMessages,
        TokenLimits limits,
        ToolResultStore store)
    {
        lock (_lock)
        {
            _messages.Clear();

            var recent = saved.TakeLast(maxMessages).ToList();

            foreach (var msg in recent)
            {
                if (msg.Role == "user")
                {
                    _messages.Add(new ChatMessage(ChatRole.User, msg.Content));
                }
                else if (msg.Role == "assistant")
                {
                    // Use empty list for tool-only messages so the model doesn't see
                    // placeholder text as if it were its own reasoning.
                    var contents = string.IsNullOrWhiteSpace(msg.Content)
                        ? new List<AIContent>()
                        : new List<AIContent> { new Microsoft.Extensions.AI.TextContent(msg.Content) };
                    var assistantMsg = new ChatMessage(ChatRole.Assistant, contents);

                    // Reconstruct FunctionCallContent from metadata
                    if (msg.ToolCalls is { Count: > 0 })
                    {
                        foreach (var tc in msg.ToolCalls)
                        {
                            IDictionary<string, object?>? args = null;
                            if (tc.ArgumentsJson is not null)
                            {
                                try
                                {
                                    args = System.Text.Json.JsonSerializer
                                        .Deserialize<Dictionary<string, object?>>(tc.ArgumentsJson);
                                }
                                catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"[ChatHistoryManager] Malformed JSON in tool call args: {ex.Message}"); }
                            }
                            assistantMsg.Contents.Add(new FunctionCallContent(tc.CallId, tc.Name, args));
                        }
                    }
                    _messages.Add(assistantMsg);

                    // Reconstruct FunctionResultContent as separate tool messages
                    if (msg.ToolResults is { Count: > 0 })
                    {
                        var toolContents = new List<AIContent>();
                        foreach (var tr in msg.ToolResults)
                        {
                            var resultStr = tr.Result ?? "";
                            // Spill oversized results during replay
                            if (resultStr.Length > limits.MaxToolResultChars)
                            {
                                var handle = store.Store(tr.Name, resultStr);
                                var previewLen = Math.Max(limits.MaxToolResultChars / 2, 500);
                                var preview = resultStr[..Math.Min(previewLen, resultStr.Length)];
                                resultStr = $"{preview}\n\n--- RESULT SPILLED ---\nresult_id: {handle}\n" +
                                            $"total_chars: {resultStr.Length:#,0}\n" +
                                            $"Use RetrieveToolResult(resultId: \"{handle}\") to read more.";
                            }
                            toolContents.Add(new FunctionResultContent(tr.CallId, resultStr));
                        }
                        _messages.Add(new ChatMessage(ChatRole.Tool, toolContents));
                    }
                }
            }
        }
    }
}
