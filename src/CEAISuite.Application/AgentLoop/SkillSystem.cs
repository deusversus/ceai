using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CEAISuite.Application.AgentLoop;

/// <summary>
/// Skill/plugin system for domain-specific instructions and context.
///
/// Modeled after Claude Code's slash commands / skills where:
/// - Skills are discovered from a directory (YAML/JSON/MD files)
/// - Only name + description are advertised initially (~100 tokens each)
/// - Full instructions are loaded on-demand via load_skill()
/// - Active skills inject instructions into the system prompt
///
/// Skills are progressive: the agent sees a catalog of available skills,
/// calls load_skill("name") to activate one, and the skill's instructions
/// are appended to the next LLM call's system prompt.
/// </summary>
public sealed class SkillSystem
{
    private readonly Dictionary<string, SkillDefinition> _catalog = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activeSkills = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly Action<string, string>? _log;

    public SkillSystem(Action<string, string>? log = null) => _log = log;

    /// <summary>All known skill definitions (snapshot).</summary>
    public IReadOnlyDictionary<string, SkillDefinition> Catalog
    {
        get { lock (_lock) return new Dictionary<string, SkillDefinition>(_catalog, StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>Currently active (loaded) skill names (snapshot).</summary>
    public IReadOnlySet<string> ActiveSkills
    {
        get { lock (_lock) return new HashSet<string>(_activeSkills, StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>Register a skill definition.</summary>
    public void Register(SkillDefinition skill)
    {
        lock (_lock) _catalog[skill.Name] = skill;
        _log?.Invoke("SKILL", $"Registered: {skill.Name} — {skill.Description}");
    }

    /// <summary>Register multiple skills.</summary>
    public void RegisterAll(IEnumerable<SkillDefinition> skills)
    {
        lock (_lock)
        {
            foreach (var skill in skills)
                _catalog[skill.Name] = skill;
        }
        _log?.Invoke("SKILL", $"Registered {skills.Count()} skills");
    }

    /// <summary>
    /// Load (activate) a skill by name. Returns the skill's full instructions
    /// for injection into the system prompt, or an error message.
    /// </summary>
    public string LoadSkill(string name)
    {
        lock (_lock) return LoadSkillCore(name, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    // Must be called under _lock
    private string LoadSkillCore(string name, HashSet<string> loadingChain)
    {
        if (!_catalog.TryGetValue(name, out var skill))
            return $"Skill '{name}' not found. Available: {string.Join(", ", _catalog.Keys)}";

        if (_activeSkills.Contains(name))
            return $"Skill '{name}' is already loaded.";

        // Circular dependency guard
        if (!loadingChain.Add(name))
        {
            _log?.Invoke("SKILL", $"Circular dependency detected: {name} — skipping");
            return $"Circular dependency detected for '{name}' — skipping.";
        }

        // Check dependencies
        foreach (var dep in skill.Dependencies)
        {
            if (!_activeSkills.Contains(dep))
            {
                _log?.Invoke("SKILL", $"Auto-loading dependency: {dep} (required by {name})");
                LoadSkillCore(dep, loadingChain);
            }
        }

        _activeSkills.Add(name);
        _log?.Invoke("SKILL", $"Loaded: {name} ({skill.Instructions.Length} chars)");

        return $"Skill '{name}' loaded. Instructions ({skill.Instructions.Length} chars) will be included in the next prompt.\n\n" +
               $"Summary: {skill.Description}";
    }

    /// <summary>Unload a skill.</summary>
    public string UnloadSkill(string name)
    {
        lock (_lock)
        {
            if (!_activeSkills.Remove(name))
                return $"Skill '{name}' is not loaded.";
        }

        _log?.Invoke("SKILL", $"Unloaded: {name}");
        return $"Skill '{name}' unloaded.";
    }

    /// <summary>
    /// Load a skill with argument substitution. Replaces {key} placeholders
    /// in the skill's instructions with provided argument values.
    /// </summary>
    public string LoadSkillWithArgs(string name, IDictionary<string, string>? arguments = null)
    {
        lock (_lock)
        {
            var result = LoadSkillCore(name, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            // Apply argument substitution if args provided
            if (arguments is { Count: > 0 } && _catalog.TryGetValue(name, out var skill))
            {
                var instructions = skill.Instructions;
                foreach (var (key, value) in arguments)
                    instructions = instructions.Replace($"{{{key}}}", value, StringComparison.OrdinalIgnoreCase);

                // Replace the skill with substituted instructions
                _catalog[name] = skill with { Instructions = instructions };
                _log?.Invoke("SKILL", $"Applied {arguments.Count} argument substitutions to {name}");
            }

            return result;
        }
    }

    /// <summary>
    /// Check if a user message matches any skill's trigger patterns.
    /// Returns the names of matching skills.
    /// </summary>
    public List<string> FindTriggeredSkills(string userMessage)
    {
        lock (_lock)
        {
            var matches = new List<string>();
            foreach (var (name, skill) in _catalog)
            {
                if (skill.TriggerPatterns is null) continue;
                if (_activeSkills.Contains(name)) continue; // Already active

                foreach (var pattern in skill.TriggerPatterns)
                {
                    try
                    {
                        if (System.Text.RegularExpressions.Regex.IsMatch(userMessage, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        {
                            matches.Add(name);
                            break;
                        }
                    }
                    catch { /* Invalid regex — skip */ }
                }
            }
            return matches;
        }
    }

    /// <summary>
    /// Build the skill instructions block to inject into the system prompt.
    /// Only includes instructions from active skills. Returns null if no skills loaded.
    /// </summary>
    public string? BuildActiveSkillInstructions()
    {
        lock (_lock)
        {
            if (_activeSkills.Count == 0)
                return null;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("\n═══ ACTIVE SKILLS ═══");

            foreach (var name in _activeSkills)
            {
                if (_catalog.TryGetValue(name, out var skill))
                {
                    sb.AppendLine($"\n## Skill: {skill.Name}");
                    sb.AppendLine(SanitizeSkillInstructions(skill.Instructions, skill.Name));
                }
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// Build the skill catalog summary for the LLM. Lists available skills
    /// with name + description (compact, ~100 tokens each).
    /// </summary>
    public string BuildCatalogSummary()
    {
        lock (_lock)
        {
            if (_catalog.Count == 0)
                return "No skills available.";

            var lines = new List<string>
            {
                $"Available skills ({_activeSkills.Count}/{_catalog.Count} loaded):"
            };

            foreach (var (name, skill) in _catalog)
            {
                var status = _activeSkills.Contains(name) ? "✓" : "○";
                lines.Add($"  {status} {name} — {skill.Description}");
            }

            lines.Add("\nUse load_skill(name) to activate a skill before using its techniques.");
            return string.Join("\n", lines);
        }
    }

    /// <summary>
    /// Defense-in-depth: strip patterns from skill instructions that could
    /// attempt to override system prompt boundaries or inject directives.
    /// </summary>
    private static readonly Regex InjectionPattern = new(
        @"^\s*\[(SYSTEM|ADMIN|OVERRIDE|IGNORE|INSTRUCTION|PROMPT)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const int MaxSkillInstructionLength = 10_000;

    private static string SanitizeSkillInstructions(string instructions, string skillName)
    {
        if (instructions.Length > MaxSkillInstructionLength)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[SkillSystem] Skill '{skillName}' instructions truncated from {instructions.Length} to {MaxSkillInstructionLength} chars");
            instructions = instructions[..MaxSkillInstructionLength];
        }

        var lines = instructions.Split('\n');
        var filtered = new System.Text.StringBuilder(instructions.Length);
        int stripped = 0;

        foreach (var line in lines)
        {
            if (InjectionPattern.IsMatch(line))
            {
                stripped++;
                continue;
            }
            filtered.AppendLine(line);
        }

        if (stripped > 0)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[SkillSystem] Skill '{skillName}': stripped {stripped} suspicious directive line(s)");
        }

        return filtered.ToString();
    }
}

/// <summary>
/// A domain-specific skill/plugin definition.
/// </summary>
public sealed record SkillDefinition
{
    /// <summary>Unique skill name (used as the key for load_skill).</summary>
    public required string Name { get; init; }

    /// <summary>Short description shown in the catalog (~1 sentence).</summary>
    public required string Description { get; init; }

    /// <summary>
    /// Full instructions injected into the system prompt when loaded.
    /// Typically 500-5000 chars of domain-specific guidance.
    /// </summary>
    public required string Instructions { get; init; }

    /// <summary>Skill names that must be loaded before this one.</summary>
    public IReadOnlyList<string> Dependencies { get; init; } = [];

    /// <summary>Optional category for grouping in the catalog UI.</summary>
    public string? Category { get; init; }

    /// <summary>Optional version string.</summary>
    public string? Version { get; init; }

    /// <summary>Named parameters for argument substitution in instructions.</summary>
    public IReadOnlyDictionary<string, string>? Parameters { get; init; }

    /// <summary>Regex patterns that trigger auto-activation when user message matches.</summary>
    public IReadOnlyList<string>? TriggerPatterns { get; init; }
}

/// <summary>
/// Loads skill definitions from a directory. Supports .json and .md files.
///
/// JSON format:
/// <code>
/// {
///   "name": "memory-scanning",
///   "description": "Expert memory scanning workflows",
///   "instructions": "When scanning memory...",
///   "dependencies": ["basic-operations"],
///   "category": "core"
/// }
/// </code>
///
/// Markdown format (frontmatter-style):
/// <code>
/// ---
/// name: memory-scanning
/// description: Expert memory scanning workflows
/// category: core
/// ---
/// When scanning memory, follow these steps...
/// </code>
/// </summary>
public static class SkillLoader
{
    /// <summary>
    /// Load all skill definitions from a directory. Scans for .json and .md files.
    /// </summary>
    public static List<SkillDefinition> LoadFromDirectory(string directoryPath, Action<string, string>? log = null)
    {
        var skills = new List<SkillDefinition>();

        if (!Directory.Exists(directoryPath))
        {
            log?.Invoke("SKILL", $"Skills directory not found: {directoryPath}");
            return skills;
        }

        foreach (var file in Directory.EnumerateFiles(directoryPath, "*.*", SearchOption.TopDirectoryOnly))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            try
            {
                SkillDefinition? skill = ext switch
                {
                    ".json" => LoadJsonSkill(file),
                    ".md" => LoadMarkdownSkill(file),
                    _ => null,
                };

                if (skill is not null)
                {
                    skills.Add(skill);
                    log?.Invoke("SKILL", $"Loaded from file: {Path.GetFileName(file)} → {skill.Name}");
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("SKILL", $"Failed to load {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        return skills;
    }

    private static SkillDefinition? LoadJsonSkill(string filePath)
    {
        var json = File.ReadAllText(filePath);
        var dto = JsonSerializer.Deserialize<SkillDto>(json, JsonOpts);
        if (dto?.Name is null || dto.Instructions is null)
            return null;

        return new SkillDefinition
        {
            Name = dto.Name,
            Description = dto.Description ?? dto.Name,
            Instructions = dto.Instructions,
            Dependencies = dto.Dependencies ?? [],
            Category = dto.Category,
            Version = dto.Version,
        };
    }

    private static SkillDefinition? LoadMarkdownSkill(string filePath)
    {
        var content = File.ReadAllText(filePath);
        var fileName = Path.GetFileNameWithoutExtension(filePath);

        // Parse simple frontmatter: --- ... ---
        string name = fileName;
        string description = fileName;
        string? category = null;
        string instructions;

        if (content.StartsWith("---"))
        {
            var endIdx = content.IndexOf("---", 3, StringComparison.Ordinal);
            if (endIdx > 0)
            {
                var frontmatter = content[3..endIdx].Trim();
                instructions = content[(endIdx + 3)..].Trim();

                // Parse key: value lines
                foreach (var line in frontmatter.Split('\n'))
                {
                    var colonIdx = line.IndexOf(':');
                    if (colonIdx < 0) continue;
                    var key = line[..colonIdx].Trim().ToLowerInvariant();
                    var value = line[(colonIdx + 1)..].Trim();

                    switch (key)
                    {
                        case "name": name = value; break;
                        case "description": description = value; break;
                        case "category": category = value; break;
                        case "arguments": break; // Parsed separately below
                        case "triggers": break; // Parsed separately below
                    }
                }
            }
            else
            {
                instructions = content;
            }
        }
        else
        {
            instructions = content;
        }

        return new SkillDefinition
        {
            Name = name,
            Description = description,
            Instructions = instructions,
            Category = category,
        };
    }

    /// <summary>
    /// Split content into YAML frontmatter and body. Returns (null, content) if
    /// no frontmatter is found. Used by both SkillLoader and AgentDefinitionLoader.
    /// </summary>
    public static (string? Frontmatter, string? Body) SplitFrontmatter(string content)
    {
        if (!content.StartsWith("---"))
            return (null, content);

        var endIdx = content.IndexOf("---", 3, StringComparison.Ordinal);
        if (endIdx <= 0)
            return (null, content);

        var frontmatter = content[3..endIdx].Trim();
        var body = content[(endIdx + 3)..].Trim();
        return (frontmatter, body);
    }

    /// <summary>
    /// Parse simple "key: value" YAML-like frontmatter into a dictionary.
    /// Keys are lowercased for case-insensitive matching.
    /// </summary>
    public static Dictionary<string, string> ParseSimpleYaml(string frontmatter)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in frontmatter.Split('\n'))
        {
            var colonIdx = line.IndexOf(':');
            if (colonIdx < 0) continue;
            var key = line[..colonIdx].Trim().ToLowerInvariant();
            var value = line[(colonIdx + 1)..].Trim();
            if (!string.IsNullOrEmpty(key))
                result[key] = value;
        }
        return result;
    }

    private sealed class SkillDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("instructions")] public string? Instructions { get; set; }
        [JsonPropertyName("dependencies")] public string[]? Dependencies { get; set; }
        [JsonPropertyName("category")] public string? Category { get; set; }
        [JsonPropertyName("version")] public string? Version { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
