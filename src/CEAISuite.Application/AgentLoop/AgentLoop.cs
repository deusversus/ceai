using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CEAISuite.Application.AgentLoop;

/// <summary>
/// The core agent loop. Replaces MAF's opaque <c>FunctionInvokingChatClient</c> +
/// <c>AIAgent</c> with an explicit state machine that gives us full control over:
/// - Tool dispatch and parallel execution
/// - Error recovery with retry and fallback
/// - State transitions (compaction retry, token escalation, diminishing returns)
/// - Post-compaction context restoration
///
/// Modeled after Claude Code's <c>queryLoop()</c> in query.ts (lines 241-1729).
///
/// The loop calls <see cref="IChatClient.GetStreamingResponseAsync"/> directly,
/// parses tool_use blocks, dispatches them via <see cref="ToolExecutor"/>,
/// and decides whether to continue, compact, escalate, or stop.
/// </summary>
public sealed class AgentLoop
{
    private readonly IChatClient _chatClient;
    private readonly AgentLoopOptions _options;
    private readonly ToolExecutor _toolExecutor;
    private readonly RetryPolicy _retryPolicy;
    private readonly CompactionPipeline _compactionPipeline;
    private readonly Action<string, string>? _log;
    private readonly ILogger<AgentLoop>? _logger;

    /// <summary>The options this loop was created with (exposed for SubagentManager/PlanExecutor).</summary>
    public AgentLoopOptions Options => _options;

    public AgentLoop(IChatClient chatClient, AgentLoopOptions options, ToolAttributeCache? attributeCache = null, ILogger<AgentLoop>? logger = null)
    {
        _chatClient = chatClient;
        _options = options;
        _toolExecutor = new ToolExecutor(options, attributeCache ?? new ToolAttributeCache());
        _retryPolicy = new RetryPolicy(log: options.Log, authRefreshCallback: options.AuthRefreshCallback);
        _compactionPipeline = new CompactionPipeline(chatClient, options.Limits, options.Log);
        _log = options.Log;
        _logger = logger;
    }

    /// <summary>
    /// Run the agent loop for a single user message. Returns a channel of streaming events
    /// that the UI consumes via <c>await foreach</c>.
    /// </summary>
    public ChannelReader<AgentStreamEvent> RunStreamingAsync(
        string userMessage,
        ChatHistoryManager history,
        Func<string>? contextProvider = null,
        IReadOnlySet<string>? activeCategories = null,
        CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<AgentStreamEvent>(
            new UnboundedChannelOptions { SingleReader = true });

        _ = Task.Run(async () =>
        {
            try
            {
                await RunLoopAsync(userMessage, history, channel.Writer,
                    contextProvider, activeCategories, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await channel.Writer.WriteAsync(
                    new AgentStreamEvent.Error("Stopped by user.")).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.Invoke("LOOP", $"Unhandled error: {ex}");
                await channel.Writer.WriteAsync(
                    new AgentStreamEvent.Error($"Agent error: {ex.Message}")).ConfigureAwait(false);
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, cancellationToken);

        return channel.Reader;
    }

    /// <summary>
    /// Run the agent loop on existing history WITHOUT adding a new user message.
    /// Used when the caller has already injected a mixed-content message (e.g., text + image).
    /// </summary>
    public ChannelReader<AgentStreamEvent> RunStreamingContinueAsync(
        ChatHistoryManager history,
        Func<string>? contextProvider = null,
        IReadOnlySet<string>? activeCategories = null,
        CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<AgentStreamEvent>(
            new UnboundedChannelOptions { SingleReader = true });

        _ = Task.Run(async () =>
        {
            try
            {
                await RunLoopAsync(null, history, channel.Writer,
                    contextProvider, activeCategories, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await channel.Writer.WriteAsync(
                    new AgentStreamEvent.Error("Stopped by user.")).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.Invoke("LOOP", $"Unhandled error: {ex}");
                await channel.Writer.WriteAsync(
                    new AgentStreamEvent.Error($"Agent error: {ex.Message}")).ConfigureAwait(false);
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, cancellationToken);

        return channel.Reader;
    }

    private async Task RunLoopAsync(
        string? userMessage,
        ChatHistoryManager history,
        ChannelWriter<AgentStreamEvent> channel,
        Func<string>? contextProvider,
        IReadOnlySet<string>? activeCategories,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Add user message with dynamic context (skip if null — caller already added)
        if (userMessage is not null)
        {
            string? contextSuffix = null;
            try { contextSuffix = contextProvider?.Invoke(); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Context provider failed"); }
            var fullContext = contextSuffix is not null ? $"[CURRENT STATE]\n{contextSuffix}" : null;
            history.AddUserMessage(userMessage, fullContext);
        }

        var state = new AgentLoopState();

        while (state.Transition == AgentTransition.NextTurn
            || state.Transition == AgentTransition.TokenEscalation)
        {
            if (ct.IsCancellationRequested)
            {
                state = state with
                {
                    Transition = AgentTransition.Aborted,
                    AbortInfo = new AbortReason { Kind = AbortKind.UserCancelled, Message = "User cancelled" }
                };
                break;
            }

            // Check turn budget
            if (state.TurnCount > _options.MaxTurns)
            {
                _log?.Invoke("LOOP", $"Max turns ({_options.MaxTurns}) reached");
                await channel.WriteAsync(
                    new AgentStreamEvent.TextDelta($"\n[Max turns ({_options.MaxTurns}) reached — stopping.]"),
                    ct).ConfigureAwait(false);
                state = state with
                {
                    Transition = AgentTransition.BudgetExhausted,
                    AbortInfo = new AbortReason { Kind = AbortKind.BudgetExhausted, Message = "Token or cost budget exhausted" }
                };
                break;
            }

            // Check cooldown expiry for model switcher (fast-mode fallback recovery)
            _options.ModelSwitcher?.CheckCooldownExpiry();

            _log?.Invoke("LOOP", $"Turn {state.TurnCount} (transition: {state.Transition})");

            // Build ChatOptions for this turn
            var chatOptions = BuildChatOptions(state, history);

            // Pre-LLM hooks
            if (_options.Hooks is { } preLlmHooks)
            {
                var preLlmCtx = new PreLlmContext
                {
                    Messages = history.GetMessages(),
                    Options = chatOptions,
                    TurnNumber = state.TurnCount,
                };
                var preLlmResult = await preLlmHooks.RunPreLlmHooksAsync(preLlmCtx, ct).ConfigureAwait(false);
                if (preLlmResult.Outcome == HookOutcome.Block)
                {
                    _log?.Invoke("HOOK", $"Pre-LLM hook blocked LLM call: {preLlmResult.Message}");
                    await channel.WriteAsync(
                        new AgentStreamEvent.TextDelta($"\n[LLM call blocked by hook: {preLlmResult.Message}]"), ct).ConfigureAwait(false);
                    state = state with { Transition = AgentTransition.Completed };
                    break;
                }
            }

            // Call LLM with retry
            var (assistantText, toolCalls, outputTokens, finishReason, speculativeTasks) =
                await CallLlmWithRetry(history, chatOptions, channel, state, ct).ConfigureAwait(false);

            // Post-LLM hooks
            if (_options.Hooks is { } postLlmHooks)
            {
                await postLlmHooks.RunPostLlmHooksAsync(new PostLlmContext
                {
                    ResponseText = assistantText,
                    TurnNumber = state.TurnCount,
                    ToolCallCount = toolCalls.Count,
                }, ct).ConfigureAwait(false);
            }

            // Handle max output tokens recovery
            if (ErrorClassifier.IsMaxOutputTokens(finishReason) && toolCalls.Count == 0)
            {
                state = HandleMaxOutputTokens(state, channel, history, assistantText);
                if (state.Transition == AgentTransition.TokenEscalation)
                    continue;
            }

            // Detect diminishing returns
            state = state with
            {
                LastTurnOutputTokens = outputTokens,
                ConsecutiveLowOutputTurns = outputTokens < _options.LowOutputTokenThreshold
                    ? state.ConsecutiveLowOutputTurns + 1
                    : 0,
            };

            if (state.ConsecutiveLowOutputTurns >= _options.MaxConsecutiveLowOutputTurns && toolCalls.Count > 0)
            {
                _log?.Invoke("LOOP", $"Diminishing returns: {state.ConsecutiveLowOutputTurns} consecutive low-output turns");
                history.AddSystemMessage(
                    "You've been producing very little output for several turns. " +
                    "Please either complete the task and provide a final response, or explain what's blocking you.");
            }

            // If no tool calls → conversation turn complete (unless stop hook forces continuation)
            if (toolCalls.Count == 0)
            {
                // Run stop hooks — Block means "force continue"
                if (_options.Hooks is { } stopHooks)
                {
                    var stopCtx = new StopHookContext
                    {
                        TurnCount = state.TurnCount,
                        TotalToolCalls = state.TotalToolCalls,
                        LastAssistantText = assistantText,
                    };
                    var stopResult = await stopHooks.RunStopHooksAsync(stopCtx, ct).ConfigureAwait(false);
                    if (stopResult.Outcome == HookOutcome.Block)
                    {
                        _log?.Invoke("HOOK", $"Stop hook forced continuation: {stopResult.Message}");
                        history.AddSystemMessage(
                            stopResult.Message ?? "A hook has determined the task is not yet complete. Continue working.");
                        state = state with
                        {
                            TurnCount = state.TurnCount + 1,
                            Transition = AgentTransition.NextTurn,
                        };
                        continue;
                    }
                }

                state = state with { Transition = AgentTransition.Completed };
                break;
            }

            // Add assistant message with tool calls to history
            var assistantContents = new List<AIContent>();
            if (!string.IsNullOrEmpty(assistantText))
                assistantContents.Add(new TextContent(assistantText));
            foreach (var tc in toolCalls)
                assistantContents.Add(tc);
            history.AddAssistantMessage(new ChatMessage(ChatRole.Assistant, assistantContents));

            // Execute tools (graceful abort: synthetic results for cancelled tools)
            _toolExecutor.TurnNumber = state.TurnCount;
            List<ToolExecutor.ToolCallResult> results;
            try
            {
                results = await _toolExecutor.ExecuteAsync(toolCalls, channel, ct, speculativeTasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Generate synthetic error results for all tool calls so the
                // LLM's history stays consistent (every FunctionCallContent needs
                // a matching FunctionResultContent).
                _log?.Invoke("LOOP", $"Abort: generating synthetic results for {toolCalls.Count} tool calls");
                results = GenerateSyntheticResults(toolCalls);
            }

            // Add tool results to history
            history.AddToolResults(results.Select(r => r.Result));

            // Emit tool use summary
            if (results.Count > 0)
            {
                var byTool = results.GroupBy(r => r.Call.Name ?? "unknown")
                    .ToDictionary(g => g.Key, g => g.Count());
                await channel.WriteAsync(new AgentStreamEvent.ToolUseSummary(results.Count, byTool), ct).ConfigureAwait(false);
            }

            state = state with
            {
                TotalToolCalls = state.TotalToolCalls + results.Count,
                TurnCount = state.TurnCount + 1,
                Transition = AgentTransition.NextTurn,
            };

            // Check token budget
            if (_options.Budget is { IsExhausted: true })
            {
                _log?.Invoke("LOOP", "Token budget exhausted — stopping");
                state = state with
                {
                    Transition = AgentTransition.BudgetExhausted,
                    TransitionReason = "token_budget_exhausted",
                    AbortInfo = new AbortReason { Kind = AbortKind.BudgetExhausted, Message = "Token or cost budget exhausted" }
                };
                history.AddSystemMessage(
                    "Token budget has been exhausted. Provide a final summary of work completed so far.");
                break;
            }

            // Microcompaction: prune oversized old tool results (no API call)
            if (_options.MicroCompaction is { } mc)
                mc.Prune(history);

            // Check if compaction is needed (exponential backoff on failure)
            bool backoffActive = state.CompactionSkipUntilTurn > state.TurnCount;
            bool cooldownExpired = state.TurnCount - state.LastCompactionTurn >= 2; // Don't compact on consecutive turns
            if (_compactionPipeline.ShouldCompact(history) && !backoffActive && cooldownExpired)
            {
                _log?.Invoke("LOOP", "Compaction triggered");

                // Pre-compaction hooks — can block compaction
                var msgCountBefore = history.GetMessages().Count;
                var estimatedTokensBefore = history.EstimateTokens();
                if (_options.Hooks is { } preCompHooks)
                {
                    var preCompResult = await preCompHooks.RunPreCompactionHooksAsync(new CompactionHookContext
                    {
                        MessageCountBefore = msgCountBefore,
                        MessageCountAfter = 0,
                        EstimatedTokensBefore = estimatedTokensBefore,
                        EstimatedTokensAfter = 0,
                    }, ct).ConfigureAwait(false);
                    if (preCompResult.Outcome == HookOutcome.Block)
                    {
                        _log?.Invoke("HOOK", $"Pre-compaction hook blocked: {preCompResult.Message}");
                        continue;
                    }
                }

                var snapshot = PostCompactionRestorer.CaptureSnapshot(
                    history, activeCategories ?? new HashSet<string>(), contextProvider);

                var compactionResult = await _compactionPipeline.CompactAsync(history, ct).ConfigureAwait(false);
                if (compactionResult.Success)
                {
                    PostCompactionRestorer.Restore(history, snapshot);
                    state = state with
                    {
                        ConsecutiveCompactionFailures = 0,
                        LastCompactionTurn = state.TurnCount,
                    };

                    // Post-restoration safety check: if still over threshold, run microcompaction
                    if (_options.MicroCompaction is { } mc2 && _compactionPipeline.ShouldCompact(history))
                    {
                        _log?.Invoke("COMPACT", "Post-restoration still over threshold — running microcompaction");
                        mc2.Prune(history);
                    }

                    // Notify UI that old messages were compacted away
                    await channel.WriteAsync(new AgentStreamEvent.Tombstone("compacted"), ct).ConfigureAwait(false);
                    await channel.WriteAsync(new AgentStreamEvent.ContentReplace(
                        "compacted", "[Context compacted — earlier messages summarized]"), ct).ConfigureAwait(false);

                    // Post-compaction hooks
                    if (_options.Hooks is { } postCompHooks)
                    {
                        await postCompHooks.RunPostCompactionHooksAsync(new CompactionHookContext
                        {
                            MessageCountBefore = msgCountBefore,
                            MessageCountAfter = history.GetMessages().Count,
                            EstimatedTokensBefore = estimatedTokensBefore,
                            EstimatedTokensAfter = history.EstimateTokens(),
                        }, ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    var failures = state.ConsecutiveCompactionFailures + 1;
                    // Exponential backoff: skip 5, 10, 20 turns (capped)
                    var skipTurns = Math.Min(
                        5 * (1 << Math.Min(failures - 1, 4)),
                        AgentLoopState.MaxCompactionBackoffTurns);
                    state = state with
                    {
                        ConsecutiveCompactionFailures = failures,
                        LastCompactionTurn = state.TurnCount,
                        CompactionSkipUntilTurn = state.TurnCount + skipTurns,
                    };
                    _log?.Invoke("COMPACT", $"Compaction failed ({failures} consecutive) — backing off for {skipTurns} turns (retry at turn {state.CompactionSkipUntilTurn})");
                }
            }
        }

        // Emit completion
        await channel.WriteAsync(
            new AgentStreamEvent.Completed(state.TotalToolCalls, sw.Elapsed), ct).ConfigureAwait(false);
    }

    private async Task<(string assistantText, List<FunctionCallContent> toolCalls, int outputTokens, string? finishReason, Dictionary<string, Task<ToolExecutor.ToolCallResult>>? speculativeTasks)>
        CallLlmWithRetry(
            ChatHistoryManager history,
            ChatOptions chatOptions,
            ChannelWriter<AgentStreamEvent> channel,
            AgentLoopState state,
            CancellationToken ct)
    {
        var retryResult = await _retryPolicy.ExecuteAsync(async retryCt =>
        {
            var assistantText = "";
            var toolCalls = new List<FunctionCallContent>();
            int outputTokens = 0;
            string? finishReason = null;

            // Speculative early tool execution: start read-only tools during streaming
            var speculativeTasks = _options.EnableEarlyToolExecution
                ? new Dictionary<string, Task<ToolExecutor.ToolCallResult>>()
                : null;

            var rawStream = _chatClient.GetStreamingResponseAsync(
                history.GetMessages(), chatOptions, retryCt);
            var watchedStream = StreamingWatchdog.WithIdleTimeout(
                rawStream, _options.StreamingIdleTimeout, _log, retryCt);

            int chunkCount = 0;
            await foreach (var update in watchedStream.ConfigureAwait(false))
            {
                chunkCount++;
                // Process streaming chunks
                foreach (var content in update.Contents)
                {
                    if (content is TextContent tc && !string.IsNullOrEmpty(tc.Text))
                    {
                        assistantText += tc.Text;
                        await channel.WriteAsync(new AgentStreamEvent.TextDelta(tc.Text), retryCt).ConfigureAwait(false);
                    }
                    else if (content is FunctionCallContent fc)
                    {
                        toolCalls.Add(fc);

                        // Speculative early execution for read-only tools
                        if (speculativeTasks is not null
                            && _toolExecutor.CanExecuteSpeculatively(fc.Name ?? ""))
                        {
                            var callId = fc.CallId ?? fc.Name ?? "";
                            if (!speculativeTasks.ContainsKey(callId))
                            {
                                _log?.Invoke("SPECULATIVE", $"Starting early execution of {fc.Name}");
                                speculativeTasks[callId] = _toolExecutor.ExecuteSingleSpeculativeAsync(fc, channel, retryCt);
                            }
                        }
                    }
                    else if (content is UsageContent usage)
                    {
                        outputTokens += (int)(usage.Details?.OutputTokenCount ?? 0);

                        // Update actual token count for accurate compaction triggers
                        var inputToks = (int)(usage.Details?.InputTokenCount ?? 0);
                        if (inputToks > 0)
                            history.LastKnownInputTokens = inputToks;

                        // Record usage in budget tracker
                        if (_options.Budget is { } budget)
                        {
                            var outputToks = usage.Details?.OutputTokenCount ?? 0;
                            var cachedToks = usage.Details?.CachedInputTokenCount ?? 0;
                            budget.RecordUsage(inputToks, outputToks, cachedToks);
                        }
                    }
                }

                // Check for finish reason
                if (update.FinishReason is ChatFinishReason fr)
                    finishReason = fr.Value;
            }

            // Detect non-streaming responses: if the provider sent everything in ≤2 chunks
            // the user saw no incremental text. Log it so we can diagnose provider-side issues.
            if (chunkCount <= 2 && assistantText.Length > 0)
                _log?.Invoke("STREAM", $"Response arrived in {chunkCount} chunk(s) ({assistantText.Length} chars) — provider may not support streaming for this model.");

            return (assistantText, toolCalls, outputTokens, finishReason, speculativeTasks);
        },
        onHeartbeat: msg => channel.TryWrite(new AgentStreamEvent.TextDelta($"\n[{msg}]\n")),
        cancellationToken: ct).ConfigureAwait(false);

        if (retryResult.Success)
        {
            var val = retryResult.Value!;
            return (val.assistantText, val.toolCalls, val.outputTokens, val.finishReason, val.speculativeTasks);
        }

        if (retryResult.NeedsCompaction
            && state.ConsecutiveCompactionFailures < AgentLoopState.MaxConsecutiveCompactionFailures)
        {
            _log?.Invoke("LOOP", "Prompt too long — triggering reactive compaction and retry");
            var snapshot = PostCompactionRestorer.CaptureSnapshot(
                history, new HashSet<string>(), null);
            var compactionResult = await _compactionPipeline.CompactAsync(history, ct).ConfigureAwait(false);
            AgentLoopState compactedState;
            if (compactionResult.Success)
            {
                PostCompactionRestorer.Restore(history, snapshot);
                compactedState = state with { ConsecutiveCompactionFailures = 0, LastCompactionTurn = state.TurnCount };
            }
            else
            {
                compactedState = state with { ConsecutiveCompactionFailures = state.ConsecutiveCompactionFailures + 1, LastCompactionTurn = state.TurnCount };
            }
            return await CallLlmWithRetry(history, BuildChatOptions(compactedState), channel,
                compactedState, ct).ConfigureAwait(false);
        }

        if (retryResult.NeedsModelFallback)
        {
            if (_options.ModelSwitcher is { } switcher)
            {
                // Differentiated fallback: cooldown (temporary) vs permanent switch
                if (retryResult.NeedsFastModeCooldown)
                {
                    switcher.TriggerCooldown(RetryPolicy.FastModeCooldownDuration);
                    return await CallLlmWithRetry(history, BuildChatOptions(state), channel, state, ct).ConfigureAwait(false);
                }

                var fallback = switcher.FallbackToNext();
                if (fallback is not null)
                {
                    _log?.Invoke("LOOP", $"Model fallback: switching to {fallback.ModelId}");
                    return await CallLlmWithRetry(history, BuildChatOptions(state), channel, state, ct).ConfigureAwait(false);
                }
                _log?.Invoke("LOOP", "Model fallback exhausted — no more fallback models");
            }
            else
            {
                _log?.Invoke("LOOP", "Model fallback signaled but no ModelSwitcher configured");
            }
        }

        if (retryResult.AdjustedMaxTokens.HasValue)
        {
            _log?.Invoke("LOOP", $"Token overflow — retrying with max_tokens={retryResult.AdjustedMaxTokens}");
            // Carry forward existing state with the adjusted token override
            var adjustedState = state with { MaxOutputTokensOverride = retryResult.AdjustedMaxTokens };
            var adjustedOptions = BuildChatOptions(adjustedState);
            return await CallLlmWithRetry(history, adjustedOptions, channel,
                adjustedState, ct).ConfigureAwait(false);
        }

        // All retries failed
        throw retryResult.Exception ?? new InvalidOperationException("LLM call failed after retries");
    }

    private AgentLoopState HandleMaxOutputTokens(
        AgentLoopState state,
        ChannelWriter<AgentStreamEvent> channel,
        ChatHistoryManager history,
        string partialText)
    {
        const int maxRecoveryAttempts = 3;
        const int escalatedTokens = 16384;

        if (state.MaxOutputTokensRecoveryCount >= maxRecoveryAttempts)
        {
            _log?.Invoke("LOOP", "Max output tokens recovery exhausted — completing with partial text");
            return state with { Transition = AgentTransition.Completed };
        }

        // First hit: escalate tokens
        if (state.MaxOutputTokensOverride is null)
        {
            _log?.Invoke("LOOP", $"Max output tokens hit — escalating to {escalatedTokens}");
            return state with
            {
                MaxOutputTokensOverride = escalatedTokens,
                MaxOutputTokensRecoveryCount = state.MaxOutputTokensRecoveryCount + 1,
                Transition = AgentTransition.TokenEscalation,
                TransitionReason = "max_output_tokens_escalate",
            };
        }

        // Subsequent hits: inject resume message
        _log?.Invoke("LOOP", "Max output tokens hit again — injecting resume message");
        history.AddSystemMessage(
            "Output token limit hit. Resume directly from where you stopped — " +
            "no apology, no recap, no restatement. Continue mid-thought.");

        return state with
        {
            MaxOutputTokensRecoveryCount = state.MaxOutputTokensRecoveryCount + 1,
            Transition = AgentTransition.TokenEscalation,
            TransitionReason = "max_output_tokens_recovery",
        };
    }

    private ChatOptions BuildChatOptions(AgentLoopState state, ChatHistoryManager? historyForMemory = null)
    {
        var maxTokens = state.MaxOutputTokensOverride ?? _options.Limits.MaxOutputTokens;

        // Compose system prompt via PromptCacheOptimizer if available, else direct concatenation.
        // The optimizer orders sections Static→Session→Volatile for maximum prefix cache hits
        // and memoizes unchanged sections to avoid re-serialization.
        string systemPrompt;
        PromptCacheResult? cacheResult = null;
        if (_options.PromptCacheOptimizer is { } optimizer)
        {
            var sections = new List<PromptSection>
            {
                new() { Name = "core", Content = _options.SystemPrompt, CacheScope = PromptCacheScope.Static },
            };

            if (_options.Skills is { } sk)
            {
                var skillText = sk.BuildActiveSkillInstructions();
                if (skillText is not null)
                    sections.Add(new PromptSection { Name = "skills", Content = skillText, CacheScope = PromptCacheScope.Session });
            }

            if (_options.Memory is { } mem)
            {
                // Extract last user message as a relevance hint for memory filtering
                string? queryHint = null;
                if (historyForMemory is not null)
                {
                    var msgs = historyForMemory.GetMessages();
                    for (int i = msgs.Count - 1; i >= 0; i--)
                    {
                        if (msgs[i].Role == ChatRole.User)
                        {
                            queryHint = msgs[i].Text;
                            break;
                        }
                    }
                }
                var memText = mem.BuildMemoryContext(queryHint: queryHint);
                if (memText is not null)
                    sections.Add(new PromptSection { Name = "memory", Content = memText, CacheScope = PromptCacheScope.Session });
            }

            cacheResult = optimizer.Build(sections);
            systemPrompt = cacheResult.FlatText;
        }
        else
        {
            // Fallback: direct concatenation (no caching optimization)
            systemPrompt = _options.SystemPrompt;
            if (_options.Skills is { } skills)
            {
                var skillInstructions = skills.BuildActiveSkillInstructions();
                if (skillInstructions is not null)
                    systemPrompt += skillInstructions;
            }
            if (_options.Memory is { } memory)
            {
                var memoryContext = memory.BuildMemoryContext();
                if (memoryContext is not null)
                    systemPrompt += memoryContext;
            }
        }

        var options = new ChatOptions
        {
            Instructions = systemPrompt,
            Tools = _options.Tools,
            Temperature = _options.Temperature,
            MaxOutputTokens = maxTokens,
        };

        // Merge additional properties — only include provider-specific keys for the active provider
        var additionalProps = new AdditionalPropertiesDictionary();

        if (_options.AdditionalProperties is not null)
        {
            foreach (var kvp in _options.AdditionalProperties)
            {
                // Gate Anthropic-specific keys: cache_control, context_management
                if (kvp.Key is "cache_control" or "context_management"
                    && _options.Provider != ProviderKind.Anthropic)
                    continue;

                additionalProps[kvp.Key] = kvp.Value;
            }
        }

        // Anthropic-only: context management strategies (server-side compaction)
        if (_options.Provider == ProviderKind.Anthropic
            && _options.ContextManagementStrategies is { Count: > 0 } strategies)
        {
            additionalProps["context_management"] = ContextManagementSerializer.Serialize(strategies);
        }

        // Anthropic-only: API-level cache_control headers for server-side prompt prefix caching.
        // Place cache breakpoints at the end of each Static/Session scope boundary.
        // Anthropic supports up to 4 breakpoints per request.
        if (_options.Provider == ProviderKind.Anthropic && cacheResult is { Blocks.Count: > 0 })
        {
            var breakpoints = new List<int>(); // Indices of blocks that get cache_control
            PromptCacheScope? lastScope = null;
            for (int i = 0; i < cacheResult.Blocks.Count; i++)
            {
                var block = cacheResult.Blocks[i];
                // Place breakpoint at the last block of each cacheable scope
                if (lastScope.HasValue && block.Scope != lastScope.Value
                    && lastScope.Value is PromptCacheScope.Static or PromptCacheScope.Session)
                {
                    breakpoints.Add(i - 1);
                }
                lastScope = block.Scope;
            }
            // Also add breakpoint at the end of the last cacheable scope
            if (lastScope is PromptCacheScope.Static or PromptCacheScope.Session)
                breakpoints.Add(cacheResult.Blocks.Count - 1);

            // Limit to 4 breakpoints (Anthropic API max)
            if (breakpoints.Count > 4)
                breakpoints = breakpoints.Take(4).ToList();

            if (breakpoints.Count > 0)
                additionalProps["cache_control_breakpoints"] = breakpoints;
        }

        if (additionalProps.Count > 0)
            options.AdditionalProperties = additionalProps;

        return options;
    }

    /// <summary>
    /// Generate synthetic error results for tool calls that were not completed
    /// due to cancellation. This keeps the chat history consistent — every
    /// <see cref="FunctionCallContent"/> must have a matching <see cref="FunctionResultContent"/>
    /// or the LLM may hallucinate missing results.
    /// </summary>
    private static List<ToolExecutor.ToolCallResult> GenerateSyntheticResults(
        IReadOnlyList<FunctionCallContent> toolCalls)
    {
        return toolCalls.Select(call => new ToolExecutor.ToolCallResult(
            call,
            new FunctionResultContent(
                call.CallId ?? Guid.NewGuid().ToString("N"),
                $"[ABORTED] Tool '{call.Name ?? "unknown"}' was not executed because the operation was cancelled by the user."),
            IsError: true
        )).ToList();
    }
}
