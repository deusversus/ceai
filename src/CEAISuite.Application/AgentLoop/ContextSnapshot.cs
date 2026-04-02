namespace CEAISuite.Application.AgentLoop;

/// <summary>
/// Snapshot of important context captured before compaction.
/// After the compaction pipeline runs (which may summarize or drop messages),
/// <see cref="PostCompactionRestorer"/> re-injects this context so the agent
/// doesn't lose track of what it was working on.
/// </summary>
public sealed record ContextSnapshot
{
    /// <summary>
    /// Most recent tool results (tool name + result text) captured before compaction.
    /// Limited to the N most recent to stay within token budget.
    /// </summary>
    public required IReadOnlyList<(string ToolName, string Result)> RecentToolResults { get; init; }

    /// <summary>Current address table summary (from context provider), if available.</summary>
    public string? AddressTableSummary { get; init; }

    /// <summary>Current process/module context (from context provider), if available.</summary>
    public string? ProcessContext { get; init; }

    /// <summary>Which tool categories are currently loaded.</summary>
    public required IReadOnlySet<string> ActiveCategories { get; init; }

    /// <summary>Maximum number of recent tool results to capture.</summary>
    public const int MaxRecentResults = 5;

    /// <summary>Maximum characters per individual tool result in the snapshot.</summary>
    public const int MaxCharsPerResult = 5000;

    /// <summary>Total character budget for all restored tool results combined.</summary>
    public const int TotalResultCharBudget = 25_000;
}
