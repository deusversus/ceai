using System.Text.Json;
using System.Text.Json.Serialization;

namespace CEAISuite.Application.AgentLoop;

/// <summary>
/// Represents an Anthropic API context management strategy that the server
/// applies automatically to manage token usage. These are "free" server-side
/// compaction mechanisms that reduce costs without any LLM call on our side.
///
/// Two strategies are available:
/// - <c>clear_thinking</c>: manages thinking block retention
/// - <c>clear_tool_uses</c>: auto-clears old tool results at a configurable threshold
///
/// Modeled after Claude Code's context_management API parameter.
/// </summary>
public sealed record ContextManagementStrategy
{
    /// <summary>Strategy name (e.g., "clear_thinking_20251015", "clear_tool_uses_20250919").</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Strategy type (always "context_management").</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "context_management";

    /// <summary>
    /// Fraction of context window at which this strategy activates (0.0-1.0).
    /// For clear_tool_uses: triggers at this fraction, retains the most recent portion.
    /// Default: 0.7 (70% of context window).
    /// </summary>
    [JsonPropertyName("trigger_threshold")]
    public decimal TriggerThreshold { get; init; } = 0.7m;

    /// <summary>
    /// Target token count to retain after clearing. Only used by clear_tool_uses.
    /// Default: 40,000 tokens.
    /// </summary>
    [JsonPropertyName("retention_target")]
    public int RetentionTarget { get; init; } = 40_000;

    // ── Built-in presets ──

    /// <summary>
    /// Clear thinking blocks when context grows large. Thinking blocks are
    /// expensive and often not needed after a few turns.
    /// </summary>
    public static ContextManagementStrategy ClearThinking => new()
    {
        Name = "clear_thinking_20251015",
        TriggerThreshold = 0.8m,
    };

    /// <summary>
    /// Clear old tool use/result pairs server-side. Triggers at 180K input tokens
    /// by default, retains the most recent 40K tokens worth of tool results.
    /// This is free compaction — no LLM call required.
    /// </summary>
    public static ContextManagementStrategy ClearToolUses => new()
    {
        Name = "clear_tool_uses_20250919",
        TriggerThreshold = 0.7m,
        RetentionTarget = 40_000,
    };

    /// <summary>Default set of context management strategies for Anthropic.</summary>
    public static IReadOnlyList<ContextManagementStrategy> AnthropicDefaults =>
    [
        ClearThinking,
        ClearToolUses,
    ];
}

/// <summary>
/// Helper to serialize context management strategies into the format expected
/// by the Anthropic API via <c>AdditionalProperties</c>.
/// </summary>
public static class ContextManagementSerializer
{
    /// <summary>
    /// Serialize strategies into the format expected by the Anthropic API.
    /// Returns a JSON-serializable object to place in <c>AdditionalProperties["context_management"]</c>.
    /// </summary>
    public static object Serialize(IReadOnlyList<ContextManagementStrategy> strategies)
    {
        // The API expects an array of strategy objects
        return strategies.Select(s =>
        {
            var d = new Dictionary<string, object>
            {
                ["name"] = s.Name,
                ["type"] = s.Type,
            };
            if (s.TriggerThreshold > 0) d["trigger_threshold"] = s.TriggerThreshold;
            if (s.RetentionTarget > 0) d["retention_target"] = s.RetentionTarget;
            return d;
        }).ToList();
    }
}
