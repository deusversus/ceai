using Microsoft.Extensions.AI;

namespace CEAISuite.Application.AgentLoop;

/// <summary>
/// Identifies the AI provider backend. Used to gate provider-specific features
/// (e.g., Anthropic cache_control, context_management) so the agent loop
/// remains model-agnostic by default.
/// </summary>
public enum ProviderKind
{
    /// <summary>Unknown/generic provider — no provider-specific features enabled.</summary>
    Unknown,
    /// <summary>OpenAI (GPT-4, GPT-5, o3, etc.)</summary>
    OpenAI,
    /// <summary>Anthropic (Claude Sonnet, Opus, Haiku).</summary>
    Anthropic,
    /// <summary>GitHub Copilot (proxied models from multiple providers).</summary>
    Copilot,
    /// <summary>OpenAI-compatible endpoint (Ollama, LM Studio, Azure, etc.).</summary>
    OpenAICompatible,
}

/// <summary>
/// Configuration for a single agent loop invocation.
/// Constructed by AiOperatorService and passed to AgentLoop.
/// </summary>
public sealed class AgentLoopOptions
{
    /// <summary>System prompt (instructions) sent with every LLM call.</summary>
    public required string SystemPrompt { get; init; }

    /// <summary>
    /// Live reference to the mutable tools list managed by AiOperatorService.
    /// Progressive loading (request_tools) modifies this list between turns.
    /// </summary>
    public required IList<AITool> Tools { get; init; }

    /// <summary>The AI provider backend. Gates provider-specific features.</summary>
    public ProviderKind Provider { get; init; } = ProviderKind.Unknown;

    /// <summary>LLM sampling temperature.</summary>
    public float Temperature { get; init; } = 0.3f;

    /// <summary>Token limits and compaction thresholds.</summary>
    public required TokenLimits Limits { get; init; }

    /// <summary>Store for spilling oversized tool results.</summary>
    public required ToolResultStore ToolResultStore { get; init; }

    /// <summary>Tool names that require user approval before execution.</summary>
    public required IReadOnlySet<string> DangerousToolNames { get; init; }

    /// <summary>Maximum LLM round-trips before the loop stops.</summary>
    public int MaxTurns { get; init; } = 25;

    /// <summary>
    /// Additional properties merged into ChatOptions (e.g., Anthropic cache_control).
    /// </summary>
    public AdditionalPropertiesDictionary? AdditionalProperties { get; init; }

    /// <summary>
    /// Callback invoked for log messages. (tool, level, message) format.
    /// </summary>
    public Action<string, string>? Log { get; init; }

    /// <summary>
    /// Optional permission engine for fine-grained tool access control.
    /// If null, falls back to DangerousToolNames for approval decisions.
    /// </summary>
    public PermissionEngine? PermissionEngine { get; init; }

    /// <summary>
    /// Optional hook registry for pre/post tool and pre-LLM lifecycle hooks.
    /// </summary>
    public HookRegistry? Hooks { get; init; }

    /// <summary>
    /// Optional skill system for domain-specific instruction loading.
    /// Active skills inject instructions into the system prompt dynamically.
    /// </summary>
    public SkillSystem? Skills { get; init; }

    /// <summary>
    /// Optional token budget tracker. Records usage after each LLM call
    /// and can halt the loop when budget is exhausted.
    /// </summary>
    public TokenBudget? Budget { get; init; }

    /// <summary>
    /// Optional persistent memory system. Relevant memories are injected
    /// into the system prompt as context.
    /// </summary>
    public MemorySystem? Memory { get; init; }

    /// <summary>
    /// Threshold for "low output" detection — if the LLM produces fewer tokens
    /// than this for consecutive turns, a nudge is injected or the loop stops.
    /// </summary>
    public int LowOutputTokenThreshold { get; init; } = 500;

    /// <summary>
    /// Number of consecutive low-output turns before injecting a nudge message.
    /// </summary>
    public int MaxConsecutiveLowOutputTurns { get; init; } = 3;

    /// <summary>
    /// Idle timeout for streaming LLM responses. If no data arrives within this
    /// period, the stream is aborted and the retry policy may fall back to
    /// a non-streaming request. Default: 90 seconds.
    /// </summary>
    public TimeSpan StreamingIdleTimeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// When true and a streaming timeout occurs, retry the LLM call as a
    /// non-streaming request (GetResponseAsync instead of GetStreamingResponseAsync).
    /// Default: false.
    /// </summary>
    public bool AllowNonStreamingFallback { get; init; }

    /// <summary>
    /// Callback invoked when an auth error (401/403) is encountered on the first
    /// retry attempt. Use to refresh OAuth tokens or clear cached credentials.
    /// </summary>
    public Func<Task>? AuthRefreshCallback { get; init; }

    /// <summary>
    /// Optional model switcher for runtime model changes and fallback chains.
    /// When the retry policy signals model fallback (3 consecutive overload errors),
    /// the loop calls <c>ModelSwitcher.FallbackToNext()</c>.
    /// </summary>
    public ModelSwitcher? ModelSwitcher { get; init; }

    /// <summary>
    /// Optional list of Anthropic API context management strategies.
    /// When set, these are serialized into <c>AdditionalProperties["context_management"]</c>
    /// for server-side context optimization. See <see cref="ContextManagementStrategy"/>.
    /// </summary>
    public IReadOnlyList<ContextManagementStrategy>? ContextManagementStrategies { get; init; }

    /// <summary>
    /// When true, read-only tools with complete parameters start executing
    /// during the LLM stream, before the full response is received. This
    /// is an optimization that reduces latency for tool-heavy conversations.
    /// Default: false (tools execute after stream completes).
    /// </summary>
    public bool EnableEarlyToolExecution { get; init; }

    /// <summary>
    /// Optional prompt cache optimizer. When set, system prompt sections are
    /// ordered Static→Session→Volatile for maximum prefix cache hits, and
    /// unchanged sections are memoized via SHA256 hashing.
    /// </summary>
    public PromptCacheOptimizer? PromptCacheOptimizer { get; init; }

    /// <summary>
    /// Optional microcompaction engine. When set, old oversized tool results
    /// are pruned after every turn without an LLM call.
    /// </summary>
    public MicroCompaction? MicroCompaction { get; init; }
}
