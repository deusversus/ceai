using System.Collections.Concurrent;

namespace CEAISuite.Application;

/// <summary>
/// In-memory store for large tool results that exceed the token budget.
/// Instead of hard-truncating, the full result is spilled here and the AI
/// receives a summary + handle it can use with <c>RetrieveToolResult</c>
/// to page through the data on demand.
/// </summary>
public sealed class ToolResultStore
{
    private readonly ConcurrentDictionary<string, StoredResult> _results = new();
    private int _nextId;

    public sealed record StoredResult(
        string Id,
        string ToolName,
        string FullText,
        int TotalChars,
        int TotalLines,
        DateTimeOffset StoredAt);

    /// <summary>
    /// Store a large tool result and return its handle ID.
    /// </summary>
    public string Store(string toolName, string fullText)
    {
        var id = $"tr_{Interlocked.Increment(ref _nextId):D4}";
        var lineCount = fullText.AsSpan().Count('\n') + 1;
        _results[id] = new StoredResult(id, toolName, fullText, fullText.Length, lineCount, DateTimeOffset.UtcNow);
        return id;
    }

    /// <summary>
    /// Retrieve a page of a stored result by character offset and length.
    /// Returns null if the handle is not found.
    /// </summary>
    public string? Retrieve(string resultId, int offset, int maxChars)
    {
        if (!_results.TryGetValue(resultId, out var stored))
            return null;

        if (offset < 0) offset = 0;
        if (offset >= stored.TotalChars) return $"[offset {offset} is past end of result ({stored.TotalChars} chars)]";

        var end = Math.Min(offset + maxChars, stored.TotalChars);
        var slice = stored.FullText[offset..end];
        var remaining = stored.TotalChars - end;

        if (remaining > 0)
            return $"{slice}\n... [{remaining:#,0} more chars remaining — use offset={end} to continue]";
        return slice;
    }

    /// <summary>Get metadata about a stored result without retrieving its content.</summary>
    public StoredResult? GetInfo(string resultId)
        => _results.TryGetValue(resultId, out var stored) ? stored : null;

    /// <summary>List all stored result handles with metadata.</summary>
    public IReadOnlyList<StoredResult> ListAll()
        => _results.Values.OrderByDescending(r => r.StoredAt).ToList();

    /// <summary>Remove a stored result to free memory.</summary>
    public bool Remove(string resultId)
        => _results.TryRemove(resultId, out _);

    /// <summary>Clear all stored results (e.g. on new chat).</summary>
    public void Clear()
    {
        _results.Clear();
        Interlocked.Exchange(ref _nextId, 0);
    }

    /// <summary>Total number of stored results.</summary>
    public int Count => _results.Count;
}
