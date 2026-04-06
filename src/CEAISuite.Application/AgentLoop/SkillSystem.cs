using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

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
    private readonly HashSet<string> _pendingApproval = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly Action<string, string>? _log;
    private readonly ILogger<SkillSystem>? _logger;

    /// <summary>Maximum characters for the catalog summary. Non-bundled descriptions are truncated to fit.</summary>
    public int MaxCatalogChars { get; set; } = 2000;

    public SkillSystem(Action<string, string>? log = null, ILogger<SkillSystem>? logger = null)
    {
        _log = log;
        _logger = logger;
    }

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

        // Elevated skills (AllowedTools, Fork) require user approval before loading
        if (skill.RequiresApproval && !_pendingApproval.Remove(name))
        {
            _pendingApproval.Add(name);
            var reasons = new List<string>();
            if (skill.AllowedTools is { Count: > 0 })
                reasons.Add($"grants tool permissions: {string.Join(", ", skill.AllowedTools)}");
            if (skill.Context == SkillContext.Fork)
                reasons.Add("runs in isolated sub-agent");
            _log?.Invoke("SKILL", $"Pending approval: {name} ({string.Join("; ", reasons)})");
            return $"Skill '{name}' requests elevated permissions ({string.Join("; ", reasons)}). " +
                   $"Use confirm_load_skill('{name}') after user approves.";
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

    /// <summary>Confirm loading of an elevated skill after user approval.</summary>
    public string ConfirmSkillLoad(string name)
    {
        lock (_lock)
        {
            if (!_pendingApproval.Contains(name))
                return $"Skill '{name}' has no pending approval request.";

            // Mark as pre-approved (Remove happens inside LoadSkillCore)
            return LoadSkillCore(name, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
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

                    // Variable substitution: ${SKILL_DIR} → skill's source directory
                    var instructions = skill.Instructions;
                    if (skill.SourceDirectory is not null)
                    {
                        var skillDir = skill.SourceDirectory.Replace('\\', '/');
                        instructions = instructions.Replace("${SKILL_DIR}", skillDir, StringComparison.OrdinalIgnoreCase);
                    }

                    // Auto-include small reference files from references/ subdirectory
                    var refsContent = LoadReferenceFiles(skill);
                    if (refsContent is not null)
                        instructions = instructions + "\n\n" + refsContent;

                    sb.AppendLine(SanitizeSkillInstructions(instructions, skill.Name));
                }
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// Build the skill catalog summary for the LLM. Lists available skills
    /// with name + description, respecting the context budget.
    /// </summary>
    /// <param name="userFacing">When true, omits skills where UserInvocable is false.</param>
    public string BuildCatalogSummary(bool userFacing = false)
    {
        lock (_lock)
        {
            if (_catalog.Count == 0)
                return "No skills available.";

            var skills = _catalog.Values
                .Where(s => !userFacing || s.UserInvocable)
                .OrderBy(s => s.Category ?? "")
                .ThenBy(s => s.Name)
                .ToList();

            if (skills.Count == 0)
                return "No skills available.";

            // Phase 1: Try full descriptions with category grouping
            var result = BuildCatalogWithDescriptions(skills, maxDescLen: int.MaxValue);
            if (result.Length <= MaxCatalogChars)
                return result;

            // Phase 2: Truncate non-bundled descriptions to 250 chars
            result = BuildCatalogWithDescriptions(skills, maxDescLen: 250);
            if (result.Length <= MaxCatalogChars)
                return result;

            // Phase 3: Names only for non-bundled, full for bundled
            return BuildCatalogNamesOnly(skills);
        }
    }

    private string BuildCatalogWithDescriptions(List<SkillDefinition> skills, int maxDescLen)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Available skills ({_activeSkills.Count}/{skills.Count} loaded):");

        string? lastCategory = null;
        foreach (var skill in skills)
        {
            var cat = skill.Category ?? "General";
            if (cat != lastCategory)
            {
                sb.AppendLine($"\n  [{cat}]");
                lastCategory = cat;
            }

            var status = _activeSkills.Contains(skill.Name) ? "✓" : "○";
            var desc = skill.Description;

            // Truncate non-bundled descriptions to budget
            if (!skill.IsBundled && desc.Length > maxDescLen)
                desc = desc[..(maxDescLen - 1)] + "…";

            var version = skill.Version is not null ? $" v{skill.Version}" : "";
            sb.AppendLine($"    {status} {skill.Name}{version} — {desc}");
        }

        sb.AppendLine("\nUse load_skill(name) to activate a skill before using its techniques.");
        return sb.ToString();
    }

    private string BuildCatalogNamesOnly(List<SkillDefinition> skills)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Available skills ({_activeSkills.Count}/{skills.Count} loaded):");

        string? lastCategory = null;
        foreach (var skill in skills)
        {
            var cat = skill.Category ?? "General";
            if (cat != lastCategory)
            {
                sb.AppendLine($"\n  [{cat}]");
                lastCategory = cat;
            }

            var status = _activeSkills.Contains(skill.Name) ? "✓" : "○";

            if (skill.IsBundled)
            {
                // Bundled skills always get full descriptions
                sb.AppendLine($"    {status} {skill.Name} — {skill.Description}");
            }
            else
            {
                sb.AppendLine($"    {status} {skill.Name}");
            }
        }

        sb.AppendLine("\nUse load_skill(name) to activate a skill.");
        return sb.ToString();
    }

    /// <summary>Read a specific reference file from an active skill's references/ directory.</summary>
    public string ReadSkillReference(string skillName, string fileName)
    {
        lock (_lock)
        {
            if (!_activeSkills.Contains(skillName))
                return $"Skill '{skillName}' is not loaded. Load it first with load_skill.";
            if (!_catalog.TryGetValue(skillName, out var skill))
                return $"Skill '{skillName}' not found.";
            if (skill.SourceDirectory is null)
                return $"Skill '{skillName}' has no source directory.";

            var refsDir = Path.Combine(skill.SourceDirectory, "references");
            if (!Directory.Exists(refsDir))
                return $"Skill '{skillName}' has no references/ directory.";

            // Path traversal prevention
            var resolved = Path.GetFullPath(Path.Combine(refsDir, fileName));
            var normalizedRefsDir = Path.GetFullPath(refsDir);
            if (!resolved.StartsWith(normalizedRefsDir, StringComparison.OrdinalIgnoreCase))
                return "Invalid file path — access denied.";

            if (!File.Exists(resolved))
            {
                var available = Directory.GetFiles(refsDir, "*.md").Select(Path.GetFileName);
                return $"File '{fileName}' not found. Available: {string.Join(", ", available)}";
            }

            return File.ReadAllText(resolved);
        }
    }

    /// <summary>Auto-include small reference files for a skill.</summary>
    private static string? LoadReferenceFiles(SkillDefinition skill)
    {
        if (skill.SourceDirectory is null) return null;
        var refsDir = Path.Combine(skill.SourceDirectory, "references");
        if (!Directory.Exists(refsDir)) return null;

        var sb = new System.Text.StringBuilder();
        const int maxFileSize = 3000;
        const int maxTotalSize = 6000;
        int totalSize = 0;

        foreach (var file in Directory.GetFiles(refsDir, "*.md"))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Length > maxFileSize) continue;
                if (totalSize + info.Length > maxTotalSize) break;

                var content = File.ReadAllText(file);
                sb.AppendLine($"\n### Reference: {Path.GetFileName(file)}");
                sb.AppendLine(content);
                totalSize += content.Length;
            }
            catch { /* skip unreadable files */ }
        }

        return sb.Length > 0 ? sb.ToString() : null;
    }

    /// <summary>
    /// Defense-in-depth: strip patterns from skill instructions that could
    /// attempt to override system prompt boundaries or inject directives.
    /// </summary>
    private static readonly Regex InjectionPattern = new(
        @"^\s*\[(SYSTEM|ADMIN|OVERRIDE|IGNORE|INSTRUCTION|PROMPT)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const int MaxSkillInstructionLength = 10_000;

    private string SanitizeSkillInstructions(string instructions, string skillName)
    {
        if (instructions.Length > MaxSkillInstructionLength)
        {
            _logger?.LogWarning("Skill {SkillName} instructions truncated from {OriginalLength} to {MaxLength} chars",
                skillName, instructions.Length, MaxSkillInstructionLength);
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
            _logger?.LogWarning("Skill {SkillName}: stripped {StrippedCount} suspicious directive line(s)",
                skillName, stripped);
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

    /// <summary>Directory containing the skill files (SKILL.md, references/, etc.).</summary>
    public string? SourceDirectory { get; init; }

    /// <summary>Whether this is a built-in skill shipped with the application.</summary>
    public bool IsBundled { get; init; }

    /// <summary>Skill author for display in the UI.</summary>
    public string? Author { get; init; }

    /// <summary>Tags for categorization and display.</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>
    /// Tool name patterns that are auto-approved while this skill is active.
    /// Modeled after Claude Code's allowed-tools frontmatter.
    /// </summary>
    public IReadOnlyList<string>? AllowedTools { get; init; }

    /// <summary>Whether this skill is visible in user-facing UI (default true).</summary>
    public bool UserInvocable { get; init; } = true;

    /// <summary>Execution context: Inline (inject into prompt) or Fork (isolated sub-agent).</summary>
    public SkillContext Context { get; init; } = SkillContext.Inline;

    /// <summary>Whether loading this skill requires user approval (has elevated permissions).</summary>
    public bool RequiresApproval => AllowedTools is { Count: > 0 } || Context == SkillContext.Fork;
}

/// <summary>Execution context for a skill.</summary>
public enum SkillContext
{
    /// <summary>Skill instructions are injected into the main system prompt.</summary>
    Inline,

    /// <summary>Skill runs in an isolated sub-agent with separate context.</summary>
    Fork,
}

/// <summary>
/// Parsed skill frontmatter — shared between SkillLoader (runtime) and SkillsManagerWindow (UI).
/// </summary>
public sealed record SkillFrontmatter
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Version { get; init; }
    public string? Author { get; init; }
    public string? Category { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> Triggers { get; init; } = [];
    public IReadOnlyList<string> AllowedTools { get; init; } = [];
    public IReadOnlyList<string> Dependencies { get; init; } = [];
    public bool UserInvocable { get; init; } = true;
    public SkillContext Context { get; init; } = SkillContext.Inline;
}

/// <summary>
/// Loads skill definitions from a directory. Supports subdirectory SKILL.md,
/// top-level .json and .md files.
/// </summary>
public static class SkillLoader
{
    /// <summary>
    /// Load all skill definitions from a directory. Scans subdirectories for
    /// SKILL.md first, then top-level .json and .md files.
    /// </summary>
    public static List<SkillDefinition> LoadFromDirectory(
        string directoryPath, Action<string, string>? log = null, bool isBundled = false)
    {
        var skills = new List<SkillDefinition>();

        if (!Directory.Exists(directoryPath))
        {
            log?.Invoke("SKILL", $"Skills directory not found: {directoryPath}");
            return skills;
        }

        // Phase 1: Subdirectory scan — each subdirectory with SKILL.md or SKILL.json is a skill
        foreach (var subDir in Directory.GetDirectories(directoryPath))
        {
            try
            {
                var skillMd = Path.Combine(subDir, "SKILL.md");
                var skillJson = Path.Combine(subDir, "SKILL.json");

                SkillDefinition? skill = null;
                if (File.Exists(skillMd))
                    skill = LoadMarkdownSkill(skillMd, subDir, isBundled);
                else if (File.Exists(skillJson))
                    skill = LoadJsonSkill(skillJson, subDir, isBundled);

                if (skill is not null)
                {
                    skills.Add(skill);
                    log?.Invoke("SKILL", $"Loaded from subdir: {Path.GetFileName(subDir)} → {skill.Name}");
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("SKILL", $"Failed to load {Path.GetFileName(subDir)}: {ex.Message}");
            }
        }

        // Phase 2: Top-level files (backward compatibility)
        foreach (var file in Directory.EnumerateFiles(directoryPath, "*.*", SearchOption.TopDirectoryOnly))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            try
            {
                SkillDefinition? skill = ext switch
                {
                    ".json" => LoadJsonSkill(file, directoryPath, isBundled),
                    ".md" => LoadMarkdownSkill(file, directoryPath, isBundled),
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

    private static SkillDefinition? LoadJsonSkill(string filePath, string sourceDir, bool isBundled)
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
            Author = dto.Author,
            Tags = dto.Tags ?? [],
            TriggerPatterns = dto.Triggers ?? [],
            AllowedTools = dto.AllowedTools ?? [],
            UserInvocable = dto.UserInvocable ?? true,
            Context = string.Equals(dto.Context, "fork", StringComparison.OrdinalIgnoreCase)
                ? SkillContext.Fork : SkillContext.Inline,
            SourceDirectory = sourceDir,
            IsBundled = isBundled,
        };
    }

    private static SkillDefinition? LoadMarkdownSkill(string filePath, string sourceDir, bool isBundled)
    {
        var content = File.ReadAllText(filePath);
        var fileName = Path.GetFileNameWithoutExtension(filePath);

        var (fm, body) = SplitFrontmatterLines(content);
        var instructions = body ?? content;

        if (fm is null)
        {
            return new SkillDefinition
            {
                Name = fileName,
                Description = fileName,
                Instructions = instructions,
                SourceDirectory = sourceDir,
                IsBundled = isBundled,
            };
        }

        return new SkillDefinition
        {
            Name = fm.Name.Length > 0 ? fm.Name : fileName,
            Description = fm.Description.Length > 0 ? fm.Description : fileName,
            Instructions = instructions,
            Dependencies = fm.Dependencies,
            Category = fm.Category,
            Version = fm.Version,
            Author = fm.Author,
            Tags = fm.Tags,
            TriggerPatterns = fm.Triggers,
            AllowedTools = fm.AllowedTools,
            UserInvocable = fm.UserInvocable,
            Context = fm.Context,
            SourceDirectory = sourceDir,
            IsBundled = isBundled,
        };
    }

    /// <summary>
    /// Parse YAML frontmatter from a markdown string. Handles folded scalars,
    /// YAML lists, and all skill metadata fields. Shared between runtime loader
    /// and SkillsManagerWindow UI.
    /// </summary>
    public static SkillFrontmatter ParseFrontmatter(string[] lines, int startLine, int endLine)
    {
        string name = "", description = "", version = "", author = "", category = "";
        var tags = new List<string>();
        var triggers = new List<string>();
        var allowedTools = new List<string>();
        var dependencies = new List<string>();
        bool userInvocable = true;
        var context = SkillContext.Inline;

        // Track which list/scalar we're currently accumulating into
        string? activeList = null;  // "tags", "triggers", "allowed-tools", "dependencies"
        bool inFoldedScalar = false; // for description: > continuation lines
        string? foldedField = null;

        for (int i = startLine; i < endLine; i++)
        {
            var line = lines[i];

            // Continuation line (indented) — belongs to active list or folded scalar
            if ((line.StartsWith("  ") || line.StartsWith("\t")) && (activeList is not null || inFoldedScalar))
            {
                var trimmed = line.Trim();

                if (inFoldedScalar && foldedField is not null)
                {
                    if (trimmed.Length > 0)
                    {
                        if (foldedField == "description")
                            description += (description.Length > 0 ? " " : "") + trimmed;
                    }
                    continue;
                }

                if (activeList is not null && trimmed.StartsWith("- "))
                {
                    var item = trimmed[2..].Trim();
                    switch (activeList)
                    {
                        case "tags": tags.Add(item); break;
                        case "triggers": triggers.Add(item); break;
                        case "allowed-tools": allowedTools.Add(item); break;
                        case "dependencies": dependencies.Add(item); break;
                    }
                    continue;
                }

                continue;
            }

            // New top-level key — reset state
            activeList = null;
            inFoldedScalar = false;
            foldedField = null;

            var colonIdx = line.IndexOf(':');
            if (colonIdx < 0) continue;

            var key = line[..colonIdx].Trim().ToLowerInvariant();
            var value = line[(colonIdx + 1)..].Trim();

            switch (key)
            {
                case "name":
                    name = value;
                    break;
                case "description":
                    if (value is ">" or "|")
                    {
                        inFoldedScalar = true;
                        foldedField = "description";
                        description = "";
                    }
                    else
                    {
                        description = value;
                    }
                    break;
                case "version":
                    version = value.Trim('"');
                    break;
                case "author":
                    author = value.Trim('"');
                    break;
                case "category":
                    category = value;
                    break;
                case "tags":
                    activeList = "tags";
                    break;
                case "triggers":
                    activeList = "triggers";
                    break;
                case "allowed-tools":
                    activeList = "allowed-tools";
                    // Also support inline comma-separated: allowed-tools: tool1, tool2
                    if (value.Length > 0)
                    {
                        allowedTools.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                        activeList = null;
                    }
                    break;
                case "dependencies":
                    activeList = "dependencies";
                    break;
                case "user-invocable":
                    userInvocable = !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
                    break;
                case "context":
                    context = string.Equals(value, "fork", StringComparison.OrdinalIgnoreCase)
                        ? SkillContext.Fork : SkillContext.Inline;
                    break;
            }
        }

        return new SkillFrontmatter
        {
            Name = name,
            Description = description,
            Version = version.Length > 0 ? version : null,
            Author = author.Length > 0 ? author : null,
            Category = category.Length > 0 ? category : null,
            Tags = tags,
            Triggers = triggers,
            AllowedTools = allowedTools,
            Dependencies = dependencies,
            UserInvocable = userInvocable,
            Context = context,
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
    /// Split content into parsed SkillFrontmatter and body string.
    /// </summary>
    public static (SkillFrontmatter? Frontmatter, string? Body) SplitFrontmatterLines(string content)
    {
        var lines = content.Split('\n');
        if (lines.Length < 3 || lines[0].Trim() != "---")
            return (null, content);

        int endIdx = -1;
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---") { endIdx = i; break; }
        }
        if (endIdx < 0) return (null, content);

        var fm = ParseFrontmatter(lines, 1, endIdx);

        // Reconstruct body from lines after closing ---
        var bodyLines = lines.Skip(endIdx + 1);
        var body = string.Join("\n", bodyLines).Trim();

        return (fm, body);
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
        [JsonPropertyName("author")] public string? Author { get; set; }
        [JsonPropertyName("tags")] public string[]? Tags { get; set; }
        [JsonPropertyName("triggers")] public string[]? Triggers { get; set; }
        [JsonPropertyName("allowed-tools")] public string[]? AllowedTools { get; set; }
        [JsonPropertyName("user-invocable")] public bool? UserInvocable { get; set; }
        [JsonPropertyName("context")] public string? Context { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
