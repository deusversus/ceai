using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CEAISuite.Application.AgentLoop;

/// <summary>
/// Cross-session persistent memory for the agent. Stores learned patterns,
/// user preferences, process-specific knowledge, and reusable findings.
///
/// Modeled after Claude Code's memory system (CLAUDE.md / project memory)
/// where the agent maintains a persistent knowledge base that carries
/// across conversations.
///
/// Memory entries are stored as JSON on disk and injected into the system
/// prompt as context. The agent can create, update, and query memories.
/// </summary>
public sealed class MemorySystem
{
    private readonly string _storagePath;
    private readonly Action<string, string>? _log;
    private readonly List<MemoryEntry> _entries = [];
    private bool _loaded;

    public MemorySystem(string? storagePath = null, Action<string, string>? log = null)
    {
        _storagePath = storagePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CEAISuite", "memory.json");
        _log = log;
    }

    /// <summary>All memory entries.</summary>
    public IReadOnlyList<MemoryEntry> Entries => _entries;

    /// <summary>
    /// Load memories from disk. Called once at startup.
    /// </summary>
    public void Load()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var entries = JsonSerializer.Deserialize<List<MemoryEntry>>(json, JsonOpts);
                if (entries is not null)
                {
                    _entries.AddRange(entries);
                    _log?.Invoke("MEMORY", $"Loaded {_entries.Count} memory entries from disk");
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke("MEMORY", $"Failed to load memories: {ex.Message}");
        }
    }

    /// <summary>Save all memories to disk.</summary>
    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (dir is not null)
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_entries, JsonOpts);
            File.WriteAllText(_storagePath, json);
            _log?.Invoke("MEMORY", $"Saved {_entries.Count} memory entries to disk");
        }
        catch (Exception ex)
        {
            _log?.Invoke("MEMORY", $"Failed to save memories: {ex.Message}");
        }
    }

    /// <summary>
    /// Add a new memory entry. Deduplicates by checking for similar existing entries.
    /// </summary>
    public string Remember(string content, MemoryCategory category, string? processName = null, string? source = null)
    {
        // Check for duplicate/similar entries
        var existing = _entries.FirstOrDefault(e =>
            e.Category == category &&
            string.Equals(e.ProcessName, processName, StringComparison.OrdinalIgnoreCase) &&
            IsSimilar(e.Content, content));

        if (existing is not null)
        {
            // Update existing entry
            existing.Content = content;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            existing.AccessCount++;
            Save();
            return $"Updated existing memory: {existing.Id}";
        }

        var entry = new MemoryEntry
        {
            Id = $"mem-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}",
            Content = content,
            Category = category,
            ProcessName = processName,
            Source = source,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _entries.Add(entry);
        Save();

        _log?.Invoke("MEMORY", $"Remembered [{category}]: {content[..Math.Min(80, content.Length)]}");
        return $"Saved memory: {entry.Id}";
    }

    /// <summary>
    /// Query memories matching a search term, category, or process.
    /// </summary>
    public List<MemoryEntry> Recall(
        string? searchTerm = null,
        MemoryCategory? category = null,
        string? processName = null,
        int maxResults = 20)
    {
        var query = _entries.AsEnumerable();

        if (category.HasValue)
            query = query.Where(e => e.Category == category.Value);

        if (processName is not null)
            query = query.Where(e =>
                e.ProcessName is null || // Global memories always match
                string.Equals(e.ProcessName, processName, StringComparison.OrdinalIgnoreCase));

        if (searchTerm is not null)
        {
            var term = searchTerm.ToLowerInvariant();
            query = query.Where(e =>
                e.Content.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (e.ProcessName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        // Sort by relevance: category priority, then recency, then access count
        return query
            .OrderByDescending(e => e.Category == MemoryCategory.UserPreference ? 1 : 0)
            .ThenByDescending(e => e.UpdatedAt)
            .ThenByDescending(e => e.AccessCount)
            .Take(maxResults)
            .ToList();
    }

    /// <summary>
    /// Forget (delete) a memory by ID.
    /// </summary>
    public string Forget(string id)
    {
        var removed = _entries.RemoveAll(e =>
            string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

        if (removed > 0)
        {
            Save();
            return $"Forgotten: {id}";
        }
        return $"Memory '{id}' not found.";
    }

    /// <summary>
    /// Build a context block of relevant memories for injection into the system prompt.
    /// Filters by process name and limits total size.
    /// </summary>
    public string? BuildMemoryContext(string? processName = null, int maxChars = 3000)
    {
        var relevant = Recall(processName: processName, maxResults: 30);
        if (relevant.Count == 0) return null;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("\n═══ AGENT MEMORY ═══");

        int totalChars = 0;
        var grouped = relevant.GroupBy(e => e.Category);

        foreach (var group in grouped)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"\n## {group.Key}");
            foreach (var entry in group)
            {
                var line = entry.ProcessName is not null
                    ? $"• [{entry.ProcessName}] {entry.Content}"
                    : $"• {entry.Content}";

                if (totalChars + line.Length > maxChars)
                {
                    sb.AppendLine("  (more memories available — use recall_memory to search)");
                    goto done;
                }

                sb.AppendLine(line);
                totalChars += line.Length;
                // Note: don't increment AccessCount here — this is called every turn
                // during system prompt construction. Counts are incremented in Recall()
                // when the agent explicitly searches memories.
            }
        }

        done:
        return sb.ToString();
    }

    /// <summary>
    /// Prune old, low-value memories to keep storage manageable.
    /// </summary>
    public int Prune(int maxEntries = 500, TimeSpan? maxAge = null)
    {
        var cutoff = DateTimeOffset.UtcNow - (maxAge ?? TimeSpan.FromDays(90));
        var before = _entries.Count;

        // Remove old, rarely-accessed entries (keep user preferences always)
        _entries.RemoveAll(e =>
            e.Category != MemoryCategory.UserPreference &&
            e.UpdatedAt < cutoff &&
            e.AccessCount < 3);

        // If still over limit, remove least-accessed
        if (_entries.Count > maxEntries)
        {
            var toRemove = _entries
                .Where(e => e.Category != MemoryCategory.UserPreference)
                .OrderBy(e => e.AccessCount)
                .ThenBy(e => e.UpdatedAt)
                .Take(_entries.Count - maxEntries)
                .ToList();

            foreach (var entry in toRemove)
                _entries.Remove(entry);
        }

        var removed = before - _entries.Count;
        if (removed > 0)
        {
            Save();
            _log?.Invoke("MEMORY", $"Pruned {removed} old memories, {_entries.Count} remaining");
        }
        return removed;
    }

    private static bool IsSimilar(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return false;
        // Simple similarity: check if one contains 80% of the other's words.
        // Exempt hex addresses (0x...) from comparison — they're often unique per-run.
        var wordsA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !w.StartsWith("0x", StringComparison.OrdinalIgnoreCase)).ToArray();
        var wordsB = new HashSet<string>(
            b.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => !w.StartsWith("0x", StringComparison.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);
        if (wordsA.Length == 0) return false;
        var overlap = wordsA.Count(w => wordsB.Contains(w));
        return overlap > wordsA.Length * 0.8;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>A single persistent memory entry.</summary>
public sealed class MemoryEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("content")] public string Content { get; set; } = "";
    [JsonPropertyName("category")] public MemoryCategory Category { get; set; }
    [JsonPropertyName("processName")] public string? ProcessName { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; set; }
    [JsonPropertyName("accessCount")] public int AccessCount { get; set; }
}

/// <summary>Categories of agent memory.</summary>
public enum MemoryCategory
{
    /// <summary>User preferences (scan settings, naming conventions, etc.).</summary>
    UserPreference,

    /// <summary>Process-specific knowledge (base addresses, structure layouts).</summary>
    ProcessKnowledge,

    /// <summary>Learned patterns (common value types, engine signatures).</summary>
    LearnedPattern,

    /// <summary>Workflow recipes (step sequences that worked before).</summary>
    WorkflowRecipe,

    /// <summary>Safety notes (addresses to avoid, known crash triggers).</summary>
    SafetyNote,

    /// <summary>Tool usage tips (which tools work best for what).</summary>
    ToolTip,
}
