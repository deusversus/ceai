using System.Text.Json;
using System.Text.Json.Serialization;

namespace CEAISuite.Application.AgentLoop;

/// <summary>
/// Tracks metadata about the current AI session: target process, discoveries,
/// timeline, cost, and tags. Persisted alongside chat history for cross-session
/// search and session branching.
///
/// Modeled after Claude Code's session enrichment with metadata, search, and branching.
/// </summary>
public sealed class SessionMetadata
{
    /// <summary>Unique session identifier.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>Session start time.</summary>
    [JsonPropertyName("startedAt")]
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Target process name (if attached).</summary>
    [JsonPropertyName("targetProcess")]
    public string? TargetProcessName { get; set; }

    /// <summary>Target process ID.</summary>
    [JsonPropertyName("targetPid")]
    public int? TargetProcessId { get; set; }

    /// <summary>Structures/offsets discovered during the session.</summary>
    [JsonPropertyName("discoveries")]
    public List<Discovery> Discoveries { get; set; } = [];

    /// <summary>Timeline of significant events.</summary>
    [JsonPropertyName("timeline")]
    public List<TimelineEvent> Timeline { get; set; } = [];

    /// <summary>Cumulative cost in USD.</summary>
    [JsonPropertyName("cumulativeCost")]
    public decimal CumulativeCost { get; set; }

    /// <summary>User-assigned tags for categorization.</summary>
    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    /// <summary>Total tool calls in this session.</summary>
    [JsonPropertyName("totalToolCalls")]
    public int TotalToolCalls { get; set; }

    /// <summary>Model(s) used during the session.</summary>
    [JsonPropertyName("modelsUsed")]
    public HashSet<string> ModelsUsed { get; set; } = [];

    /// <summary>Add a timeline event.</summary>
    public void AddEvent(string description, string? toolName = null)
    {
        Timeline.Add(new TimelineEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Description = description,
            ToolName = toolName,
        });
    }

    /// <summary>Add a discovery.</summary>
    public void AddDiscovery(string name, string? address = null, string? type = null)
    {
        Discoveries.Add(new Discovery
        {
            Name = name,
            Address = address,
            Type = type,
            DiscoveredAt = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>Get a compact summary string.</summary>
    public string GetSummary()
    {
        var proc = TargetProcessName is not null ? $" (target: {TargetProcessName})" : "";
        return $"Session {Id}{proc} — {Timeline.Count} events, {Discoveries.Count} discoveries, " +
               $"{TotalToolCalls} tool calls, ~${CumulativeCost:F4}";
    }
}

/// <summary>A discovered structure, offset, or pattern.</summary>
public sealed class Discovery
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("address")] public string? Address { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("discoveredAt")] public DateTimeOffset DiscoveredAt { get; set; }
}

/// <summary>A timestamped event in the session timeline.</summary>
public sealed class TimelineEvent
{
    [JsonPropertyName("timestamp")] public DateTimeOffset Timestamp { get; set; }
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("toolName")] public string? ToolName { get; set; }
}

/// <summary>
/// Indexes saved sessions for cross-session search. Scans the chat store
/// directory for metadata files and builds a searchable in-memory index.
/// </summary>
public sealed class SessionIndex
{
    private readonly string _storeDirectory;
    private readonly Action<string, string>? _log;
    private readonly List<SessionMetadata> _index = [];
    private readonly object _lock = new();

    public SessionIndex(string storeDirectory, Action<string, string>? log = null)
    {
        _storeDirectory = storeDirectory;
        _log = log;
    }

    /// <summary>Rebuild the index by scanning the store directory.</summary>
    public void Rebuild()
    {
        if (!Directory.Exists(_storeDirectory)) return;

        var sessions = new List<SessionMetadata>();
        foreach (var file in Directory.EnumerateFiles(_storeDirectory, "*.session.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var meta = JsonSerializer.Deserialize<SessionMetadata>(json, JsonOpts);
                if (meta is not null)
                    sessions.Add(meta);
            }
            catch (Exception ex)
            {
                _log?.Invoke("SESSION", $"Failed to index {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        lock (_lock)
        {
            _index.Clear();
            _index.AddRange(sessions);
        }
        _log?.Invoke("SESSION", $"Indexed {sessions.Count} sessions");
    }

    /// <summary>Search sessions by keyword across all metadata fields.</summary>
    public List<SessionMetadata> Search(string query, int maxResults = 10)
    {
        var q = query.ToLowerInvariant();
        lock (_lock)
        {
            return _index
                .Where(s => MatchesQuery(s, q))
                .OrderByDescending(s => s.StartedAt)
                .Take(maxResults)
                .ToList();
        }
    }

    private static bool MatchesQuery(SessionMetadata s, string q)
    {
        if (s.TargetProcessName?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) return true;
        if (s.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase))) return true;
        if (s.Discoveries.Any(d => d.Name.Contains(q, StringComparison.OrdinalIgnoreCase))) return true;
        if (s.Timeline.Any(e => e.Description.Contains(q, StringComparison.OrdinalIgnoreCase))) return true;
        return false;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
