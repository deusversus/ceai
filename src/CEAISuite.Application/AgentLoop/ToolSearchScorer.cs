namespace CEAISuite.Application.AgentLoop;

/// <summary>
/// Scores tool relevance against a keyword query for deferred tool discovery.
/// Used by the enhanced <c>request_tools</c> meta-tool to find the best
/// category when the agent searches by keyword instead of category name.
///
/// Scoring: exact name match (100) > prefix match (70) > substring (40) >
/// SearchHint match (50) > description match (20).
/// </summary>
public static class ToolSearchScorer
{
    /// <summary>
    /// Score how well a tool matches a keyword query.
    /// Returns 0-100 relevance score.
    /// </summary>
    public static int Score(string keyword, string toolName, string[]? searchHints = null, string? description = null)
    {
        var kw = keyword.ToLowerInvariant();
        var tn = toolName.ToLowerInvariant();

        // Exact match (case-insensitive)
        if (tn == kw) return 100;

        // Prefix match
        if (tn.StartsWith(kw, StringComparison.OrdinalIgnoreCase)) return 70;

        // SearchHint match
        if (searchHints is not null)
        {
            foreach (var hint in searchHints)
            {
                if (hint.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    return 50;
            }
        }

        // Substring match in tool name
        if (tn.Contains(kw, StringComparison.OrdinalIgnoreCase)) return 40;

        // Description match
        if (description is not null && description.Contains(kw, StringComparison.OrdinalIgnoreCase))
            return 20;

        return 0;
    }

    /// <summary>
    /// Score an entire category of tools against a keyword. Returns the
    /// highest score among all tools in the category.
    /// </summary>
    public static int ScoreCategory(
        string keyword,
        IEnumerable<(string ToolName, string[]? SearchHints, string? Description)> tools)
    {
        int best = 0;
        foreach (var (name, hints, desc) in tools)
        {
            var score = Score(keyword, name, hints, desc);
            if (score > best) best = score;
        }
        return best;
    }
}
