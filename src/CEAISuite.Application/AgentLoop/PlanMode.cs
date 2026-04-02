using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.AI;

namespace CEAISuite.Application.AgentLoop;

/// <summary>
/// Plan Mode: a two-phase execution model where the agent first creates
/// a structured plan, gets user approval, then executes it.
///
/// Modeled after Claude Code's plan mode where the agent:
/// 1. Analyzes the request and produces a structured plan
/// 2. User reviews/modifies the plan
/// 3. Agent executes steps sequentially, reporting progress
///
/// This prevents expensive multi-tool operations from running without
/// user awareness, and gives the user a chance to redirect.
///
/// Plan mode is triggered by:
/// - User explicitly requesting "/plan" or "plan first"
/// - Complex requests detected by the agent (many potential steps)
/// - Settings flag requiring plan mode for destructive operations
/// </summary>
public sealed class PlanExecutor
{
    private readonly IChatClient _chatClient;
    private readonly AgentLoopOptions _options;
    private readonly Action<string, string>? _log;

    public PlanExecutor(
        IChatClient chatClient,
        AgentLoopOptions options)
    {
        _chatClient = chatClient;
        _options = options;
        _log = options.Log;
    }

    /// <summary>
    /// Generate a plan for a user request. Calls the LLM with planning-specific
    /// instructions to produce a structured plan without executing any tools.
    /// </summary>
    public async Task<ExecutionPlan> GeneratePlanAsync(
        string userRequest,
        ChatHistoryManager history,
        Func<string>? contextProvider = null,
        CancellationToken ct = default)
    {
        _log?.Invoke("PLAN", $"Generating plan for: {userRequest}");

        // Build planning prompt
        var contextSuffix = "";
        if (contextProvider is not null)
        {
            try { contextSuffix = $"\n\n[CURRENT STATE]\n{contextProvider()}"; }
            catch { /* ignore */ }
        }

        var planMessages = new List<ChatMessage>
        {
            new(ChatRole.System, PlanningPrompt),
        };

        // Include recent conversation context
        var recentMessages = history.GetMessages();
        if (recentMessages.Count > 0)
        {
            var toInclude = recentMessages.TakeLast(Math.Min(10, recentMessages.Count));
            planMessages.AddRange(toInclude);
        }

        planMessages.Add(new ChatMessage(ChatRole.User,
            $"{userRequest}{contextSuffix}\n\n" +
            "Create a structured execution plan. Respond with ONLY a JSON object matching the plan schema."));

        var response = await _chatClient.GetResponseAsync(
            planMessages,
            new ChatOptions
            {
                Temperature = 0.2f,
                MaxOutputTokens = 4096,
            },
            ct);

        var planText = response.Text ?? "";
        _log?.Invoke("PLAN", $"Plan response: {planText.Length} chars");

        // Parse plan from LLM response
        return ParsePlan(planText, userRequest);
    }

    /// <summary>
    /// Execute an approved plan step by step. Yields progress events.
    /// </summary>
    public ChannelReader<PlanProgressEvent> ExecutePlanAsync(
        ExecutionPlan plan,
        AgentLoop agentLoop,
        ChatHistoryManager history,
        Func<string>? contextProvider = null,
        CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<PlanProgressEvent>();

        _ = Task.Run(async () =>
        {
            try
            {
                await channel.Writer.WriteAsync(
                    new PlanProgressEvent.PlanStarted(plan), ct);

                for (int i = 0; i < plan.Steps.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var step = plan.Steps[i];

                    await channel.Writer.WriteAsync(
                        new PlanProgressEvent.StepStarted(i, step), ct);

                    _log?.Invoke("PLAN", $"Executing step {i + 1}/{plan.Steps.Count}: {step.Description}");

                    // Execute the step via the agent loop
                    var stepPrompt = $"Execute step {i + 1} of the plan: {step.Description}";
                    if (step.Details is not null)
                        stepPrompt += $"\n\nDetails: {step.Details}";
                    if (step.ExpectedTools is { Count: > 0 })
                        stepPrompt += $"\n\nExpected tools: {string.Join(", ", step.ExpectedTools)}";

                    var reader = agentLoop.RunStreamingAsync(
                        stepPrompt, history, contextProvider, null, ct);

                    var stepText = "";
                    var toolCalls = 0;

                    await foreach (var evt in reader.ReadAllAsync(ct))
                    {
                        switch (evt)
                        {
                            case AgentStreamEvent.TextDelta delta:
                                stepText += delta.Text;
                                break;
                            case AgentStreamEvent.ToolCallCompleted:
                                toolCalls++;
                                break;
                        }

                        // Forward raw events for UI display
                        await channel.Writer.WriteAsync(
                            new PlanProgressEvent.StepStreamEvent(i, evt), ct);
                    }

                    var stepResult = new StepResult
                    {
                        StepIndex = i,
                        Success = true,
                        Text = stepText,
                        ToolCallCount = toolCalls,
                    };

                    await channel.Writer.WriteAsync(
                        new PlanProgressEvent.StepCompleted(i, step, stepResult), ct);

                    _log?.Invoke("PLAN", $"Step {i + 1} done: {toolCalls} tools, {stepText.Length} chars");
                }

                await channel.Writer.WriteAsync(
                    new PlanProgressEvent.PlanCompleted(plan), ct);
            }
            catch (OperationCanceledException)
            {
                await channel.Writer.WriteAsync(
                    new PlanProgressEvent.PlanCancelled(), CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log?.Invoke("PLAN", $"Plan execution failed: {ex.Message}");
                await channel.Writer.WriteAsync(
                    new PlanProgressEvent.PlanFailed(ex.Message), CancellationToken.None);
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, ct);

        return channel.Reader;
    }

    private ExecutionPlan ParsePlan(string llmResponse, string originalRequest)
    {
        // Try to extract JSON from the response (may be wrapped in markdown code blocks)
        var json = llmResponse.Trim();
        if (json.StartsWith("```"))
        {
            var start = json.IndexOf('{');
            var end = json.LastIndexOf('}');
            if (start >= 0 && end > start)
                json = json[start..(end + 1)];
        }

        try
        {
            var dto = JsonSerializer.Deserialize<PlanDto>(json, JsonOpts);
            if (dto?.Steps is { Count: > 0 })
            {
                return new ExecutionPlan
                {
                    Title = dto.Title ?? "Execution Plan",
                    Summary = dto.Summary ?? originalRequest,
                    Steps = dto.Steps.Select((s, i) => new PlanStep
                    {
                        Index = i,
                        Description = s.Description ?? $"Step {i + 1}",
                        Details = s.Details,
                        ExpectedTools = s.ExpectedTools,
                        IsDestructive = s.IsDestructive,
                        EstimatedDuration = s.EstimatedDuration,
                    }).ToList(),
                    EstimatedToolCalls = dto.EstimatedToolCalls,
                    Warnings = dto.Warnings,
                };
            }
        }
        catch (JsonException ex)
        {
            _log?.Invoke("PLAN", $"JSON parse failed: {ex.Message} — creating single-step plan");
        }

        // Fallback: create a single-step plan from the raw text
        return new ExecutionPlan
        {
            Title = "Execution Plan",
            Summary = originalRequest,
            Steps =
            [
                new PlanStep
                {
                    Index = 0,
                    Description = originalRequest,
                    Details = llmResponse,
                }
            ],
        };
    }

    private const string PlanningPrompt = """
        You are a planning agent. Analyze the user's request and create a structured execution plan.
        Do NOT execute any tools — only plan what steps should be taken.

        Respond with a JSON object matching this schema:
        {
          "title": "Short plan title",
          "summary": "One-sentence summary of the approach",
          "steps": [
            {
              "description": "What this step does",
              "details": "Specific instructions or parameters",
              "expectedTools": ["ToolName1", "ToolName2"],
              "isDestructive": false,
              "estimatedDuration": "fast|medium|slow"
            }
          ],
          "estimatedToolCalls": 10,
          "warnings": ["Any safety concerns or caveats"]
        }

        Guidelines:
        - Break complex tasks into clear, sequential steps
        - List which tools each step will likely use
        - Flag destructive steps (memory writes, hook installs, etc.)
        - Keep steps atomic — each should accomplish one thing
        - Order steps by dependency (reads before writes, scan before modify)
        - Include verification steps after destructive operations
        """;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class PlanDto
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("summary")] public string? Summary { get; set; }
        [JsonPropertyName("steps")] public List<PlanStepDto>? Steps { get; set; }
        [JsonPropertyName("estimatedToolCalls")] public int? EstimatedToolCalls { get; set; }
        [JsonPropertyName("warnings")] public List<string>? Warnings { get; set; }
    }

    private sealed class PlanStepDto
    {
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("details")] public string? Details { get; set; }
        [JsonPropertyName("expectedTools")] public List<string>? ExpectedTools { get; set; }
        [JsonPropertyName("isDestructive")] public bool IsDestructive { get; set; }
        [JsonPropertyName("estimatedDuration")] public string? EstimatedDuration { get; set; }
    }
}

/// <summary>A structured execution plan.</summary>
public sealed record ExecutionPlan
{
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public required IReadOnlyList<PlanStep> Steps { get; init; }
    public int? EstimatedToolCalls { get; init; }
    public IReadOnlyList<string>? Warnings { get; init; }
}

/// <summary>A single step in an execution plan.</summary>
public sealed record PlanStep
{
    public int Index { get; init; }
    public required string Description { get; init; }
    public string? Details { get; init; }
    public IReadOnlyList<string>? ExpectedTools { get; init; }
    public bool IsDestructive { get; init; }
    public string? EstimatedDuration { get; init; }
}

/// <summary>Result of executing a single plan step.</summary>
public sealed record StepResult
{
    public int StepIndex { get; init; }
    public bool Success { get; init; }
    public string Text { get; init; } = "";
    public int ToolCallCount { get; init; }
}

/// <summary>Events emitted during plan execution for UI progress tracking.</summary>
public abstract record PlanProgressEvent
{
    public sealed record PlanStarted(ExecutionPlan Plan) : PlanProgressEvent;
    public sealed record StepStarted(int StepIndex, PlanStep Step) : PlanProgressEvent;
    public sealed record StepStreamEvent(int StepIndex, AgentStreamEvent Event) : PlanProgressEvent;
    public sealed record StepCompleted(int StepIndex, PlanStep Step, StepResult Result) : PlanProgressEvent;
    public sealed record PlanCompleted(ExecutionPlan Plan) : PlanProgressEvent;
    public sealed record PlanCancelled : PlanProgressEvent;
    public sealed record PlanFailed(string Error) : PlanProgressEvent;
}

/// <summary>
/// Tracks the state of an active plan mode session. When <see cref="IsInPlanningPhase"/>
/// is true, only read-only and planning tools are permitted.
///
/// Persisted to <c>%LocalAppData%/CEAISuite/active-plan.json</c> so it survives compaction.
/// </summary>
public sealed class PlanModeState
{
    /// <summary>The plan being executed.</summary>
    public ExecutionPlan? Plan { get; set; }

    /// <summary>Index of the current step (0-based).</summary>
    public int CurrentStepIndex { get; set; }

    /// <summary>Whether the agent is still in the planning phase (read-only tools only).</summary>
    public bool IsInPlanningPhase { get; set; }

    /// <summary>Results collected so far.</summary>
    public List<StepResult> StepResults { get; set; } = [];

    /// <summary>Save the plan state to disk.</summary>
    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        var dir = Path.GetDirectoryName(path);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = System.Text.Json.JsonSerializer.Serialize(this, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        });
        File.WriteAllText(path, json);
    }

    /// <summary>Load plan state from disk. Returns null if no saved state.</summary>
    public static PlanModeState? Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            return System.Text.Json.JsonSerializer.Deserialize<PlanModeState>(json, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch { return null; }
    }

    /// <summary>Clear saved plan state from disk.</summary>
    public static void ClearSaved(string? path = null)
    {
        path ??= DefaultPath;
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>Get a compact summary for post-compaction restoration.</summary>
    public string GetRestorationSummary()
    {
        if (Plan is null) return "No active plan.";
        var completed = StepResults.Count(r => r.Success);
        return $"Active plan: {Plan.Title}\n" +
               $"Progress: {completed}/{Plan.Steps.Count} steps completed\n" +
               $"Current step: {(CurrentStepIndex < Plan.Steps.Count ? Plan.Steps[CurrentStepIndex].Description : "done")}";
    }

    private static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CEAISuite", "active-plan.json");
}
