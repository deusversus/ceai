using System.Reflection;
using Microsoft.Extensions.AI;

namespace CEAISuite.Application;

public sealed record AiChatMessage(string Role, string Content, DateTimeOffset Timestamp);

public sealed record AiActionLogEntry(
    string ToolName,
    string Arguments,
    string Result,
    DateTimeOffset Timestamp);

public sealed class AiOperatorService
{
    private readonly IChatClient _chatClient;
    private readonly List<ChatMessage> _conversationHistory = new();
    private readonly List<AiChatMessage> _displayHistory = new();
    private readonly List<AiActionLogEntry> _actionLog = new();
    private readonly ChatOptions _chatOptions;
    private readonly Func<string>? _contextProvider;
    private readonly AiToolFunctions? _toolFunctions;
    private readonly AiChatStore _chatStore = new();
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CEAISuite", "logs");
    private static readonly string LogPath = Path.Combine(LogDir, $"ai-agent-{DateTime.Now:yyyy-MM-dd}.log");

    /// <summary>Raised when the agent's status changes (tool calls, thinking, errors).</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Raised when the chat list changes (new chat, delete, rename).</summary>
    public event Action? ChatListChanged;

    public IReadOnlyList<AiChatMessage> DisplayHistory => _displayHistory;
    public IReadOnlyList<AiActionLogEntry> ActionLog => _actionLog;
    public bool IsConfigured { get; }

    /// <summary>Current chat session ID.</summary>
    public string CurrentChatId { get; private set; } = "";

    /// <summary>Current chat title.</summary>
    public string CurrentChatTitle { get; private set; } = "New Chat";

    public AiOperatorService(IChatClient? chatClient, AiToolFunctions toolFunctions, Func<string>? contextProvider = null)
    {
        IsConfigured = chatClient is not null;
        _contextProvider = contextProvider;
        _toolFunctions = toolFunctions;
        var baseClient = chatClient ?? new StubChatClient();

        // Build AIFunction list from the tool functions instance using reflection
        var methods = typeof(AiToolFunctions).GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        var tools = methods
            .Where(m => !m.IsSpecialName)
            .Select(m => AIFunctionFactory.Create(m, toolFunctions))
            .Cast<AITool>()
            .ToList();

        _chatOptions = new ChatOptions
        {
            Tools = tools,
            Temperature = 0.3f,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["reasoning_effort"] = "high"
            }
        };

        // Wrap the client with function invocation middleware so tool calls
        // are automatically executed and results fed back to the model.
        // MaximumIterationsPerRequest caps the auto-loop so it doesn't spin forever.
        _chatClient = new ChatClientBuilder(baseClient)
            .UseFunctionInvocation(null, options =>
            {
                options.MaximumIterationsPerRequest = 12;
            })
            .Build();

        var systemPrompt = new ChatMessage(ChatRole.System, SystemPrompt);
        _conversationHistory.Add(systemPrompt);

        // Start with a fresh chat session
        NewChat();

        // Ensure log directory exists
        try { Directory.CreateDirectory(LogDir); } catch { /* best effort */ }
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
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Log("INFO", $"User: {userMessage}");
        _displayHistory.Add(new AiChatMessage("user", userMessage, DateTimeOffset.UtcNow));

        // Inject dynamic context so the agent knows the current state
        var contextMsg = BuildContextMessage();
        if (contextMsg is not null)
            _conversationHistory.Add(contextMsg);

        _conversationHistory.Add(new ChatMessage(ChatRole.User, userMessage));

        try
        {
            UpdateStatus("Thinking...");
            var response = await _chatClient.GetResponseAsync(
                _conversationHistory,
                _chatOptions,
                cancellationToken);

            // Extract tool calls and final text from all response messages
            var assistantText = "";
            int toolCallCount = 0;
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
                        UpdateStatus($"Tool: {functionCall.Name ?? "unknown"} ({toolCallCount} calls, {sw.Elapsed.TotalSeconds:F0}s)");
                        Log("TOOL", $"Call: {functionCall.Name}({argsStr})");
                        _actionLog.Add(new AiActionLogEntry(
                            functionCall.Name ?? "unknown",
                            argsStr,
                            "invoked",
                            DateTimeOffset.UtcNow));
                    }
                    else if (content is FunctionResultContent functionResult)
                    {
                        var resultStr = functionResult.Result?.ToString() ?? "";
                        var truncated = resultStr.Length > 200 ? resultStr[..200] + "..." : resultStr;
                        Log("TOOL", $"Result: {truncated}");
                        // Update the matching action log entry with result
                        for (int i = _actionLog.Count - 1; i >= 0; i--)
                        {
                            if (_actionLog[i].Result == "invoked")
                            {
                                _actionLog[i] = _actionLog[i] with { Result = truncated };
                                break;
                            }
                        }
                    }
                }
            }

            // Add all response messages to conversation history
            foreach (var message in response.Messages)
            {
                _conversationHistory.Add(message);
            }

            // Inject any pending screenshots as image content for future turns
            if (_toolFunctions is not null)
            {
                while (_toolFunctions.PendingImages.TryDequeue(out var img))
                {
                    UpdateStatus("Analyzing screenshot...");
                    var imageContent = new List<AIContent>
                    {
                        new TextContent($"[Screenshot: {img.Description}]"),
                        new DataContent(img.PngData, "image/png")
                    };
                    var imageMsg = new ChatMessage(ChatRole.User, imageContent);
                    _conversationHistory.Add(imageMsg);

                    // Re-invoke the model so it can analyze the screenshot
                    var imageResponse = await _chatClient.GetResponseAsync(
                        _conversationHistory,
                        _chatOptions,
                        cancellationToken);

                    foreach (var msg in imageResponse.Messages)
                    {
                        foreach (var content in msg.Contents)
                        {
                            if (content is TextContent tc && !string.IsNullOrWhiteSpace(tc.Text))
                                assistantText += "\n" + tc.Text;
                        }
                        _conversationHistory.Add(msg);
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(assistantText))
            {
                assistantText = "(Tool calls executed — see action log for details)";
            }

            sw.Stop();
            var summary = toolCallCount > 0
                ? $"Done ({toolCallCount} tool calls, {sw.Elapsed.TotalSeconds:F1}s)"
                : $"Done ({sw.Elapsed.TotalSeconds:F1}s)";
            UpdateStatus(summary);
            Log("INFO", $"Assistant: {(assistantText.Length > 300 ? assistantText[..300] + "..." : assistantText)}");
            _displayHistory.Add(new AiChatMessage("assistant", assistantText, DateTimeOffset.UtcNow));
            SaveCurrentChat();
            return assistantText;
        }
        catch (Exception ex)
        {
            sw.Stop();
            var errorMessage = $"AI error: {ex.Message}";
            Log("ERROR", $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            UpdateStatus($"Error ({sw.Elapsed.TotalSeconds:F1}s)");
            _displayHistory.Add(new AiChatMessage("assistant", errorMessage, DateTimeOffset.UtcNow));
            return errorMessage;
        }
    }

    public void ClearHistory()
    {
        var systemPrompt = _conversationHistory[0];
        _conversationHistory.Clear();
        _conversationHistory.Add(systemPrompt);
        _displayHistory.Clear();
        _actionLog.Clear();
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
        ChatListChanged?.Invoke();
    }

    /// <summary>Switch to an existing chat by ID.</summary>
    public void SwitchChat(string chatId)
    {
        if (chatId == CurrentChatId) return;

        // Save current
        if (!string.IsNullOrEmpty(CurrentChatId) && _displayHistory.Count > 0)
            SaveCurrentChat();

        var session = _chatStore.Load(chatId);
        if (session is null) return;

        CurrentChatId = session.Id;
        CurrentChatTitle = session.Title;
        ClearHistory();

        // Restore display history
        _displayHistory.AddRange(session.Messages);

        // Rebuild conversation history for the API
        foreach (var msg in session.Messages)
        {
            _conversationHistory.Add(new ChatMessage(
                msg.Role == "user" ? ChatRole.User : ChatRole.Assistant,
                msg.Content));
        }

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

    private ChatMessage? BuildContextMessage()
    {
        if (_contextProvider is null) return null;
        try
        {
            var ctx = _contextProvider();
            if (string.IsNullOrWhiteSpace(ctx)) return null;
            return new ChatMessage(ChatRole.System, $"[CURRENT STATE]\n{ctx}");
        }
        catch { return null; }
    }

    private const string SystemPrompt = """
        You are the AI Operator for CE AI Suite — a Cheat Engine-class memory analysis and reverse-engineering tool.
        You are an expert in game hacking, memory analysis, x86/x64 assembly, and reverse engineering.
        You operate autonomously using your tools to accomplish user goals.

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

        ═══ YOUR TOOLS ═══
        Process: ListProcesses, InspectProcess, AttachProcess, FindProcess
        Memory: ReadMemory, WriteMemory, BrowseMemory, ProbeAddress, HexDump
        Scanning: StartScan, RefineScan, GetScanResults, ListMemoryRegions
        Analysis: Disassemble, DissectStructure, ScanForPointers, GenerateSignature, TestSignatureUniqueness
        Address Table: ListAddressTable, AddToAddressTable, RemoveFromAddressTable, RenameAddressTableEntry,
                        SetEntryNotes, CreateAddressGroup, MoveEntryToGroup, RefreshAddressTable,
                        FreezeAddress, UnfreezeAddress, FreezeAddressAtValue, ToggleScript, GetAddressTableNode
        Breakpoints: SetBreakpoint (with mode: Auto/Stealth/PageGuard/Hardware/Software), RemoveBreakpoint, ListBreakpoints, GetBreakpointHitLog
        Code Cave Hooks: InstallCodeCaveHook, RemoveCodeCaveHook, ListCodeCaveHooks, GetCodeCaveHookHits
        Call Stack: GetCallStack, GetAllThreadStacks
        Scripts: ListScripts, ViewScript, ValidateScript, EnableScript, DisableScript,
                 EditScript, CreateScriptEntry
        Sessions: SaveSession, ListSessions, LoadSession
        Vision: CaptureProcessWindow (captures game window screenshot for visual analysis)
        Memory Protection: ChangeMemoryProtection, AllocateMemory, FreeMemory, QueryMemoryProtection
        Snapshots: CaptureSnapshot, CompareSnapshots, CompareSnapshotWithLive, ListSnapshots, DeleteSnapshot
        Pointer Rescan: RescanPointerPath, ValidatePointerPaths
        Artifacts: GenerateTrainerScript, GenerateAutoAssemblerScript, GenerateLuaScript, SaveCheatTable
        Other: SummarizeInvestigation, SetHotkey, ListHotkeys, RemoveHotkey, GetCurrentContext,
               UndoWrite, RedoWrite, PatchHistory, LoadCheatTable

        ═══ KEY WORKFLOWS ═══

        FINDING A VALUE (health, gold, ammo, etc.):
        1. If no process attached: FindProcess → AttachProcess
        2. Ask user what the current value is
        3. StartScan with ExactValue + appropriate type (Int32 for whole numbers, Float for decimals)
        4. If too many results (>50): Ask user to change the value in-game, then RefineScan
        5. Repeat refine cycle: Increased/Decreased/ExactValue until <5 results
        6. AddToAddressTable with a descriptive label
        7. If user wants to freeze: FreezeAddress
        TIP: If Int32 scan finds nothing, try Float. Games often store HP as float.
        TIP: For "unknown initial value", start with UnknownInitialValue, then use Increased/Decreased.

        ANALYZING CODE / "WHAT WRITES TO THIS ADDRESS":
        1. SetBreakpoint with HardwareWrite on the address (mode=Auto will pick the best approach)
        2. Wait for hits, then GetBreakpointHitLog
        3. Disassemble at the instruction address from the hit log
        4. Analyze the assembly to understand the write pattern
        5. Explain findings to user in plain language

        ═══ BREAKPOINT MODES (IMPORTANT) ═══

        You have FIVE breakpoint intrusiveness levels — pick the right one:

        • Auto (default): Engine picks the least intrusive mode that works for the breakpoint type.
          Execute → Hardware, Write/ReadWrite → PageGuard, Software → Software.

        • Stealth: Code cave JMP detour — NO debugger attached, completely invisible to anti-debug.
          ⚠️ ONLY WORKS ON EXECUTABLE CODE ADDRESSES. Cannot monitor data writes.
          If you request Stealth on a write breakpoint, it auto-downgrades to PageGuard.
          Best for: anti-cheat/anti-debug games, long-running monitoring, execution hooks.
          Use InstallCodeCaveHook directly for full control over register capture.

        • PageGuard: Uses PAGE_GUARD memory protection. Less intrusive than hardware BPs for
          monitoring memory writes/reads. Still requires debugger. Good for data breakpoints on
          pages with frequent access.

        • Hardware: DR0-DR3 debug registers. Requires thread suspension to write CONTEXT.
          Best for: single-shot analysis (find what writes/reads an address). Limited to 4 active.
          WARNING: Suspends all threads — can freeze anti-debug-sensitive games.

        • Software: INT3 byte patch. Most intrusive. Best for: specific instruction tracing.
          Cannot monitor data writes — only executable code.

        ═══ MODE COMPATIBILITY MATRIX ═══

        Stealth   + Execute/Software → ✅ Code cave hook (safest, no debugger)
        Stealth   + Write/ReadWrite  → ❌ INVALID — auto-downgrades to PageGuard
        PageGuard + Write/ReadWrite  → ✅ Recommended for data write monitoring
        PageGuard + Execute          → ✅ Works but unnecessary (use Stealth instead)
        Hardware  + any              → ✅ Works but intrusive (debugger + thread suspend)
        Software  + Execute          → ✅ INT3 patching — most intrusive
        Software  + Write/ReadWrite  → ❌ INVALID — rejected with error

        ═══ SAFETY FEATURES ═══

        • Hit-rate throttle: If a breakpoint fires >200 times/second, it is AUTO-DISABLED to
          prevent game freezes. The hit log is preserved. Check breakpoint status after setting.

        • Single-hit mode: Pass singleHit=true to SetBreakpoint for risky targets. The BP fires
          once, captures the data, then auto-removes itself. Ideal for "find what writes this address".

        • When monitoring data writes on HOT addresses (written every frame), ALWAYS use:
          1. singleHit=true — capture one hit, auto-remove
          2. OR use mode=PageGuard (not Hardware) to minimize thread suspension
          3. NEVER use Stealth for data writes — it will be auto-downgraded

        ANTI-DEBUG GAMES (freezes, crashes, or detects debugger):
        1. ALWAYS use mode=Stealth or InstallCodeCaveHook for EXECUTION hooks on these targets
        2. Code cave hooks work by JMP redirection — no DebugActiveProcess call at all
        3. If Hardware mode freezes the game, remove the breakpoint and switch to Stealth
        4. Code caves capture register snapshots in a ring buffer you can read with GetCodeCaveHookHits
        5. For finding what WRITES a data address, use mode=PageGuard with singleHit=true

        LOADING A CHEAT TABLE:
        1. LoadCheatTable with full path
        2. ListAddressTable to show what was loaded
        3. RefreshAddressTable to populate live values
        4. Explain the structure to the user

        POINTER SCANNING:
        1. Find the dynamic address first (via scanning)
        2. ScanForPointers to find static pointer chains
        3. Add the pointer chain to the address table

        ═══ ITERATING ON SCRIPTS (CRITICAL WORKFLOW) ═══

        When the user asks you to fix, improve, or create Auto Assembler scripts:

        UNDERSTANDING AN EXISTING SCRIPT:
        1. ViewScript to read the current script source
        2. Identify: what address it hooks, what it does (multiply, set, NOP, etc.)
        3. Disassemble at the hook address to see surrounding code
        4. Check if the hook point makes sense (is it too late? too early? wrong register?)

        FINDING A BETTER HOOK POINT:
        1. SetBreakpoint (HardwareWrite or HardwareExecute, mode=Auto) on the relevant address
           — If the game is anti-debug-sensitive, use mode=Stealth or InstallCodeCaveHook instead
        2. Ask user to trigger the game event (fight a battle, gain EXP, etc.)
        3. GetBreakpointHitLog (or GetCodeCaveHookHits for stealth hooks) to see which instruction wrote the value
        4. Disassemble the hit instruction and surrounding code
        5. Trace backward to find where the value is CALCULATED (not just stored)
        6. The best hook is where the value is computed BEFORE it's written

        WRITING/EDITING A SCRIPT:
        1. Write the full AA script with [ENABLE] and [DISABLE] sections
        2. Use EditScript to replace existing script, or CreateScriptEntry for new ones
        3. ValidateScript to check syntax
        4. EnableScript to test it
        5. Ask user to trigger the game event to test
        6. CaptureProcessWindow to visually verify if possible
        7. If wrong, DisableScript, analyze what went wrong, EditScript with fix, repeat

        COMMON AA SCRIPT PATTERNS:
        • Multiplier: imul reg,reg,N or mov reg,N then imul dest,src,reg
        • NOP (disable a write): nop or db 90 90 90...
        • Value override: mov [addr],value
        • Code cave: alloc memory, jmp to cave, do custom logic, jmp back

        ITERATION PHILOSOPHY FOR SCRIPTS:
        • A script rarely works perfectly the first time
        • Check register contents with breakpoints to verify your assumptions
        • If an EXP multiplier gives wrong values, the hook may be at the wrong stage
        • Use HexDump to verify original bytes match the assert pattern
        • After enabling a script, ALWAYS ask the user to test it in-game
        • If the user reports it's not working, gather more data before guessing

        ═══ SCREEN CAPTURE ═══
        Use CaptureProcessWindow to take a screenshot of the game window. This lets you:
        • Verify game state (menus, battle screens, inventory)
        • Confirm that value changes are reflected visually
        • Help diagnose why a script isn't working
        • See what the user sees without relying on their description

        ═══ DOMAIN KNOWLEDGE ═══
        Common data types in games:
        • Health/MP/Stamina: often Float (try both Int32 and Float)
        • Gold/Currency/Score: usually Int32 or Int64
        • Item counts: Int32 or Int16
        • Coordinates (x,y,z): Float or Double
        • Boolean flags: Byte (0/1) or Int32

        Unity games (GameAssembly.dll):
        • Static fields go through Il2Cpp metadata → pointer chains from GameAssembly.dll
        • Mono games use mono.dll or GameAssembly.dll as base
        • Common pattern: base+offset → ptr → ptr → value (2-3 levels deep)

        Unreal Engine (UE4/UE5):
        • GNames and GObjects tables
        • FName pooling for strings
        • UObject hierarchy with consistent offsets

        x86/x64 Assembly Quick Reference:
        • mov [rax+14],ebx — writes ebx (4 bytes) to address in rax+0x14
        • imul ebx,ecx,4 — ebx = ecx * 4
        • test reg,reg / jle — jump if reg <= 0
        • nop = 0x90, jmp near = 0xE9 + 4-byte relative offset

        ═══ COMMUNICATION STYLE ═══
        • Be concise but informative
        • Show addresses in hex format (0x...)
        • After tool calls, summarize what you found in plain language
        • Explain technical findings (assembly, pointer chains) clearly
        • When multiple approaches exist, pick the best one and execute — don't list options
        • Warn before writing to memory, but don't require confirmation for reads/scans/analysis
        • If a tool returns an error, explain what went wrong and what you'll try next
        • When you need the user to act in-game, be specific:
          "Please fight a battle and tell me the EXP you received" NOT "try it out"

        ═══ CONTEXT ═══
        A [CURRENT STATE] system message is injected before each user message with:
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
                "AI operator is not configured. Set your OpenAI API key in the OPENAI_API_KEY environment variable and restart the application."));
            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
