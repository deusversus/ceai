using System.Text.RegularExpressions;

namespace CEAISuite.Application.AgentLoop;

/// <summary>
/// Determines whether a tool invocation is allowed, denied, or requires approval
/// based on configurable permission rules. Evaluated by <see cref="ToolExecutor"/>
/// before every tool invocation.
///
/// Modeled after Claude Code's permission system where tools have Allow/Deny/Ask
/// rules with glob-style patterns on tool names and argument values.
///
/// Rules are evaluated in order — first match wins. If no rule matches, the
/// default behavior applies: dangerous tools require approval, others are allowed.
/// </summary>
public sealed class PermissionEngine
{
    private static readonly System.Text.Json.JsonSerializerOptions s_saveJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
    };
    private static readonly System.Text.Json.JsonSerializerOptions s_loadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private readonly List<PermissionRule> _rules = [];
    private readonly object _rulesLock = new();
    private readonly IReadOnlySet<string> _dangerousToolNames;
    private readonly Action<string, string>? _log;
    private PermissionMode _activeMode = PermissionMode.Normal;
    private readonly Dictionary<string, int> _denialCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ToolAttributeCache? _attributeCache;

    // Skill-scoped rules: keyed by skill name, evaluated after user rules but before DangerousTools default
    private readonly Dictionary<string, List<PermissionRule>> _skillRules = new(StringComparer.OrdinalIgnoreCase);

    public PermissionEngine(
        IReadOnlySet<string> dangerousToolNames,
        Action<string, string>? log = null,
        ToolAttributeCache? attributeCache = null)
    {
        _dangerousToolNames = dangerousToolNames;
        _log = log;
        _attributeCache = attributeCache;
    }

    /// <summary>
    /// The current global permission mode. Checked as a fast-path before individual rules.
    /// </summary>
    public PermissionMode ActiveMode
    {
        get { lock (_rulesLock) return _activeMode; }
        set { lock (_rulesLock) _activeMode = value; }
    }

    /// <summary>Number of configured rules.</summary>
    public int RuleCount { get { lock (_rulesLock) return _rules.Count; } }

    /// <summary>Add a permission rule. Rules are evaluated in insertion order.</summary>
    public void AddRule(PermissionRule rule) { lock (_rulesLock) _rules.Add(rule); }

    /// <summary>Add multiple rules at once.</summary>
    public void AddRules(IEnumerable<PermissionRule> rules) { lock (_rulesLock) _rules.AddRange(rules); }

    /// <summary>Clear all rules.</summary>
    public void ClearRules() { lock (_rulesLock) _rules.Clear(); }

    /// <summary>Add auto-allow rules granted by an active skill. Evaluated after user rules but before DangerousTools default.</summary>
    public void AddSkillRules(string skillName, IEnumerable<PermissionRule> rules)
    {
        lock (_rulesLock) _skillRules[skillName] = rules.ToList();
        _log?.Invoke("PERMISSION", $"Added skill rules for '{skillName}'");
    }

    /// <summary>Remove rules granted by a skill (on unload).</summary>
    public void RemoveSkillRules(string skillName)
    {
        lock (_rulesLock) _skillRules.Remove(skillName);
        _log?.Invoke("PERMISSION", $"Removed skill rules for '{skillName}'");
    }

    /// <summary>Get a read-only snapshot of current rules.</summary>
    public IReadOnlyList<PermissionRule> Rules { get { lock (_rulesLock) return _rules.ToList(); } }

    /// <summary>
    /// Evaluate whether a tool call should be allowed, denied, or require approval.
    /// Returns the decision and the matching rule (if any).
    /// </summary>
    public PermissionDecision Evaluate(string toolName, IDictionary<string, object?>? arguments)
    {
        // Fast-path: check global permission mode
        switch (_activeMode)
        {
            case PermissionMode.Unrestricted:
                return new PermissionDecision(PermissionEffect.Allow, null);

            case PermissionMode.ReadOnly:
                if (_attributeCache is not null && !_attributeCache.Get(toolName).IsReadOnly)
                    return new PermissionDecision(
                        PermissionEffect.Deny,
                        new PermissionRule
                        {
                            ToolPattern = "*",
                            Effect = PermissionEffect.Deny,
                            Description = "Read-only mode: only read-only tools are allowed",
                        });
                return new PermissionDecision(PermissionEffect.Allow, null);

            case PermissionMode.PlanOnly:
                var planTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "plan_task", "list_tool_categories", "request_tools",
                    "get_budget_status", "recall_memory", "list_skills",
                    "get_session_info", "search_sessions",
                };
                if (!planTools.Contains(toolName) && (_attributeCache is null || !_attributeCache.Get(toolName).IsReadOnly))
                    return new PermissionDecision(
                        PermissionEffect.Deny,
                        new PermissionRule
                        {
                            ToolPattern = "*",
                            Effect = PermissionEffect.Deny,
                            Description = "Plan-only mode: only planning and read-only tools are allowed",
                        });
                return new PermissionDecision(PermissionEffect.Allow, null);

            case PermissionMode.Normal:
            default:
                break; // Fall through to normal rule evaluation
        }

        List<PermissionRule> snapshot;
        lock (_rulesLock) snapshot = _rules.ToList();

        foreach (var rule in snapshot)
        {
            if (rule.Matches(toolName, arguments))
            {
                _log?.Invoke("PERMISSION",
                    $"{rule.Effect} → {toolName}({FormatArgs(arguments)}) matched rule: {rule}");
                return new PermissionDecision(rule.Effect, rule);
            }
        }

        // Skill-granted rules (lower priority than user-configured rules)
        List<List<PermissionRule>>? skillRuleSnapshot = null;
        lock (_rulesLock)
        {
            if (_skillRules.Count > 0)
                skillRuleSnapshot = _skillRules.Values.ToList();
        }

        if (skillRuleSnapshot is not null)
        {
            foreach (var ruleList in skillRuleSnapshot)
            {
                foreach (var rule in ruleList)
                {
                    if (rule.Matches(toolName, arguments))
                    {
                        _log?.Invoke("PERMISSION",
                            $"{rule.Effect} (skill-granted) → {toolName}({FormatArgs(arguments)})");
                        return new PermissionDecision(rule.Effect, rule);
                    }
                }
            }
        }

        // Default behavior: dangerous tools need approval, others allowed
        if (_dangerousToolNames.Contains(toolName))
        {
            _log?.Invoke("PERMISSION", $"ASK (default dangerous) → {toolName}");
            return new PermissionDecision(PermissionEffect.Ask, null);
        }

        return new PermissionDecision(PermissionEffect.Allow, null);
    }

    private static string FormatArgs(IDictionary<string, object?>? args)
    {
        if (args is null or { Count: 0 }) return "";
        return string.Join(", ", args.Take(3).Select(kv => $"{kv.Key}={kv.Value}"));
    }

    /// <summary>Track a denial for escalation detection.</summary>
    public void TrackDenial(string toolName)
    {
        lock (_rulesLock)
        {
            _denialCounts.TryGetValue(toolName, out var count);
            _denialCounts[toolName] = count + 1;
        }
    }

    /// <summary>Get the consecutive denial count for a tool.</summary>
    public int GetDenialCount(string toolName)
    {
        lock (_rulesLock)
        {
            return _denialCounts.TryGetValue(toolName, out var count) ? count : 0;
        }
    }

    /// <summary>Reset denial tracking (e.g., when permissions change).</summary>
    public void ResetDenialTracking()
    {
        lock (_rulesLock) _denialCounts.Clear();
    }

    /// <summary>
    /// Threshold for denial escalation. After this many consecutive denials
    /// of the same tool, a system message should be injected.
    /// </summary>
    public int DenialEscalationThreshold { get; set; } = 3;

    /// <summary>
    /// Check if a tool has hit the denial escalation threshold.
    /// </summary>
    public bool ShouldEscalateDenial(string toolName)
    {
        lock (_rulesLock)
        {
            return _denialCounts.TryGetValue(toolName, out var count) && count >= DenialEscalationThreshold;
        }
    }

    /// <summary>Save current rules to a JSON file.</summary>
    public void SaveRules(string path)
    {
        List<PermissionRule> snapshot;
        lock (_rulesLock) snapshot = _rules.ToList();

        var json = System.Text.Json.JsonSerializer.Serialize(snapshot, s_saveJsonOptions);

        var dir = Path.GetDirectoryName(path);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(path, json);
        _log?.Invoke("PERMISSION", $"Saved {snapshot.Count} rules to {path}");
    }

    /// <summary>Load rules from a JSON file. Replaces existing rules.</summary>
    public void LoadRules(string path)
    {
        if (!File.Exists(path)) return;

        try
        {
            var json = File.ReadAllText(path);
            var rules = System.Text.Json.JsonSerializer.Deserialize<List<PermissionRule>>(json, s_loadJsonOptions);

            if (rules is not null)
            {
                lock (_rulesLock)
                {
                    _rules.Clear();
                    _rules.AddRange(rules);
                }
                _log?.Invoke("PERMISSION", $"Loaded {rules.Count} rules from {path}");
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke("PERMISSION", $"Failed to load rules from {path}: {ex.Message}");
        }
    }
}

/// <summary>
/// The outcome of a permission evaluation.
/// </summary>
public sealed record PermissionDecision(PermissionEffect Effect, PermissionRule? MatchedRule);

/// <summary>
/// What happens when a permission rule matches.
/// </summary>
public enum PermissionEffect
{
    /// <summary>Tool call is allowed to proceed without approval.</summary>
    Allow,

    /// <summary>Tool call is denied — return an error to the LLM.</summary>
    Deny,

    /// <summary>Tool call requires user approval before executing.</summary>
    Ask,
}

/// <summary>
/// A single permission rule that matches tool calls by name and optionally
/// by argument patterns. Rules support glob-style wildcards.
///
/// Examples:
///   - <c>new PermissionRule("WriteMemory", PermissionEffect.Ask)</c>
///     → All WriteMemory calls need approval
///   - <c>new PermissionRule("WriteMemory", PermissionEffect.Allow, argumentPatterns: new() { ["address"] = "0x7FF*" })</c>
///     → WriteMemory to high addresses is auto-approved
///   - <c>new PermissionRule("Read*", PermissionEffect.Allow)</c>
///     → All Read* tools are auto-approved
///   - <c>new PermissionRule("*", PermissionEffect.Deny)</c>
///     → Deny everything (emergency lockdown)
/// </summary>
public sealed record PermissionRule
{
    /// <summary>Tool name pattern (supports * and ? wildcards).</summary>
    public required string ToolPattern { get; init; }

    /// <summary>What to do when this rule matches.</summary>
    public required PermissionEffect Effect { get; init; }

    /// <summary>
    /// Optional argument value patterns. Each key is an argument name,
    /// each value is a glob pattern that the argument value must match.
    /// ALL argument patterns must match for the rule to apply.
    /// If null/empty, rule matches on tool name alone.
    /// </summary>
    public Dictionary<string, string>? ArgumentPatterns { get; init; }

    /// <summary>Optional human-readable description for UI display.</summary>
    public string? Description { get; init; }

    /// <summary>Check if this rule matches a tool call.</summary>
    public bool Matches(string toolName, IDictionary<string, object?>? arguments)
    {
        // Check tool name pattern
        if (!GlobMatch(toolName, ToolPattern))
            return false;

        // Check argument patterns (if any)
        if (ArgumentPatterns is { Count: > 0 })
        {
            if (arguments is null)
                return false;

            foreach (var (argName, pattern) in ArgumentPatterns)
            {
                if (!arguments.TryGetValue(argName, out var value))
                    return false;

                var valueStr = value?.ToString() ?? "";
                if (!GlobMatch(valueStr, pattern))
                    return false;
            }
        }

        return true;
    }

    public override string ToString()
    {
        var desc = $"{Effect}({ToolPattern})";
        if (ArgumentPatterns is { Count: > 0 })
            desc += $" when [{string.Join(", ", ArgumentPatterns.Select(kv => $"{kv.Key}={kv.Value}"))}]";
        if (Description is not null)
            desc += $" — {Description}";
        return desc;
    }

    /// <summary>
    /// Simple glob-style pattern matching: * matches any sequence, ? matches one char.
    /// Case-insensitive.
    /// </summary>
    internal static bool GlobMatch(string input, string pattern)
    {
        // Convert glob pattern to regex
        var regexPattern = "^" +
            Regex.Escape(pattern)
                .Replace(@"\*", ".*")
                .Replace(@"\?", ".") +
            "$";
        return Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase);
    }
}
