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

    public IReadOnlyList<AiChatMessage> DisplayHistory => _displayHistory;
    public IReadOnlyList<AiActionLogEntry> ActionLog => _actionLog;
    public bool IsConfigured { get; }

    public AiOperatorService(IChatClient? chatClient, AiToolFunctions toolFunctions, Func<string>? contextProvider = null)
    {
        IsConfigured = chatClient is not null;
        _contextProvider = contextProvider;
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
                options.MaximumIterationsPerRequest = 25;
            })
            .Build();

        var systemPrompt = new ChatMessage(ChatRole.System, SystemPrompt);
        _conversationHistory.Add(systemPrompt);
    }

    public async Task<string> SendMessageAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        _displayHistory.Add(new AiChatMessage("user", userMessage, DateTimeOffset.UtcNow));

        // Inject dynamic context so the agent knows the current state
        var contextMsg = BuildContextMessage();
        if (contextMsg is not null)
            _conversationHistory.Add(contextMsg);

        _conversationHistory.Add(new ChatMessage(ChatRole.User, userMessage));

        try
        {
            var response = await _chatClient.GetResponseAsync(
                _conversationHistory,
                _chatOptions,
                cancellationToken);

            // Extract tool calls and final text from all response messages
            var assistantText = "";
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
                        var argsStr = functionCall.Arguments is not null
                            ? string.Join(", ", functionCall.Arguments.Select(kv => $"{kv.Key}={kv.Value}"))
                            : "";
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

            if (string.IsNullOrWhiteSpace(assistantText))
            {
                assistantText = "(Tool calls executed — see action log for details)";
            }

            _displayHistory.Add(new AiChatMessage("assistant", assistantText, DateTimeOffset.UtcNow));
            return assistantText;
        }
        catch (Exception ex)
        {
            var errorMessage = $"AI error: {ex.Message}";
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

        ═══ CORE PHILOSOPHY ═══
        • Be iterative and persistent. Don't give up after one attempt.
        • Use tools proactively — don't ask the user to do things you can do yourself.
        • When something fails, analyze why, adjust your approach, and try again.
        • Chain multiple tool calls in sequence to accomplish complex tasks.
        • After completing actions, verify the results before reporting success.

        ═══ YOUR TOOLS ═══
        Process: ListProcesses, InspectProcess, AttachProcess, FindProcess
        Memory: ReadMemory, WriteMemory, BrowseMemory
        Scanning: StartScan, RefineScan, GetScanResults
        Analysis: Disassemble, DissectStructure, ScanForPointers
        Address Table: ListAddressTable, AddToAddressTable, RefreshAddressTable,
                       FreezeAddress, UnfreezeAddress, ToggleScript, GetAddressTableNode
        Breakpoints: SetBreakpoint, RemoveBreakpoint, ListBreakpoints, GetBreakpointHitLog
        Scripts: ListScripts, ViewScript, ValidateScript, LoadCheatTable
        Artifacts: GenerateTrainerScript, GenerateAutoAssemblerScript, GenerateLuaScript
        Other: SummarizeInvestigation, SetHotkey

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
        1. SetBreakpoint with HardwareWrite on the address
        2. Wait for hits, then GetBreakpointHitLog
        3. Disassemble at the instruction address from the hit log
        4. Analyze the assembly to understand the write pattern
        5. Explain findings to user in plain language

        LOADING A CHEAT TABLE:
        1. LoadCheatTable with full path
        2. ListAddressTable to show what was loaded
        3. RefreshAddressTable to populate live values
        4. Explain the structure to the user

        POINTER SCANNING:
        1. Find the dynamic address first (via scanning)
        2. ScanForPointers to find static pointer chains
        3. Add the pointer chain to the address table

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

        ═══ COMMUNICATION STYLE ═══
        • Be concise but informative
        • Show addresses in hex format (0x...)
        • After tool calls, summarize what you found in plain language
        • Explain technical findings (assembly, pointer chains) clearly
        • When multiple approaches exist, pick the best one and execute — don't list options
        • Warn before writing to memory, but don't require confirmation for reads/scans/analysis
        • If a tool returns an error, explain what went wrong and what you'll try next

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
