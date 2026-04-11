using System.Globalization;
using Microsoft.Extensions.AI;

namespace CEAISuite.Application.AgentLoop;

/// <summary>
/// Four-stage conversation compaction pipeline. Runs progressively from cheapest
/// to most expensive strategy until the conversation fits within limits.
///
/// Stage 1: Tool result collapse (no API call) — replace old FunctionResultContent with summaries
/// Stage 2: LLM summarization (API call) — summarize older messages into a compact block
/// Stage 3: Sliding window (no API call) — keep only the most recent N user turns
/// Stage 4: Truncation (no API call) — emergency drop of oldest messages
///
/// Modeled after Claude Code's PipelineCompactionStrategy + our existing MAF pipeline.
/// </summary>
public sealed class CompactionPipeline
{
    private readonly IChatClient _summarizationClient;
    private readonly TokenLimits _limits;
    private readonly Action<string, string>? _log;

    public CompactionPipeline(
        IChatClient summarizationClient,
        TokenLimits limits,
        Action<string, string>? log = null)
    {
        _summarizationClient = summarizationClient;
        _limits = limits;
        _log = log;
    }

    /// <summary>
    /// Run the compaction pipeline on the history. Returns a <see cref="CompactionResult"/>
    /// with success/failure, token counts before and after.
    /// Stages are applied in order; each stage checks its trigger condition before running.
    /// </summary>
    public async Task<CompactionResult> CompactAsync(
        ChatHistoryManager history,
        CancellationToken cancellationToken = default)
    {
        int tokensBefore = history.EstimateTokens();
        bool compacted = false;

        try
        {
            // Stage 1: Tool result collapse
            if (CountToolResultMessages(history) > _limits.CompactionToolResultMessages)
            {
                _log?.Invoke("COMPACT", $"Stage 1: Collapsing old tool results (>{_limits.CompactionToolResultMessages} messages)");
                CollapseToolResults(history);
                compacted = true;
            }

            // Stage 2: LLM summarization
            if (history.EstimateTokens() > _limits.CompactionSummarizationTokens)
            {
                _log?.Invoke("COMPACT", $"Stage 2: LLM summarization (~{history.EstimateTokens():#,0} tokens > {_limits.CompactionSummarizationTokens:#,0})");
                await SummarizeOlderMessages(history, cancellationToken).ConfigureAwait(false);
                compacted = true;
            }

            // Stage 3: Sliding window
            if (history.CountUserTurns() > _limits.CompactionSlidingWindowTurns)
            {
                _log?.Invoke("COMPACT", $"Stage 3: Sliding window ({history.CountUserTurns()} turns > {_limits.CompactionSlidingWindowTurns})");
                ApplySlidingWindow(history);
                compacted = true;
            }

            // Stage 4: Emergency truncation
            if (history.EstimateTokens() > _limits.CompactionTruncationTokens)
            {
                _log?.Invoke("COMPACT", $"Stage 4: Emergency truncation (~{history.EstimateTokens():#,0} tokens > {_limits.CompactionTruncationTokens:#,0})");
                TruncateOldest(history);
                compacted = true;
            }

            int tokensAfter = history.EstimateTokens();
            if (compacted)
                _log?.Invoke("COMPACT", $"Compaction complete: ~{tokensAfter:#,0} tokens (was {tokensBefore:#,0}), {history.Count} messages");

            return new CompactionResult(compacted, tokensBefore, tokensAfter);
        }
        catch (Exception ex)
        {
            _log?.Invoke("COMPACT", $"Compaction failed: {ex.Message}");
            return new CompactionResult(false, tokensBefore, history.EstimateTokens());
        }
    }

    /// <summary>
    /// Check whether compaction should be triggered based on current history state.
    /// Call this at the end of each turn to decide if compaction is needed.
    /// </summary>
    public bool ShouldCompact(ChatHistoryManager history)
    {
        return CountToolResultMessages(history) > _limits.CompactionToolResultMessages
            || history.EstimateTokens() > _limits.CompactionSummarizationTokens
            || history.CountUserTurns() > _limits.CompactionSlidingWindowTurns
            || history.EstimateTokens() > _limits.CompactionTruncationTokens;
    }

    // ── Stage 1: Tool result collapse ──

    private static void CollapseToolResults(ChatHistoryManager history)
    {
        // Prune old tool results to short summaries, keeping recent turns intact
        history.PruneOldToolResults(keepRecentTurns: 3, summaryMaxChars: 100);
    }

    private static int CountToolResultMessages(ChatHistoryManager history)
    {
        return history.GetMessages().Count(m =>
            m.Role == ChatRole.Tool && m.Contents.Any(c => c is FunctionResultContent));
    }

    // ── Stage 2: LLM summarization ──

    private const string SummarizationPrompt =
        """
        You are summarizing an AI-assisted memory analysis conversation for context compaction.
        The summary REPLACES the original messages — the AI will only see your summary going forward.

        PRESERVE with exact values (these are critical and cannot be re-derived):
        - Memory addresses (hex), offsets, and pointer chains
        - Data types, struct layouts, and field sizes
        - Scan results: value type, scan parameters, number of results, key addresses found
        - Byte sequences, patterns, and signatures
        - AOB (array of bytes) patterns and their locations
        - Breakpoint addresses and hit conditions
        - Code cave addresses and injected code
        - Cheat table entries and their activation state
        - All tool calls that CHANGED state (writes, patches, breakpoints) with their exact parameters
        - Error messages and their resolution

        DROP (these can be re-derived by re-running tools):
        - Raw memory dump output (keep only the interpretation/findings)
        - Full disassembly listings (keep only the key instructions referenced)
        - Large scan result lists (keep the count and top addresses)
        - Module list output (keep only modules that were actually referenced)
        - Routine status messages and tool call confirmations

        FORMAT: Use a structured format with headers. Be factual and terse.
        Start with "## Compacted Session Summary" and organize by topic, not chronology.
        """;

    private async Task SummarizeOlderMessages(
        ChatHistoryManager history,
        CancellationToken ct)
    {
        var messages = history.GetMessages();
        if (messages.Count < 6) return; // Too few to summarize

        // Summarize the first 2/3 of messages, keep the last 1/3 intact.
        // The split point must land on a valid boundary — never between an
        // assistant(tool_calls) message and its tool(results) message, or the
        // kept portion starts with an orphaned tool message that the API rejects.
        int keepCount = Math.Max(messages.Count / 3, 4);
        int summarizeCount = messages.Count - keepCount;

        // Adjust split point: if toKeep would start with a tool-role message,
        // pull the split earlier until we hit a user or standalone assistant message.
        while (summarizeCount > 0 && summarizeCount < messages.Count
            && messages[summarizeCount].Role == ChatRole.Tool)
        {
            summarizeCount--;
            keepCount++;
        }

        // Build text to summarize
        var toSummarize = messages.Take(summarizeCount).ToList();
        var toKeep = messages.Skip(summarizeCount).ToList();

        var summaryText = BuildSummaryInput(toSummarize);

        try
        {
            var summaryResponse = await _summarizationClient.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, SummarizationPrompt),
                    new ChatMessage(ChatRole.User, summaryText),
                ],
                new ChatOptions { MaxOutputTokens = 4096, Temperature = 0.1f },
                ct).ConfigureAwait(false);

            var summary = summaryResponse.Text ?? "[summary unavailable]";
            _log?.Invoke("COMPACT", $"Summarized {summarizeCount} messages into {summary.Length} chars");

            // Rebuild history: summary message + kept messages
            var compacted = new List<ChatMessage>
            {
                new(ChatRole.User,
                    $"<result>\n{summary}\n</result>\n\n" +
                    "The above is a compacted summary of earlier work in this session. " +
                    "Treat it as authoritative — do not ask the user to repeat information that appears in the summary.")
            };
            compacted.AddRange(toKeep);

            history.ReplaceAll(compacted);
        }
        catch (Exception ex)
        {
            _log?.Invoke("COMPACT", $"Summarization failed: {ex.Message} — falling back to sliding window");
            // Fall through to next stage
        }
    }

    private static string BuildSummaryInput(IList<ChatMessage> messages)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var msg in messages)
        {
            sb.Append(CultureInfo.InvariantCulture, $"[{msg.Role}] ");
            foreach (var content in msg.Contents)
            {
                if (content is TextContent tc)
                    sb.AppendLine(tc.Text);
                else if (content is FunctionCallContent fc)
                    sb.AppendLine(CultureInfo.InvariantCulture, $"Called {fc.Name}({FormatArgs(fc)})");
                else if (content is FunctionResultContent fr)
                {
                    var resultStr = fr.Result?.ToString() ?? "";
                    sb.AppendLine(CultureInfo.InvariantCulture, $"Result: {(resultStr.Length > 200 ? string.Concat(resultStr.AsSpan(0, 200), "...") : resultStr)}");
                }
            }
        }
        return sb.ToString();
    }

    private static string FormatArgs(FunctionCallContent fc)
    {
        if (fc.Arguments is null or { Count: 0 }) return "";
        return string.Join(", ", fc.Arguments.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    // ── Stage 3: Sliding window ──

    private void ApplySlidingWindow(ChatHistoryManager history)
    {
        var messages = history.GetMessages();
        int keepTurns = _limits.CompactionSlidingWindowTurns;

        // Find the start index of the Nth-from-last user turn
        int userTurnsSeen = 0;
        int cutIndex = 0;
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == ChatRole.User)
            {
                userTurnsSeen++;
                if (userTurnsSeen >= keepTurns)
                {
                    cutIndex = i;
                    break;
                }
            }
        }

        if (cutIndex > 0)
        {
            var kept = messages.Skip(cutIndex).ToList();
            _log?.Invoke("COMPACT", $"Sliding window: dropped {cutIndex} messages, keeping {kept.Count}");
            history.ReplaceAll(kept);
        }
    }

    // ── Stage 4: Emergency truncation ──

    private void TruncateOldest(ChatHistoryManager history)
    {
        var messages = history.GetMessages();
        // Drop messages from the front until under the token limit
        var kept = new List<ChatMessage>();
        int tokenEstimate = 0;
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            var msgTokens = EstimateMessageTokens(messages[i]);
            if (tokenEstimate + msgTokens > _limits.CompactionTruncationTokens && kept.Count >= 4)
                break;
            kept.Insert(0, messages[i]);
            tokenEstimate += msgTokens;
        }
        // Drop orphaned tool messages from the front (no preceding assistant with tool_calls)
        while (kept.Count > 0 && kept[0].Role == ChatRole.Tool)
            kept.RemoveAt(0);

        _log?.Invoke("COMPACT", $"Truncation: dropped {messages.Count - kept.Count} messages, keeping {kept.Count} (~{tokenEstimate:#,0} tokens)");
        history.ReplaceAll(kept);
    }

    private static int EstimateMessageTokens(ChatMessage msg)
    {
        int chars = 0;
        foreach (var content in msg.Contents)
        {
            if (content is TextContent tc) chars += tc.Text?.Length ?? 0;
            else if (content is FunctionCallContent fc) chars += (fc.Name?.Length ?? 0) + (fc.Arguments?.ToString()?.Length ?? 0);
            else if (content is FunctionResultContent fr) chars += fr.Result?.ToString()?.Length ?? 0;
        }
        return chars / 4;
    }
}

/// <summary>
/// Result of a compaction pipeline run. Allows callers to distinguish success
/// from failure and track token savings.
/// </summary>
public sealed record CompactionResult(bool Success, int TokensBefore, int TokensAfter)
{
    /// <summary>Number of tokens freed by compaction.</summary>
    public int TokensSaved => TokensBefore - TokensAfter;
}
