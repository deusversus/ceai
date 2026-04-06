using System.Threading.Channels;
using Microsoft.Extensions.AI;

namespace CEAISuite.Application.AgentLoop;

/// <summary>
/// Spawns child <see cref="AgentLoop"/> instances for parallel subtask execution.
///
/// Modeled after Claude Code's Agent tool which launches specialized subprocesses
/// that work autonomously on complex tasks and report back. Each subagent gets:
/// - Its own <see cref="ChatHistoryManager"/> (independent conversation)
/// - A filtered tool set (configurable per subagent type)
/// - A token budget slice from the parent
/// - Its own compaction pipeline
///
/// The parent agent can spawn multiple subagents concurrently, wait for results,
/// and integrate findings back into its own conversation.
/// </summary>
public sealed class SubagentManager
{
    private readonly IChatClient _chatClient;
    private readonly AgentLoopOptions _parentOptions;
    private readonly ToolAttributeCache _attributeCache;
    private readonly Action<string, string>? _log;
    private readonly List<SubagentHandle> _activeSubagents = [];
    private readonly object _subagentsLock = new();
    private int _subagentCounter;

    public SubagentManager(
        IChatClient chatClient,
        AgentLoopOptions parentOptions,
        ToolAttributeCache attributeCache)
    {
        _chatClient = chatClient;
        _parentOptions = parentOptions;
        _attributeCache = attributeCache;
        _log = parentOptions.Log;
    }

    /// <summary>Currently active subagent handles (snapshot).</summary>
    public IReadOnlyList<SubagentHandle> ActiveSubagents { get { lock (_subagentsLock) return _activeSubagents.ToList(); } }

    /// <summary>
    /// Spawn a new subagent with a specific task. Returns a handle that can be
    /// awaited for the result or cancelled.
    /// </summary>
    public SubagentHandle Spawn(SubagentRequest request)
    {
        var id = $"subagent-{Interlocked.Increment(ref _subagentCounter)}";
        _log?.Invoke("SUBAGENT", $"Spawning [{id}]: {request.Description}");

        // Build subagent-specific options
        var subagentOptions = new AgentLoopOptions
        {
            SystemPrompt = BuildSubagentPrompt(request),
            Tools = FilterTools(request.AllowedToolPatterns),
            Temperature = _parentOptions.Temperature,
            Limits = ScaleLimits(request),
            ToolResultStore = _parentOptions.ToolResultStore,
            DangerousToolNames = _parentOptions.DangerousToolNames,
            MaxTurns = request.MaxTurns ?? 15,
            AdditionalProperties = _parentOptions.AdditionalProperties,
            Log = (level, msg) => _log?.Invoke("SUBAGENT", $"[{id}] [{level}] {msg}"),
            PermissionEngine = _parentOptions.PermissionEngine,
            Hooks = _parentOptions.Hooks,
            Skills = _parentOptions.Skills,
        };

        var history = new ChatHistoryManager();
        var loop = new AgentLoop(_chatClient, subagentOptions, _attributeCache);
        var cts = new CancellationTokenSource();

        var handle = new SubagentHandle
        {
            Id = id,
            Description = request.Description,
            StartedAt = DateTimeOffset.UtcNow,
            CancellationSource = cts,
        };

        // Fire subagent start hooks
        if (_parentOptions.Hooks is { } hooks)
        {
            var startCtx = new SubagentLifecycleContext
            {
                SubagentId = id,
                Description = request.Description,
                IsStart = true,
            };
            _ = Task.Run(async () =>
            {
                try { await hooks.RunSubagentStartHooksAsync(startCtx, CancellationToken.None); }
                catch (Exception ex) { _log?.Invoke("HOOK", $"Subagent start hook error: {ex.Message}"); }
            });
        }

        // Launch the subagent on a background task
        handle.Task = Task.Run(async () =>
        {
            var textParts = new List<string>();
            var toolCalls = 0;

            try
            {
                var reader = loop.RunStreamingAsync(
                    request.Task, history, request.ContextProvider,
                    null, cts.Token);

                await foreach (var evt in reader.ReadAllAsync(cts.Token))
                {
                    switch (evt)
                    {
                        case AgentStreamEvent.TextDelta delta:
                            textParts.Add(delta.Text);
                            break;
                        case AgentStreamEvent.ToolCallCompleted:
                            toolCalls++;
                            break;
                        case AgentStreamEvent.Completed done:
                            break;
                    }
                }

                var result = string.Join("", textParts);
                _log?.Invoke("SUBAGENT", $"[{id}] Completed: {toolCalls} tool calls, {result.Length} chars");

                return new SubagentResult
                {
                    Id = id,
                    Success = true,
                    Text = result,
                    ToolCallCount = toolCalls,
                    Duration = DateTimeOffset.UtcNow - handle.StartedAt,
                };
            }
            catch (OperationCanceledException)
            {
                return new SubagentResult
                {
                    Id = id,
                    Success = false,
                    Text = $"Subagent '{request.Description}' was cancelled.",
                    Duration = DateTimeOffset.UtcNow - handle.StartedAt,
                };
            }
            catch (Exception ex)
            {
                _log?.Invoke("SUBAGENT", $"[{id}] Failed: {ex.GetType().Name}: {ex.Message}");
                return new SubagentResult
                {
                    Id = id,
                    Success = false,
                    Text = $"Subagent error: {ex.Message}",
                    Duration = DateTimeOffset.UtcNow - handle.StartedAt,
                };
            }
            finally
            {
                // Fire subagent end hooks
                if (_parentOptions.Hooks is { } endHooks)
                {
                    var duration = DateTimeOffset.UtcNow - handle.StartedAt;
                    var endCtx = new SubagentLifecycleContext
                    {
                        SubagentId = id,
                        Description = request.Description,
                        IsStart = false,
                        Duration = duration,
                        ToolCallCount = toolCalls,
                    };
                    try { await endHooks.RunSubagentEndHooksAsync(endCtx, CancellationToken.None); }
                    catch (Exception ex) { _log?.Invoke("HOOK", $"Subagent end hook error: {ex.Message}"); }
                }

                lock (_subagentsLock) _activeSubagents.Remove(handle);
                cts.Dispose();
            }
        }, cts.Token);

        lock (_subagentsLock) _activeSubagents.Add(handle);
        return handle;
    }

    /// <summary>
    /// Spawn multiple subagents concurrently and wait for all to complete.
    /// Returns results in the same order as the requests.
    /// </summary>
    public async Task<SubagentResult[]> SpawnAndWaitAsync(
        IReadOnlyList<SubagentRequest> requests,
        CancellationToken ct = default)
    {
        var handles = requests.Select(Spawn).ToList();

        // Link parent cancellation to all subagents.
        // Guard against ObjectDisposedException: the CTS in each handle is disposed
        // by the background task's finally block, which may race with this callback.
        using var registration = ct.Register(() =>
        {
            foreach (var h in handles)
            {
                try { h.CancellationSource.Cancel(); }
                catch (ObjectDisposedException) { /* already finished and disposed */ }
            }
        });

        var tasks = handles.Select(h => h.Task).ToArray();
        return await Task.WhenAll(tasks);
    }

    /// <summary>Cancel all active subagents.</summary>
    public void CancelAll()
    {
        List<SubagentHandle> snapshot;
        lock (_subagentsLock) snapshot = _activeSubagents.ToList();

        foreach (var handle in snapshot)
        {
            try { handle.CancellationSource.Cancel(); }
            catch (ObjectDisposedException) { /* already finished and disposed */ }
        }
    }

    private string BuildSubagentPrompt(SubagentRequest request)
    {
        var prompt = $"""
            You are a focused subagent working on a specific subtask.
            Your parent agent has delegated this task to you. Work autonomously,
            use tools as needed, and return a comprehensive result.

            TASK: {request.Description}

            CONSTRAINTS:
            - Stay focused on the assigned task
            - Use tools proactively — don't just describe what you'd do
            - Return your findings clearly and concisely
            - If you encounter errors, retry with adjusted parameters
            """;

        if (request.Context is not null)
            prompt += $"\n\nCONTEXT FROM PARENT:\n{request.Context}";

        return prompt;
    }

    private IList<AITool> FilterTools(IReadOnlyList<string>? allowedPatterns)
    {
        if (allowedPatterns is null or { Count: 0 })
            return _parentOptions.Tools; // All tools

        var filtered = new List<AITool>();
        foreach (var tool in _parentOptions.Tools)
        {
            var name = tool is AIFunction fn ? fn.Name : "";
            if (allowedPatterns.Any(p => PermissionRule.GlobMatch(name, p)))
                filtered.Add(tool);
        }
        return filtered;
    }

    private TokenLimits ScaleLimits(SubagentRequest request)
    {
        // Subagents get reduced limits to prevent budget exhaustion
        var parent = _parentOptions.Limits;
        var scale = request.BudgetFraction ?? 0.3;

        return new TokenLimits
        {
            MaxOutputTokens = parent.MaxOutputTokens,
            MaxToolResultChars = parent.MaxToolResultChars,
            MaxReplayMessages = (int)(parent.MaxReplayMessages * scale),
            MaxImagesPerTurn = 1,
            MaxApprovalRounds = parent.MaxApprovalRounds,
            CompactionToolResultMessages = (int)(parent.CompactionToolResultMessages * scale),
            CompactionSummarizationTokens = (int)(parent.CompactionSummarizationTokens * scale),
            CompactionSlidingWindowTurns = (int)(parent.CompactionSlidingWindowTurns * scale),
            CompactionTruncationTokens = (int)(parent.CompactionTruncationTokens * scale),
        };
    }
}

/// <summary>Request to spawn a subagent.</summary>
public sealed record SubagentRequest
{
    /// <summary>The task description/prompt for the subagent.</summary>
    public required string Task { get; init; }

    /// <summary>Short human-readable description (shown in status).</summary>
    public required string Description { get; init; }

    /// <summary>
    /// Optional context from the parent agent (e.g., relevant findings,
    /// current state, addresses discovered so far).
    /// </summary>
    public string? Context { get; init; }

    /// <summary>
    /// Tool name patterns the subagent is allowed to use. Null = all tools.
    /// Uses glob patterns (e.g., "Read*", "List*", "Disassemble").
    /// </summary>
    public IReadOnlyList<string>? AllowedToolPatterns { get; init; }

    /// <summary>Maximum turns for the subagent loop (default 15).</summary>
    public int? MaxTurns { get; init; }

    /// <summary>
    /// Fraction of parent's token budget (0.0-1.0). Default 0.3 (30%).
    /// </summary>
    public double? BudgetFraction { get; init; }

    /// <summary>Optional context provider delegate for dynamic state.</summary>
    public Func<string>? ContextProvider { get; init; }

    /// <summary>
    /// Optional callback for permission bubbling. When a subagent encounters a
    /// tool that requires approval, it calls this to bubble the request to the
    /// parent. Parameters: (toolName, argsDescription) → approved.
    /// If null, approval requests within subagents are auto-denied.
    /// </summary>
    public Func<string, string, Task<bool>>? ApprovalBubbleCallback { get; init; }

    /// <summary>
    /// Working directory for the subagent (e.g., for file-system scoped operations).
    /// </summary>
    public string? WorkingDirectory { get; init; }
}

/// <summary>Handle to a running subagent.</summary>
public sealed class SubagentHandle
{
    public required string Id { get; init; }
    public required string Description { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required CancellationTokenSource CancellationSource { get; init; }
    public Task<SubagentResult> Task { get; set; } = null!;

    /// <summary>Cancel this subagent.</summary>
    public void Cancel() => CancellationSource.Cancel();
}

/// <summary>Result from a completed subagent.</summary>
public sealed record SubagentResult
{
    public required string Id { get; init; }
    public required bool Success { get; init; }
    public required string Text { get; init; }
    public int ToolCallCount { get; init; }
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// Factory methods for common subagent configurations with preset tool restrictions.
/// These match Claude Code's built-in agent types: Explore, Plan, Verify, Script.
/// </summary>
public static class SubagentPresets
{
    /// <summary>
    /// Read-only investigation agent. Can read memory, disassemble, list processes,
    /// browse memory regions, and hex dump — but cannot write or modify anything.
    /// </summary>
    public static SubagentRequest Explore(string task, string? context = null) => new()
    {
        Task = task,
        Description = $"[Explore] {Truncate(task, 60)}",
        Context = context,
        AllowedToolPatterns = ["Read*", "List*", "Disassemble*", "Get*", "Browse*", "Hex*",
            "FindFunction*", "Inspect*", "Search*", "Probe*", "request_tools", "list_tool_categories"],
        MaxTurns = 15,
        BudgetFraction = 0.2,
    };

    /// <summary>
    /// Planning-only agent. No tool execution — generates structured plans
    /// and architectural analysis using only meta-tools.
    /// </summary>
    public static SubagentRequest Plan(string task, string? context = null) => new()
    {
        Task = task,
        Description = $"[Plan] {Truncate(task, 60)}",
        Context = context,
        AllowedToolPatterns = ["plan_task", "list_tool_categories", "request_tools",
            "get_budget_status", "recall_memory", "list_skills"],
        MaxTurns = 10,
        BudgetFraction = 0.15,
    };

    /// <summary>
    /// Verification agent. Same read-only tools as Explore, but with a system
    /// prompt emphasizing validation and comparison against expected results.
    /// </summary>
    public static SubagentRequest Verify(string task, string? context = null) => new()
    {
        Task = task,
        Description = $"[Verify] {Truncate(task, 60)}",
        Context = $"VERIFICATION TASK: Confirm that the following operation had the intended effect. " +
                  $"Compare actual state against expected state. Report discrepancies.\n\n{context}",
        AllowedToolPatterns = ["Read*", "List*", "Disassemble*", "Get*", "Browse*", "Hex*",
            "FindFunction*", "Inspect*", "Probe*", "request_tools"],
        MaxTurns = 10,
        BudgetFraction = 0.15,
    };

    /// <summary>
    /// Script-focused agent. Can create, validate, and manage scripts
    /// but has limited access to other tools.
    /// </summary>
    public static SubagentRequest Script(string task, string? context = null) => new()
    {
        Task = task,
        Description = $"[Script] {Truncate(task, 60)}",
        Context = context,
        AllowedToolPatterns = ["*Script*", "ValidateScript*", "ToggleScript",
            "ListScripts", "Read*", "Disassemble*", "Get*"],
        MaxTurns = 15,
        BudgetFraction = 0.25,
    };

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "...");
}

/// <summary>
/// Definition of a subagent type loaded from an AGENT.md file.
/// AGENT.md files use YAML frontmatter (name, tools, maxTurns, model)
/// with the body serving as the agent's instructions.
///
/// Modeled after Claude Code's agent definition files.
/// </summary>
public sealed record SubagentDefinition
{
    /// <summary>Agent type name (from frontmatter or filename).</summary>
    public required string Name { get; init; }

    /// <summary>Optional human-readable description.</summary>
    public string? Description { get; init; }

    /// <summary>Tool glob patterns this agent type is allowed to use.</summary>
    public IReadOnlyList<string>? AllowedToolPatterns { get; init; }

    /// <summary>Maximum turns for this agent type.</summary>
    public int MaxTurns { get; init; } = 15;

    /// <summary>Optional model override for this agent type.</summary>
    public string? Model { get; init; }

    /// <summary>Custom instructions loaded from the markdown body.</summary>
    public string? Instructions { get; init; }

    /// <summary>Convert this definition into a SubagentRequest for a specific task.</summary>
    public SubagentRequest ToRequest(string task, string? context = null) => new()
    {
        Task = task,
        Description = $"[{Name}] {(task.Length > 60 ? task[..60] + "..." : task)}",
        Context = context,
        AllowedToolPatterns = AllowedToolPatterns,
        MaxTurns = MaxTurns,
    };
}

/// <summary>
/// Loads <see cref="SubagentDefinition"/>s from AGENT.md files in a directory.
///
/// File format:
/// ```
/// ---
/// name: explore
/// tools: ["Read*", "List*", "Get*"]
/// maxTurns: 15
/// model: claude-sonnet-4-20250514
/// description: Read-only investigation agent
/// ---
/// You are a focused investigation agent...
/// ```
///
/// Modeled after Claude Code's AGENT.md file loading.
/// </summary>
public static class AgentDefinitionLoader
{
    /// <summary>
    /// Scan a directory for *.md files and parse them as agent definitions.
    /// Files without valid frontmatter are skipped.
    /// </summary>
    public static List<SubagentDefinition> LoadFromDirectory(
        string path, Action<string, string>? log = null)
    {
        var definitions = new List<SubagentDefinition>();

        if (!Directory.Exists(path))
        {
            log?.Invoke("AGENT", $"Agent definition directory not found: {path}");
            return definitions;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*.md"))
        {
            try
            {
                var def = LoadFromFile(file);
                if (def is not null)
                {
                    definitions.Add(def);
                    log?.Invoke("AGENT", $"Loaded agent definition: {def.Name} from {Path.GetFileName(file)}");
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("AGENT", $"Failed to load {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        return definitions;
    }

    /// <summary>
    /// Parse a single AGENT.md file into a <see cref="SubagentDefinition"/>.
    /// Returns null if the file has no valid frontmatter.
    /// </summary>
    public static SubagentDefinition? LoadFromFile(string filePath)
    {
        var content = File.ReadAllText(filePath);
        var (frontmatter, body) = SkillLoader.SplitFrontmatter(content);

        if (frontmatter is null)
            return null;

        // Parse YAML-like frontmatter (simple key: value pairs)
        var props = SkillLoader.ParseSimpleYaml(frontmatter);

        var name = props.GetValueOrDefault("name")
            ?? Path.GetFileNameWithoutExtension(filePath);

        List<string>? toolPatterns = null;
        if (props.TryGetValue("tools", out var toolsStr) && toolsStr is not null)
        {
            // Parse JSON array or comma-separated list
            toolsStr = toolsStr.Trim();
            if (toolsStr.StartsWith('['))
            {
                try
                {
                    toolPatterns = System.Text.Json.JsonSerializer
                        .Deserialize<List<string>>(toolsStr);
                }
                catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"[SubagentSystem] JSON parse failed for tools list, falling back to comma-split: {ex.Message}"); }
            }
            toolPatterns ??= toolsStr.Trim('[', ']')
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.Trim('"', '\''))
                .ToList();
        }

        var maxTurns = 15;
        if (props.TryGetValue("maxturns", out var maxTurnsStr)
            && int.TryParse(maxTurnsStr, out var parsed))
            maxTurns = parsed;

        return new SubagentDefinition
        {
            Name = name,
            Description = props.GetValueOrDefault("description"),
            AllowedToolPatterns = toolPatterns,
            MaxTurns = maxTurns,
            Model = props.GetValueOrDefault("model"),
            Instructions = body?.Trim(),
        };
    }
}
