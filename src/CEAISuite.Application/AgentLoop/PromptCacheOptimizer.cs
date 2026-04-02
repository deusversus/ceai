using System.Security.Cryptography;
using System.Text;

namespace CEAISuite.Application.AgentLoop;

/// <summary>
/// Optimizes system prompt construction for maximum cache hit rates.
/// Splits the prompt into sections with different cache lifetimes and
/// memoizes unchanged sections to avoid re-serialization.
///
/// Strategy: place static content first (cacheable), volatile content last
/// (changes every turn). This maximizes prefix cache hits since caching
/// is prefix-based.
///
/// Modeled after Claude Code's prompt section caching with global/org/null scopes.
/// </summary>
public sealed class PromptCacheOptimizer
{
    private readonly Dictionary<string, string> _sectionHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _cachedSections = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly Action<string, string>? _log;

    /// <summary>Number of sections that were cache hits on the last build.</summary>
    public int LastCacheHits { get; private set; }

    /// <summary>Total sections processed on the last build.</summary>
    public int LastTotalSections { get; private set; }

    public PromptCacheOptimizer(Action<string, string>? log = null) => _log = log;

    /// <summary>
    /// Build an optimized system prompt from sections. Static sections are placed
    /// first for better prefix caching. Returns a <see cref="PromptCacheResult"/>
    /// with both flat text and structured blocks for API-level cache control.
    /// </summary>
    public PromptCacheResult Build(IEnumerable<PromptSection> sections)
    {
        var sectionList = sections.ToList();

        lock (_lock)
        {
            LastTotalSections = sectionList.Count;
            LastCacheHits = 0;

            // Sort: static first, then semi-static, then volatile
            var ordered = sectionList
                .OrderBy(s => s.CacheScope switch
                {
                    PromptCacheScope.Static => 0,
                    PromptCacheScope.Session => 1,
                    PromptCacheScope.Volatile => 2,
                    _ => 3,
                })
                .ToList();

            var sb = new StringBuilder();
            var blocks = new List<PromptCacheBlock>(ordered.Count);

            foreach (var section in ordered)
            {
                var hash = ComputeHash(section.Content);
                bool isUnchanged;
                if (_sectionHashes.TryGetValue(section.Name, out var existingHash) && existingHash == hash)
                {
                    // Cache hit — content unchanged
                    LastCacheHits++;
                    sb.AppendLine(_cachedSections[section.Name]);
                    isUnchanged = true;
                }
                else
                {
                    // Cache miss — update
                    _sectionHashes[section.Name] = hash;
                    _cachedSections[section.Name] = section.Content;
                    sb.AppendLine(section.Content);
                    isUnchanged = false;
                }

                blocks.Add(new PromptCacheBlock
                {
                    Text = section.Content,
                    Scope = section.CacheScope,
                    IsUnchanged = isUnchanged,
                });
            }

            _log?.Invoke("CACHE", $"Prompt sections: {LastCacheHits}/{LastTotalSections} cache hits");
            return new PromptCacheResult
            {
                FlatText = sb.ToString(),
                Blocks = blocks,
            };
        }
    }

    /// <summary>Invalidate all cached sections (e.g., on /clear or /compact).</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _sectionHashes.Clear();
            _cachedSections.Clear();
        }
    }

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes)[..16]; // First 8 bytes is enough
    }
}

/// <summary>
/// Result of building the optimized prompt. Contains both a flat text string
/// (for providers that don't support caching) and structured blocks with
/// cache control metadata (for Anthropic API-level cache_control headers).
/// </summary>
public sealed record PromptCacheResult
{
    /// <summary>The full concatenated prompt text (for providers that don't support caching).</summary>
    public required string FlatText { get; init; }

    /// <summary>Structured sections with cache control metadata (for Anthropic).</summary>
    public required IReadOnlyList<PromptCacheBlock> Blocks { get; init; }
}

/// <summary>
/// A single block of the system prompt with cache scope and change detection.
/// Used to set <c>cache_control: { type: "ephemeral" }</c> on Anthropic API calls.
/// </summary>
public sealed record PromptCacheBlock
{
    /// <summary>The prompt text content.</summary>
    public required string Text { get; init; }

    /// <summary>Cache scope for this block.</summary>
    public required PromptCacheScope Scope { get; init; }

    /// <summary>
    /// Whether this block's content is unchanged since the last build.
    /// Only unchanged Static/Session blocks benefit from server-side caching.
    /// </summary>
    public required bool IsUnchanged { get; init; }
}

/// <summary>A section of the system prompt with its cache scope.</summary>
public sealed record PromptSection
{
    /// <summary>Section name for identification and memoization.</summary>
    public required string Name { get; init; }

    /// <summary>The actual prompt content.</summary>
    public required string Content { get; init; }

    /// <summary>Cache scope for this section.</summary>
    public PromptCacheScope CacheScope { get; init; } = PromptCacheScope.Volatile;
}

/// <summary>
/// Cache scope for prompt sections. Static sections are placed first
/// in the prompt to maximize prefix cache hits.
/// </summary>
public enum PromptCacheScope
{
    /// <summary>Content never changes across conversations. Place first.</summary>
    Static,
    /// <summary>Content changes per session but not per turn.</summary>
    Session,
    /// <summary>Content changes every turn. Place last.</summary>
    Volatile,
}
