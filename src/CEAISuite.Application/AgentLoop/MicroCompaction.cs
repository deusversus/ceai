using Microsoft.Extensions.AI;

namespace CEAISuite.Application.AgentLoop;

/// <summary>
/// Lightweight tool result pruning that runs after each tool execution turn.
/// Replaces old, oversized tool results with short summaries to free context
/// space without requiring a full compaction cycle (no API call).
///
/// Only targets "compactable" tools whose output is typically large and
/// ephemeral (bash, file reads, greps, scans). Does NOT touch tool results
/// from the most recent N turns (those may still be referenced by the LLM).
/// </summary>
public sealed class MicroCompaction
{
    private readonly int _keepRecentTurns;
    private readonly int _maxResultCharsBeforePrune;
    private readonly Action<string, string>? _log;

    /// <summary>Tool names whose results are eligible for microcompaction.</summary>
    private static readonly HashSet<string> CompactableTools = new(StringComparer.OrdinalIgnoreCase)
    {
        // CEAI-specific tools with potentially large output
        "read_memory", "scan_memory", "disassemble", "list_modules",
        "list_regions", "read_stack", "get_threads",
        // Address table & CT tools (can be very large with 200+ entries / 50+ scripts)
        "ViewScript", "ListAddressTable", "RefreshAddressTable", "ListScripts",
        "SummarizeCheatTable", "LoadCheatTable", "SaveCheatTable",
        // Generic large-output tools
        "execute_command", "read_file", "search_files", "glob_files",
    };

    public MicroCompaction(
        int keepRecentTurns = 3,
        int maxResultCharsBeforePrune = 2000,
        Action<string, string>? log = null)
    {
        _keepRecentTurns = keepRecentTurns;
        _maxResultCharsBeforePrune = maxResultCharsBeforePrune;
        _log = log;
    }

    /// <summary>
    /// Run microcompaction on the chat history. Returns the number of
    /// tool results that were pruned. Safe to call after every turn.
    /// </summary>
    public int Prune(ChatHistoryManager history)
    {
        var messages = history.GetMessages();
        if (messages.Count < 4) return 0; // Nothing worth pruning

        // Find the cutoff: keep the most recent _keepRecentTurns user turns intact
        int userTurnsSeen = 0;
        int cutoffIndex = messages.Count;
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == ChatRole.User)
            {
                userTurnsSeen++;
                if (userTurnsSeen >= _keepRecentTurns)
                {
                    cutoffIndex = i;
                    break;
                }
            }
        }

        if (cutoffIndex <= 0) return 0; // All messages are "recent"

        int pruned = 0;
        for (int i = 0; i < cutoffIndex; i++)
        {
            var msg = messages[i];
            if (msg.Role != ChatRole.Tool) continue;

            for (int j = 0; j < msg.Contents.Count; j++)
            {
                if (msg.Contents[j] is FunctionResultContent frc)
                {
                    var toolName = frc.CallId ?? ""; // CallId often encodes the tool name
                    var resultStr = frc.Result?.ToString();
                    if (resultStr is null || resultStr.Length <= _maxResultCharsBeforePrune)
                        continue;

                    // Check if the tool name matches any compactable tool
                    // FunctionResultContent doesn't store tool name directly,
                    // so we also check via the preceding assistant message's FunctionCallContent
                    var actualToolName = FindToolNameForResult(messages, i, frc.CallId);
                    if (actualToolName is not null && !CompactableTools.Contains(actualToolName))
                        continue;

                    // Prune: keep a preview + summary (use ChatHistoryManager's lock)
                    var previewLen = Math.Min(100, resultStr.Length);
                    var summary = $"[Pruned tool result ({resultStr.Length:#,0} chars). Preview: {resultStr[..previewLen]}...]";
                    if (frc.CallId is not null)
                        history.ReplaceToolResult(frc.CallId, summary);
                    else
                    {
                        // CallId is null — can't use ReplaceToolResult, but this is rare.
                        // Create a replacement content item via the history manager's lock.
                        history.MutateFunctionResult(msg, j, new FunctionResultContent(frc.CallId ?? "", summary));
                    }
                    pruned++;
                }
            }
        }

        if (pruned > 0)
            _log?.Invoke("MICROCOMPACT", $"Pruned {pruned} old tool results");

        return pruned;
    }

    /// <summary>Add a tool name to the compactable set at runtime.</summary>
    public static void RegisterCompactableTool(string toolName)
        => CompactableTools.Add(toolName);

    /// <summary>
    /// Walk backwards from a tool-result message to find the matching FunctionCallContent
    /// in a preceding assistant message, to determine the tool name.
    /// </summary>
    private static string? FindToolNameForResult(IList<ChatMessage> messages, int toolMsgIndex, string? callId)
    {
        if (callId is null) return null;

        for (int i = toolMsgIndex - 1; i >= 0; i--)
        {
            if (messages[i].Role != ChatRole.Assistant) continue;
            foreach (var content in messages[i].Contents)
            {
                if (content is FunctionCallContent fc && fc.CallId == callId)
                    return fc.Name;
            }
            break; // Only check the immediately preceding assistant message
        }
        return null;
    }
}
