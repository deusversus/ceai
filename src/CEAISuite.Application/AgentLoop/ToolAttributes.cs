namespace CEAISuite.Application.AgentLoop;

/// <summary>
/// Marks a tool method as safe to execute concurrently with other
/// <see cref="ConcurrencySafeAttribute"/> tools. The <see cref="ToolExecutor"/>
/// batches concurrent-safe calls via <c>Task.WhenAll</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ConcurrencySafeAttribute : Attribute;

/// <summary>
/// Marks a tool that only reads data — never mutates process memory,
/// address table, or session state. All read-only tools are implicitly
/// concurrency-safe.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ReadOnlyToolAttribute : Attribute;

/// <summary>
/// Marks a tool that performs destructive or hard-to-undo mutations
/// (memory writes, hook installs, breakpoint sets, script execution).
/// Destructive tools always run serially and may require approval.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DestructiveAttribute : Attribute;

/// <summary>
/// Declares the maximum expected result size for a tool. Helps the
/// <see cref="ToolExecutor"/> pre-allocate or decide when to spill
/// results to <see cref="ToolResultStore"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class MaxResultSizeAttribute : Attribute
{
    /// <summary>Maximum expected character count of the tool's result.</summary>
    public int MaxChars { get; }

    public MaxResultSizeAttribute(int maxChars) => MaxChars = maxChars;

    /// <summary>Common preset: small result (< 500 chars).</summary>
    public const int Small = 500;
    /// <summary>Common preset: medium result (< 5 000 chars).</summary>
    public const int Medium = 5_000;
    /// <summary>Common preset: large result (< 50 000 chars).</summary>
    public const int Large = 50_000;
    /// <summary>Common preset: unbounded (hex dumps, disassembly, etc.).</summary>
    public const int Unbounded = int.MaxValue;
}

/// <summary>
/// Declares how a tool should behave when the agent loop is aborted
/// mid-execution (e.g., user cancellation).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class InterruptBehaviorAttribute : Attribute
{
    public ToolInterruptMode Mode { get; }
    public InterruptBehaviorAttribute(ToolInterruptMode mode) => Mode = mode;
}

/// <summary>
/// How a tool responds to cancellation / abort.
/// </summary>
public enum ToolInterruptMode
{
    /// <summary>Tool can be safely cancelled at any point (default for read-only).</summary>
    Safe,

    /// <summary>Tool should be allowed to complete even if the loop is aborting
    /// (e.g., it holds a lock or is mid-transaction).</summary>
    MustComplete,

    /// <summary>Tool should run cleanup logic on abort (e.g., remove partial hook,
    /// restore memory protection).</summary>
    RequiresCleanup,
}

/// <summary>
/// Declares keywords for deferred tool discovery. When the agent searches for
/// tools by keyword (via request_tools), these hints are matched against the query.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SearchHintAttribute : Attribute
{
    public string[] Keywords { get; }
    public SearchHintAttribute(params string[] keywords) => Keywords = keywords;
}

/// <summary>
/// Declares an input validation rule for a specific parameter. The regex pattern
/// is checked before tool execution; on mismatch, an error result is returned
/// without invoking the tool.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class ValidateInputAttribute : Attribute
{
    public string ParameterName { get; }
    public string Pattern { get; }
    public ValidateInputAttribute(string parameterName, string pattern)
    {
        ParameterName = parameterName;
        Pattern = pattern;
    }
}

/// <summary>
/// Helper to query tool attributes at runtime. Builds a lookup from method
/// name → attribute set when <see cref="ToolExecutor"/> initializes.
/// </summary>
public sealed class ToolAttributeCache
{
    private readonly Dictionary<string, ToolMetadata> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Scan a type for tool methods and cache their attributes.
    /// Call once at startup with <c>typeof(AiToolFunctions)</c>.
    /// </summary>
    public void ScanType(Type type)
    {
        foreach (var method in type.GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
        {
            if (method.IsSpecialName) continue;

            var meta = new ToolMetadata
            {
                Name = method.Name,
                IsReadOnly = method.GetCustomAttributes(typeof(ReadOnlyToolAttribute), false).Length > 0,
                IsConcurrencySafe = method.GetCustomAttributes(typeof(ConcurrencySafeAttribute), false).Length > 0
                                    || method.GetCustomAttributes(typeof(ReadOnlyToolAttribute), false).Length > 0,
                IsDestructive = method.GetCustomAttributes(typeof(DestructiveAttribute), false).Length > 0,
                MaxResultSize = method.GetCustomAttributes(typeof(MaxResultSizeAttribute), false)
                    is [MaxResultSizeAttribute attr] ? attr.MaxChars : null,
                InterruptMode = method.GetCustomAttributes(typeof(InterruptBehaviorAttribute), false)
                    is [InterruptBehaviorAttribute ib] ? ib.Mode : null,
                SearchHints = method.GetCustomAttributes(typeof(SearchHintAttribute), false)
                    is [SearchHintAttribute sh] ? sh.Keywords : null,
                ValidationRules = method.GetCustomAttributes(typeof(ValidateInputAttribute), false)
                    .Cast<ValidateInputAttribute>()
                    .Select(v => (v.ParameterName, v.Pattern))
                    .ToList() is { Count: > 0 } rules ? rules : null,
            };

            _cache[method.Name] = meta;
        }
    }

    /// <summary>Get metadata for a tool by name. Returns default metadata if not found.</summary>
    public ToolMetadata Get(string toolName)
        => _cache.TryGetValue(toolName, out var meta) ? meta : ToolMetadata.Default;

    /// <summary>Check if a tool is safe to run concurrently.</summary>
    public bool IsConcurrencySafe(string toolName) => Get(toolName).IsConcurrencySafe;

    /// <summary>Check if a tool is destructive.</summary>
    public bool IsDestructive(string toolName) => Get(toolName).IsDestructive;
}

/// <summary>Cached metadata for a single tool method.</summary>
public sealed record ToolMetadata
{
    public required string Name { get; init; }
    public bool IsReadOnly { get; init; }
    public bool IsConcurrencySafe { get; init; }
    public bool IsDestructive { get; init; }
    public int? MaxResultSize { get; init; }
    public ToolInterruptMode? InterruptMode { get; init; }
    public string[]? SearchHints { get; init; }
    public IReadOnlyList<(string ParameterName, string Pattern)>? ValidationRules { get; init; }

    /// <summary>
    /// Effective interrupt mode: explicit attribute > inferred from IsReadOnly/IsDestructive.
    /// </summary>
    public ToolInterruptMode EffectiveInterruptMode =>
        InterruptMode ?? (IsReadOnly ? ToolInterruptMode.Safe
            : IsDestructive ? ToolInterruptMode.RequiresCleanup
            : ToolInterruptMode.Safe);

    public static ToolMetadata Default => new()
    {
        Name = "unknown",
        IsReadOnly = false,
        IsConcurrencySafe = false,
        IsDestructive = false,
    };
}
