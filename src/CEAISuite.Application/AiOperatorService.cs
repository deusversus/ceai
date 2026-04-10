using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Channels;
using CEAISuite.Application.AgentLoop;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CEAISuite.Application;

public sealed record AiChatMessage(string Role, string Content, DateTimeOffset Timestamp)
{
    /// <summary>Tool calls made by the assistant in this message (null if none).</summary>
    public List<AiToolCallInfo>? ToolCalls { get; init; }
    /// <summary>Tool results returned during this message (null if none).</summary>
    public List<AiToolResultInfo>? ToolResults { get; init; }
    /// <summary>Image data attached to this message (for display purposes). Not serialized to chat store.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<byte[]>? ImageDataList { get; init; }

    /// <summary>
    /// Content suitable for display. Returns a placeholder for tool-only assistant messages
    /// where <see cref="Content"/> is empty but tool calls were executed.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayContent =>
        !string.IsNullOrWhiteSpace(Content) ? Content
        : ToolCalls is { Count: > 0 } ? "(Tool calls executed — see action log for details)"
        : Role == "assistant" ? "(Empty response from model)"
        : Content;
}

public sealed record AiToolCallInfo(string CallId, string Name, string? ArgumentsJson);
public sealed record AiToolResultInfo(string CallId, string Name, string? Result);

public sealed record AiActionLogEntry(
    string ToolName,
    string Arguments,
    string Result,
    DateTimeOffset Timestamp);

/// <summary>Events emitted during streaming AI responses.</summary>
public abstract record AgentStreamEvent
{
    public sealed record TextDelta(string Text) : AgentStreamEvent;
    public sealed record ToolCallStarted(string ToolName, string Arguments) : AgentStreamEvent;
    public sealed record ToolCallCompleted(string ToolName, string Result) : AgentStreamEvent;
    /// <summary>
    /// Emitted when a dangerous tool needs user approval. The UI should present
    /// Allow/Deny buttons and call Resolve(true/false) to continue the agent.
    /// </summary>
    public sealed record ApprovalRequested(string ToolName, string Arguments) : AgentStreamEvent
    {
        private readonly TaskCompletionSource<bool> _tcs = new();
        public Task<bool> UserDecision => _tcs.Task;
        public void Resolve(bool approved) => _tcs.TrySetResult(approved);
    }
    public sealed record Completed(int ToolCallCount, TimeSpan Elapsed) : AgentStreamEvent;
    public sealed record Error(string Message) : AgentStreamEvent;
    public sealed record ToolProgress(string ToolName, double PercentComplete, string? StatusMessage) : AgentStreamEvent;
    public sealed record ToolUseSummary(int TotalCalls, IReadOnlyDictionary<string, int> CallsByTool) : AgentStreamEvent;
    public sealed record Attachment(string ToolName, string ContentType, string Data) : AgentStreamEvent;
    /// <summary>
    /// Emitted when messages are removed from the conversation (e.g., after compaction).
    /// The UI should remove or grey-out the corresponding message bubble.
    /// </summary>
    public sealed record Tombstone(string MessageId) : AgentStreamEvent;
    /// <summary>
    /// Emitted when a message's content is replaced (e.g., compaction summary replaces original).
    /// The UI should update the corresponding message bubble in-place.
    /// </summary>
    public sealed record ContentReplace(string MessageId, string NewContent) : AgentStreamEvent;
}

/// <summary>
/// Names of tools that require user approval before execution.
/// The ToolExecutor checks this set and emits ApprovalRequested events.
/// </summary>
internal static class DangerousTools
{
    public static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "WriteMemory",
        "SetBreakpoint",
        "RemoveBreakpoint",
        "InstallCodeCaveHook",
        "RemoveCodeCaveHook",
        "ToggleScript",
        "ForceDetachAndCleanup",
        "EmergencyRestorePageProtection",
        "ChangeMemoryProtection",
        "AllocateMemory",
        "FreeMemory",
        "RollbackTransaction",
        "RegisterBreakpointLuaCallback",
        "ExecuteAutoAssemblerScript",
        "DisableAutoAssemblerScript",
        "ExecuteLuaScript",
    };
}

/// <summary>
/// Tool categories for progressive loading. Only core tools are loaded initially;
/// the agent requests additional categories on-demand via <c>request_tools</c>.
/// </summary>
internal static class ToolCategories
{
    /// <summary>Tools loaded at session start — always available.</summary>
    public static readonly HashSet<string> Core = new(StringComparer.OrdinalIgnoreCase)
    {
        // Process management
        "ListProcesses", "FindProcess", "AttachProcess", "InspectProcess", "CheckProcessLiveness",
        // Basic memory
        "ReadMemory", "WriteMemory", "ProbeAddress", "BrowseMemory",
        // Basic scanning
        "StartScan", "RefineScan", "GetScanResults",
        // Address table essentials
        "ListAddressTable", "AddToAddressTable", "RemoveFromAddressTable", "RefreshAddressTable",
        "FreezeAddress",
        // Context
        "GetCurrentContext",
        // Spilled-result retrieval (always available)
        "RetrieveToolResult", "ListStoredResults",
        // Meta-tools (always available)
        "request_tools", "list_tool_categories", "unload_tools",
        // Skill meta-tools (always available)
        "load_skill", "list_skills", "unload_skill", "confirm_load_skill", "view_skill_reference",
        // Memory meta-tools (always available when enabled)
        "remember", "recall_memory", "forget_memory",
        // Budget meta-tool
        "get_budget_status",
        // Subagent & plan meta-tools
        "spawn_subagent", "plan_task", "execute_plan",
        // Phase 5 meta-tools
        "switch_model", "schedule_task", "list_tasks", "cancel_task",
        "get_session_info", "search_sessions",
    };

    /// <summary>Category name → tool names. Agent calls request_tools(category) to load these.</summary>
    public static readonly Dictionary<string, string[]> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sessions"] = [
            "SummarizeInvestigation", "SaveSession", "ListSessions", "LoadSession", "SearchChatHistory",
            "DeleteSession" ],
        ["memory_advanced"] = [
            "HexDump", "ListMemoryRegions", "DissectStructure",
            "ChangeMemoryProtection", "AllocateMemory", "FreeMemory", "QueryMemoryProtection" ],
        ["address_table"] = [
            "RenameAddressTableEntry", "SetEntryNotes", "GetAddressTableNode",
            "CreateAddressGroup", "MoveEntryToGroup", "ToggleScript" ],
        ["scanning_advanced"] = [
            "ScanForPointers", "RescanPointerPath", "ValidatePointerPaths",
            "UndoScan", "GroupedScan", "ResumePointerScan", "RescanAllPointerPaths",
            "SavePointerMap", "LoadPointerMap", "ComparePointerMaps" ],
        ["breakpoints"] = [
            "SetBreakpoint", "RemoveBreakpoint", "ListBreakpoints",
            "GetBreakpointHitLog", "GetBreakpointHealth", "GetBreakpointModeCapabilities",
            "ProbeTargetRisk", "EmergencyRestorePageProtection", "ForceDetachAndCleanup",
            "SetConditionalBreakpoint", "TraceFromAddress",
            "RegisterBreakpointLuaCallback", "UnregisterBreakpointLuaCallback" ],
        ["disassembly"] = [
            "Disassemble", "FindWritersToOffset", "FindFunctionBoundaries", "GetCallerGraph",
            "SearchInstructionPattern", "FindByMemoryOperand",
            "GetCallStack", "GetAllThreadStacks", "ResolveSymbol",
            "TraceFieldWriters", "LoadSymbolsForModule", "ResolveAddressToSymbol" ],
        ["hooks"] = [
            "InstallCodeCaveHook", "RemoveCodeCaveHook", "ListCodeCaveHooks",
            "GetCodeCaveHookHits", "DryRunHookInstall" ],
        ["scripts"] = [
            "ListScripts", "ViewScript", "ValidateScript", "ValidateScriptDeep",
            "EditScript", "CreateScriptEntry",
            "GenerateAutoAssemblerScript", "GenerateLuaScript", "GenerateTrainerScript",
            "ExecuteAutoAssemblerScript", "DisableAutoAssemblerScript",
            "ListRegisteredSymbols", "ResolveRegisteredSymbol" ],
        ["snapshots"] = [
            "CaptureSnapshot", "CompareSnapshots", "CompareSnapshotWithLive",
            "ListSnapshots", "DeleteSnapshot" ],
        ["safety"] = [
            "CheckHookConflicts", "CheckAddressSafety", "ListUnsafeAddresses",
            "ClearUnsafeAddress", "SampledWriteTrace" ],
        ["signatures"] = [
            "GenerateSignature", "TestSignatureUniqueness" ],
        ["hotkeys"] = [
            "SetHotkey", "ListHotkeys", "RemoveHotkey" ],
        ["undo"] = [
            "UndoWrite", "RedoWrite", "PatchHistory" ],
        ["transactions"] = [
            "BeginTransaction", "RollbackTransaction", "ListJournalEntries" ],
        ["cheat_tables"] = [
            "LoadCheatTable", "SaveCheatTable" ],
        ["vision"] = [
            "CaptureProcessWindow" ],
        ["lua"] = [
            "ExecuteLuaScript", "ValidateLuaScript", "EvaluateLuaExpression" ],
        ["utility"] = [
            "IdentifyArtifact" ],
    };

    /// <summary>
    /// Categories that are automatically co-loaded when a trigger category is requested.
    /// Breakpoint workflows frequently reference disassembly tools (FindWritersToOffset,
    /// TraceFieldWriters) in rejection/suggestion messages, so load them together.
    /// </summary>
    public static readonly (string Trigger, string CoLoad)[] CoLoadCategories =
    [
        ("breakpoints", "disassembly"),
        ("hooks", "disassembly"),
        ("lua", "scripts"),
    ];
}

[SupportedOSPlatform("windows")]
public sealed class AiOperatorService : IDisposable, IAsyncDisposable
{
    private const int DefaultMaxContextTokens = 200_000;

    private static readonly System.Text.Json.JsonSerializerOptions s_sessionMetaJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
    private IChatClient _baseChatClient;
    private readonly List<AiChatMessage> _displayHistory = new();
    private readonly List<AiActionLogEntry> _actionLog = new();
    private Timer? _autoSaveTimer;
    private Func<string>? _contextProvider;
    private readonly AiToolFunctions? _toolFunctions;
    private readonly AiChatStore _chatStore;
    private readonly ToolResultStore _toolResultStore;
    private readonly List<AITool> _tools;
    private readonly Dictionary<string, AITool> _allToolsByName;
    private readonly HashSet<string> _loadedCategories = new(StringComparer.OrdinalIgnoreCase);

    // ── New AgentLoop (replaces MAF's opaque inner loop) ──
    private AgentLoop.AgentLoop? _agentLoop;
    private ChatHistoryManager? _historyManager;
    private readonly ToolAttributeCache _toolAttributeCache;
    private readonly SkillSystem _skillSystem = new();
    private PermissionEngine? _permissionEngine;

    // ── Phase 4 systems ──
    private readonly McpManager _mcpManager;
    private readonly TokenBudget _tokenBudget;
    private readonly MemorySystem? _memorySystem;
    private SubagentManager? _subagentManager;
    private PlanExecutor? _planExecutor;
    private readonly AppSettings? _appSettings;

    // ── Phase 5 systems ──
    private readonly ModelSwitcher? _modelSwitcher;
    private readonly CeaiTaskScheduler? _taskScheduler;
    private readonly PluginHost? _pluginHost;
    private readonly SessionMetadata _sessionMetadata = new();
    private readonly SessionIndex? _sessionIndex;
    private readonly PromptCacheOptimizer _promptCacheOptimizer;

    // ── Auto-unload idle categories ──
    private readonly Dictionary<string, int> _categoryLastUsedTurn = new(StringComparer.OrdinalIgnoreCase);
    private int _currentTurn;

    // Rate limiting
    private DateTimeOffset? _lastRequestTime;
    private readonly object _rateLimitLock = new();

    /// <summary>Cumulative input tokens sent across all requests in this session.</summary>
    public long TotalPromptTokens => _tokenBudget.TotalInputTokens;
    /// <summary>Cumulative output tokens received across all requests in this session.</summary>
    public long TotalCompletionTokens => _tokenBudget.TotalOutputTokens;
    /// <summary>Cumulative cached input tokens (prompt cache hits) across all requests.</summary>
    public long TotalCachedTokens => _tokenBudget.TotalCachedTokens;
    /// <summary>Total number of API requests made in this session.</summary>
    public int TotalRequests => _tokenBudget.TotalRequests;

    /// <summary>Maximum number of conversation messages (excluding system prompt) to send to the API.</summary>
    public int MaxConversationMessages { get; set; } = 40;

    /// <summary>Minimum seconds between AI requests. 0 = disabled.</summary>
    public int RateLimitSeconds { get; set; }

    /// <summary>If true, queue and wait for cooldown; if false, reject with error.</summary>
    public bool RateLimitWait { get; set; } = true;

    /// <summary>Configurable token-efficiency limits. Swap at runtime via settings.</summary>
    public TokenLimits Limits { get; set; } = TokenLimits.Balanced;

    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CEAISuite", "logs");
    private static readonly string LogPath = Path.Combine(LogDir, $"ai-agent-{DateTime.Now:yyyy-MM-dd}.log");

    /// <summary>Raised when the agent's status changes (tool calls, thinking, errors).</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Raised when the chat list changes (new chat, delete, rename).</summary>
    public event Action? ChatListChanged;

    public IReadOnlyList<AiChatMessage> DisplayHistory => _displayHistory;
    public IReadOnlyList<AiActionLogEntry> ActionLog => _actionLog;
    public bool IsConfigured { get; private set; }

    /// <summary>Number of messages in the agent session history (post-compaction). Useful for monitoring.</summary>
    public int SessionMessageCount => _historyManager?.Count ?? 0;

    /// <summary>Current chat session ID.</summary>
    public string CurrentChatId { get; private set; } = "";

    /// <summary>Current chat title.</summary>
    public string CurrentChatTitle { get; private set; } = "New Chat";

    /// <summary>Per-chat permission mode. Stored on the chat session so each chat keeps its own policy.</summary>
    public string CurrentPermissionMode { get; private set; } = "Normal";

    public AiOperatorService(IChatClient? chatClient, AiToolFunctions toolFunctions,
        Func<string>? contextProvider = null, AiChatStore? chatStore = null,
        AppSettingsService? settingsService = null)
    {
        IsConfigured = chatClient is not null;
        _contextProvider = contextProvider;
        _toolFunctions = toolFunctions;
        _chatStore = chatStore ?? new AiChatStore();
        _toolResultStore = toolFunctions.ToolResultStore;
        var baseClient = chatClient ?? new StubChatClient();
        _baseChatClient = baseClient;

        var settings = settingsService?.Settings;
        _appSettings = settings;

        // Build AIFunction list from the tool functions instance using reflection.
        var methods = typeof(AiToolFunctions).GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        var allTools = methods
            .Where(m => !m.IsSpecialName)
            .Select(m => (AITool)AIFunctionFactory.Create(m, toolFunctions))
            .ToList();

        // Index all tools by name for on-demand loading
        _allToolsByName = new(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in allTools)
        {
            var name = tool is AIFunction f ? f.Name : tool.GetType().Name;
            _allToolsByName[name] = tool;
        }

        // Add the meta-tools for progressive tool loading
        _allToolsByName["request_tools"] = AIFunctionFactory.Create(RequestTools, "request_tools",
            "Load additional tool categories on-demand. Call this before using specialized tools. " +
            "Categories: " + string.Join(", ", ToolCategories.Categories.Keys));
        _allToolsByName["list_tool_categories"] = AIFunctionFactory.Create(ListToolCategories,
            "list_tool_categories", "List available tool categories and which are currently loaded.");
        _allToolsByName["unload_tools"] = AIFunctionFactory.Create(UnloadTools, "unload_tools",
            "Unload tool categories no longer needed to free token budget. Use 'all' to reset to core only.");

        // Skill meta-tools
        _allToolsByName["load_skill"] = AIFunctionFactory.Create(LoadSkillTool, "load_skill",
            "Load a domain skill for expert guidance. Loaded skills inject instructions into the system prompt. " +
            "Available skills: " + string.Join(", ", _skillSystem.Catalog.Keys));
        _allToolsByName["list_skills"] = AIFunctionFactory.Create(ListSkillsTool,
            "list_skills", "List available domain skills and which are currently loaded.");
        _allToolsByName["unload_skill"] = AIFunctionFactory.Create(UnloadSkillTool,
            "unload_skill", "Unload a domain skill to free token budget.");
        _allToolsByName["confirm_load_skill"] = AIFunctionFactory.Create(ConfirmLoadSkillTool,
            "confirm_load_skill", "Confirm loading an elevated skill after the user approves its permissions.");
        _allToolsByName["view_skill_reference"] = AIFunctionFactory.Create(ViewSkillReferenceTool,
            "view_skill_reference", "Read a reference file from an active skill's references/ directory.");

        // ── Token Budget ──
        _tokenBudget = new TokenBudget(Log);
        if (settings is not null)
        {
            _tokenBudget.SetPricing(
                settings.InputPricePerMillion,
                settings.OutputPricePerMillion,
                settings.CachedInputPricePerMillion);
            if (settings.MaxSessionCostDollars > 0)
                _tokenBudget.SetLimits(maxCostDollars: settings.MaxSessionCostDollars);
        }

        _allToolsByName["get_budget_status"] = AIFunctionFactory.Create(GetBudgetStatusTool,
            "get_budget_status", "Get current token usage, cost estimate, and budget status for this session.");

        // ── Memory System ──
        if (settings?.EnableAgentMemory != false)
        {
            _memorySystem = new MemorySystem(log: Log);
            _memorySystem.Load();
            _memorySystem.Prune(settings?.MaxMemoryEntries ?? 500);

            _allToolsByName["remember"] = AIFunctionFactory.Create(RememberTool,
                "remember",
                "Save a persistent memory entry. Memories carry across sessions. Categories: " +
                "UserPreference, ProcessKnowledge, LearnedPattern, WorkflowRecipe, SafetyNote, ToolTip.");
            _allToolsByName["recall_memory"] = AIFunctionFactory.Create(RecallMemoryTool,
                "recall_memory", "Search persistent memories by keyword, category, or process name.");
            _allToolsByName["forget_memory"] = AIFunctionFactory.Create(ForgetMemoryTool,
                "forget_memory", "Delete a persistent memory entry by ID.");
        }

        // ── Subagent & Plan meta-tools ──
        _allToolsByName["spawn_subagent"] = AIFunctionFactory.Create(SpawnSubagentTool,
            "spawn_subagent",
            "Spawn a focused subagent for a subtask. The subagent gets its own conversation, " +
            "works autonomously, and returns results. Use for parallel investigation or complex " +
            "sub-tasks. Provide: task (prompt), description (short label), context (optional), " +
            "allowed_tools (optional glob patterns like 'Read*'), max_turns (optional, default 15).");
        _allToolsByName["plan_task"] = AIFunctionFactory.Create(PlanTaskTool,
            "plan_task",
            "Generate a structured execution plan for a complex task. Returns a plan with " +
            "numbered steps, expected tools, and safety warnings. The user can review before execution. " +
            "Use for multi-step operations or when destructive tools are involved.");
        _allToolsByName["execute_plan"] = AIFunctionFactory.Create(ExecutePlanTool,
            "execute_plan",
            "Generate AND immediately execute a structured plan for a complex task. " +
            "Each step is executed sequentially and progress is reported. " +
            "Use when you are confident the plan should run without user review.");

        // ── MCP Manager ──
        _mcpManager = new McpManager(Log);

        // Start with only core tools — agent requests more via request_tools()
        _tools = [];
        LoadCoreTools();

        Log("INFO", $"Progressive tools: {_tools.Count} core loaded, {_allToolsByName.Count} total available");

        // Build tool attribute cache for parallel execution decisions
        _toolAttributeCache = new ToolAttributeCache();
        _toolAttributeCache.ScanType(typeof(AiToolFunctions));

        // Load skills from built-in and user directories
        LoadSkillsFromDisk();

        _historyManager = new ChatHistoryManager();
        _agentLoop = BuildNewAgentLoop(baseClient);
        Log("INFO", "Using new AgentLoop (owns tool-call loop directly)");

        // Connect MCP servers from settings (fire-and-forget, non-blocking)
        if (settings?.McpServers is { Count: > 0 } mcpServers)
            _ = ConnectMcpServersAsync(mcpServers).ContinueWith(
                t => { if (t.IsFaulted) Log("ERROR", $"MCP server connection failed: {t.Exception?.InnerException?.Message}"); },
                TaskScheduler.Default);

        // ── Model Switcher ──
        if (settings?.FallbackModels is { Count: > 0 } fallbackModels)
        {
            var models = new List<ModelConfig>
            {
                new() { ModelId = settings.Model ?? "default", MaxContextTokens = DefaultMaxContextTokens }
            };
            // Fallback entries have ModelId only — client factory wired on Reconfigure
            foreach (var fb in fallbackModels)
                models.Add(new ModelConfig { ModelId = fb, MaxContextTokens = DefaultMaxContextTokens });
            _modelSwitcher = new ModelSwitcher(models, Log);
        }

        _allToolsByName["switch_model"] = AIFunctionFactory.Create(SwitchModelTool,
            "switch_model", "Switch to a different AI model mid-conversation. Available models: " +
            (_modelSwitcher?.Models.Select(m => m.ModelId).DefaultIfEmpty("none configured")
                .Aggregate((a, b) => a + ", " + b) ?? "none configured"));

        // ── Scheduled Tasks ──
        _taskScheduler = new CeaiTaskScheduler(log: Log);
        _taskScheduler.TaskDue += OnScheduledTaskDue;
        _taskScheduler.Start();

        _allToolsByName["schedule_task"] = AIFunctionFactory.Create(ScheduleTaskTool,
            "schedule_task", "Schedule a recurring or one-shot task using a cron expression. " +
            "The task runs as a subagent. Format: minute hour day-of-month month day-of-week. " +
            "Example: '0 9 * * 1-5' = weekdays at 9am.");
        _allToolsByName["list_tasks"] = AIFunctionFactory.Create(ListTasksTool,
            "list_tasks", "List all scheduled tasks with their status and next run time.");
        _allToolsByName["cancel_task"] = AIFunctionFactory.Create(CancelTaskTool,
            "cancel_task", "Cancel and remove a scheduled task by ID.");

        // ── Session Metadata ──
        _allToolsByName["get_session_info"] = AIFunctionFactory.Create(GetSessionInfoTool,
            "get_session_info", "Get metadata about the current session: target process, discoveries, timeline, cost.");
        _allToolsByName["search_sessions"] = AIFunctionFactory.Create(SearchSessionsTool,
            "search_sessions", "Search across all saved sessions by keyword (process name, discoveries, events).");

        var chatStoreDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CEAISuite", "chats");
        _sessionIndex = new SessionIndex(chatStoreDir, Log);

        // ── Prompt Cache Optimizer ──
        _promptCacheOptimizer = new PromptCacheOptimizer(Log);

        // ── Plugin Host ──
        _pluginHost = new PluginHost(log: Log);
        _ = LoadPluginsAsync().ContinueWith(
            t => { if (t.IsFaulted) Log("ERROR", $"Plugin loading failed: {t.Exception?.InnerException?.Message}"); },
            TaskScheduler.Default);

        // Start with a fresh chat session
        NewChat();

        // Ensure log directory exists
        try { Directory.CreateDirectory(LogDir); } catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"[AiOperatorService] Failed to create log directory: {ex}"); }
    }

    // ── Shared hook registry (single instance across sessions) ──
    private readonly HookRegistry _hookRegistry = new();

    private AgentLoop.AgentLoop BuildNewAgentLoop(IChatClient client)
    {
        var effectiveSystemPrompt = SystemPrompt;
        if (_appSettings?.RequirePlanForDestructive == true)
        {
            effectiveSystemPrompt += "\n\n" +
                "[POLICY] The user has enabled RequirePlanForDestructive. Before performing any " +
                "destructive operation (WriteMemory, SetBreakpoint, InstallCodeCaveHook, ToggleScript, " +
                "ForceDetachAndCleanup, ChangeMemoryProtection), you MUST first call plan_task to " +
                "generate and present an execution plan for user approval. Do not execute destructive " +
                "tools directly without planning first.";
        }

        // Build permission engine with optional mode from settings
        var permissionEngine = new PermissionEngine(DangerousTools.Names, Log, _toolAttributeCache);
        _permissionEngine = permissionEngine;
        if (_appSettings is not null && Enum.TryParse<PermissionMode>(_appSettings.PermissionMode ?? "Normal", true, out var mode))
            permissionEngine.ActiveMode = mode;

        // Determine the active provider for gating provider-specific features
        var providerKind = ResolveProviderKind();

        // Build provider-aware additional properties
        AdditionalPropertiesDictionary? additionalProps = null;
        if (providerKind == ProviderKind.Anthropic)
        {
            additionalProps = new AdditionalPropertiesDictionary
            {
                ["cache_control"] = new Dictionary<string, string> { ["type"] = "ephemeral" }
            };
        }

        var loop = new AgentLoop.AgentLoop(client, new AgentLoopOptions
        {
            SystemPrompt = effectiveSystemPrompt,
            Tools = _tools,
            Provider = providerKind,
            Temperature = 0.3f,
            Limits = Limits,
            ToolResultStore = _toolResultStore,
            DangerousToolNames = DangerousTools.Names,
            MaxTurns = 25,
            AdditionalProperties = additionalProps,
            ContextManagementStrategies = providerKind == ProviderKind.Anthropic
                ? ContextManagementStrategy.AnthropicDefaults
                : null,
            Budget = _tokenBudget,
            Memory = _memorySystem,
            Log = Log,
            Skills = _skillSystem,
            PermissionEngine = permissionEngine,
            Hooks = _hookRegistry,
            EnableEarlyToolExecution = _appSettings?.EnableEarlyToolExecution ?? false,
            ModelSwitcher = _modelSwitcher,
            PromptCacheOptimizer = _promptCacheOptimizer,
            MicroCompaction = new MicroCompaction(log: Log),
        }, _toolAttributeCache);

        // Build subagent manager and plan executor using the same options
        _subagentManager = new SubagentManager(client, loop.Options, _toolAttributeCache);
        _planExecutor = new PlanExecutor(client, loop.Options);

        return loop;
    }

    /// <summary>Map the settings provider string to the <see cref="ProviderKind"/> enum.</summary>
    private ProviderKind ResolveProviderKind()
    {
        var provider = (_appSettings?.Provider ?? "openai").ToLowerInvariant();
        return provider switch
        {
            "anthropic" => ProviderKind.Anthropic,
            "copilot" => ProviderKind.Copilot,
            "gemini" => ProviderKind.Gemini,
            "openai-compatible" => ProviderKind.OpenAICompatible,
            "openai" => ProviderKind.OpenAI,
            _ => ProviderKind.Unknown,
        };
    }

    /// <summary>
    /// Hot-swap the AI provider without restarting the app.
    /// Preserves display history and action log; creates a new agent session.
    /// </summary>
    /// <summary>Set the dynamic context provider delegate (for late-binding in DI scenarios).</summary>
    public void SetContextProvider(Func<string> provider) => _contextProvider = provider;

    public void SetPermissionMode(string mode)
    {
        if (Enum.TryParse<PermissionMode>(mode, true, out var parsed))
        {
            // Update the live agent loop's permission engine if active
            if (_agentLoop?.Options.PermissionEngine is { } engine)
                engine.ActiveMode = parsed;

            // Store on the current chat session (per-chat, not global)
            CurrentPermissionMode = mode;

            Log("INFO", $"Permission mode changed to: {mode}");
        }
    }

    public void Reconfigure(IChatClient? newClient)
    {
        var baseClient = newClient ?? new StubChatClient();
        _baseChatClient = baseClient;
        IsConfigured = newClient is not null;

        // Save current chat, then start a fresh session with the new agent
        if (!string.IsNullOrEmpty(CurrentChatId) && _displayHistory.Count > 0)
            SaveCurrentChat();

        _agentLoop = BuildNewAgentLoop(baseClient);
        _historyManager = new ChatHistoryManager();

        // Replay existing display history into the new history manager
        if (_displayHistory.Count > 0)
            _historyManager.ReplayFromSaved(_displayHistory, Limits.MaxReplayMessages, Limits, _toolResultStore);

        Log("INFO", $"Reconfigured AI provider (IsConfigured={IsConfigured})");
        StatusChanged?.Invoke(IsConfigured ? "Ready (provider updated)" : "Not configured");
    }

    private void Log(string level, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}";
        try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"[AiOperatorService] Failed to write log: {ex}"); }
    }

    private void UpdateStatus(string status)
    {
        Log("INFO", status);
        StatusChanged?.Invoke(status);
    }

    public async Task<string> SendMessageAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        AddUserMessageToHistory(userMessage);
        var reader = SendMessageStreamingAsync(userMessage, cancellationToken);
        var text = "";
        await foreach (var evt in reader.ReadAllAsync(cancellationToken))
        {
            if (evt is AgentStreamEvent.TextDelta delta)
                text += delta.Text;
            else if (evt is AgentStreamEvent.Error err)
                return $"AI error: {err.Message}";
        }
        return string.IsNullOrWhiteSpace(text) ? "(No response)" : text;
    }

    /// <summary>
    /// Adds a user message to display history. Call this from the UI BEFORE
    /// calling SendMessageAsync/SendMessageStreamingAsync so the message
    /// appears in the chat immediately.
    /// </summary>
    public void AddUserMessageToHistory(string userMessage)
    {
        _displayHistory.Add(new AiChatMessage("user", userMessage, DateTimeOffset.UtcNow));
        ScheduleAutoSave();
    }

    /// <summary>
    /// Adds a user message with images to both display history and the LLM history.
    /// The text portion is stored in display history; images are added to the LLM
    /// ChatMessage as DataContent so vision-capable models can see them.
    /// </summary>
    public void AddUserMessageWithImages(string textPart, IList<(byte[] Data, string MediaType)> images)
    {
        // Display history only stores text (images are too large to serialize)
        var imageLabels = string.Join(", ", images.Select((img, i) => $"[Image {i + 1}: {img.MediaType}, {img.Data.Length / 1024}KB]"));
        _displayHistory.Add(new AiChatMessage("user", $"{textPart}\n{imageLabels}", DateTimeOffset.UtcNow)
        {
            ImageDataList = images.Select(i => i.Data).ToList()
        });
        ScheduleAutoSave();

        // Build mixed-content message for the LLM
        var contents = new List<Microsoft.Extensions.AI.AIContent>();
        if (!string.IsNullOrWhiteSpace(textPart))
            contents.Add(new Microsoft.Extensions.AI.TextContent(textPart));
        foreach (var (data, mediaType) in images)
            contents.Add(new Microsoft.Extensions.AI.DataContent(data, mediaType));

        _historyManager?.AddUserMessage(contents);
    }

    /// <summary>Pending image data keyed by chat message for later UI display.</summary>
    private readonly Dictionary<string, List<byte[]>> _messageImages = new();

    public ChannelReader<AgentStreamEvent> SendMessageStreamingAsync(
        string userMessage, CancellationToken cancellationToken = default)
    {
        return SendMessageViaNewLoop(userMessage, cancellationToken);
    }

    /// <summary>
    /// New AgentLoop streaming path. Delegates to AgentLoop.RunStreamingAsync() and
    /// wraps the events with display history tracking, usage tracking, and persistence.
    /// </summary>
    private ChannelReader<AgentStreamEvent> SendMessageViaNewLoop(
        string userMessage, CancellationToken cancellationToken)
    {
        var outerChannel = Channel.CreateUnbounded<AgentStreamEvent>();

        _ = Task.Run(async () =>
        {
            // Rate limiting
            if (RateLimitSeconds > 0)
            {
                var now = DateTimeOffset.UtcNow;
                DateTimeOffset? last;
                lock (_rateLimitLock) { last = _lastRequestTime; }

                if (last.HasValue)
                {
                    var elapsed = now - last.Value;
                    var cooldown = TimeSpan.FromSeconds(RateLimitSeconds);
                    if (elapsed < cooldown)
                    {
                        if (RateLimitWait)
                        {
                            var remaining = cooldown - elapsed;
                            UpdateStatus($"Rate limited — waiting {remaining.TotalSeconds:F1}s…");
                            await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            await outerChannel.Writer.WriteAsync(
                                new AgentStreamEvent.Error($"Rate limited: wait {(cooldown - elapsed).TotalSeconds:F0}s"), cancellationToken).ConfigureAwait(false);
                            outerChannel.Writer.Complete();
                            return;
                        }
                    }
                }
                lock (_rateLimitLock) { _lastRequestTime = DateTimeOffset.UtcNow; }
            }

            // Track turn for idle category auto-unload
            _currentTurn++;

            // Auto-activate skills based on trigger patterns
            var triggered = _skillSystem.FindTriggeredSkills(userMessage);
            foreach (var skillName in triggered)
            {
                _skillSystem.LoadSkill(skillName);
                Log("SKILL", $"Auto-activated skill: {skillName}");
            }

            Log("INFO", $"User (new loop): {userMessage}");
            UpdateStatus("Thinking...");

            var toolCalls = new List<AiToolCallInfo>();
            var toolResults = new List<AiToolResultInfo>();
            var assistantTextSb = new System.Text.StringBuilder();
            int toolCallCount = 0;

            try
            {
                var reader = _agentLoop!.RunStreamingAsync(
                    userMessage, _historyManager!, _contextProvider,
                    _loadedCategories, cancellationToken);

                await foreach (var evt in reader.ReadAllAsync(cancellationToken))
                {
                    switch (evt)
                    {
                        case AgentStreamEvent.TextDelta delta:
                            assistantTextSb.Append(delta.Text);
                            break;

                        case AgentStreamEvent.ToolCallStarted started:
                            toolCallCount++;
                            var argsJson = started.Arguments.Length > 0
                                ? $"{{{started.Arguments}}}" : null;
                            toolCalls.Add(new AiToolCallInfo(
                                $"call_{toolCallCount}", started.ToolName, argsJson));
                            _actionLog.Add(new AiActionLogEntry(
                                started.ToolName, started.Arguments, "invoked", DateTimeOffset.UtcNow));
                            UpdateStatus($"Tool: {started.ToolName} ({toolCallCount} calls)");
                            break;

                        case AgentStreamEvent.ToolCallCompleted completed:
                            var truncResult = completed.Result.Length > 200
                                ? completed.Result[..200] + "..." : completed.Result;
                            toolResults.Add(new AiToolResultInfo(
                                $"call_{toolCallCount}", completed.ToolName, completed.Result));
                            for (int i = _actionLog.Count - 1; i >= 0; i--)
                            {
                                if (_actionLog[i].Result == "invoked")
                                {
                                    _actionLog[i] = _actionLog[i] with { Result = truncResult };
                                    break;
                                }
                            }
                            break;

                        case AgentStreamEvent.Completed done:
                            // Final — will be emitted below after history update
                            break;
                    }

                    // Forward all events to the outer channel for the ViewModel
                    await outerChannel.Writer.WriteAsync(evt, cancellationToken).ConfigureAwait(false);
                }

                // Pending images — inject as DataContent so vision-capable models can analyze them
                if (_toolFunctions is not null)
                {
                    int imagesProcessed = 0;
                    while (imagesProcessed < Limits.MaxImagesPerTurn
                        && _toolFunctions.PendingImages.TryDequeue(out var img))
                    {
                        imagesProcessed++;
                        UpdateStatus($"Analyzing screenshot ({imagesProcessed})...");

                        // Build mixed-content user message with actual image bytes
                        var imageContents = new List<Microsoft.Extensions.AI.AIContent>
                        {
                            new Microsoft.Extensions.AI.TextContent(
                                $"[Screenshot: {img.Description}] Analyze this screenshot of the target process window."),
                            new Microsoft.Extensions.AI.DataContent(img.PngData, "image/png")
                        };
                        _historyManager!.AddUserMessage(imageContents);

                        // Run the agent loop on existing history (message already added)
                        var imageReader = _agentLoop!.RunStreamingContinueAsync(
                            _historyManager!, _contextProvider,
                            _loadedCategories, cancellationToken);
                        await foreach (var evt in imageReader.ReadAllAsync(cancellationToken))
                        {
                            if (evt is AgentStreamEvent.TextDelta delta)
                                assistantTextSb.Append('\n').Append(delta.Text);
                            await outerChannel.Writer.WriteAsync(evt, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    while (_toolFunctions.PendingImages.TryDequeue(out _))
                        Log("WARN", $"Discarded excess pending image (max {Limits.MaxImagesPerTurn} per turn)");
                }

                // Auto-unload categories not used in the last 10 turns
                AutoUnloadIdleCategories();

                // Update session metadata
                _sessionMetadata.TotalToolCalls += toolCallCount;
                if (toolCallCount > 0)
                    _sessionMetadata.AddEvent($"Turn {_currentTurn}: {toolCallCount} tool calls");
                if (_tokenBudget.EstimatedCostUsd > 0)
                    _sessionMetadata.CumulativeCost = _tokenBudget.EstimatedCostUsd;

                var assistantText = assistantTextSb.ToString();
                Log("INFO", $"Done, {_historyManager!.Count} msgs in context, text={assistantText.Length} chars, tools={toolCallCount}");

                // Store the real text (empty for tool-only responses) so chat resume
                // doesn't feed placeholder text to the model. DisplayContent provides
                // a UI-friendly placeholder via the record property.
                _displayHistory.Add(new AiChatMessage("assistant", assistantText, DateTimeOffset.UtcNow)
                {
                    ToolCalls = toolCalls.Count > 0 ? toolCalls : null,
                    ToolResults = toolResults.Count > 0 ? toolResults : null,
                });
                _historyManager!.PruneOldToolResults();
                SaveCurrentChat();

                var budgetPart = _tokenBudget.TotalRequests > 0
                    ? $", ~${_tokenBudget.EstimatedCostUsd:F4}"
                    : "";
                var usagePart = _tokenBudget.TotalRequests > 0
                    ? $", tokens: {_tokenBudget.TotalInputTokens:#,0}↑ {_tokenBudget.TotalOutputTokens:#,0}↓ {_tokenBudget.TotalCachedTokens:#,0}⚡{budgetPart}"
                    : "";
                var historyPart = $", {_historyManager!.Count} msgs in context";
                var summary = toolCallCount > 0
                    ? $"Done ({toolCallCount} tool calls{usagePart}{historyPart})"
                    : $"Done{usagePart}{historyPart}";
                UpdateStatus(summary);
            }
            catch (OperationCanceledException)
            {
                var cancelMsg = "⏹ Stopped by user.";
                Log("INFO", "Streaming cancelled by user (new loop)");
                UpdateStatus("Stopped");
                _displayHistory.Add(new AiChatMessage("assistant",
                    assistantTextSb.Length > 0 ? assistantTextSb.ToString() + "\n\n" + cancelMsg : cancelMsg,
                    DateTimeOffset.UtcNow)
                {
                    ToolCalls = toolCalls.Count > 0 ? toolCalls : null,
                    ToolResults = toolResults.Count > 0 ? toolResults : null,
                });
                SaveCurrentChat();
                await outerChannel.Writer.WriteAsync(
                    new AgentStreamEvent.Error(cancelMsg), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var errorMsg = "AI error: The request failed. Check the log for details.";
                Log("ERROR", $"New loop error: {ex.GetType().Name}: {ex}\n{ex.StackTrace}");
                UpdateStatus("Error");
                _displayHistory.Add(new AiChatMessage("assistant", errorMsg, DateTimeOffset.UtcNow));
                SaveCurrentChat();
                await outerChannel.Writer.WriteAsync(
                    new AgentStreamEvent.Error(errorMsg), CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                outerChannel.Writer.Complete();
            }
        }, cancellationToken);

        return outerChannel.Reader;
    }

    // ─── Progressive Tool Loading ───────────────────────────────────────

    /// <summary>Load core tools into the active tool list.</summary>
    private void LoadCoreTools()
    {
        _tools.Clear();
        _loadedCategories.Clear();
        foreach (var name in ToolCategories.Core)
        {
            if (_allToolsByName.TryGetValue(name, out var tool))
                _tools.Add(tool);
        }
    }

    /// <summary>
    /// Meta-tool: agent calls this to load additional tool categories on-demand.
    /// Returns the list of newly loaded tool names.
    /// </summary>
    [System.ComponentModel.Description("Load additional tool categories on-demand.")]
    private string RequestTools(
        [System.ComponentModel.Description("Category to load, OR a keyword to search for tools by name/description (e.g. 'breakpoints', 'disassembly', 'hex dump', 'memory scan'). Comma-separated for multiple categories.")] string categories)
    {
        var loaded = new List<string>();
        var alreadyLoaded = new List<string>();
        var activeNames = new HashSet<string>(_tools.Select(t => t is AIFunction f ? f.Name : ""),
            StringComparer.OrdinalIgnoreCase);

        foreach (var rawCat in categories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cat = rawCat;

            if (_loadedCategories.Contains(cat))
            {
                alreadyLoaded.Add(cat);
                continue;
            }

            // Try exact category match first
            if (!ToolCategories.Categories.TryGetValue(cat, out var toolNames))
            {
                // Fall back to keyword search across all categories
                var bestCategory = FindBestCategoryByKeyword(cat);
                if (bestCategory is not null && ToolCategories.Categories.TryGetValue(bestCategory, out toolNames))
                {
                    Log("TOOLS", $"Keyword '{cat}' matched category '{bestCategory}' via search scoring");
                    cat = bestCategory;
                    if (_loadedCategories.Contains(cat))
                    {
                        alreadyLoaded.Add(cat);
                        continue;
                    }
                }
                else
                {
                    continue;
                }
            }

            foreach (var name in toolNames)
            {
                if (activeNames.Contains(name)) continue;
                if (_allToolsByName.TryGetValue(name, out var tool))
                {
                    _tools.Add(tool);
                    activeNames.Add(name);
                    loaded.Add(name);
                }
            }
            _loadedCategories.Add(cat);
            _categoryLastUsedTurn[cat] = _currentTurn;
        }

        // Co-load tightly coupled categories: breakpoint workflows reference disassembly tools
        // (FindWritersToOffset, TraceFieldWriters, Disassemble) in their rejection/suggestion messages.
        foreach (var (trigger, coload) in ToolCategories.CoLoadCategories)
        {
            if (_loadedCategories.Contains(trigger) && !_loadedCategories.Contains(coload) &&
                ToolCategories.Categories.TryGetValue(coload, out var coloadTools))
            {
                foreach (var name in coloadTools)
                {
                    if (activeNames.Contains(name)) continue;
                    if (_allToolsByName.TryGetValue(name, out var tool))
                    {
                        _tools.Add(tool);
                        activeNames.Add(name);
                        loaded.Add(name);
                    }
                }
                _loadedCategories.Add(coload);
                _categoryLastUsedTurn[coload] = _currentTurn;
                Log("TOOLS", $"Co-loaded '{coload}' (triggered by '{trigger}')");
            }
        }

        Log("TOOLS", $"Loaded categories: [{categories}] → {loaded.Count} new tools " +
            $"(active: {_tools.Count}, already loaded: [{string.Join(", ", alreadyLoaded)}])");

        if (loaded.Count == 0 && alreadyLoaded.Count > 0)
            return $"Categories already loaded: {string.Join(", ", alreadyLoaded)}. No new tools added.";
        if (loaded.Count == 0)
            return $"No matching categories found for '{categories}'. Available: {string.Join(", ", ToolCategories.Categories.Keys)}";

        return $"Loaded {loaded.Count} tools: {string.Join(", ", loaded)}. Active tool count: {_tools.Count}.";
    }

    /// <summary>Find the best-matching category for a keyword search.</summary>
    private string? FindBestCategoryByKeyword(string keyword)
    {
        var bestScore = 0;
        string? bestCategory = null;

        foreach (var (cat, toolNames) in ToolCategories.Categories)
        {
            // Score each tool in the category
            var tools = toolNames.Select(name =>
            {
                var meta = _toolAttributeCache.Get(name);
                return (name, meta.SearchHints, (string?)null);
            });

            var score = ToolSearchScorer.ScoreCategory(keyword, tools);
            if (score > bestScore)
            {
                bestScore = score;
                bestCategory = cat;
            }
        }

        return bestScore >= 20 ? bestCategory : null; // Minimum threshold
    }

    /// <summary>Meta-tool: list available categories and their load status.</summary>
    [System.ComponentModel.Description("List available tool categories and which are currently loaded.")]
    private string ListToolCategories()
    {
        var lines = new List<string>();
        foreach (var (cat, tools) in ToolCategories.Categories)
        {
            var status = _loadedCategories.Contains(cat) ? "✓ loaded" : "○ available";
            lines.Add($"  {cat} ({tools.Length} tools) — {status}");
        }
        return $"Tool categories ({_loadedCategories.Count}/{ToolCategories.Categories.Count} loaded):\n" +
               string.Join("\n", lines) +
               $"\n\nActive tools: {_tools.Count} / {_allToolsByName.Count} total";
    }

    /// <summary>Meta-tool: unload tool categories no longer needed to reclaim token budget.</summary>
    [System.ComponentModel.Description("Unload tool categories to free token budget. Use when done with a category.")]
    private string UnloadTools(
        [System.ComponentModel.Description("Category to unload (comma-separated for multiple). Use 'all' to reset to core only.")] string categories)
    {
        if (string.Equals(categories.Trim(), "all", StringComparison.OrdinalIgnoreCase))
        {
            var removedCount = _tools.Count;
            LoadCoreTools();
            Log("TOOLS", $"Unloaded all categories, reset to {_tools.Count} core tools");
            return $"Reset to core tools. Removed {removedCount - _tools.Count} tools. Active: {_tools.Count}.";
        }

        var unloaded = new List<string>();
        var notLoaded = new List<string>();

        foreach (var cat in categories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!_loadedCategories.Remove(cat))
            {
                notLoaded.Add(cat);
                continue;
            }

            if (ToolCategories.Categories.TryGetValue(cat, out var toolNames))
            {
                var coreNames = ToolCategories.Core;
                foreach (var name in toolNames)
                {
                    if (coreNames.Contains(name)) continue; // don't remove core tools
                    _tools.RemoveAll(t => t is AIFunction f &&
                        string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
                }
            }
            unloaded.Add(cat);
        }

        Log("TOOLS", $"Unloaded [{string.Join(", ", unloaded)}]. Active: {_tools.Count} tools.");
        if (unloaded.Count == 0)
            return $"No categories were loaded to unload. Not loaded: {string.Join(", ", notLoaded)}.";
        return $"Unloaded {string.Join(", ", unloaded)}. Active tools: {_tools.Count}.";
    }

    /// <summary>
    /// Automatically unload tool categories that haven't been used in the last N turns.
    /// Called after each agent turn to keep the active tool set lean.
    /// </summary>
    private void AutoUnloadIdleCategories(int maxIdleTurns = 10)
    {
        var toUnload = _categoryLastUsedTurn
            .Where(kv => _currentTurn - kv.Value > maxIdleTurns)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var cat in toUnload)
        {
            UnloadCategory(cat);
            _categoryLastUsedTurn.Remove(cat);
            Log("TOOLS", $"Auto-unloaded idle category '{cat}' (idle for {_currentTurn - _categoryLastUsedTurn.GetValueOrDefault(cat)} turns)");
        }
    }

    /// <summary>
    /// Remove all tools belonging to a category from the active tool list.
    /// Does not remove core tools even if they overlap with the category.
    /// </summary>
    private void UnloadCategory(string category)
    {
        if (!_loadedCategories.Remove(category)) return;

        if (ToolCategories.Categories.TryGetValue(category, out var toolNames))
        {
            var coreNames = ToolCategories.Core;
            foreach (var name in toolNames)
            {
                if (coreNames.Contains(name)) continue;
                _tools.RemoveAll(t => t is AIFunction f &&
                    string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    // ─── Skill Meta-Tools ───────────────────────────────────────────────

    [System.ComponentModel.Description("Load a domain skill for expert guidance.")]
    private string LoadSkillTool(
        [System.ComponentModel.Description("Skill name to load (e.g. memory-scanning, code-analysis, breakpoint-mastery)")] string name)
    {
        // Check if this is a fork-context skill — route to subagent instead of injecting into prompt
        if (_skillSystem.Catalog.TryGetValue(name, out var skillDef) && skillDef.Context == SkillContext.Fork)
        {
            if (skillDef.RequiresApproval)
                return $"Skill '{name}' runs in an isolated sub-agent and requires user approval. Use confirm_load_skill('{name}') after the user approves.";

            return SpawnForkSkill(skillDef);
        }

        var result = _skillSystem.LoadSkill(name);
        ApplySkillPermissionRules(name);
        return result;
    }

    [System.ComponentModel.Description("List available domain skills and their load status.")]
    private string ListSkillsTool() => _skillSystem.BuildCatalogSummary();

    [System.ComponentModel.Description("Unload a domain skill to free token budget.")]
    private string UnloadSkillTool(
        [System.ComponentModel.Description("Skill name to unload")] string name)
    {
        _permissionEngine?.RemoveSkillRules(name);
        return _skillSystem.UnloadSkill(name);
    }

    [System.ComponentModel.Description("Confirm loading an elevated skill after user approval.")]
    private string ConfirmLoadSkillTool(
        [System.ComponentModel.Description("Skill name to confirm")] string name)
    {
        var result = _skillSystem.ConfirmSkillLoad(name);
        ApplySkillPermissionRules(name);
        return result;
    }

    [System.ComponentModel.Description("Read a reference file from a loaded skill.")]
    private string ViewSkillReferenceTool(
        [System.ComponentModel.Description("Name of the active skill")] string skillName,
        [System.ComponentModel.Description("File name within the skill's references/ directory")] string fileName)
    {
        return _skillSystem.ReadSkillReference(skillName, fileName);
    }

    private string SpawnForkSkill(SkillDefinition skill)
    {
        if (_subagentManager is null)
            return $"Sub-agent system not available. Skill '{skill.Name}' requires fork context.";

        var request = new SubagentRequest
        {
            Description = $"Skill: {skill.Name}",
            Task = skill.Instructions,
            AllowedToolPatterns = skill.AllowedTools?.Count > 0 ? skill.AllowedTools.ToList() : null,
            BudgetFraction = 0.4,
        };

        var handle = _subagentManager.Spawn(request);
        Log("SKILL", $"Fork skill '{skill.Name}' spawned as subagent {handle.Id}");
        return $"Skill '{skill.Name}' launched as isolated sub-agent (ID: {handle.Id}). " +
               $"It will execute with its own context window and return results when complete.";
    }

    private void ApplySkillPermissionRules(string skillName)
    {
        if (_permissionEngine is null) return;
        if (!_skillSystem.Catalog.TryGetValue(skillName, out var skill)) return;
        if (!_skillSystem.ActiveSkills.Contains(skillName)) return;
        if (skill.AllowedTools is not { Count: > 0 }) return;

        var rules = skill.AllowedTools.Select(pattern => new PermissionRule
        {
            ToolPattern = pattern,
            Effect = PermissionEffect.Allow,
            Description = $"Granted by skill '{skillName}'",
        });
        _permissionEngine.AddSkillRules(skillName, rules);
    }

    /// <summary>Load skill definitions from built-in and user directories.</summary>
    private void LoadSkillsFromDisk()
    {
        // Built-in skills shipped alongside the application binary
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var builtInSkills = Path.Combine(appDir, "skills");

        // User-defined skills in the app data directory
        var userSkills = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CEAISuite", "skills");

        var builtIn = SkillLoader.LoadFromDirectory(builtInSkills, Log, isBundled: true);
        _skillSystem.RegisterAll(builtIn);

        var user = SkillLoader.LoadFromDirectory(userSkills, Log, isBundled: false);
        _skillSystem.RegisterAll(user);

        if (_skillSystem.Catalog.Count > 0)
            Log("INFO", $"Skills: {_skillSystem.Catalog.Count} loaded from disk");
    }

    // ─── MCP Server Management ─────────────────────────────────────────

    /// <summary>
    /// Connect to configured MCP servers and inject their discovered tools.
    /// Called from the constructor (fire-and-forget) for auto-connect servers.
    /// </summary>
    private async Task ConnectMcpServersAsync(IReadOnlyList<McpServerSettingsEntry> servers)
    {
        foreach (var entry in servers)
        {
            if (!entry.Enabled || !entry.AutoConnect) continue;
            if (string.IsNullOrWhiteSpace(entry.Command)) continue;

            try
            {
                var config = new McpServerConfig
                {
                    Name = entry.Name,
                    Command = entry.Command,
                    Arguments = entry.Arguments,
                    Environment = entry.Environment,
                    AutoConnect = entry.AutoConnect,
                };

                var discoveredTools = await _mcpManager.AddServerAsync(config).ConfigureAwait(false);

                // Inject discovered MCP tools into the tool index
                foreach (var fn in discoveredTools)
                {
                    _allToolsByName[fn.Name] = fn;
                    _tools.Add(fn); // MCP tools are always active (no progressive loading)
                }

                Log("MCP", $"Server '{entry.Name}': {discoveredTools.Count} tools injected");
            }
            catch (Exception ex)
            {
                Log("MCP", $"Failed to connect to '{entry.Name}': {ex}");
            }
        }

        if (_mcpManager.Clients.Count > 0)
            Log("MCP", $"Connected to {_mcpManager.Clients.Count} MCP server(s)");
    }

    /// <summary>MCP server status summary (exposed for UI).</summary>
    public string GetMcpStatus() => _mcpManager.GetStatusSummary();

    // ─── Token Budget Meta-Tools ────────────────────────────────────────

    [System.ComponentModel.Description("Get current token usage and cost estimate.")]
    private string GetBudgetStatusTool()
    {
        return _tokenBudget.GetSummary();
    }

    /// <summary>Token budget tracker (exposed for UI status bar).</summary>
    public TokenBudget TokenBudget => _tokenBudget;

    // ─── Memory Meta-Tools ──────────────────────────────────────────────

    [System.ComponentModel.Description("Save a persistent memory entry.")]
    private string RememberTool(
        [System.ComponentModel.Description("Content to remember")] string content,
        [System.ComponentModel.Description("Category: UserPreference, ProcessKnowledge, LearnedPattern, WorkflowRecipe, SafetyNote, ToolTip")] string category,
        [System.ComponentModel.Description("Process name this memory relates to (optional, null for global)")] string? processName = null)
    {
        if (_memorySystem is null) return "Memory system is disabled.";
        if (!Enum.TryParse<MemoryCategory>(category, true, out var cat))
            return $"Invalid category '{category}'. Valid: {string.Join(", ", Enum.GetNames<MemoryCategory>())}";
        return _memorySystem.Remember(content, cat, processName, source: "agent");
    }

    [System.ComponentModel.Description("Search persistent memories.")]
    private string RecallMemoryTool(
        [System.ComponentModel.Description("Search keyword (optional)")] string? query = null,
        [System.ComponentModel.Description("Filter by category (optional)")] string? category = null,
        [System.ComponentModel.Description("Filter by process name (optional)")] string? processName = null)
    {
        if (_memorySystem is null) return "Memory system is disabled.";
        MemoryCategory? cat = null;
        if (category is not null && Enum.TryParse<MemoryCategory>(category, true, out var parsed))
            cat = parsed;

        var results = _memorySystem.Recall(query, cat, processName);
        if (results.Count == 0) return "No matching memories found.";

        var lines = results.Select(e =>
            $"[{e.Id}] [{e.Category}] {(e.ProcessName is not null ? $"({e.ProcessName}) " : "")}{e.Content}");
        return $"Found {results.Count} memories:\n" + string.Join("\n", lines);
    }

    [System.ComponentModel.Description("Delete a persistent memory entry.")]
    private string ForgetMemoryTool(
        [System.ComponentModel.Description("Memory ID to delete (e.g. mem-20260401-120000-000)")] string id)
    {
        if (_memorySystem is null) return "Memory system is disabled.";
        return _memorySystem.Forget(id);
    }

    /// <summary>Memory system (exposed for UI management).</summary>
    public MemorySystem? Memory => _memorySystem;

    /// <summary>Prompt cache optimizer (exposed for UI cache hit status).</summary>
    public PromptCacheOptimizer PromptCacheOptimizer => _promptCacheOptimizer;

    // ─── Subagent Meta-Tools ────────────────────────────────────────────

    [System.ComponentModel.Description("Spawn a focused subagent for a subtask.")]
    private async Task<string> SpawnSubagentTool(
        [System.ComponentModel.Description("The task/prompt for the subagent")] string task,
        [System.ComponentModel.Description("Short description (shown in status)")] string? description = null,
        [System.ComponentModel.Description("Context from parent (optional)")] string? context = null,
        [System.ComponentModel.Description("Comma-separated tool name glob patterns to allow (optional, e.g. 'Read*,List*')")] string? allowed_tools = null,
        [System.ComponentModel.Description("Maximum turns (default 15)")] int? max_turns = null,
        [System.ComponentModel.Description("Use a preset configuration: explore, plan, verify, script")] string? preset = null)
    {
        if (preset is not null)
        {
            var presetRequest = preset.ToLowerInvariant() switch
            {
                "explore" => SubagentPresets.Explore(task, context),
                "plan" => SubagentPresets.Plan(task, context),
                "verify" => SubagentPresets.Verify(task, context),
                "script" => SubagentPresets.Script(task, context),
                _ => (SubagentRequest?)null,
            };
            if (presetRequest is not null)
            {
                // Override with any explicitly provided parameters
                if (max_turns.HasValue)
                    presetRequest = presetRequest with { MaxTurns = max_turns.Value };
                if (description is not null)
                    presetRequest = presetRequest with { Description = description };

                if (_subagentManager is null) return "SubagentManager not initialized. Start a new chat first.";
                var presetResults = await _subagentManager.SpawnAndWaitAsync(new[] { presetRequest }).ConfigureAwait(false);
                return presetResults[0].Text;
            }
            return $"Unknown preset: '{preset}'. Available: explore, plan, verify, script";
        }

        if (_subagentManager is null) return "Subagent system not initialized.";

        var patterns = allowed_tools?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var request = new SubagentRequest
        {
            Task = task,
            Description = description ?? task,
            Context = context,
            AllowedToolPatterns = patterns,
            MaxTurns = max_turns ?? 15,
            ContextProvider = _contextProvider,
        };

        var handle = _subagentManager.Spawn(request);
        Log("SUBAGENT", $"Spawned [{handle.Id}]: {description}");

        // Wait for the subagent to complete
        var result = await handle.Task.ConfigureAwait(false);
        return result.Success
            ? $"[Subagent completed: {result.ToolCallCount} tool calls, {result.Duration.TotalSeconds:F1}s]\n\n{result.Text}"
            : $"[Subagent failed: {result.Duration.TotalSeconds:F1}s]\n\n{result.Text}";
    }

    // ─── Plan Mode Meta-Tools ───────────────────────────────────────────

    [System.ComponentModel.Description("Generate a structured execution plan for a complex task.")]
    private async Task<string> PlanTaskTool(
        [System.ComponentModel.Description("The task to plan (describe what you want to accomplish)")] string task)
    {
        if (_planExecutor is null || _historyManager is null)
            return "Plan system not initialized.";

        var plan = await _planExecutor.GeneratePlanAsync(task, _historyManager, _contextProvider).ConfigureAwait(false);

        // Format the plan for display
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"## {plan.Title}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"*{plan.Summary}*");
        sb.AppendLine();

        for (int i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            var destructiveTag = step.IsDestructive ? " ⚠️ DESTRUCTIVE" : "";
            sb.AppendLine(CultureInfo.InvariantCulture, $"**Step {i + 1}**: {step.Description}{destructiveTag}");
            if (step.Details is not null)
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Details: {step.Details}");
            if (step.ExpectedTools is { Count: > 0 })
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Tools: {string.Join(", ", step.ExpectedTools)}");
            if (step.EstimatedDuration is not null)
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Duration: {step.EstimatedDuration}");
            sb.AppendLine();
        }

        if (plan.EstimatedToolCalls.HasValue)
            sb.AppendLine(CultureInfo.InvariantCulture, $"Estimated tool calls: ~{plan.EstimatedToolCalls}");
        if (plan.Warnings is { Count: > 0 })
        {
            sb.AppendLine("\n⚠️ **Warnings:**");
            foreach (var w in plan.Warnings)
                sb.AppendLine(CultureInfo.InvariantCulture, $"  • {w}");
        }

        sb.AppendLine("\n*To execute this plan, use execute_plan or describe the task directly.*");

        return sb.ToString();
    }

    [System.ComponentModel.Description("Generate and immediately execute a structured plan for a complex task.")]
    private async Task<string> ExecutePlanTool(
        [System.ComponentModel.Description("The task to plan and execute")] string task)
    {
        if (_planExecutor is null || _historyManager is null || _agentLoop is null)
            return "Plan system not initialized.";

        // Phase 1: Generate the plan
        var plan = await _planExecutor.GeneratePlanAsync(task, _historyManager, _contextProvider).ConfigureAwait(false);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"## Executing Plan: {plan.Title}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"*{plan.Summary}*");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Steps: {plan.Steps.Count}");
        sb.AppendLine();

        // Phase 2: Execute the plan step by step
        var reader = _planExecutor.ExecutePlanAsync(plan, _agentLoop, _historyManager, _contextProvider);

        await foreach (var evt in reader.ReadAllAsync())
        {
            switch (evt)
            {
                case AgentLoop.PlanProgressEvent.StepStarted ss:
                    sb.AppendLine(CultureInfo.InvariantCulture, $"--- Step {ss.StepIndex + 1}/{plan.Steps.Count}: {ss.Step.Description} ---");
                    break;
                case AgentLoop.PlanProgressEvent.StepCompleted sc:
                    var status = sc.Result.Success ? "OK" : "FAILED";
                    sb.AppendLine(CultureInfo.InvariantCulture, $"[Step {sc.StepIndex + 1} {status}: {sc.Result.ToolCallCount} tool calls]");
                    if (!string.IsNullOrWhiteSpace(sc.Result.Text))
                    {
                        // Include a summary of the step result (truncated)
                        var text = sc.Result.Text.Length > 500
                            ? sc.Result.Text[..500] + "..."
                            : sc.Result.Text;
                        sb.AppendLine(text);
                    }
                    sb.AppendLine();
                    break;
                case AgentLoop.PlanProgressEvent.PlanCompleted:
                    sb.AppendLine("**Plan execution complete.**");
                    break;
                case AgentLoop.PlanProgressEvent.PlanFailed pf:
                    sb.AppendLine(CultureInfo.InvariantCulture, $"**Plan execution failed:** {pf.Error}");
                    break;
                case AgentLoop.PlanProgressEvent.PlanCancelled:
                    sb.AppendLine("**Plan execution cancelled.**");
                    break;
            }
        }

        return sb.ToString();
    }

    public void ClearHistory()
    {
        _historyManager?.Clear();
        _displayHistory.Clear();
        _actionLog.Clear();
        _lastContextSuffix = null;
    }

    /// <summary>Schedule a debounced auto-save. Resets the 10-second timer on each call.</summary>
    private void ScheduleAutoSave()
    {
        _autoSaveTimer?.Dispose();
        _autoSaveTimer = new Timer(_ =>
        {
            try { SaveCurrentChat(); }
            catch { /* Swallow — best-effort crash protection */ }
        }, null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
    }

    /// <summary>Save the current chat to disk.</summary>
    public void SaveCurrentChat()
    {
        if (string.IsNullOrEmpty(CurrentChatId)) return;
        if (_displayHistory.Count == 0) return; // Don't persist empty chats

        // Auto-title from first user message if still "New Chat"
        if (CurrentChatTitle == "New Chat" && _displayHistory.Count > 0)
        {
            var first = _displayHistory.FirstOrDefault(m => m.Role == "user");
            if (first is not null)
            {
                CurrentChatTitle = first.Content.Length > 50
                    ? first.Content[..50] + "…"
                    : first.Content;
            }
        }

        AiChatStore.Save(new AiChatSession
        {
            Id = CurrentChatId,
            Title = CurrentChatTitle,
            Messages = _displayHistory.ToList(),
            PermissionMode = CurrentPermissionMode,
        });

        // Persist session metadata alongside the chat for cross-session search
        try
        {
            _sessionMetadata.Id = CurrentChatId;
            var metaJson = System.Text.Json.JsonSerializer.Serialize(_sessionMetadata, s_sessionMetaJsonOptions);
            var metaDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CEAISuite", "chats");
            if (!Directory.Exists(metaDir)) Directory.CreateDirectory(metaDir);
            File.WriteAllText(Path.Combine(metaDir, $"{CurrentChatId}.session.json"), metaJson);
        }
        catch (Exception ex) { Log("SESSION", $"Failed to save session metadata: {ex}"); }
    }

    /// <summary>Create a new chat, saving the current one first.</summary>
    public void NewChat()
    {
        if (!string.IsNullOrEmpty(CurrentChatId) && _displayHistory.Count > 0)
            SaveCurrentChat();

        // Fire session end hooks for the previous session
        if (_agentLoop?.Options.Hooks is { } endHooks && !string.IsNullOrEmpty(CurrentChatId))
        {
            var endCtx = new SessionLifecycleContext { SessionId = CurrentChatId, IsNewSession = false };
            _ = Task.Run(async () =>
            {
                try { await endHooks.RunSessionEndHooksAsync(endCtx, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { Log("HOOK", $"Session end hook error: {ex}"); }
            });
        }

        CurrentChatId = $"chat-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}";
        CurrentChatTitle = "New Chat";
        CurrentPermissionMode = _appSettings?.PermissionMode ?? "Normal";
        ClearHistory();
        _toolResultStore.Clear(); // Discard spilled results from previous chat
        _tokenBudget.Reset(); // Reset cost tracking for new session
        LoadCoreTools(); // Reset to core tools for fresh conversation

        // Apply the default permission mode from settings to the live engine
        SetPermissionMode(CurrentPermissionMode);

        // Fire session start hooks
        if (_agentLoop?.Options.Hooks is { } startHooks)
        {
            var startCtx = new SessionLifecycleContext { SessionId = CurrentChatId, IsNewSession = true };
            _ = Task.Run(async () =>
            {
                try { await startHooks.RunSessionStartHooksAsync(startCtx, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { Log("HOOK", $"Session start hook error: {ex}"); }
            });
        }

        ChatListChanged?.Invoke();
    }

    /// <summary>Switch to an existing chat by ID.</summary>
    public async Task SwitchChatAsync(string chatId)
    {
        try
        {
            if (chatId == CurrentChatId) return;

            // Save current
            if (!string.IsNullOrEmpty(CurrentChatId) && _displayHistory.Count > 0)
                SaveCurrentChat();

            var chatSession = AiChatStore.Load(chatId);
            if (chatSession is null) return;

            CurrentChatId = chatSession.Id;
            CurrentChatTitle = chatSession.Title;
            CurrentPermissionMode = chatSession.PermissionMode ?? "Normal";
            ClearHistory();

            // Restore display history
            _displayHistory.AddRange(chatSession.Messages);

            // Rebuild agent session history from saved messages
            _historyManager?.ReplayFromSaved(chatSession.Messages, Limits.MaxReplayMessages, Limits, _toolResultStore);

            // Inject a system-level orientation so the AI knows this is a resumed session
            if (_historyManager is not null && chatSession.Messages.Count > 0)
            {
                var userMsgCount = chatSession.Messages.Count(m => m.Role == "user");
                var lastTopics = chatSession.Messages
                    .Where(m => m.Role == "user")
                    .TakeLast(3)
                    .Select(m => m.Content.Length > 60 ? m.Content[..60] + "…" : m.Content);
                var resumeContext = $"[Session resumed] Chat: \"{chatSession.Title}\" " +
                    $"({userMsgCount} user messages). Recent topics: {string.Join(" | ", lastTopics)}";
                _historyManager.AddSystemMessage(resumeContext);
            }

            // Restore the chat's permission mode to the live engine
            SetPermissionMode(CurrentPermissionMode);

            ChatListChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log("CHAT", $"SwitchChat failed: {ex}");
        }
    }

    /// <summary>List all saved chats, most recent first.</summary>
    public static List<AiChatSession> ListChats() => AiChatStore.ListAll();

    /// <summary>Delete a chat by ID.</summary>
    public void DeleteChat(string chatId)
    {
        AiChatStore.Delete(chatId);
        if (chatId == CurrentChatId) NewChat();
        ChatListChanged?.Invoke();
    }

    /// <summary>Rename a chat.</summary>
    public void RenameChat(string chatId, string newTitle)
    {
        AiChatStore.Rename(chatId, newTitle);
        if (chatId == CurrentChatId) CurrentChatTitle = newTitle;
        ChatListChanged?.Invoke();
    }

    /// <summary>Export current chat as a Markdown string.</summary>
    public string ExportChatToMarkdown()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# {CurrentChatTitle}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"*Exported {DateTimeOffset.Now:yyyy-MM-dd HH:mm}*");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        foreach (var msg in _displayHistory)
        {
            var role = msg.Role == "user" ? "**You**" : "**AI Operator**";
            var time = msg.Timestamp.ToLocalTime().ToString("h:mm tt", CultureInfo.InvariantCulture);
            sb.AppendLine(CultureInfo.InvariantCulture, $"### {role} — {time}");
            sb.AppendLine();
            sb.AppendLine(msg.Content);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Build the dynamic context string to append to the user message.
    /// Returns null if context is unavailable or unchanged since last injection.
    /// </summary>
    private string? _lastContextSuffix;
    private string? BuildContextSuffix()
    {
        if (_contextProvider is null) return null;
        try
        {
            var ctx = _contextProvider();
            if (string.IsNullOrWhiteSpace(ctx)) return null;
            var suffix = $"[CURRENT STATE]\n{ctx}";
            // Skip if identical to the last injected context (avoid redundant tokens in history)
            if (string.Equals(suffix, _lastContextSuffix, StringComparison.Ordinal))
                return null;
            _lastContextSuffix = suffix;
            return suffix;
        }
        catch (Exception ex) { Log("WARN", $"Context provider failed: {ex.GetType().Name}: {ex}"); return null; }
    }

    private const string SystemPrompt = """
        You are the AI Operator for CE AI Suite — a Cheat Engine-class memory analysis and reverse-engineering tool.
        Expert in game hacking, memory analysis, x86/x64 assembly, and reverse engineering.
        You operate autonomously using tools. ACT first, report after — never narrate intentions.

        ═══ SAFETY (CRITICAL) ═══
        • NEVER write to code/text sections — only data sections.
        • ValidateScript before enabling. Analyze errors before retrying.
        • If "Process N is no longer running" → STOP all operations immediately.
        • Max 3 writes without verifying. Max 2 retries on errors. Prefer small precise changes.

        ═══ OPERATING RULES ═══
        • Be iterative and persistent — adjust approach on failure, don't give up.
        • Use tools proactively. Chain calls for complex tasks. Verify results before reporting.
        • When you can't verify (e.g. in-game effects), ask the user specifically:
          "Please fight a battle and tell me the EXP you received" — not "try it out".
        • Load skills silently. Your first response should contain TOOL CALLS.
        • If context was compacted, call request_tools("sessions") then SearchChatHistory("keyword").
        • After completing a sub-task (found an address, wrote a script, set breakpoints),
          briefly summarize what you accomplished and key findings. This helps context
          compaction preserve important data when conversation gets long.

        ═══ TOOLS (PROGRESSIVE) ═══
        Core tools are always loaded. For specialized ops, call request_tools(category).
        Use list_tool_categories to see what's available and loaded.
        Key categories: sessions (save/load/search), scripts, breakpoints, disassembly, hooks.

        ═══ ARTIFACT IDS ═══
        hook-* bp-* script-* addr-* group-* scan-* — use IdentifyArtifact(id) if unsure.

        ═══ SKILLS ═══
        Domain skills provide deep expertise. Load with load_skill before complex workflows:
        memory-scanning, code-analysis, breakpoint-mastery, script-engineering,
        pointer-resolution, data-mining, stealth-awareness, unity-il2cpp, unreal-engine.

        ═══ QUICK REFERENCE ═══
        Types: HP/MP→Float, Gold/Score→Int32, Coords→Float[3], Flags→Byte
        ASM: mov [rax+14],ebx=write4; nop=0x90; jmp near=E9+4B offset
        Unity: GameAssembly.dll+offset→pointer chain; ResolveSymbol for ASLR
        Unreal: GWorld/GNames/GObjects; UObject hierarchy; TArray=Data+Count+Max

        ═══ CONTEXT ═══
        [CURRENT STATE] is appended to each message with process info, address table, and scan state.
        Use it to avoid redundant calls. Addresses in hex (0x...). Be concise.
        """;

    // ─── Phase 5 Meta-Tools ───────────────────────────────────────────

    private string SwitchModelTool(string modelId)
    {
        if (_modelSwitcher is null) return "No model switching configured. Add FallbackModels to settings.";
        return _modelSwitcher.SwitchToModel(modelId)
            ? $"Switched to model: {modelId}"
            : $"Model '{modelId}' not found. Available: {string.Join(", ", _modelSwitcher.Models.Select(m => m.ModelId))}";
    }

    private string ScheduleTaskTool(string cron, string prompt, string description, bool oneShot = false)
    {
        if (_taskScheduler is null) return "Scheduler not available.";
        try
        {
            var id = _taskScheduler.AddTask(cron, prompt, description, oneShot);
            return $"Task scheduled: {id} ({description}) — cron: {cron}";
        }
        catch (ArgumentException ex) { return $"Invalid cron: {ex.Message}"; }
    }

    private string ListTasksTool() => _taskScheduler?.GetSummary() ?? "Scheduler not available.";

    private string CancelTaskTool(string id)
    {
        if (_taskScheduler is null) return "Scheduler not available.";
        return _taskScheduler.RemoveTask(id) ? $"Task {id} cancelled." : $"Task {id} not found.";
    }

    private void OnScheduledTaskDue(ScheduledTask task)
    {
        Log("SCHEDULER", $"Executing due task: {task.Id} ({task.Description})");
        // Fire-and-forget via subagent
        if (_subagentManager is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var results = await _subagentManager.SpawnAndWaitAsync(
                        [new SubagentRequest
                        {
                            Task = task.Prompt,
                            Description = $"[Scheduled] {task.Description}",
                            MaxTurns = 10,
                        }]).ConfigureAwait(false);
                    var text = results[0].Text ?? "(no output)"; Log("SCHEDULER", $"Task {task.Id} completed: {text[..Math.Min(200, text.Length)]}");
                }
                catch (Exception ex) { Log("SCHEDULER", $"Task {task.Id} failed: {ex}"); }
            });
        }
    }

    private string GetSessionInfoTool() => _sessionMetadata.GetSummary();

    private string SearchSessionsTool(string query)
    {
        if (_sessionIndex is null) return "Session index not available.";
        _sessionIndex.Rebuild();
        var results = _sessionIndex.Search(query, 5);
        if (results.Count == 0) return $"No sessions found matching '{query}'.";
        return string.Join("\n\n", results.Select(r => r.GetSummary()));
    }

    private async Task LoadPluginsAsync()
    {
        if (_pluginHost is null) return;
        try
        {
            var ctx = new PluginContext
            {
                Log = Log,
                Limits = Limits,
                ResultStore = _toolResultStore,
                StorageDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CEAISuite", "plugins"),
            };
            var count = await _pluginHost.LoadAllAsync(ctx).ConfigureAwait(false);
            if (count > 0)
            {
                foreach (var tool in _pluginHost.GetAllTools())
                {
                    _allToolsByName[tool.Name] = tool;
                    _tools.Add(tool); // Plugin tools always active
                }
                Log("PLUGIN", $"Loaded {count} tools from plugins");
            }
        }
        catch (Exception ex) { Log("PLUGIN", $"Plugin loading failed: {ex}"); }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _mcpManager.DisposeAsync().ConfigureAwait(false);
            if (_pluginHost is not null)
                await _pluginHost.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log("DISPOSE", $"DisposeAsync error (non-fatal): {ex}");
        }
        _taskScheduler?.Dispose();
    }

    public void Dispose()
    {
        try
        {
            // Fire-and-forget with timeout — DisposeAsync is the preferred path.
            // This fallback avoids the 5s hang when callers use IDisposable.
            _ = Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await _mcpManager.DisposeAsync().ConfigureAwait(false);
                    if (_pluginHost is not null)
                        await _pluginHost.DisposeAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
            });
        }
        catch (Exception ex)
        {
            Log("DISPOSE", $"Dispose error (non-fatal): {ex}");
        }
        _taskScheduler?.Dispose();
    }

    private sealed class StubChatClient : IChatClient
    {
#pragma warning disable CA1822 // Interface implementation cannot be static
        public ChatClientMetadata Metadata => new("stub");
#pragma warning restore CA1822

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant,
                "AI operator is not configured. Set your API key in Settings (or via the OPENAI_API_KEY / ANTHROPIC_API_KEY environment variable) and restart the application."));
            return Task.FromResult(response);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new TextContent("AI operator is not configured. Set your API key in Settings.")]
            };
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

}
