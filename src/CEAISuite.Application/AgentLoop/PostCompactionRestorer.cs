using System.Text;

namespace CEAISuite.Application.AgentLoop;

/// <summary>
/// After conversation compaction, restores critical context that was lost
/// in the summarization. Injects a system-level message with:
/// - Recent tool results (capped to budget)
/// - Current address table state
/// - Active tool categories
/// - Process context
///
/// Modeled after Claude Code's post-compaction restoration (compact.ts)
/// which restores up to 5 files at 5K tokens each after summarization.
/// </summary>
public static class PostCompactionRestorer
{
    /// <summary>
    /// Build and inject a restoration message into the chat history.
    /// Call this immediately after compaction completes.
    /// </summary>
    public static void Restore(ChatHistoryManager history, ContextSnapshot snapshot)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Context was compacted. Here is restored context from before compaction:");
        sb.AppendLine();

        // Restore recent tool results within budget
        int totalChars = 0;
        int restoredCount = 0;
        foreach (var (toolName, result) in snapshot.RecentToolResults)
        {
            if (totalChars >= ContextSnapshot.TotalResultCharBudget)
                break;

            var remaining = ContextSnapshot.TotalResultCharBudget - totalChars;
            var maxForThis = Math.Min(remaining, ContextSnapshot.MaxCharsPerResult);
            var truncated = result.Length > maxForThis
                ? string.Concat(result.AsSpan(0, maxForThis), $"... [truncated at {maxForThis:#,0} chars]")
                : result;

            sb.AppendLine($"## Recent tool result: {toolName}");
            sb.AppendLine(truncated);
            sb.AppendLine();

            totalChars += truncated.Length;
            restoredCount++;
        }

        // Restore address table summary
        if (snapshot.AddressTableSummary is not null)
        {
            sb.AppendLine("## Address Table State");
            sb.AppendLine(snapshot.AddressTableSummary);
            sb.AppendLine();
        }

        // Restore process context
        if (snapshot.ProcessContext is not null)
        {
            sb.AppendLine("## Process Context");
            sb.AppendLine(snapshot.ProcessContext);
            sb.AppendLine();
        }

        // Restore active categories
        if (snapshot.ActiveCategories.Count > 0)
        {
            sb.AppendLine("## Active Tool Categories");
            sb.AppendLine(string.Join(", ", snapshot.ActiveCategories));
            sb.AppendLine();
        }

        if (restoredCount > 0 || snapshot.AddressTableSummary is not null || snapshot.ProcessContext is not null)
        {
            history.AddSystemMessage(sb.ToString());
        }
    }

    /// <summary>
    /// Capture a snapshot of current context before compaction runs.
    /// Call this BEFORE the compaction pipeline.
    /// </summary>
    public static ContextSnapshot CaptureSnapshot(
        ChatHistoryManager history,
        IReadOnlySet<string> activeCategories,
        Func<string>? contextProvider = null)
    {
        var messages = history.GetMessages();
        var recentResults = new List<(string ToolName, string Result)>();

        // Walk backwards to find the most recent tool results
        for (int i = messages.Count - 1; i >= 0 && recentResults.Count < ContextSnapshot.MaxRecentResults; i--)
        {
            var msg = messages[i];
            if (msg.Role != Microsoft.Extensions.AI.ChatRole.Tool) continue;

            foreach (var content in msg.Contents)
            {
                if (content is Microsoft.Extensions.AI.FunctionResultContent frc
                    && recentResults.Count < ContextSnapshot.MaxRecentResults)
                {
                    var resultStr = frc.Result?.ToString();
                    if (resultStr is not null && resultStr.Length > 50) // Skip trivial results
                    {
                        // Look up the actual tool name from matching FunctionCallContent
                        string toolName = FindToolName(messages, frc.CallId) ?? frc.CallId ?? "unknown";
                        recentResults.Add((toolName, resultStr));
                    }
                }
            }
        }

        // Get dynamic context from provider
        string? processContext = null;
        try { processContext = contextProvider?.Invoke(); } catch { /* ignore */ }

        return new ContextSnapshot
        {
            RecentToolResults = recentResults,
            ProcessContext = processContext,
            AddressTableSummary = null, // Will be populated by the context provider in future
            ActiveCategories = activeCategories,
        };
    }

    /// <summary>
    /// Find the tool name for a given call ID by searching assistant messages
    /// for the matching FunctionCallContent.
    /// </summary>
    private static string? FindToolName(IList<Microsoft.Extensions.AI.ChatMessage> messages, string? callId)
    {
        if (callId is null) return null;
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role != Microsoft.Extensions.AI.ChatRole.Assistant) continue;
            foreach (var content in messages[i].Contents)
            {
                if (content is Microsoft.Extensions.AI.FunctionCallContent fcc
                    && string.Equals(fcc.CallId, callId, StringComparison.Ordinal))
                    return fcc.Name;
            }
        }
        return null;
    }
}
