using System.Threading.Channels;
using Microsoft.Extensions.AI;

namespace CEAISuite.Application.AgentLoop;

/// <summary>
/// Resolves, permission-checks, and invokes AI tools. Handles dangerous tool
/// approval via <see cref="AgentStreamEvent.ApprovalRequested"/>, result capping
/// via <see cref="ToolResultStore"/>, and progress reporting via streaming events.
///
/// Batched parallel execution: tools annotated with <see cref="ReadOnlyToolAttribute"/>
/// or <see cref="ConcurrencySafeAttribute"/> run concurrently via <c>Task.WhenAll</c>.
/// Destructive or unannotated tools run serially. Max concurrency is capped at
/// <see cref="MaxParallelTools"/> to avoid overwhelming the target process.
/// </summary>
public sealed class ToolExecutor
{
    private readonly AgentLoopOptions _options;
    private readonly Action<string, string>? _log;
    private readonly ToolAttributeCache _attributeCache;
    private int _totalToolCalls;

    /// <summary>Maximum number of tools to execute in parallel within a single batch.</summary>
    public const int MaxParallelTools = 10;

    /// <summary>
    /// Current turn number, set by <see cref="AgentLoop"/> before each tool execution batch.
    /// </summary>
    public int TurnNumber { get; set; }

    public ToolExecutor(AgentLoopOptions options, ToolAttributeCache attributeCache)
    {
        _options = options;
        _log = options.Log;
        _attributeCache = attributeCache;
    }

    /// <summary>
    /// Execute a batch of tool calls with intelligent parallelism.
    ///
    /// Partitioning strategy:
    /// 1. Walk the call list in order. Accumulate consecutive concurrent-safe calls into a parallel batch.
    /// 2. When a non-concurrent-safe (serial) call is encountered, flush the accumulated parallel batch,
    ///    then execute the serial call alone.
    /// 3. Dangerous tools always run serially (require approval prompt which blocks on user).
    /// 4. Within a parallel batch, calls run via Task.WhenAll with SemaphoreSlim(MaxParallelTools).
    /// 5. If any parallel call fails, remaining siblings continue (no sibling cancellation —
    ///    the LLM needs all results to understand what happened).
    ///
    /// Returns the list of (call, result) pairs in original order for injection into chat history.
    /// </summary>
    public async Task<List<ToolCallResult>> ExecuteAsync(
        IReadOnlyList<FunctionCallContent> toolCalls,
        ChannelWriter<AgentStreamEvent> channel,
        CancellationToken cancellationToken,
        Dictionary<string, Task<ToolCallResult>>? speculativeResults = null)
    {
        if (toolCalls.Count == 0)
            return [];

        // Single tool — fast path, no batching overhead
        if (toolCalls.Count == 1)
        {
            // Check if speculative result is available
            var callId = toolCalls[0].CallId ?? toolCalls[0].Name ?? "";
            if (speculativeResults?.TryGetValue(callId, out var specTask) == true
                && specTask.IsCompletedSuccessfully)
            {
                var specResult = await specTask.ConfigureAwait(false);
                _log?.Invoke("SPECULATIVE", $"Using pre-computed result for {toolCalls[0].Name}");
                await channel.WriteAsync(new AgentStreamEvent.ToolCallStarted(
                    toolCalls[0].Name ?? "unknown", FormatArguments(toolCalls[0])), cancellationToken).ConfigureAwait(false);
                await channel.WriteAsync(new AgentStreamEvent.ToolCallCompleted(
                    toolCalls[0].Name ?? "unknown", Truncate(specResult.Result.Result?.ToString() ?? "", 200)), cancellationToken).ConfigureAwait(false);
                return [specResult];
            }

            var result = await ExecuteSingleAsync(toolCalls[0], channel, cancellationToken).ConfigureAwait(false);
            return [result];
        }

        // Partition into ordered segments of parallel + serial
        var allResults = new ToolCallResult[toolCalls.Count];
        var parallelBatch = new List<(int Index, FunctionCallContent Call)>();

        for (int i = 0; i < toolCalls.Count; i++)
        {
            var call = toolCalls[i];
            var name = call.Name ?? "unknown";

            // Check if we already have a speculative result for this call
            var specCallId = call.CallId ?? name;
            if (speculativeResults?.TryGetValue(specCallId, out var specTask) == true
                && specTask.IsCompletedSuccessfully)
            {
                // Flush any accumulated parallel batch first
                if (parallelBatch.Count > 0)
                {
                    await ExecuteParallelBatch(parallelBatch, allResults, channel, cancellationToken).ConfigureAwait(false);
                    parallelBatch.Clear();
                }

                var specResult = await specTask.ConfigureAwait(false);
                _log?.Invoke("SPECULATIVE", $"Using pre-computed result for {name}");
                await channel.WriteAsync(new AgentStreamEvent.ToolCallStarted(name, FormatArguments(call)), cancellationToken).ConfigureAwait(false);
                await channel.WriteAsync(new AgentStreamEvent.ToolCallCompleted(name,
                    Truncate(specResult.Result.Result?.ToString() ?? "", 200)), cancellationToken).ConfigureAwait(false);
                allResults[i] = specResult;
                continue;
            }

            bool isConcurrencySafe = _attributeCache.IsConcurrencySafe(name)
                                     && !_options.DangerousToolNames.Contains(name);

            if (isConcurrencySafe)
            {
                parallelBatch.Add((i, call));
            }
            else
            {
                // Flush any accumulated parallel batch first
                if (parallelBatch.Count > 0)
                {
                    await ExecuteParallelBatch(parallelBatch, allResults, channel, cancellationToken).ConfigureAwait(false);
                    parallelBatch.Clear();
                }
                // Execute serial call
                allResults[i] = await ExecuteSingleAsync(call, channel, cancellationToken).ConfigureAwait(false);
            }
        }

        // Flush trailing parallel batch
        if (parallelBatch.Count > 0)
            await ExecuteParallelBatch(parallelBatch, allResults, channel, cancellationToken).ConfigureAwait(false);

        return [.. allResults];
    }

    /// <summary>Result of a single tool call execution.</summary>
    public sealed record ToolCallResult(
        FunctionCallContent Call,
        FunctionResultContent Result,
        bool IsError = false);

    // ── Speculative (early) tool execution ──

    /// <summary>
    /// Check if a tool can be speculatively executed during the LLM stream.
    /// Only read-only or concurrency-safe tools that aren't dangerous qualify.
    /// </summary>
    public bool CanExecuteSpeculatively(string toolName)
    {
        if (string.IsNullOrEmpty(toolName)) return false;
        var meta = _attributeCache.Get(toolName);
        return (meta.IsReadOnly || meta.IsConcurrencySafe)
            && !_options.DangerousToolNames.Contains(toolName);
    }

    /// <summary>
    /// Fire-and-forget speculative execution of a single tool during the LLM stream.
    /// No hooks or approval — only safe tools qualify (vetted by CanExecuteSpeculatively).
    /// Returns a task that resolves to the ToolCallResult.
    /// </summary>
    public Task<ToolCallResult> ExecuteSingleSpeculativeAsync(
        FunctionCallContent call,
        ChannelWriter<AgentStreamEvent> channel,
        CancellationToken ct)
    {
        return Task.Run(async () =>
        {
            var toolName = call.Name ?? "unknown";
            var (result, isError) = await InvokeToolAsync(call, ct).ConfigureAwait(false);
            Interlocked.Increment(ref _totalToolCalls);
            var capped = CapResult(toolName, result);
            return new ToolCallResult(call, CreateResult(call, capped), isError);
        }, ct);
    }

    // ── Parallel batch execution ──

    private async Task ExecuteParallelBatch(
        List<(int Index, FunctionCallContent Call)> batch,
        ToolCallResult[] results,
        ChannelWriter<AgentStreamEvent> channel,
        CancellationToken ct)
    {
        if (batch.Count == 1)
        {
            // Single item — no parallelism needed
            results[batch[0].Index] = await ExecuteSingleAsync(batch[0].Call, channel, ct).ConfigureAwait(false);
            return;
        }

        _log?.Invoke("PARALLEL", $"Executing {batch.Count} concurrent-safe tools in parallel");

        using var semaphore = new SemaphoreSlim(MaxParallelTools);

        // Emit all "started" events immediately so the UI shows them as in-flight
        foreach (var (_, call) in batch)
        {
            var name = call.Name ?? "unknown";
            await channel.WriteAsync(
                new AgentStreamEvent.ToolCallStarted(name, FormatArguments(call)), ct).ConfigureAwait(false);
        }

        // Execute all in parallel with semaphore throttling
        // Each task runs permission checks, pre/post hooks, and invocation (same as serial path
        // but without the serial-only event emission for "started" — those were emitted above).
        var tasks = batch.Select(async item =>
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var call = item.Call;
                var toolName = call.Name ?? "unknown";

                // Pre-tool hooks (run BEFORE permission check)
                bool hookGrantedPermission = false;
                if (_options.Hooks is { } hooks)
                {
                    var hookCtx = new ToolHookContext
                    {
                        ToolName = toolName,
                        Arguments = call.Arguments,
                        TurnNumber = TurnNumber,
                        TotalToolCalls = _totalToolCalls,
                    };
                    var hookResult = await hooks.RunPreToolHooksAsync(hookCtx, ct).ConfigureAwait(false);
                    if (hookResult.Outcome == HookOutcome.Block)
                    {
                        var msg = hookResult.Message ?? $"Tool '{toolName}' blocked by hook";
                        return (item.Index, (msg, true));
                    }
                    if (hookResult.Outcome == HookOutcome.Allow)
                        hookGrantedPermission = true;
                }

                // Permission check (skip Ask if hook granted, but still enforce Deny)
                if (!hookGrantedPermission)
                {
                    var permResult = await CheckPermissionAsync(call, toolName, FormatArguments(call), channel, ct).ConfigureAwait(false);
                    if (permResult is not null)
                        return (item.Index, (permResult.Result.Result?.ToString() ?? "denied", true));
                }
                else if (_options.PermissionEngine is { } engine)
                {
                    var decision = engine.Evaluate(toolName, call.Arguments);
                    if (decision.Effect == PermissionEffect.Deny)
                    {
                        var reason = decision.MatchedRule?.Description ?? "denied by permission rule";
                        return (item.Index, ($"Tool '{toolName}' blocked: {reason}", true));
                    }
                }

                // Invoke tool
                var result = await InvokeToolAsync(call, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _totalToolCalls);

                // Post-tool hooks: failure hooks for errors, success hooks for success
                if (result.IsError && _options.Hooks is { } failHooks)
                {
                    var hookCtx = new ToolHookContext
                    {
                        ToolName = toolName,
                        Arguments = call.Arguments,
                        TurnNumber = TurnNumber,
                        TotalToolCalls = _totalToolCalls,
                    };
                    var failureAction = await failHooks.RunPostToolFailureHooksAsync(
                        hookCtx, result.Result, wasInterrupted: false, ct).ConfigureAwait(false);
                    if (failureAction is not null)
                    {
                        var modifiedResult = result.Result;
                        if (failureAction.SuppressDetailedError)
                            modifiedResult = $"Tool '{toolName}' failed. Details suppressed by hook.";
                        if (failureAction.RetryHint is { } hint)
                            modifiedResult += $"\n[HINT] {hint}";
                        return (item.Index, (modifiedResult, true));
                    }
                }
                else if (!result.IsError && _options.Hooks is { } postHooks)
                {
                    var hookCtx = new ToolHookContext
                    {
                        ToolName = toolName,
                        Arguments = call.Arguments,
                        TurnNumber = TurnNumber,
                        TotalToolCalls = _totalToolCalls,
                    };
                    await postHooks.RunPostToolHooksAsync(hookCtx, result.Result, result.IsError, ct).ConfigureAwait(false);
                }

                return (item.Index, result);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        var completedTasks = await Task.WhenAll(tasks).ConfigureAwait(false);

        // Write results in original order and emit completed events
        foreach (var (index, (resultStr, isError)) in completedTasks)
        {
            var call = batch.First(b => b.Index == index).Call;
            var toolName = call.Name ?? "unknown";

            var cappedResult = CapResult(toolName, resultStr);
            await channel.WriteAsync(
                new AgentStreamEvent.ToolCallCompleted(toolName, Truncate(cappedResult, 200)), ct).ConfigureAwait(false);

            _log?.Invoke("TOOL", $"{(isError ? "FAIL" : "OK")}: {toolName} → {Truncate(cappedResult, 120)}");
            results[index] = new ToolCallResult(call, CreateResult(call, cappedResult), isError);
        }
    }

    // ── Single (serial) tool execution ──

    private async Task<ToolCallResult> ExecuteSingleAsync(
        FunctionCallContent call,
        ChannelWriter<AgentStreamEvent> channel,
        CancellationToken cancellationToken)
    {
        var toolName = call.Name ?? "unknown";
        var argsStr = FormatArguments(call);

        // 1. Resolve tool
        var tool = ResolveFunction(toolName);
        if (tool is null)
        {
            var errorResult = $"Tool '{toolName}' not found. Use list_tool_categories() to see available tools, or request_tools(category) to load more.";
            _log?.Invoke("TOOL", $"NOT FOUND: {toolName}");
            await EmitCompleted(channel, toolName, argsStr, errorResult, isError: true).ConfigureAwait(false);
            return new ToolCallResult(call, CreateResult(call, errorResult), IsError: true);
        }

        // 2. Pre-tool hooks (run BEFORE permission check — hooks can grant permission via Allow)
        bool hookGrantedPermission = false;
        if (_options.Hooks is { } hooks)
        {
            var hookCtx = new ToolHookContext
            {
                ToolName = toolName,
                Arguments = call.Arguments,
                TurnNumber = TurnNumber,
                TotalToolCalls = _totalToolCalls,
            };
            var hookResult = await hooks.RunPreToolHooksAsync(hookCtx, cancellationToken).ConfigureAwait(false);
            if (hookResult.Outcome == HookOutcome.Block)
            {
                var blockedMsg = hookResult.Message ?? $"Tool '{toolName}' blocked by hook";
                _log?.Invoke("HOOK", $"BLOCKED: {toolName} — {blockedMsg}");
                await EmitCompleted(channel, toolName, argsStr, blockedMsg, isError: true).ConfigureAwait(false);
                return new ToolCallResult(call, CreateResult(call, blockedMsg), IsError: true);
            }
            if (hookResult.Outcome == HookOutcome.Allow)
                hookGrantedPermission = true;
        }

        // 3. Permission check (skip Ask if hook granted, but still enforce Deny rules)
        if (!hookGrantedPermission)
        {
            var permissionResult = await CheckPermissionAsync(call, toolName, argsStr, channel, cancellationToken).ConfigureAwait(false);
            if (permissionResult is not null)
                return permissionResult; // Denied or error
        }
        else if (_options.PermissionEngine is { } engine)
        {
            // Hook granted, but still enforce hard denies
            var decision = engine.Evaluate(toolName, call.Arguments);
            if (decision.Effect == PermissionEffect.Deny)
            {
                var reason = decision.MatchedRule?.Description ?? "denied by permission rule";
                var deniedResult = $"Tool '{toolName}' blocked: {reason}";
                _log?.Invoke("PERMISSION", $"DENIED (override hook allow): {toolName} — {reason}");
                await EmitCompleted(channel, toolName, argsStr, deniedResult, isError: true).ConfigureAwait(false);
                return new ToolCallResult(call, CreateResult(call, deniedResult), IsError: true);
            }
        }

        // 4. Emit started
        await channel.WriteAsync(new AgentStreamEvent.ToolCallStarted(toolName, argsStr), cancellationToken).ConfigureAwait(false);

        // 5. Execute
        var (resultStr, isError) = await InvokeToolAsync(call, cancellationToken).ConfigureAwait(false);

        // 6. Track tool call count
        Interlocked.Increment(ref _totalToolCalls);

        // 7. Post-tool hooks: failure hooks for errors, success hooks for success
        if (isError && _options.Hooks is { } failureHooks)
        {
            var hookCtx = new ToolHookContext
            {
                ToolName = toolName,
                Arguments = call.Arguments,
                TurnNumber = TurnNumber,
                TotalToolCalls = _totalToolCalls,
            };
            var failureAction = await failureHooks.RunPostToolFailureHooksAsync(
                hookCtx, resultStr, wasInterrupted: false, cancellationToken).ConfigureAwait(false);

            if (failureAction?.SuppressDetailedError == true)
                resultStr = $"Tool '{toolName}' failed. Details suppressed by hook.";
            if (failureAction?.RetryHint is { } hint)
                resultStr += $"\n[HINT] {hint}";
        }
        else if (!isError && _options.Hooks is { } postHooks)
        {
            var hookCtx = new ToolHookContext
            {
                ToolName = toolName,
                Arguments = call.Arguments,
                TurnNumber = TurnNumber,
                TotalToolCalls = _totalToolCalls,
            };
            await postHooks.RunPostToolHooksAsync(hookCtx, resultStr, isError, cancellationToken).ConfigureAwait(false);
        }

        // 8. Cap result if oversized
        resultStr = CapResult(toolName, resultStr);

        // 9. Emit completed
        await channel.WriteAsync(
            new AgentStreamEvent.ToolCallCompleted(toolName, Truncate(resultStr, 200)),
            cancellationToken).ConfigureAwait(false);

        _log?.Invoke("TOOL", $"{(isError ? "FAIL" : "OK")}: {toolName} → {Truncate(resultStr, 120)}");
        return new ToolCallResult(call, CreateResult(call, resultStr), isError);
    }

    /// <summary>
    /// Invoke a tool function and return the result string + error flag.
    /// Shared between serial and parallel paths.
    ///
    /// Respects <see cref="ToolInterruptMode"/>:
    /// - <c>MustComplete</c>: tool runs with CancellationToken.None (cannot be interrupted)
    /// - <c>RequiresCleanup</c>: tool gets a grace period CTS (10s) after abort
    /// - <c>Safe</c> (default): tool gets the caller's cancellation token
    /// </summary>
    private async Task<(string Result, bool IsError)> InvokeToolAsync(
        FunctionCallContent call,
        CancellationToken ct)
    {
        var toolName = call.Name ?? "unknown";
        var tool = ResolveFunction(toolName);
        if (tool is null)
            return ($"Tool '{toolName}' not found.", true);

        var meta = _attributeCache.Get(toolName);
        var effectiveMode = meta.EffectiveInterruptMode;

        // Determine which cancellation token to pass the tool
        CancellationToken toolCt;
        CancellationTokenSource? graceCts = null;
        CancellationTokenRegistration graceRegistration = default;

        switch (effectiveMode)
        {
            case ToolInterruptMode.MustComplete:
                // Tool must not be interrupted — give it CancellationToken.None
                toolCt = CancellationToken.None;
                break;

            case ToolInterruptMode.RequiresCleanup:
                // Give the tool a grace period if the parent is cancelled
                graceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                // On parent cancellation, give 10s more to finish cleanup
                var localGraceCts = graceCts; // capture for closure
                graceRegistration = ct.Register(() =>
                {
                    _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(t =>
                    {
                        if (t.IsFaulted) return; // Timer cancelled — ignore
                        try { localGraceCts.Cancel(); } catch (ObjectDisposedException) { }
                    }, TaskScheduler.Default);
                });
                toolCt = graceCts.Token;
                break;

            default:
                toolCt = ct;
                break;
        }

        try
        {
            var args = call.Arguments is not null
                ? new AIFunctionArguments(call.Arguments)
                : new AIFunctionArguments();
            var rawResult = await tool.InvokeAsync(args, toolCt).ConfigureAwait(false);
            return (rawResult?.ToString() ?? "(no output)", false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ("Tool execution cancelled by user.", true);
        }
        catch (Exception ex)
        {
            _log?.Invoke("TOOL", $"ERROR: {toolName} → {ex.GetType().Name}: {ex.Message}");
            return ($"Tool error: {ex.GetType().Name}: {ex.Message}", true);
        }
        finally
        {
            graceRegistration.Dispose();
            graceCts?.Dispose();
        }
    }

    // ── Permission check ──

    /// <summary>
    /// Evaluate permissions for a tool call. Returns null if the call is allowed,
    /// or a ToolCallResult (denied/error) if the call should not proceed.
    /// </summary>
    private async Task<ToolCallResult?> CheckPermissionAsync(
        FunctionCallContent call,
        string toolName,
        string argsStr,
        ChannelWriter<AgentStreamEvent> channel,
        CancellationToken ct)
    {
        // If we have a permission engine, use it first
        if (_options.PermissionEngine is { } engine)
        {
            var decision = engine.Evaluate(toolName, call.Arguments);

            switch (decision.Effect)
            {
                case PermissionEffect.Allow:
                    return null; // Proceed without approval

                case PermissionEffect.Deny:
                    var reason = decision.MatchedRule?.Description ?? "denied by permission rule";
                    var deniedResult = $"Tool '{toolName}' blocked: {reason}";
                    _log?.Invoke("PERMISSION", $"DENIED: {toolName}({argsStr}) — {reason}");
                    await EmitCompleted(channel, toolName, argsStr, deniedResult, isError: true).ConfigureAwait(false);
                    return new ToolCallResult(call, CreateResult(call, deniedResult), IsError: true);

                case PermissionEffect.Ask:
                    // Fall through to approval prompt below
                    break;
            }
        }
        else if (!_options.DangerousToolNames.Contains(toolName))
        {
            // No permission engine and not dangerous → allow
            return null;
        }

        // Tool requires user approval (either from permission engine Ask or dangerous tools fallback)
        var approved = await RequestApproval(channel, toolName, argsStr, ct).ConfigureAwait(false);
        if (!approved)
        {
            var deniedMsg = $"Tool '{toolName}' execution denied by user.";
            _log?.Invoke("APPROVAL", $"DENIED: {toolName}({argsStr})");
            await EmitCompleted(channel, toolName, argsStr, deniedMsg, isError: true).ConfigureAwait(false);
            return new ToolCallResult(call, CreateResult(call, deniedMsg), IsError: true);
        }
        _log?.Invoke("APPROVAL", $"APPROVED: {toolName}({argsStr})");
        return null; // Proceed
    }

    // ── Private helpers ──

    private AIFunction? ResolveFunction(string name)
    {
        // Snapshot to avoid races with progressive tool loading on another thread
        var tools = _options.Tools.ToList();
        foreach (var tool in tools)
        {
            if (tool is AIFunction fn && string.Equals(fn.Name, name, StringComparison.OrdinalIgnoreCase))
                return fn;
        }
        return null;
    }

    private async Task<bool> RequestApproval(
        ChannelWriter<AgentStreamEvent> channel,
        string toolName,
        string argsStr,
        CancellationToken ct)
    {
        var approval = new AgentStreamEvent.ApprovalRequested(toolName, argsStr);
        await channel.WriteAsync(approval, ct).ConfigureAwait(false);

        // Wait for user decision with 5-minute timeout
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

        try
        {
            return await approval.UserDecision.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log?.Invoke("APPROVAL", $"TIMEOUT: {toolName} — auto-denying after 5 minutes");
            return false; // Timeout → deny
        }
    }

    private string CapResult(string toolName, string result)
    {
        var meta = _attributeCache.Get(toolName);
        var maxChars = meta.MaxResultSize ?? _options.Limits.MaxToolResultChars;
        if (result.Length <= maxChars)
            return result;

        var handle = _options.ToolResultStore.Store(toolName, result);
        var previewLen = Math.Max(maxChars / 2, 500);
        var preview = result[..Math.Min(previewLen, result.Length)];

        return $"{preview}\n\n" +
               $"--- RESULT SPILLED (too large for context) ---\n" +
               $"result_id: {handle}\n" +
               $"total_chars: {result.Length:#,0}\n" +
               $"shown_chars: {preview.Length:#,0}\n" +
               $"Use RetrieveToolResult(resultId: \"{handle}\", offset: {preview.Length}) to read more.";
    }

    private static FunctionResultContent CreateResult(FunctionCallContent call, string result)
        => new(call.CallId ?? Guid.NewGuid().ToString("N"), result);

    private static async Task EmitCompleted(
        ChannelWriter<AgentStreamEvent> channel,
        string toolName,
        string args,
        string result,
        bool isError)
    {
        await channel.WriteAsync(new AgentStreamEvent.ToolCallStarted(toolName, args)).ConfigureAwait(false);
        await channel.WriteAsync(new AgentStreamEvent.ToolCallCompleted(toolName,
            isError ? $"[ERROR] {Truncate(result, 150)}" : Truncate(result, 200))).ConfigureAwait(false);
    }

    private static string FormatArguments(FunctionCallContent call)
    {
        if (call.Arguments is null or { Count: 0 })
            return "";
        return string.Join(", ", call.Arguments.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "...");
}
