using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

#pragma warning disable MAAI001 // MAF compaction APIs are experimental in RC4
#pragma warning disable MEAI001 // M.E.AI approval APIs are experimental

namespace CEAISuite.Application;

public sealed record AiChatMessage(string Role, string Content, DateTimeOffset Timestamp)
{
    /// <summary>Tool calls made by the assistant in this message (null if none).</summary>
    public List<AiToolCallInfo>? ToolCalls { get; init; }
    /// <summary>Tool results returned during this message (null if none).</summary>
    public List<AiToolResultInfo>? ToolResults { get; init; }
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
}

/// <summary>
/// Names of tools that require user approval before execution.
/// These are wrapped with <see cref="ApprovalRequiredAIFunction"/> in MAF.
/// </summary>
internal static class DangerousTools
{
    public static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "WriteMemory",
        "SetBreakpoint",
        "InstallCodeCaveHook",
        "EnableScript",
        "ForceDetachAndCleanup",
        "ChangeMemoryProtection",
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
        "FreezeAddress", "UnfreezeAddress",
        // Context & sessions
        "GetCurrentContext", "SummarizeInvestigation",
        "SaveSession", "ListSessions", "LoadSession",
        "SearchChatHistory",
        // Spilled-result retrieval (always available)
        "RetrieveToolResult", "ListStoredResults",
        // Meta-tools (always available)
        "request_tools", "list_tool_categories", "unload_tools",
    };

    /// <summary>Category name → tool names. Agent calls request_tools(category) to load these.</summary>
    public static readonly Dictionary<string, string[]> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["memory_advanced"] = [
            "HexDump", "ListMemoryRegions", "DissectStructure",
            "ChangeMemoryProtection", "AllocateMemory", "FreeMemory", "QueryMemoryProtection" ],
        ["address_table"] = [
            "RenameAddressTableEntry", "SetEntryNotes", "GetAddressTableNode",
            "CreateAddressGroup", "MoveEntryToGroup", "FreezeAddressAtValue", "ToggleScript" ],
        ["scanning_advanced"] = [
            "ScanForPointers", "RescanPointerPath", "ValidatePointerPaths" ],
        ["breakpoints"] = [
            "SetBreakpoint", "RemoveBreakpoint", "ListBreakpoints",
            "GetBreakpointHitLog", "GetBreakpointHealth", "GetBreakpointModeCapabilities",
            "ProbeTargetRisk", "EmergencyRestorePageProtection", "ForceDetachAndCleanup" ],
        ["disassembly"] = [
            "Disassemble", "FindWritersToOffset", "FindFunctionBoundaries", "GetCallerGraph",
            "SearchInstructionPattern", "FindByMemoryOperand",
            "GetCallStack", "GetAllThreadStacks", "ResolveSymbol" ],
        ["hooks"] = [
            "InstallCodeCaveHook", "RemoveCodeCaveHook", "ListCodeCaveHooks",
            "GetCodeCaveHookHits", "DryRunHookInstall" ],
        ["scripts"] = [
            "ListScripts", "ViewScript", "ValidateScript", "ValidateScriptDeep",
            "EnableScript", "DisableScript", "EditScript", "CreateScriptEntry",
            "GenerateAutoAssemblerScript", "GenerateLuaScript", "GenerateTrainerScript" ],
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
        ["utility"] = [
            "IdentifyArtifact" ],
    };
}

public sealed class AiOperatorService
{
    private AIAgent _agent;
    private IChatClient _baseChatClient;
    private AgentSession _session = null!;
    private readonly List<AiChatMessage> _displayHistory = new();
    private readonly List<AiActionLogEntry> _actionLog = new();
    private Func<string>? _contextProvider;
    private readonly AiToolFunctions? _toolFunctions;
    private readonly AiChatStore _chatStore;
    private readonly ToolResultStore _toolResultStore;
    private readonly List<AITool> _tools;
    private readonly Dictionary<string, AITool> _allToolsByName;
    private readonly HashSet<string> _loadedCategories = new(StringComparer.OrdinalIgnoreCase);

    // Token usage tracking
    private long _totalPromptTokens;
    private long _totalCompletionTokens;
    private long _totalCachedTokens;
    private int _totalRequests;

    // Rate limiting
    private DateTimeOffset? _lastRequestTime;
    private readonly object _rateLimitLock = new();

    /// <summary>Cumulative input tokens sent across all requests in this session.</summary>
    public long TotalPromptTokens => _totalPromptTokens;
    /// <summary>Cumulative output tokens received across all requests in this session.</summary>
    public long TotalCompletionTokens => _totalCompletionTokens;
    /// <summary>Cumulative cached input tokens (prompt cache hits) across all requests.</summary>
    public long TotalCachedTokens => _totalCachedTokens;
    /// <summary>Total number of API requests made in this session.</summary>
    public int TotalRequests => _totalRequests;

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

    /// <summary>
    /// Raised when the agent wants to execute a dangerous tool and needs user approval.
    /// The handler should return true to approve, false to deny.
    /// </summary>
    public event Func<string, string, Task<bool>>? ApprovalRequested;

    public IReadOnlyList<AiChatMessage> DisplayHistory => _displayHistory;
    public IReadOnlyList<AiActionLogEntry> ActionLog => _actionLog;
    public bool IsConfigured { get; private set; }

    /// <summary>Number of messages in the MAF session history (post-compaction). Useful for monitoring.</summary>
    public int SessionMessageCount =>
        _session?.TryGetInMemoryChatHistory(out var h) == true ? h.Count : 0;

    /// <summary>Current chat session ID.</summary>
    public string CurrentChatId { get; private set; } = "";

    /// <summary>Current chat title.</summary>
    public string CurrentChatTitle { get; private set; } = "New Chat";

    public AiOperatorService(IChatClient? chatClient, AiToolFunctions toolFunctions, Func<string>? contextProvider = null, AiChatStore? chatStore = null)
    {
        IsConfigured = chatClient is not null;
        _contextProvider = contextProvider;
        _toolFunctions = toolFunctions;
        _chatStore = chatStore ?? new AiChatStore();
        _toolResultStore = toolFunctions.ToolResultStore;
        var baseClient = chatClient ?? new StubChatClient();
        _baseChatClient = baseClient;

        // Build AIFunction list from the tool functions instance using reflection.
        // Dangerous tools are wrapped with ApprovalRequiredAIFunction.
        var methods = typeof(AiToolFunctions).GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        var allTools = methods
            .Where(m => !m.IsSpecialName)
            .Select(m => AIFunctionFactory.Create(m, toolFunctions))
            .Select(fn => DangerousTools.Names.Contains(fn.Name)
                ? (AITool)new ApprovalRequiredAIFunction(fn)
                : fn)
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

        // Start with only core tools — agent requests more via request_tools()
        _tools = [];
        LoadCoreTools();

        Log("INFO", $"Progressive tools: {_tools.Count} core loaded, {_allToolsByName.Count} total available");

        _agent = BuildAgent(baseClient);

        // Start with a fresh chat session
        NewChat();

        // Ensure log directory exists
        try { Directory.CreateDirectory(LogDir); } catch { /* best effort */ }
    }

    private AIAgent BuildAgent(IChatClient client)
    {
        Log("DEBUG", $"BuildAgent: tool count = {_tools.Count}, " +
            $"tool names = [{string.Join(", ", _tools.Take(5).Select(t => t is AIFunction f ? f.Name : t.GetType().Name))}...]");

        // Build the MAF compaction pipeline (gentle → aggressive):
        // 1. Collapse old tool-call groups into summaries (cheap, no API call)
        // 2. LLM-powered summarization when context gets large (costs an API call!)
        // 3. Sliding window: keep most recent N user turns (cheap)
        // 4. Emergency truncation backstop (cheap)
        // Budget: Copilot model limit = 128K. Tool defs ~10K + system prompt ~3K = ~13K overhead.
        // Leave ~15K for the response → message budget ≈ 100K. Triggers set conservatively.
        var compactionPipeline = new PipelineCompactionStrategy(
            new ToolResultCompactionStrategy(CompactionTriggers.MessagesExceed(12)),
            new SummarizationCompactionStrategy(client, CompactionTriggers.TokensExceed(32_000)),
            new SlidingWindowCompactionStrategy(CompactionTriggers.TurnsExceed(15)),
            new TruncationCompactionStrategy(CompactionTriggers.TokensExceed(64_000)));

        // Discover agent skills from the skills/ directory.
        // Progressive disclosure: only name+description are advertised per skill (~100 tokens each),
        // full instructions loaded on-demand via load_skill tool (<5K tokens per skill).
        var contextProviders = new List<AIContextProvider>
        {
            new CompactionProvider(compactionPipeline),
        };

        // Only one FileAgentSkillsProvider allowed — it registers load_skill and
        // read_skill_resource tools, and duplicate tool names cause Copilot API 400s.
        // Prefer user skills dir (overrides), fall back to built-in.
        foreach (var skillsDir in ResolveSkillsPaths().Reverse())
        {
            if (Directory.Exists(skillsDir))
            {
                try
                {
                    contextProviders.Add(new FileAgentSkillsProvider(skillsDir));
                    Log("INFO", $"Loaded skills from: {skillsDir}");
                    break;
                }
                catch (Exception ex)
                {
                    Log("WARN", $"Failed to load skills from {skillsDir}: {ex.Message}");
                }
            }
        }

        // Build the MAF agent with compaction, skills, tools, and system prompt.
        // UseFunctionInvocation() inserts FunctionInvokingChatClient — required for
        // the agent to actually execute tool calls returned by the model.
        // ToolResultCappingChatClient sits between function invocation and the LLM,
        // truncating any tool result that exceeds MaxToolResultChars before it
        // consumes API tokens. This is the universal safety net.
        return client
            .AsBuilder()
            .UseFunctionInvocation(configure: client => client.IncludeDetailedErrors = true)
            .Use(inner => new ToolResultCappingChatClient(inner, Limits, _toolResultStore))
            .UseAIContextProviders(contextProviders.ToArray())
            .BuildAIAgent(new ChatClientAgentOptions
            {
                Name = "CEAIOperator",
                ChatHistoryProvider = new InMemoryChatHistoryProvider(),
                ChatOptions = new ChatOptions
                {
                    Instructions = SystemPrompt,
                    Tools = _tools,
                    Temperature = 0.3f,
                    MaxOutputTokens = Limits.MaxOutputTokens,
                },
            });
    }

    /// <summary>
    /// Returns skill directory paths in priority order: built-in (ships with app) and user-defined.
    /// </summary>
    private static IEnumerable<string> ResolveSkillsPaths()
    {
        // Built-in skills shipped alongside the application binary
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        yield return Path.Combine(appDir, "skills");

        // User-defined skills in the app data directory
        var userSkills = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CEAISuite", "skills");
        yield return userSkills;
    }

    /// <summary>
    /// Hot-swap the AI provider without restarting the app.
    /// Preserves display history and action log; creates a new MAF agent session.
    /// </summary>
    /// <summary>Set the dynamic context provider delegate (for late-binding in DI scenarios).</summary>
    public void SetContextProvider(Func<string> provider) => _contextProvider = provider;

    public void Reconfigure(IChatClient? newClient)
    {
        var baseClient = newClient ?? new StubChatClient();
        _baseChatClient = baseClient;
        _agent = BuildAgent(baseClient);
        IsConfigured = newClient is not null;

        // Save current chat, then start a fresh session with the new agent
        if (!string.IsNullOrEmpty(CurrentChatId) && _displayHistory.Count > 0)
            SaveCurrentChat();

        _session = _agent.CreateSessionAsync().GetAwaiter().GetResult();

        // Replay existing display history into the new agent session
        if (_displayHistory.Count > 0 && _session.TryGetInMemoryChatHistory(out var history))
            ReplayHistoryInto(history, _displayHistory);

        Log("INFO", $"Reconfigured AI provider (IsConfigured={IsConfigured})");
        StatusChanged?.Invoke(IsConfigured ? "Ready (provider updated)" : "Not configured");
    }

    private void Log(string level, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}";
        try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { /* best effort */ }
    }

    private void UpdateStatus(string status)
    {
        Log("INFO", status);
        StatusChanged?.Invoke(status);
    }

    public async Task<string> SendMessageAsync(string userMessage, CancellationToken cancellationToken = default)
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
                        await Task.Delay(remaining, cancellationToken);
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"Rate limited: please wait {(cooldown - elapsed).TotalSeconds:F0}s before sending another request.");
                    }
                }
            }

            lock (_rateLimitLock) { _lastRequestTime = DateTimeOffset.UtcNow; }
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Log("INFO", $"User: {userMessage}");
        // Note: caller should call AddUserMessageToHistory() before this method
        // so the UI shows the user message immediately.

        // Append dynamic context to the user message (keeps system prompt prefix stable for cache hits)
        var contextSuffix = BuildContextSuffix();
        var fullUserMessage = contextSuffix is not null
            ? $"{userMessage}\n\n{contextSuffix}"
            : userMessage;

        try
        {
            UpdateStatus("Thinking...");

            // Run the agent using MAF's structured agent loop.
            // This handles tool invocation, compaction, and history automatically.
            var response = await _agent.RunAsync(fullUserMessage, _session, cancellationToken: cancellationToken);

            TrackUsage(response);

            // Extract tool calls and final text from all response messages
            var assistantText = "";
            int toolCallCount = 0;
            var toolCalls = new List<AiToolCallInfo>();
            var toolResults = new List<AiToolResultInfo>();
            foreach (var message in response.Messages)
            {
                foreach (var content in message.Contents)
                {
                    if (content is TextContent textContent && !string.IsNullOrWhiteSpace(textContent.Text))
                    {
                        assistantText = textContent.Text;
                    }
                    else if (content is FunctionCallContent functionCall)
                    {
                        toolCallCount++;
                        var argsStr = functionCall.Arguments is not null
                            ? string.Join(", ", functionCall.Arguments.Select(kv => $"{kv.Key}={kv.Value}"))
                            : "";
                        var argsJson = functionCall.Arguments is not null
                            ? JsonSerializer.Serialize(functionCall.Arguments)
                            : null;
                        UpdateStatus($"Tool: {functionCall.Name ?? "unknown"} ({toolCallCount} calls, {sw.Elapsed.TotalSeconds:F0}s)");
                        Log("TOOL", $"Call: {functionCall.Name}({argsStr})");
                        _actionLog.Add(new AiActionLogEntry(
                            functionCall.Name ?? "unknown",
                            argsStr,
                            "invoked",
                            DateTimeOffset.UtcNow));
                        toolCalls.Add(new AiToolCallInfo(
                            functionCall.CallId ?? "", functionCall.Name ?? "unknown", argsJson));
                    }
                    else if (content is FunctionResultContent functionResult)
                    {
                        var resultStr = functionResult.Result?.ToString() ?? "";
                        var truncated = resultStr.Length > 200 ? resultStr[..200] + "..." : resultStr;
                        Log("TOOL", $"Result: {truncated}");
                        for (int i = _actionLog.Count - 1; i >= 0; i--)
                        {
                            if (_actionLog[i].Result == "invoked")
                            {
                                _actionLog[i] = _actionLog[i] with { Result = truncated };
                                break;
                            }
                        }
                        toolResults.Add(new AiToolResultInfo(
                            functionResult.CallId ?? "", LookupToolName(toolCalls, functionResult.CallId), resultStr));
                    }
                    else if (content is FunctionApprovalRequestContent approvalRequest)
                    {
                        // Handle dangerous tool approval via MAF's approval flow
                        var approved = await HandleApprovalRequestAsync(approvalRequest);
                        if (approved)
                        {
                            // Re-run agent with approval response
                            var approvalMsg = new ChatMessage(ChatRole.User, [approvalRequest.CreateResponse(true)]);
                            response = await _agent.RunAsync([approvalMsg], _session, cancellationToken: cancellationToken);
                            TrackUsage(response);

                            // Process additional response messages
                            foreach (var msg in response.Messages)
                            {
                                foreach (var c in msg.Contents)
                                {
                                    if (c is TextContent tc && !string.IsNullOrWhiteSpace(tc.Text))
                                        assistantText = tc.Text;
                                    else if (c is FunctionCallContent fc2)
                                    {
                                        toolCallCount++;
                                        var a = fc2.Arguments is not null
                                            ? string.Join(", ", fc2.Arguments.Select(kv => $"{kv.Key}={kv.Value}"))
                                            : "";
                                        var aj = fc2.Arguments is not null
                                            ? JsonSerializer.Serialize(fc2.Arguments) : null;
                                        UpdateStatus($"Tool: {fc2.Name ?? "unknown"} ({toolCallCount} calls, {sw.Elapsed.TotalSeconds:F0}s)");
                                        Log("TOOL", $"Call: {fc2.Name}({a})");
                                        _actionLog.Add(new AiActionLogEntry(fc2.Name ?? "unknown", a, "invoked", DateTimeOffset.UtcNow));
                                        toolCalls.Add(new AiToolCallInfo(
                                            fc2.CallId ?? "", fc2.Name ?? "unknown", aj));
                                    }
                                    else if (c is FunctionResultContent fr2)
                                    {
                                        var r = fr2.Result?.ToString() ?? "";
                                        var t = r.Length > 200 ? r[..200] + "..." : r;
                                        Log("TOOL", $"Result: {t}");
                                        for (int i = _actionLog.Count - 1; i >= 0; i--)
                                        {
                                            if (_actionLog[i].Result == "invoked") { _actionLog[i] = _actionLog[i] with { Result = t }; break; }
                                        }
                                        toolResults.Add(new AiToolResultInfo(
                                            fr2.CallId ?? "", LookupToolName(toolCalls, fr2.CallId), r));
                                    }
                                }
                            }
                        }
                        else
                        {
                            var denialMsg = new ChatMessage(ChatRole.User, [approvalRequest.CreateResponse(false)]);
                            response = await _agent.RunAsync([denialMsg], _session, cancellationToken: cancellationToken);
                            TrackUsage(response);
                            foreach (var msg in response.Messages)
                                foreach (var c in msg.Contents)
                                    if (c is TextContent tc && !string.IsNullOrWhiteSpace(tc.Text))
                                        assistantText = tc.Text;
                        }
                    }
                }
            }

            // Inject any pending screenshots as image content for future turns (max 3 per turn)
            if (_toolFunctions is not null)
            {
                int imagesProcessed = 0;
                int maxImagesPerTurn = Limits.MaxImagesPerTurn;
                while (imagesProcessed < maxImagesPerTurn && _toolFunctions.PendingImages.TryDequeue(out var img))
                {
                    imagesProcessed++;
                    UpdateStatus($"Analyzing screenshot ({imagesProcessed})...");
                    var imageMsg = new ChatMessage(ChatRole.User, new List<AIContent>
                    {
                        new TextContent($"[Screenshot: {img.Description}]"),
                        new DataContent(img.PngData, "image/png")
                    });

                    var imageResponse = await _agent.RunAsync([imageMsg], _session, cancellationToken: cancellationToken);
                    TrackUsage(imageResponse);

                    foreach (var msg in imageResponse.Messages)
                        foreach (var c in msg.Contents)
                            if (c is TextContent tc && !string.IsNullOrWhiteSpace(tc.Text))
                                assistantText += "\n" + tc.Text;
                }
                // Drain any excess images to prevent stale accumulation
                while (_toolFunctions.PendingImages.TryDequeue(out _))
                    Log("WARN", $"Discarded excess pending image (max {maxImagesPerTurn} per turn)");
            }

            if (string.IsNullOrWhiteSpace(assistantText))
            {
                assistantText = "(Tool calls executed — see action log for details)";
            }

            sw.Stop();
            var usagePart = _totalRequests > 0
                ? $", tokens: {_totalPromptTokens}↑ {_totalCompletionTokens}↓ {_totalCachedTokens}⚡"
                : "";
            var historyPart = $", {SessionMessageCount} msgs in context";
            var summary = toolCallCount > 0
                ? $"Done ({toolCallCount} tool calls, {sw.Elapsed.TotalSeconds:F1}s{usagePart}{historyPart})"
                : $"Done ({sw.Elapsed.TotalSeconds:F1}s{usagePart}{historyPart})";
            UpdateStatus(summary);
            Log("INFO", $"Assistant: {(assistantText.Length > 300 ? assistantText[..300] + "..." : assistantText)}");
            _displayHistory.Add(new AiChatMessage("assistant", assistantText, DateTimeOffset.UtcNow)
            {
                ToolCalls = toolCalls.Count > 0 ? toolCalls : null,
                ToolResults = toolResults.Count > 0 ? toolResults : null
            });
            SaveCurrentChat();
            return assistantText;
        }
        catch (Exception ex)
        {
            sw.Stop();
            // Try to extract response body for better diagnostics (ClientResultException from OpenAI SDK)
            var detail = "";
            try
            {
                var rawMethod = ex.GetType().GetMethod("GetRawResponse");
                if (rawMethod is not null)
                {
                    var raw = rawMethod.Invoke(ex, null);
                    var contentProp = raw?.GetType().GetProperty("Content");
                    if (contentProp is not null)
                    {
                        var body = contentProp.GetValue(raw)?.ToString();
                        if (!string.IsNullOrEmpty(body))
                            detail = $" | Response: {body}";
                    }
                }
            }
            catch { /* best effort */ }
            var errorMessage = $"AI error: {ex.Message}";
            Log("ERROR", $"{ex.GetType().Name}: {ex.Message}{detail}\n{ex.StackTrace}");
            UpdateStatus($"Error ({sw.Elapsed.TotalSeconds:F1}s)");
            _displayHistory.Add(new AiChatMessage("assistant", errorMessage, DateTimeOffset.UtcNow));
            return errorMessage;
        }
    }

    /// <summary>
    /// Adds a user message to display history. Call this from the UI BEFORE
    /// calling SendMessageAsync/SendMessageStreamingAsync so the message
    /// appears in the chat immediately.
    /// </summary>
    public void AddUserMessageToHistory(string userMessage)
    {
        _displayHistory.Add(new AiChatMessage("user", userMessage, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Streaming version of SendMessageAsync. Returns a ChannelReader that yields
    /// AgentStreamEvents as they arrive — text deltas, tool status, completion.
    /// The UI can consume these to update the chat in real-time.
    ///
    /// Approval flow: When a dangerous tool needs approval, the stream emits an
    /// <see cref="AgentStreamEvent.ApprovalRequested"/> event with a TaskCompletionSource.
    /// The UI should present Allow/Deny buttons and call Resolve(true/false).
    /// The service then re-runs the agent with the approval response (MAF multi-turn pattern).
    /// </summary>
    public ChannelReader<AgentStreamEvent> SendMessageStreamingAsync(
        string userMessage, CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<AgentStreamEvent>();

        _ = Task.Run(async () =>
        {
            // Rate limiting (mirrors non-streaming path)
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
                            await Task.Delay(remaining, cancellationToken);
                        }
                        else
                        {
                            await channel.Writer.WriteAsync(
                                new AgentStreamEvent.Error($"Rate limited: please wait {(cooldown - elapsed).TotalSeconds:F0}s"), cancellationToken);
                            channel.Writer.Complete();
                            return;
                        }
                    }
                }

                lock (_rateLimitLock) { _lastRequestTime = DateTimeOffset.UtcNow; }
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            Log("INFO", $"User (streaming): {userMessage}");

            var contextSuffix = BuildContextSuffix();
            var fullUserMessage = contextSuffix is not null
                ? $"{userMessage}\n\n{contextSuffix}"
                : userMessage;

            var state = new StreamState();
            var toolCalls = new List<AiToolCallInfo>();
            var toolResults = new List<AiToolResultInfo>();

            try
            {
                UpdateStatus("Thinking...");

                // --- First run: user message ---
                var pendingApprovals = new List<FunctionApprovalRequestContent>();

                await foreach (var update in _agent.RunStreamingAsync(
                    fullUserMessage, _session, cancellationToken: cancellationToken))
                {
                    await ProcessStreamUpdate(update, channel, sw, state,
                        toolCalls, toolResults, pendingApprovals, cancellationToken);
                }

                // --- Multi-turn approval loop (MAF pattern) ---
                // RunStreamingAsync returns EARLY when it hits approval-required tools.
                // We must collect approvals, get user decision, then re-run with the response.
                int maxApprovalRounds = Limits.MaxApprovalRounds;
                int approvalRound = 0;
                while (pendingApprovals.Count > 0 && approvalRound++ < maxApprovalRounds)
                {
                    var approvalResponses = new List<AIContent>();

                    foreach (var approvalRequest in pendingApprovals)
                    {
                        var toolName = approvalRequest.FunctionCall.Name ?? "unknown";
                        var argsStr = approvalRequest.FunctionCall.Arguments is not null
                            ? string.Join(", ", approvalRequest.FunctionCall.Arguments.Select(kv => $"{kv.Key}={kv.Value}"))
                            : "";

                        Log("APPROVAL", $"Requesting approval for {toolName}({argsStr})");

                        // Emit interactive event — UI will show Allow/Deny and resolve the TCS
                        var approvalEvent = new AgentStreamEvent.ApprovalRequested(toolName, argsStr);
                        await channel.Writer.WriteAsync(approvalEvent, cancellationToken);

                        // Wait for the user's decision (blocks until UI calls Resolve)
                        bool approved;
                        try
                        {
                            approved = await approvalEvent.UserDecision.WaitAsync(
                                TimeSpan.FromMinutes(5), cancellationToken);
                        }
                        catch (TimeoutException)
                        {
                            Log("APPROVAL", $"Timed out waiting for approval of {toolName} — auto-denying");
                            approved = false;
                        }

                        Log("APPROVAL", $"{toolName}: {(approved ? "APPROVED" : "DENIED")} by user");
                        approvalResponses.Add(approvalRequest.CreateResponse(approved));
                    }

                    pendingApprovals.Clear();

                    // Re-run agent with all approval responses
                    var approvalMsg = new ChatMessage(ChatRole.User, approvalResponses);
                    UpdateStatus("Executing approved tools...");

                    await foreach (var update in _agent.RunStreamingAsync(
                        [approvalMsg], _session, cancellationToken: cancellationToken))
                    {
                        await ProcessStreamUpdate(update, channel, sw, state,
                            toolCalls, toolResults, pendingApprovals, cancellationToken);
                    }
                    // Loop again if the re-run triggered MORE approval requests
                }

                if (approvalRound >= maxApprovalRounds && pendingApprovals.Count > 0)
                {
                    Log("APPROVAL", $"Hit max approval rounds ({maxApprovalRounds}), aborting remaining approvals");
                    state.AssistantText += "\n\n⚠️ Reached maximum approval rounds — some tool calls were skipped.";
                    pendingApprovals.Clear();
                }

                // Inject any pending screenshots as image content for the model to analyze
                if (_toolFunctions is not null)
                {
                    int imagesProcessed = 0;
                    int maxImagesPerTurn = Limits.MaxImagesPerTurn;
                    while (imagesProcessed < maxImagesPerTurn && _toolFunctions.PendingImages.TryDequeue(out var img))
                    {
                        imagesProcessed++;
                        UpdateStatus($"Analyzing screenshot ({imagesProcessed})...");
                        var imageMsg = new ChatMessage(ChatRole.User, new List<AIContent>
                        {
                            new TextContent($"[Screenshot: {img.Description}]"),
                            new DataContent(img.PngData, "image/png")
                        });

                        // Stream the image analysis response
                        await foreach (var update in _agent.RunStreamingAsync(
                            [imageMsg], _session, cancellationToken: cancellationToken))
                        {
                            await ProcessStreamUpdate(update, channel, sw, state,
                                toolCalls, toolResults, pendingApprovals, cancellationToken);
                        }
                    }
                    while (_toolFunctions.PendingImages.TryDequeue(out _))
                        Log("WARN", $"Discarded excess pending image (max {maxImagesPerTurn} per turn)");
                }

                sw.Stop();
                if (string.IsNullOrWhiteSpace(state.AssistantText))
                    state.AssistantText = "(Tool calls executed — see action log for details)";

                Log("INFO", $"Assistant: {(state.AssistantText.Length > 300 ? state.AssistantText[..300] + "..." : state.AssistantText)}");
                _displayHistory.Add(new AiChatMessage("assistant", state.AssistantText, DateTimeOffset.UtcNow)
                {
                    ToolCalls = toolCalls.Count > 0 ? toolCalls : null,
                    ToolResults = toolResults.Count > 0 ? toolResults : null
                });
                SaveCurrentChat();

                var usagePart = _totalRequests > 0
                    ? $", tokens: {_totalPromptTokens}↑ {_totalCompletionTokens}↓ {_totalCachedTokens}⚡"
                    : "";
                var historyPart = $", {SessionMessageCount} msgs in context";
                var summary = state.ToolCallCount > 0
                    ? $"Done ({state.ToolCallCount} tool calls, {sw.Elapsed.TotalSeconds:F1}s{usagePart}{historyPart})"
                    : $"Done ({sw.Elapsed.TotalSeconds:F1}s{usagePart}{historyPart})";
                UpdateStatus(summary);

                await channel.Writer.WriteAsync(
                    new AgentStreamEvent.Completed(state.ToolCallCount, sw.Elapsed), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                var cancelMsg = "⏹ Stopped by user.";
                Log("INFO", "Streaming cancelled by user");
                UpdateStatus($"Stopped ({sw.Elapsed.TotalSeconds:F1}s)");
                _displayHistory.Add(new AiChatMessage("assistant",
                    state.AssistantText.Length > 0 ? state.AssistantText + "\n\n" + cancelMsg : cancelMsg,
                    DateTimeOffset.UtcNow)
                {
                    ToolCalls = toolCalls.Count > 0 ? toolCalls : null,
                    ToolResults = toolResults.Count > 0 ? toolResults : null
                });
                SaveCurrentChat();
                await channel.Writer.WriteAsync(
                    new AgentStreamEvent.Error(cancelMsg), CancellationToken.None);
            }
            catch (Exception ex)
            {
                sw.Stop();
                // Try to extract response body for better diagnostics (ClientResultException from OpenAI SDK)
                var detail = "";
                try
                {
                    var rawMethod = ex.GetType().GetMethod("GetRawResponse");
                    if (rawMethod is not null)
                    {
                        var raw = rawMethod.Invoke(ex, null);
                        var contentProp = raw?.GetType().GetProperty("Content");
                        if (contentProp is not null)
                        {
                            var body = contentProp.GetValue(raw)?.ToString();
                            if (!string.IsNullOrEmpty(body))
                                detail = $" | Response: {body}";
                        }
                    }
                }
                catch { /* best effort */ }
                var errorMsg = $"AI error: {ex.Message}";
                Log("ERROR", $"{ex.GetType().Name}: {ex.Message}{detail}\n{ex.StackTrace}");
                UpdateStatus($"Error ({sw.Elapsed.TotalSeconds:F1}s)");
                _displayHistory.Add(new AiChatMessage("assistant", errorMsg, DateTimeOffset.UtcNow));
                await channel.Writer.WriteAsync(
                    new AgentStreamEvent.Error(errorMsg), CancellationToken.None);
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, cancellationToken);

        return channel.Reader;
    }

    /// <summary>Mutable holder for streaming state (avoids ref params in async methods).</summary>
    private sealed class StreamState
    {
        public int ToolCallCount;
        public string AssistantText = "";
    }

    /// <summary>
    /// Process a single streaming update from RunStreamingAsync — extracts text, tool calls,
    /// tool results, and approval requests into the appropriate collections.
    /// </summary>
    private async Task ProcessStreamUpdate(
        AgentResponseUpdate update,
        Channel<AgentStreamEvent> channel,
        System.Diagnostics.Stopwatch sw,
        StreamState state,
        List<AiToolCallInfo> toolCalls,
        List<AiToolResultInfo> toolResults,
        List<FunctionApprovalRequestContent> pendingApprovals,
        CancellationToken cancellationToken)
    {
        foreach (var content in update.Contents)
        {
            if (content is TextContent tc && !string.IsNullOrEmpty(tc.Text))
            {
                state.AssistantText += tc.Text;
                await channel.Writer.WriteAsync(
                    new AgentStreamEvent.TextDelta(tc.Text), cancellationToken);
            }
            else if (content is FunctionCallContent fc)
            {
                state.ToolCallCount++;
                var args = fc.Arguments is not null
                    ? string.Join(", ", fc.Arguments.Select(kv => $"{kv.Key}={kv.Value}"))
                    : "";
                var argsJson = fc.Arguments is not null
                    ? JsonSerializer.Serialize(fc.Arguments) : null;
                UpdateStatus($"Tool: {fc.Name ?? "unknown"} ({state.ToolCallCount} calls, {sw.Elapsed.TotalSeconds:F0}s)");
                Log("TOOL", $"Call: {fc.Name}({args})");
                _actionLog.Add(new AiActionLogEntry(fc.Name ?? "unknown", args, "invoked", DateTimeOffset.UtcNow));
                toolCalls.Add(new AiToolCallInfo(
                    fc.CallId ?? "", fc.Name ?? "unknown", argsJson));
                await channel.Writer.WriteAsync(
                    new AgentStreamEvent.ToolCallStarted(fc.Name ?? "unknown", args), cancellationToken);
            }
            else if (content is FunctionResultContent fr)
            {
                var resultStr = fr.Result?.ToString() ?? "";
                var truncated = resultStr.Length > 200 ? resultStr[..200] + "..." : resultStr;
                Log("TOOL", $"Result: {truncated}");
                for (int i = _actionLog.Count - 1; i >= 0; i--)
                {
                    if (_actionLog[i].Result == "invoked")
                    {
                        _actionLog[i] = _actionLog[i] with { Result = truncated };
                        break;
                    }
                }
                toolResults.Add(new AiToolResultInfo(
                    fr.CallId ?? "", LookupToolName(toolCalls, fr.CallId), resultStr));
                await channel.Writer.WriteAsync(
                    new AgentStreamEvent.ToolCallCompleted(fr.CallId ?? "unknown", truncated), cancellationToken);
            }
            else if (content is FunctionApprovalRequestContent approvalRequest)
            {
                // Collect — don't process inline. MAF expects the stream to complete first,
                // then we re-run with approval responses (multi-turn pattern).
                pendingApprovals.Add(approvalRequest);
            }
            else if (content is UsageContent usage)
            {
                _totalPromptTokens += usage.Details?.InputTokenCount ?? 0;
                _totalCompletionTokens += usage.Details?.OutputTokenCount ?? 0;
                _totalCachedTokens += usage.Details?.AdditionalCounts?.GetValueOrDefault("CachedInputTokenCount") ?? 0;
                _totalRequests++;
                Log("USAGE", $"Streaming cumulative prompt: {_totalPromptTokens}, completion: {_totalCompletionTokens}, cached: {_totalCachedTokens}, requests: {_totalRequests}");
            }
        }
    }

    /// <summary>Handle an approval request for a dangerous tool call.</summary>
    private async Task<bool> HandleApprovalRequestAsync(FunctionApprovalRequestContent request)
    {
        var toolName = request.FunctionCall.Name ?? "unknown";
        var argsStr = request.FunctionCall.Arguments is not null
            ? string.Join(", ", request.FunctionCall.Arguments.Select(kv => $"{kv.Key}={kv.Value}"))
            : "";

        Log("APPROVAL", $"Approval requested for {toolName}({argsStr})");

        if (ApprovalRequested is not null)
        {
            return await ApprovalRequested(toolName, argsStr);
        }

        // Default: auto-approve if no handler is registered (preserves existing behavior)
        Log("APPROVAL", $"Auto-approved {toolName} (no approval handler registered)");
        return true;
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
        [System.ComponentModel.Description("Category to load (e.g. breakpoints, disassembly, scripts, hooks, snapshots, memory_advanced, scanning_advanced, address_table, safety, signatures, hotkeys, undo, transactions, cheat_tables, vision, utility). Comma-separated for multiple.")] string categories)
    {
        var loaded = new List<string>();
        var alreadyLoaded = new List<string>();
        var activeNames = new HashSet<string>(_tools.Select(t => t is AIFunction f ? f.Name : ""),
            StringComparer.OrdinalIgnoreCase);

        foreach (var cat in categories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (_loadedCategories.Contains(cat))
            {
                alreadyLoaded.Add(cat);
                continue;
            }

            if (!ToolCategories.Categories.TryGetValue(cat, out var toolNames))
                continue;

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
        }

        Log("TOOLS", $"Loaded categories: [{categories}] → {loaded.Count} new tools " +
            $"(active: {_tools.Count}, already loaded: [{string.Join(", ", alreadyLoaded)}])");

        if (loaded.Count == 0 && alreadyLoaded.Count > 0)
            return $"Categories already loaded: {string.Join(", ", alreadyLoaded)}. No new tools added.";
        if (loaded.Count == 0)
            return $"No matching categories found. Available: {string.Join(", ", ToolCategories.Categories.Keys)}";

        return $"Loaded {loaded.Count} tools: {string.Join(", ", loaded)}. Active tool count: {_tools.Count}.";
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
    /// Replay saved chat messages into an agent session's history with full fidelity.
    /// Reconstructs FunctionCallContent/FunctionResultContent from stored metadata
    /// so the agent remembers which tools it called and what they returned.
    /// Limits replay to the most recent messages and truncates large tool results
    /// to avoid blowing the token budget on chat restore.
    /// </summary>
    private void ReplayHistoryInto(IList<ChatMessage> history, IEnumerable<AiChatMessage> messages)
    {
        int maxReplayMessages = Limits.MaxReplayMessages;
        int maxToolResultChars = Limits.MaxToolResultChars;

        // Take only the most recent messages to avoid token explosion on restore
        var messageList = messages as IList<AiChatMessage> ?? messages.ToList();
        var replayMessages = messageList.Count > maxReplayMessages
            ? messageList.Skip(messageList.Count - maxReplayMessages)
            : messageList;

        foreach (var msg in replayMessages)
        {
            var role = msg.Role == "user" ? ChatRole.User : ChatRole.Assistant;

            // Simple message (no tool data) — plain text replay
            if (msg.ToolCalls is null or { Count: 0 } && msg.ToolResults is null or { Count: 0 })
            {
                history.Add(new ChatMessage(role, msg.Content));
                continue;
            }

            // Assistant message with tool calls — reconstruct structured content
            var contents = new List<AIContent>();
            if (!string.IsNullOrEmpty(msg.Content))
                contents.Add(new TextContent(msg.Content));

            if (msg.ToolCalls is not null)
            {
                foreach (var tc in msg.ToolCalls)
                {
                    IDictionary<string, object?>? args = null;
                    if (tc.ArgumentsJson is not null)
                    {
                        try
                        {
                            args = JsonSerializer.Deserialize<Dictionary<string, object?>>(tc.ArgumentsJson);
                        }
                        catch { /* fall back to null args */ }
                    }
                    contents.Add(new FunctionCallContent(tc.CallId, tc.Name, args));
                }
            }
            history.Add(new ChatMessage(ChatRole.Assistant, contents));

            // Tool results as separate messages — spill large results to store
            if (msg.ToolResults is not null)
            {
                foreach (var tr in msg.ToolResults)
                {
                    var result = tr.Result ?? "";
                    if (result.Length > maxToolResultChars)
                    {
                        var handle = _toolResultStore.Store(tr.Name ?? "unknown", result);
                        var previewLen = Math.Max(maxToolResultChars / 2, 500);
                        var preview = result[..Math.Min(previewLen, result.Length)];
                        result = $"{preview}\n\n" +
                            $"--- RESULT SPILLED (too large for context) ---\n" +
                            $"result_id: {handle}\n" +
                            $"total_chars: {result.Length:#,0}\n" +
                            $"Use RetrieveToolResult(resultId: \"{handle}\", offset: {preview.Length}) to read more.";
                    }
                    history.Add(new ChatMessage(ChatRole.Tool,
                        [new FunctionResultContent(tr.CallId, result)]));
                }
            }
        }

        if (messageList.Count > maxReplayMessages)
            Log("INFO", $"Chat restore: replayed {maxReplayMessages} of {messageList.Count} messages (older messages trimmed)");
    }

    /// <summary>Look up tool name from collected tool calls by matching CallId.</summary>
    private static string LookupToolName(List<AiToolCallInfo> toolCalls, string? callId) =>
        toolCalls.FirstOrDefault(tc => tc.CallId == callId)?.Name ?? "unknown";

    /// <summary>Extract token usage from an agent response and update cumulative counters.</summary>
    private void TrackUsage(AgentResponse response)
    {
        foreach (var message in response.Messages)
        {
            if (message.AdditionalProperties?.TryGetValue("Usage", out var usageObj) == true
                && usageObj is UsageContent usage)
            {
                _totalPromptTokens += usage.Details?.InputTokenCount ?? 0;
                _totalCompletionTokens += usage.Details?.OutputTokenCount ?? 0;
                _totalCachedTokens += usage.Details?.AdditionalCounts?.GetValueOrDefault("CachedInputTokenCount") ?? 0;
                _totalRequests++;
            }
        }

        // Also check the response-level usage if available from the underlying ChatResponse
        if (response.Messages.Count > 0)
        {
            var lastMsg = response.Messages[^1];
            if (lastMsg.AdditionalProperties?.TryGetValue("usage", out var rawUsage) == true)
            {
                Log("USAGE", $"Cumulative prompt: {_totalPromptTokens}, completion: {_totalCompletionTokens}, cached: {_totalCachedTokens}, requests: {_totalRequests}");
            }
        }
    }

    public async void ClearHistory()
    {
        _session = await _agent.CreateSessionAsync();
        _displayHistory.Clear();
        _actionLog.Clear();
        _lastContextSuffix = null;
    }

    /// <summary>Save the current chat to disk.</summary>
    public void SaveCurrentChat()
    {
        if (string.IsNullOrEmpty(CurrentChatId)) return;

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

        _chatStore.Save(new AiChatSession
        {
            Id = CurrentChatId,
            Title = CurrentChatTitle,
            Messages = _displayHistory.ToList()
        });
    }

    /// <summary>Create a new chat, saving the current one first.</summary>
    public void NewChat()
    {
        if (!string.IsNullOrEmpty(CurrentChatId) && _displayHistory.Count > 0)
            SaveCurrentChat();

        CurrentChatId = $"chat-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}";
        CurrentChatTitle = "New Chat";
        ClearHistory();
        _toolResultStore.Clear(); // Discard spilled results from previous chat
        LoadCoreTools(); // Reset to core tools for fresh conversation
        ChatListChanged?.Invoke();
    }

    /// <summary>Switch to an existing chat by ID.</summary>
    public async void SwitchChat(string chatId)
    {
        if (chatId == CurrentChatId) return;

        // Save current
        if (!string.IsNullOrEmpty(CurrentChatId) && _displayHistory.Count > 0)
            SaveCurrentChat();

        var chatSession = _chatStore.Load(chatId);
        if (chatSession is null) return;

        CurrentChatId = chatSession.Id;
        CurrentChatTitle = chatSession.Title;
        ClearHistory();

        // Restore display history
        _displayHistory.AddRange(chatSession.Messages);

        // Rebuild MAF agent session history from saved messages.
        // We inject them into the session's in-memory chat history so the agent
        // has context from the previous conversation, including tool call/result structure.
        if (_session.TryGetInMemoryChatHistory(out var history))
            ReplayHistoryInto(history, chatSession.Messages);

        ChatListChanged?.Invoke();
    }

    /// <summary>List all saved chats, most recent first.</summary>
    public List<AiChatSession> ListChats() => _chatStore.ListAll();

    /// <summary>Delete a chat by ID.</summary>
    public void DeleteChat(string chatId)
    {
        _chatStore.Delete(chatId);
        if (chatId == CurrentChatId) NewChat();
        ChatListChanged?.Invoke();
    }

    /// <summary>Rename a chat.</summary>
    public void RenameChat(string chatId, string newTitle)
    {
        _chatStore.Rename(chatId, newTitle);
        if (chatId == CurrentChatId) CurrentChatTitle = newTitle;
        ChatListChanged?.Invoke();
    }

    /// <summary>Export current chat as a Markdown string.</summary>
    public string ExportChatToMarkdown()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# {CurrentChatTitle}");
        sb.AppendLine();
        sb.AppendLine($"*Exported {DateTimeOffset.Now:yyyy-MM-dd HH:mm}*");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        foreach (var msg in _displayHistory)
        {
            var role = msg.Role == "user" ? "**You**" : "**AI Operator**";
            var time = msg.Timestamp.ToLocalTime().ToString("h:mm tt");
            sb.AppendLine($"### {role} — {time}");
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
        catch { return null; }
    }

    private const string SystemPrompt = """
        You are the AI Operator for CE AI Suite — a Cheat Engine-class memory analysis and reverse-engineering tool.
        You are an expert in game hacking, memory analysis, x86/x64 assembly, and reverse engineering.
        You operate autonomously using your tools to accomplish user goals.

        ═══ CONTEXT RECOVERY ═══
        Conversation history is compacted to stay within token limits. If you've lost context
        about prior findings (addresses, values, tool results), use SearchChatHistory to search
        the full uncompacted transcript. This searches all saved chats plus the current one.
        Example: SearchChatHistory("health address") to recall a previously found address.

        ═══ SAFETY RULES (CRITICAL) ═══
        • NEVER write to code/text sections (.text, .code) of the process — only data sections.
        • Before enabling a script, always ValidateScript first.
        • If a tool returns "Process N is no longer running", STOP all operations and inform the user.
        • Don't chain more than 3 write operations without pausing to verify results.
        • If a tool returns an error, do NOT retry the same operation more than twice.
        • When modifying memory, prefer small precise changes over bulk writes.
        • If EnableScript fails, do NOT immediately retry — analyze the error first.

        ═══ CORE PHILOSOPHY ═══
        • Be iterative and persistent. Don't give up after one attempt.
        • Use tools proactively — don't ask the user to do things you can do yourself.
        • When something fails, analyze why, adjust your approach, and try again.
        • Chain multiple tool calls in sequence to accomplish complex tasks.
        • After completing actions, verify the results before reporting success.
        • When you CANNOT verify something (e.g. "did the damage increase?"), ASK the user to check
          and report back. Frame these clearly: "Please do X in-game and tell me what happens."
        • NEVER respond with planning statements like "Let me load..." or "I'll start by...".
          Instead, ACTUALLY call the tools and THEN report what you found.
        • Your first response to a task should include TOOL CALLS, not descriptions of what you intend to do.
        • If you need to load skills, do it silently — don't narrate your preparation steps.

        ═══ YOUR TOOLS (PROGRESSIVE LOADING) ═══
        You start each conversation with a core set of tools (process, basic memory, scanning, address table).
        For specialized operations, call request_tools(category) to load additional tools on-demand.
        This keeps context lean and saves tokens — only load what you need.

        ALWAYS LOADED (core):
        Process: ListProcesses, FindProcess, AttachProcess, InspectProcess, CheckProcessLiveness
        Memory: ReadMemory, WriteMemory, ProbeAddress, BrowseMemory
        Scanning: StartScan, RefineScan, GetScanResults
        Address Table: ListAddressTable, AddToAddressTable, RemoveFromAddressTable, RefreshAddressTable,
                       FreezeAddress, UnfreezeAddress
        Context: GetCurrentContext, SummarizeInvestigation, SaveSession, ListSessions, LoadSession,
                 SearchChatHistory

        ON-DEMAND CATEGORIES (call request_tools to load):
        • breakpoints — SetBreakpoint, RemoveBreakpoint, ListBreakpoints, GetBreakpointHitLog, ProbeTargetRisk...
        • disassembly — Disassemble, FindWritersToOffset, GetCallerGraph, GetCallStack, ResolveSymbol...
        • hooks — InstallCodeCaveHook, RemoveCodeCaveHook, DryRunHookInstall...
        • scripts — ListScripts, ViewScript, EnableScript, CreateScriptEntry, GenerateAutoAssemblerScript...
        • memory_advanced — HexDump, ListMemoryRegions, DissectStructure, ChangeMemoryProtection...
        • scanning_advanced — ScanForPointers, RescanPointerPath, ValidatePointerPaths
        • address_table — RenameAddressTableEntry, SetEntryNotes, CreateAddressGroup, FreezeAddressAtValue...
        • snapshots — CaptureSnapshot, CompareSnapshots, CompareSnapshotWithLive...
        • safety — CheckHookConflicts, CheckAddressSafety, SampledWriteTrace...
        • signatures, hotkeys, undo, transactions, cheat_tables, vision, utility

        Example: Before disassembling code, call request_tools("disassembly").
        You can load multiple at once: request_tools("breakpoints,hooks,scripts")

        ═══ ARTIFACT ID PREFIXES ═══
        All IDs are prefixed by type:
          hook-* → Code cave hook   bp-* → Breakpoint   script-* → Script entry
          addr-* → Address entry    group-* → Group      scan-* → Scan result set
        If unsure about an ID, call IdentifyArtifact(id).

        ═══ AGENT SKILLS (PROGRESSIVE DISCLOSURE) ═══
        You have specialized domain skills available. These are loaded on-demand to keep context lean.
        When a task matches a skill's domain, use `load_skill` to load its detailed instructions.
        After loading, you can use `read_skill_resource` to access reference documents.

        Skills are automatically advertised at session start. Load them when you need deep expertise
        for a specific workflow — they contain step-by-step procedures, reference tables, code patterns,
        and engine-specific knowledge that would be too costly to keep in context at all times.

        ALWAYS load relevant skills before starting complex workflows. For example:
        • Scanning for values → load memory-scanning
        • Analyzing what writes to an address → load code-analysis
        • Setting breakpoints or hooks → load breakpoint-mastery
        • Writing/editing AA scripts → load script-engineering
        • Reversing a Unity Il2Cpp game → load unity-il2cpp
        • Reversing an Unreal Engine game → load unreal-engine
        • Building pointer chains → load pointer-resolution
        • Exploring unknown memory structures → load data-mining
        • Working with anti-cheat protected games → load stealth-awareness

        ═══ QUICK REFERENCE ═══
        Common data types: HP/MP → Float; Gold/Score → Int32; Coords → Float[3]; Flags → Byte
        Assembly: mov [rax+14],ebx = write 4 bytes; nop = 0x90; jmp near = E9 + 4-byte offset
        Unity: GameAssembly.dll + offset → pointer chain (2-3 levels); use ResolveSymbol for ASLR
        Unreal: GWorld/GNames/GObjects globals; UObject hierarchy; TArray = Data+Count+Max

        ═══ COMMUNICATION STYLE ═══
        • Be concise but informative
        • Show addresses in hex format (0x...)
        • After tool calls, summarize findings in plain language
        • Explain technical findings clearly
        • Pick the best approach and execute — don't list options
        • Warn before writing to memory, but don't require confirmation for reads/scans/analysis
        • When you need the user to act in-game, be specific:
          "Please fight a battle and tell me the EXP you received" NOT "try it out"

        ═══ CONTEXT ═══
        A [CURRENT STATE] block is appended to each user message with:
        - Attached process info (name, PID, modules)
        - Address table summary (entries, locked count, scripts)
        - Active scan info (result count, data type)
        Use this context to avoid redundant tool calls. Don't re-list processes if you
        already know which one is attached. Don't re-scan if results are still valid.
        """;

    private sealed class StubChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("stub");

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

    /// <summary>
    /// Delegating chat client that intercepts all messages flowing to the LLM and
    /// spills any <see cref="FunctionResultContent"/> whose string representation
    /// exceeds <see cref="TokenLimits.MaxToolResultChars"/> to a <see cref="ToolResultStore"/>.
    /// The AI receives a summary with the result handle and can retrieve pages via
    /// <c>RetrieveToolResult</c>. Sits between the
    /// <see cref="Microsoft.Extensions.AI.FunctionInvokingChatClient"/> and the
    /// underlying LLM client so tool results are capped before consuming API tokens.
    /// </summary>
    private sealed class ToolResultCappingChatClient(IChatClient inner, TokenLimits limits, ToolResultStore store) : DelegatingChatClient(inner)
    {
        public override Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var capped = CapToolResults(messages);
            DeduplicateTools(options);
            return base.GetResponseAsync(capped, options, cancellationToken);
        }

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var capped = CapToolResults(messages);
            DeduplicateTools(options);
            return base.GetStreamingResponseAsync(capped, options, cancellationToken);
        }

        /// <summary>
        /// Remove duplicate tool names from ChatOptions.Tools.
        /// Context providers (e.g. FileAgentSkillsProvider) may inject tools that overlap
        /// with our progressive tool list, and some APIs (Anthropic) reject duplicates.
        /// </summary>
        private static void DeduplicateTools(ChatOptions? options)
        {
            if (options?.Tools is not { Count: > 0 } tools) return;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = tools.Count - 1; i >= 0; i--)
            {
                var name = tools[i] is AIFunction f ? f.Name : tools[i].GetType().Name;
                if (!seen.Add(name))
                    tools.RemoveAt(i);
            }
        }

        private IEnumerable<ChatMessage> CapToolResults(IEnumerable<ChatMessage> messages)
        {
            foreach (var msg in messages)
            {
                bool hasFunctionResult = false;
                foreach (var content in msg.Contents)
                {
                    if (content is FunctionResultContent)
                    {
                        hasFunctionResult = true;
                        break;
                    }
                }

                if (!hasFunctionResult)
                {
                    yield return msg;
                    continue;
                }

                // Rebuild the contents list, spilling oversized tool results to the store
                var newContents = new List<AIContent>(msg.Contents.Count);
                foreach (var content in msg.Contents)
                {
                    if (content is FunctionResultContent frc)
                    {
                        var resultStr = frc.Result?.ToString();
                        if (resultStr is not null && resultStr.Length > limits.MaxToolResultChars)
                        {
                            // Spill full result to store, return summary + handle
                            var toolName = frc.CallId ?? "unknown";
                            var handle = store.Store(toolName, resultStr);
                            var previewLen = Math.Max(limits.MaxToolResultChars / 2, 500);
                            var preview = resultStr[..Math.Min(previewLen, resultStr.Length)];
                            var summary = $"{preview}\n\n" +
                                $"--- RESULT SPILLED (too large for context) ---\n" +
                                $"result_id: {handle}\n" +
                                $"total_chars: {resultStr.Length:#,0}\n" +
                                $"shown_chars: {preview.Length:#,0}\n" +
                                $"Use RetrieveToolResult(resultId: \"{handle}\", offset: {preview.Length}) to read more.";
                            newContents.Add(new FunctionResultContent(frc.CallId ?? "", summary));
                        }
                        else
                        {
                            newContents.Add(content);
                        }
                    }
                    else
                    {
                        newContents.Add(content);
                    }
                }
                yield return new ChatMessage(msg.Role, newContents);
            }
        }
    }
}
