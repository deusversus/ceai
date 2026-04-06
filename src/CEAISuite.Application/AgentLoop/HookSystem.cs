using System.Globalization;

namespace CEAISuite.Application.AgentLoop;

/// <summary>
/// Lifecycle hooks that fire before/after tool execution, enabling
/// user-configurable automation (logging, safety gates, side-effects).
///
/// Modeled after Claude Code's hooks system (settings.json) where users
/// configure pre/post tool hooks with shell commands or delegates.
///
/// Hooks are evaluated in registration order. A PreToolHook returning
/// <see cref="HookResult.Block"/> prevents the tool from executing.
/// </summary>
public sealed class HookRegistry
{
    private readonly List<PreToolHook> _preHooks = [];
    private readonly List<PostToolHook> _postHooks = [];
    private readonly List<PostToolFailureHook> _postFailureHooks = [];
    private readonly List<PreLlmHook> _preLlmHooks = [];
    private readonly List<PostLlmHook> _postLlmHooks = [];
    private readonly List<PreCompactionHook> _preCompactionHooks = [];
    private readonly List<PostCompactionHook> _postCompactionHooks = [];
    private readonly List<StopHook> _stopHooks = [];
    private readonly List<SessionLifecycleHook> _sessionStartHooks = [];
    private readonly List<SessionLifecycleHook> _sessionEndHooks = [];
    private readonly List<SubagentLifecycleHook> _subagentStartHooks = [];
    private readonly List<SubagentLifecycleHook> _subagentEndHooks = [];
    private readonly object _hooksLock = new();
    private readonly Action<string, string>? _log;

    public HookRegistry(Action<string, string>? log = null) => _log = log;

    /// <summary>Register a hook that fires before a tool executes.</summary>
    public void AddPreToolHook(PreToolHook hook) { lock (_hooksLock) _preHooks.Add(hook); }

    /// <summary>Register a hook that fires after a tool completes successfully.</summary>
    public void AddPostToolHook(PostToolHook hook) { lock (_hooksLock) _postHooks.Add(hook); }

    /// <summary>Register a hook that fires only when a tool execution fails.</summary>
    public void AddPostToolFailureHook(PostToolFailureHook hook) { lock (_hooksLock) _postFailureHooks.Add(hook); }

    /// <summary>Register a hook that fires before an LLM call (can modify messages).</summary>
    public void AddPreLlmHook(PreLlmHook hook) { lock (_hooksLock) _preLlmHooks.Add(hook); }

    /// <summary>Register a stop hook (fires when agent loop is about to complete).</summary>
    public void AddStopHook(StopHook hook) { lock (_hooksLock) _stopHooks.Add(hook); }

    /// <summary>Register a session start hook.</summary>
    public void AddSessionStartHook(SessionLifecycleHook hook) { lock (_hooksLock) _sessionStartHooks.Add(hook); }

    /// <summary>Register a session end hook.</summary>
    public void AddSessionEndHook(SessionLifecycleHook hook) { lock (_hooksLock) _sessionEndHooks.Add(hook); }

    /// <summary>Register a subagent start hook.</summary>
    public void AddSubagentStartHook(SubagentLifecycleHook hook) { lock (_hooksLock) _subagentStartHooks.Add(hook); }

    /// <summary>Register a subagent end hook.</summary>
    public void AddSubagentEndHook(SubagentLifecycleHook hook) { lock (_hooksLock) _subagentEndHooks.Add(hook); }

    /// <summary>Remove all hooks.</summary>
    public void Clear()
    {
        lock (_hooksLock)
        {
            _preHooks.Clear();
            _postHooks.Clear();
            _postFailureHooks.Clear();
            _preLlmHooks.Clear();
            _postLlmHooks.Clear();
            _preCompactionHooks.Clear();
            _postCompactionHooks.Clear();
            _stopHooks.Clear();
            _sessionStartHooks.Clear();
            _sessionEndHooks.Clear();
            _subagentStartHooks.Clear();
            _subagentEndHooks.Clear();
        }
    }

    public IReadOnlyList<PreToolHook> PreToolHooks { get { lock (_hooksLock) return _preHooks.ToList(); } }
    public IReadOnlyList<PostToolHook> PostToolHooks { get { lock (_hooksLock) return _postHooks.ToList(); } }
    public IReadOnlyList<PreLlmHook> PreLlmHooks { get { lock (_hooksLock) return _preLlmHooks.ToList(); } }

    /// <summary>
    /// Run all pre-tool hooks for a tool call. Returns the combined result.
    /// If any hook blocks, the tool should not execute.
    /// </summary>
    public async Task<HookResult> RunPreToolHooksAsync(ToolHookContext context, CancellationToken ct)
    {
        List<PreToolHook> snapshot;
        lock (_hooksLock) snapshot = _preHooks.ToList();

        foreach (var hook in snapshot)
        {
            if (!hook.MatchesToolPattern(context.ToolName))
                continue;

            try
            {
                var result = await hook.ExecuteAsync(context, ct);
                _log?.Invoke("HOOK", $"Pre-tool [{hook.Name}] on {context.ToolName}: {result.Outcome}");

                if (result.Outcome == HookOutcome.Block)
                    return result;

                // Transform: hook may modify arguments
                if (result.ModifiedArguments is not null)
                    context = context with { Arguments = result.ModifiedArguments };
            }
            catch (Exception ex)
            {
                _log?.Invoke("HOOK", $"Pre-tool [{hook.Name}] failed: {ex.Message}");
                // Hook failures don't block tool execution by default
                if (hook.FailurePolicy == HookFailurePolicy.Block)
                    return HookResult.Blocked($"Hook '{hook.Name}' failed: {ex.Message}");
            }
        }

        return HookResult.Continue();
    }

    /// <summary>Run all post-tool hooks after a tool completes successfully.</summary>
    public async Task RunPostToolHooksAsync(ToolHookContext context, string result, bool isError, CancellationToken ct)
    {
        List<PostToolHook> snapshot;
        lock (_hooksLock) snapshot = _postHooks.ToList();

        foreach (var hook in snapshot)
        {
            if (!hook.MatchesToolPattern(context.ToolName))
                continue;

            try
            {
                await hook.ExecuteAsync(context, result, isError, ct);
                _log?.Invoke("HOOK", $"Post-tool [{hook.Name}] on {context.ToolName}");
            }
            catch (Exception ex)
            {
                _log?.Invoke("HOOK", $"Post-tool [{hook.Name}] failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Run all post-tool failure hooks when a tool execution fails.
    /// Returns a combined <see cref="PostFailureAction"/> if any hook provides one.
    /// </summary>
    public async Task<PostFailureAction?> RunPostToolFailureHooksAsync(
        ToolHookContext context, string errorMessage, bool wasInterrupted, CancellationToken ct)
    {
        List<PostToolFailureHook> snapshot;
        lock (_hooksLock) snapshot = _postFailureHooks.ToList();

        PostFailureAction? combinedAction = null;
        foreach (var hook in snapshot)
        {
            if (!hook.MatchesToolPattern(context.ToolName)) continue;
            try
            {
                var action = await hook.ExecuteAsync(context, errorMessage, wasInterrupted, ct);
                if (action is not null)
                {
                    // Merge: last non-null RetryHint wins, any SuppressDetailedError wins
                    combinedAction = new PostFailureAction
                    {
                        RetryHint = action.RetryHint ?? combinedAction?.RetryHint,
                        SuppressDetailedError = action.SuppressDetailedError || (combinedAction?.SuppressDetailedError ?? false),
                    };
                }
                _log?.Invoke("HOOK", $"PostFailure [{hook.Name}] on {context.ToolName}");
            }
            catch (Exception ex)
            {
                _log?.Invoke("HOOK", $"PostFailure [{hook.Name}] error: {ex.Message}");
            }
        }
        return combinedAction;
    }

    /// <summary>
    /// Run all pre-LLM hooks before an API call. Returns the combined result.
    /// If any hook blocks, the LLM call should be skipped.
    /// </summary>
    public async Task<HookResult> RunPreLlmHooksAsync(PreLlmContext context, CancellationToken ct)
    {
        List<PreLlmHook> snapshot;
        lock (_hooksLock) snapshot = _preLlmHooks.ToList();

        foreach (var hook in snapshot)
        {
            try
            {
                var result = await hook.ExecuteAsync(context, ct);
                _log?.Invoke("HOOK", $"Pre-LLM [{hook.Name}]: {result.Outcome}");

                if (result.Outcome == HookOutcome.Block)
                    return result;
            }
            catch (Exception ex)
            {
                _log?.Invoke("HOOK", $"Pre-LLM [{hook.Name}] failed: {ex.Message}");
            }
        }

        return HookResult.Continue();
    }

    public void AddPostLlmHook(PostLlmHook hook) { lock (_hooksLock) _postLlmHooks.Add(hook); }
    public void AddPreCompactionHook(PreCompactionHook hook) { lock (_hooksLock) _preCompactionHooks.Add(hook); }
    public void AddPostCompactionHook(PostCompactionHook hook) { lock (_hooksLock) _postCompactionHooks.Add(hook); }

    public async Task RunPostLlmHooksAsync(PostLlmContext ctx, CancellationToken ct)
    {
        List<PostLlmHook> snapshot;
        lock (_hooksLock) snapshot = _postLlmHooks.ToList();
        foreach (var hook in snapshot)
        {
            try { await hook.ExecuteAsync(ctx, ct); }
            catch (Exception ex) { _log?.Invoke("HOOK", $"PostLlm hook error: {ex.Message}"); }
        }
    }

    public async Task<HookResult> RunPreCompactionHooksAsync(CompactionHookContext ctx, CancellationToken ct)
    {
        List<PreCompactionHook> snapshot;
        lock (_hooksLock) snapshot = _preCompactionHooks.ToList();
        foreach (var hook in snapshot)
        {
            try
            {
                var result = await hook.ExecuteAsync(ctx, ct);
                if (result.Outcome == HookOutcome.Block)
                    return result;
            }
            catch (Exception ex) { _log?.Invoke("HOOK", $"PreCompaction hook error: {ex.Message}"); }
        }
        return new HookResult { Outcome = HookOutcome.Continue };
    }

    public async Task RunPostCompactionHooksAsync(CompactionHookContext ctx, CancellationToken ct)
    {
        List<PostCompactionHook> snapshot;
        lock (_hooksLock) snapshot = _postCompactionHooks.ToList();
        foreach (var hook in snapshot)
        {
            try { await hook.ExecuteAsync(ctx, ct); }
            catch (Exception ex) { _log?.Invoke("HOOK", $"PostCompaction hook error: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Run stop hooks when the agent loop is about to complete. If any hook
    /// returns Block, the caller should inject a "continue" message and set NextTurn.
    /// </summary>
    public async Task<HookResult> RunStopHooksAsync(StopHookContext ctx, CancellationToken ct)
    {
        List<StopHook> snapshot;
        lock (_hooksLock) snapshot = _stopHooks.ToList();

        foreach (var hook in snapshot)
        {
            try
            {
                var result = await hook.ExecuteAsync(ctx, ct);
                _log?.Invoke("HOOK", $"Stop [{hook.Name}]: {result.Outcome}");
                if (result.Outcome == HookOutcome.Block)
                    return result; // Block means "force continue"
            }
            catch (Exception ex)
            {
                _log?.Invoke("HOOK", $"Stop [{hook.Name}] error: {ex.Message}");
            }
        }

        return HookResult.Continue();
    }

    /// <summary>Run session start hooks (fire-and-forget, no blocking).</summary>
    public async Task RunSessionStartHooksAsync(SessionLifecycleContext ctx, CancellationToken ct)
    {
        List<SessionLifecycleHook> snapshot;
        lock (_hooksLock) snapshot = _sessionStartHooks.ToList();

        foreach (var hook in snapshot)
        {
            try
            {
                await hook.ExecuteAsync(ctx, ct);
                _log?.Invoke("HOOK", $"SessionStart [{hook.Name}]");
            }
            catch (Exception ex)
            {
                _log?.Invoke("HOOK", $"SessionStart [{hook.Name}] error: {ex.Message}");
            }
        }
    }

    /// <summary>Run session end hooks (fire-and-forget, no blocking).</summary>
    public async Task RunSessionEndHooksAsync(SessionLifecycleContext ctx, CancellationToken ct)
    {
        List<SessionLifecycleHook> snapshot;
        lock (_hooksLock) snapshot = _sessionEndHooks.ToList();

        foreach (var hook in snapshot)
        {
            try
            {
                await hook.ExecuteAsync(ctx, ct);
                _log?.Invoke("HOOK", $"SessionEnd [{hook.Name}]");
            }
            catch (Exception ex)
            {
                _log?.Invoke("HOOK", $"SessionEnd [{hook.Name}] error: {ex.Message}");
            }
        }
    }

    /// <summary>Run subagent start hooks (fire-and-forget).</summary>
    public async Task RunSubagentStartHooksAsync(SubagentLifecycleContext ctx, CancellationToken ct)
    {
        List<SubagentLifecycleHook> snapshot;
        lock (_hooksLock) snapshot = _subagentStartHooks.ToList();

        foreach (var hook in snapshot)
        {
            try
            {
                await hook.ExecuteAsync(ctx, ct);
                _log?.Invoke("HOOK", $"SubagentStart [{hook.Name}] on {ctx.SubagentId}");
            }
            catch (Exception ex)
            {
                _log?.Invoke("HOOK", $"SubagentStart [{hook.Name}] error: {ex.Message}");
            }
        }
    }

    /// <summary>Run subagent end hooks (fire-and-forget).</summary>
    public async Task RunSubagentEndHooksAsync(SubagentLifecycleContext ctx, CancellationToken ct)
    {
        List<SubagentLifecycleHook> snapshot;
        lock (_hooksLock) snapshot = _subagentEndHooks.ToList();

        foreach (var hook in snapshot)
        {
            try
            {
                await hook.ExecuteAsync(ctx, ct);
                _log?.Invoke("HOOK", $"SubagentEnd [{hook.Name}] on {ctx.SubagentId}");
            }
            catch (Exception ex)
            {
                _log?.Invoke("HOOK", $"SubagentEnd [{hook.Name}] error: {ex.Message}");
            }
        }
    }
}

/// <summary>Context passed to tool hooks.</summary>
public sealed record ToolHookContext
{
    public required string ToolName { get; init; }
    public IDictionary<string, object?>? Arguments { get; init; }
    public int TurnNumber { get; init; }
    public int TotalToolCalls { get; init; }
}

/// <summary>Context passed to pre-LLM hooks.</summary>
public sealed record PreLlmContext
{
    public required IList<Microsoft.Extensions.AI.ChatMessage> Messages { get; init; }
    public required Microsoft.Extensions.AI.ChatOptions Options { get; init; }
    public int TurnNumber { get; init; }
}

/// <summary>Result returned by a pre-tool hook.</summary>
public sealed record HookResult
{
    public HookOutcome Outcome { get; init; }
    public string? Message { get; init; }
    public IDictionary<string, object?>? ModifiedArguments { get; init; }

    public static HookResult Continue(IDictionary<string, object?>? modifiedArgs = null)
        => new() { Outcome = HookOutcome.Continue, ModifiedArguments = modifiedArgs };

    public static HookResult Blocked(string reason)
        => new() { Outcome = HookOutcome.Block, Message = reason };

    /// <summary>Hook explicitly grants permission (skips the Ask prompt but NOT Deny rules).</summary>
    public static HookResult Allowed(string? reason = null)
        => new() { Outcome = HookOutcome.Allow, Message = reason };
}

public enum HookOutcome { Continue, Block, Allow }

/// <summary>What happens when a hook itself throws an exception.</summary>
public enum HookFailurePolicy
{
    /// <summary>Hook failure is logged but doesn't block tool execution (default).</summary>
    Ignore,
    /// <summary>Hook failure blocks the tool execution.</summary>
    Block,
}

/// <summary>
/// A hook that runs before a tool executes. Can inspect, modify arguments,
/// or block execution entirely.
/// </summary>
public abstract class PreToolHook
{
    public string Name { get; }
    public string? ToolPattern { get; init; }
    public HookFailurePolicy FailurePolicy { get; init; } = HookFailurePolicy.Ignore;

    protected PreToolHook(string name) => Name = name;

    /// <summary>Check if this hook applies to the given tool name.</summary>
    public bool MatchesToolPattern(string toolName)
    {
        if (ToolPattern is null or "*") return true;
        return PermissionRule.GlobMatch(toolName, ToolPattern);
    }

    public abstract Task<HookResult> ExecuteAsync(ToolHookContext context, CancellationToken ct);
}

/// <summary>
/// A hook that runs after a tool completes. Can observe the result,
/// trigger side effects, or log.
/// </summary>
public abstract class PostToolHook
{
    public string Name { get; }
    public string? ToolPattern { get; init; }

    protected PostToolHook(string name) => Name = name;

    public bool MatchesToolPattern(string toolName)
    {
        if (ToolPattern is null or "*") return true;
        return PermissionRule.GlobMatch(toolName, ToolPattern);
    }

    public abstract Task ExecuteAsync(ToolHookContext context, string result, bool isError, CancellationToken ct);
}

/// <summary>
/// A hook that fires ONLY when a tool execution fails (throws or returns an error).
/// Separate from PostToolHook which fires on all completions.
/// Use this for: cleanup actions, error alerting, retry hint injection, undo operations.
/// </summary>
public abstract class PostToolFailureHook
{
    public string Name { get; }
    public string? ToolPattern { get; init; }

    protected PostToolFailureHook(string name) => Name = name;

    public bool MatchesToolPattern(string toolName)
    {
        if (ToolPattern is null or "*") return true;
        return PermissionRule.GlobMatch(toolName, ToolPattern);
    }

    /// <param name="context">Tool call context (name, args, turn number).</param>
    /// <param name="errorMessage">The error message or exception text from the failed tool.</param>
    /// <param name="wasInterrupted">True if the tool was interrupted (cancelled), not just errored.</param>
    public abstract Task<PostFailureAction?> ExecuteAsync(
        ToolHookContext context, string errorMessage, bool wasInterrupted, CancellationToken ct);
}

/// <summary>Action to take after a tool failure hook runs.</summary>
public sealed record PostFailureAction
{
    /// <summary>If set, inject this message into the tool result as a hint for the LLM.</summary>
    public string? RetryHint { get; init; }

    /// <summary>If true, the tool result should be replaced with a generic error (suppress details).</summary>
    public bool SuppressDetailedError { get; init; }
}

/// <summary>A hook that runs before each LLM API call. Can block the call.</summary>
public abstract class PreLlmHook
{
    public string Name { get; }
    protected PreLlmHook(string name) => Name = name;
    public abstract Task<HookResult> ExecuteAsync(PreLlmContext context, CancellationToken ct);
}

// ── Built-in hook implementations ──

/// <summary>
/// Hook that logs all tool executions to a delegate. Useful for audit trails.
/// </summary>
public sealed class ToolAuditHook : PostToolHook
{
    private readonly Action<string, string, string, bool> _onToolCompleted;

    public ToolAuditHook(Action<string, string, string, bool> onToolCompleted)
        : base("ToolAudit")
    {
        _onToolCompleted = onToolCompleted;
    }

    public override Task ExecuteAsync(ToolHookContext context, string result, bool isError, CancellationToken ct)
    {
        _onToolCompleted(context.ToolName,
            context.Arguments is not null
                ? string.Join(", ", context.Arguments.Select(kv => $"{kv.Key}={kv.Value}"))
                : "",
            result, isError);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Hook that blocks tool execution if the tool name matches a blocklist.
/// More dynamic than permission rules — can be updated at runtime.
/// </summary>
public sealed class ToolBlocklistHook : PreToolHook
{
    private readonly HashSet<string> _blocked;
    private readonly object _blockedLock = new();

    public ToolBlocklistHook(IEnumerable<string> blockedTools)
        : base("ToolBlocklist")
    {
        _blocked = new HashSet<string>(blockedTools, StringComparer.OrdinalIgnoreCase);
        FailurePolicy = HookFailurePolicy.Block;
    }

    public void Add(string toolName) { lock (_blockedLock) _blocked.Add(toolName); }
    public void Remove(string toolName) { lock (_blockedLock) _blocked.Remove(toolName); }

    public override Task<HookResult> ExecuteAsync(ToolHookContext context, CancellationToken ct)
    {
        bool isBlocked;
        lock (_blockedLock) isBlocked = _blocked.Contains(context.ToolName);

        if (isBlocked)
            return Task.FromResult(HookResult.Blocked($"Tool '{context.ToolName}' is in the blocklist"));
        return Task.FromResult(HookResult.Continue());
    }
}

/// <summary>
/// Hook that enforces a maximum number of tool calls per conversation turn.
/// Prevents runaway tool loops.
/// </summary>
public sealed class MaxToolCallsHook : PreToolHook
{
    private readonly int _maxCalls;

    public MaxToolCallsHook(int maxCalls = 50) : base("MaxToolCalls")
    {
        _maxCalls = maxCalls;
        FailurePolicy = HookFailurePolicy.Block;
    }

    public override Task<HookResult> ExecuteAsync(ToolHookContext context, CancellationToken ct)
    {
        if (context.TotalToolCalls >= _maxCalls)
            return Task.FromResult(HookResult.Blocked(
                $"Maximum tool calls ({_maxCalls}) reached for this conversation. " +
                "Please provide a final response."));
        return Task.FromResult(HookResult.Continue());
    }
}

/// <summary>
/// Delegate-based hook for simple inline hooks without subclassing.
/// </summary>
public sealed class DelegatePreToolHook : PreToolHook
{
    private readonly Func<ToolHookContext, CancellationToken, Task<HookResult>> _handler;

    public DelegatePreToolHook(string name, Func<ToolHookContext, CancellationToken, Task<HookResult>> handler)
        : base(name)
    {
        _handler = handler;
    }

    public override Task<HookResult> ExecuteAsync(ToolHookContext context, CancellationToken ct)
        => _handler(context, ct);
}

/// <summary>
/// Delegate-based post-tool hook for simple inline hooks.
/// </summary>
public sealed class DelegatePostToolHook : PostToolHook
{
    private readonly Func<ToolHookContext, string, bool, CancellationToken, Task> _handler;

    public DelegatePostToolHook(string name, Func<ToolHookContext, string, bool, CancellationToken, Task> handler)
        : base(name)
    {
        _handler = handler;
    }

    public override Task ExecuteAsync(ToolHookContext context, string result, bool isError, CancellationToken ct)
        => _handler(context, result, isError, ct);
}

/// <summary>
/// Hook that fires after the LLM response is received, before tool execution.
/// Can inspect and potentially modify the response text.
/// </summary>
public abstract class PostLlmHook
{
    public string? Name { get; init; }
    public abstract Task<HookResult> ExecuteAsync(PostLlmContext context, CancellationToken ct);
}

/// <summary>Context for post-LLM hooks.</summary>
public sealed record PostLlmContext
{
    public required string ResponseText { get; init; }
    public required int TurnNumber { get; init; }
    public required int ToolCallCount { get; init; }
}

/// <summary>
/// Hook that fires before compaction occurs. Can inject context to preserve
/// or block compaction entirely.
/// </summary>
public abstract class PreCompactionHook
{
    public string? Name { get; init; }
    public abstract Task<HookResult> ExecuteAsync(CompactionHookContext context, CancellationToken ct);
}

/// <summary>
/// Hook that fires after compaction completes. Can add restoration context.
/// </summary>
public abstract class PostCompactionHook
{
    public string? Name { get; init; }
    public abstract Task<HookResult> ExecuteAsync(CompactionHookContext context, CancellationToken ct);
}

/// <summary>Context for compaction hooks.</summary>
public sealed record CompactionHookContext
{
    public required int MessageCountBefore { get; init; }
    public required int MessageCountAfter { get; init; }
    public required int EstimatedTokensBefore { get; init; }
    public required int EstimatedTokensAfter { get; init; }
}

/// <summary>
/// Hook that fires at session start. Can initialize context, load state, etc.
/// </summary>
public abstract class SessionLifecycleHook
{
    public string? Name { get; init; }
    public abstract Task ExecuteAsync(SessionLifecycleContext context, CancellationToken ct);
}

/// <summary>Context for session lifecycle hooks.</summary>
public sealed record SessionLifecycleContext
{
    public required string SessionId { get; init; }
    public required bool IsNewSession { get; init; }
}

/// <summary>
/// Hook that fires when the agent loop is about to complete (no more tool calls).
/// If it returns Block, the loop injects a "continue" message and stays alive.
/// This allows hooks to force continuation when work is detected as incomplete.
/// </summary>
public abstract class StopHook
{
    public string Name { get; }
    protected StopHook(string name) => Name = name;
    public abstract Task<HookResult> ExecuteAsync(StopHookContext ctx, CancellationToken ct);
}

/// <summary>Context passed to stop hooks.</summary>
public sealed record StopHookContext
{
    public required int TurnCount { get; init; }
    public required int TotalToolCalls { get; init; }
    public required string? LastAssistantText { get; init; }
}

/// <summary>
/// Hook that fires when a subagent is spawned or completes.
/// Used for logging, telemetry, or orchestration side-effects.
/// </summary>
public abstract class SubagentLifecycleHook
{
    public string? Name { get; init; }
    public abstract Task ExecuteAsync(SubagentLifecycleContext ctx, CancellationToken ct);
}

/// <summary>Context for subagent lifecycle hooks.</summary>
public sealed record SubagentLifecycleContext
{
    public required string SubagentId { get; init; }
    public required string Description { get; init; }
    /// <summary>True when the subagent is starting, false when it's completing.</summary>
    public required bool IsStart { get; init; }
    /// <summary>Duration of the subagent run (only set when IsStart is false).</summary>
    public TimeSpan? Duration { get; init; }
    /// <summary>Number of tool calls made by the subagent (only set when IsStart is false).</summary>
    public int? ToolCallCount { get; init; }
}

/// <summary>
/// A hook that executes a shell command, passing tool context as environment
/// variables and reading stdout as the hook result.
///
/// Modeled after Claude Code's command-format hooks in settings.json.
/// </summary>
public sealed class CommandHook : PreToolHook
{
    private readonly string _command;
    private readonly string? _arguments;
    private readonly TimeSpan _timeout;
    private readonly Action<string, string>? _log;

    public CommandHook(string command, string? arguments = null,
        string? toolPattern = null, TimeSpan? timeout = null,
        Action<string, string>? log = null)
        : base(toolPattern ?? command)
    {
        _command = command;
        _arguments = arguments;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        _log = log;
    }

    public override async Task<HookResult> ExecuteAsync(ToolHookContext context, CancellationToken ct)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = _command,
                Arguments = _arguments ?? "",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            // Pass tool context as environment variables
            psi.EnvironmentVariables["CEAI_TOOL_NAME"] = context.ToolName;
            psi.EnvironmentVariables["CEAI_TURN_NUMBER"] = context.TurnNumber.ToString(CultureInfo.InvariantCulture);
            if (context.Arguments is not null)
            {
                foreach (var (key, value) in context.Arguments)
                    psi.EnvironmentVariables[$"CEAI_ARG_{key.ToUpperInvariant()}"] = value?.ToString() ?? "";
            }

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
                return new HookResult { Outcome = HookOutcome.Continue, Message = "Failed to start hook command" };

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_timeout);

            var stdout = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);

            _log?.Invoke("HOOK", $"Command hook '{_command}' exited with code {process.ExitCode}");

            // Exit code 0 = continue, 1 = block, 2 = continue with context
            return process.ExitCode switch
            {
                0 => new HookResult { Outcome = HookOutcome.Continue },
                1 => new HookResult { Outcome = HookOutcome.Block, Message = stdout.Trim() },
                _ => new HookResult { Outcome = HookOutcome.Continue, Message = stdout.Trim() },
            };
        }
        catch (Exception ex)
        {
            _log?.Invoke("HOOK", $"Command hook error: {ex.Message}");
            return new HookResult { Outcome = HookOutcome.Continue, Message = $"Hook error: {ex.Message}" };
        }
    }
}

/// <summary>
/// Condition for hook execution. Extends simple tool name matching with
/// argument pattern matching and turn number constraints.
/// </summary>
public sealed record HookCondition
{
    /// <summary>Glob pattern for tool name (e.g., "Write*").</summary>
    public string? ToolPattern { get; init; }

    /// <summary>Regex pattern matched against argument values.</summary>
    public string? ArgumentPattern { get; init; }

    /// <summary>Minimum turn number for this hook to fire.</summary>
    public int? MinTurnNumber { get; init; }

    /// <summary>Check if this condition matches the given context.</summary>
    public bool Matches(ToolHookContext context)
    {
        if (MinTurnNumber.HasValue && context.TurnNumber < MinTurnNumber.Value)
            return false;

        if (ToolPattern is not null && !GlobMatch(context.ToolName, ToolPattern))
            return false;

        if (ArgumentPattern is not null && context.Arguments is not null)
        {
            var anyMatch = context.Arguments.Values
                .Any(v => v is not null && System.Text.RegularExpressions.Regex.IsMatch(
                    v.ToString() ?? "", ArgumentPattern));
            if (!anyMatch) return false;
        }

        return true;
    }

    private static bool GlobMatch(string input, string pattern)
        => PermissionRule.GlobMatch(input, pattern);
}

/// <summary>
/// Trust level for hooks. Plugin hooks are sandboxed -- their exceptions
/// are caught and logged but never block by default.
/// </summary>
public enum HookTrustLevel
{
    /// <summary>Built-in hooks: full trust, can block.</summary>
    Builtin,
    /// <summary>User-defined hooks: trusted, can block.</summary>
    User,
    /// <summary>Plugin-provided hooks: sandboxed, exceptions caught.</summary>
    Plugin,
}
