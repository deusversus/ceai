using System.IO;
using System.Runtime.CompilerServices;
using CEAISuite.Application;
using CEAISuite.Application.AgentLoop;
using Microsoft.Extensions.AI;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for the subagent system: <see cref="SubagentPresets"/>, <see cref="SubagentDefinition"/>,
/// <see cref="AgentDefinitionLoader"/>, <see cref="SubagentManager"/> spawning/cancellation,
/// and <see cref="SubagentRequest"/> construction.
/// </summary>
public class SubagentSystemTests : IDisposable
{
    private readonly string _tempDir;

    public SubagentSystemTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ceai-subagent-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── SubagentPresets ─────────────────────────────────────────────

    [Fact]
    public void SubagentPresets_Explore_HasReadOnlyToolPatterns()
    {
        var req = SubagentPresets.Explore("find the HP address");

        Assert.Contains("[Explore]", req.Description);
        Assert.Equal("find the HP address", req.Task);
        Assert.NotNull(req.AllowedToolPatterns);
        Assert.Contains("Read*", req.AllowedToolPatterns);
        Assert.Contains("Disassemble*", req.AllowedToolPatterns);
        Assert.Equal(15, req.MaxTurns);
        Assert.Equal(0.2, req.BudgetFraction);
    }

    [Fact]
    public void SubagentPresets_Plan_HasMetaToolPatterns()
    {
        var req = SubagentPresets.Plan("create a scan strategy");

        Assert.Contains("[Plan]", req.Description);
        Assert.NotNull(req.AllowedToolPatterns);
        Assert.Contains("plan_task", req.AllowedToolPatterns);
        Assert.Equal(10, req.MaxTurns);
        Assert.Equal(0.15, req.BudgetFraction);
    }

    [Fact]
    public void SubagentPresets_Verify_IncludesVerificationContext()
    {
        var req = SubagentPresets.Verify("check HP changed", "expected HP = 100");

        Assert.Contains("[Verify]", req.Description);
        Assert.NotNull(req.Context);
        Assert.Contains("VERIFICATION TASK", req.Context);
        Assert.Contains("expected HP = 100", req.Context);
    }

    [Fact]
    public void SubagentPresets_Script_HasScriptToolPatterns()
    {
        var req = SubagentPresets.Script("create a freeze script");

        Assert.Contains("[Script]", req.Description);
        Assert.NotNull(req.AllowedToolPatterns);
        Assert.Contains("*Script*", req.AllowedToolPatterns);
        Assert.Equal(0.25, req.BudgetFraction);
    }

    [Fact]
    public void SubagentPresets_LongTask_Truncated()
    {
        var longTask = new string('x', 200);
        var req = SubagentPresets.Explore(longTask);
        Assert.True(req.Description.Length < longTask.Length);
        Assert.Contains("...", req.Description);
    }

    // ── SubagentRequest ─────────────────────────────────────────────

    [Fact]
    public void SubagentRequest_OptionalFields_DefaultCorrectly()
    {
        var req = new SubagentRequest
        {
            Task = "test task",
            Description = "test desc",
        };

        Assert.Null(req.Context);
        Assert.Null(req.AllowedToolPatterns);
        Assert.Null(req.MaxTurns);
        Assert.Null(req.BudgetFraction);
        Assert.Null(req.ContextProvider);
        Assert.Null(req.ApprovalBubbleCallback);
        Assert.Null(req.WorkingDirectory);
    }

    // ── SubagentResult ──────────────────────────────────────────────

    [Fact]
    public void SubagentResult_Properties_SetCorrectly()
    {
        var result = new SubagentResult
        {
            Id = "sub-1",
            Success = true,
            Text = "found it",
            ToolCallCount = 5,
            Duration = TimeSpan.FromSeconds(10),
        };

        Assert.Equal("sub-1", result.Id);
        Assert.True(result.Success);
        Assert.Equal("found it", result.Text);
        Assert.Equal(5, result.ToolCallCount);
        Assert.Equal(10, result.Duration.TotalSeconds);
    }

    // ── SubagentDefinition ──────────────────────────────────────────

    [Fact]
    public void SubagentDefinition_ToRequest_MapsCorrectly()
    {
        var def = new SubagentDefinition
        {
            Name = "investigate",
            Description = "Investigation agent",
            AllowedToolPatterns = ["Read*", "List*"],
            MaxTurns = 20,
        };

        var req = def.ToRequest("find the value at 0x1000", "some context");

        Assert.Contains("[investigate]", req.Description);
        Assert.Equal("find the value at 0x1000", req.Task);
        Assert.Equal("some context", req.Context);
        Assert.NotNull(req.AllowedToolPatterns);
        Assert.Equal(2, req.AllowedToolPatterns.Count);
        Assert.Equal(20, req.MaxTurns);
    }

    [Fact]
    public void SubagentDefinition_Defaults_AreReasonable()
    {
        var def = new SubagentDefinition { Name = "test" };
        Assert.Equal(15, def.MaxTurns);
        Assert.Null(def.Description);
        Assert.Null(def.AllowedToolPatterns);
        Assert.Null(def.Model);
        Assert.Null(def.Instructions);
    }

    // ── AgentDefinitionLoader ───────────────────────────────────────

    [Fact]
    public void LoadFromDirectory_ValidMdFile_ParsesCorrectly()
    {
        File.WriteAllText(Path.Combine(_tempDir, "explore.md"), """
            ---
            name: explore
            tools: ["Read*", "List*", "Get*"]
            maxTurns: 10
            description: Read-only investigation agent
            ---
            You are a focused investigation agent.
            """);

        var defs = AgentDefinitionLoader.LoadFromDirectory(_tempDir);
        Assert.Single(defs);
        Assert.Equal("explore", defs[0].Name);
        Assert.Equal("Read-only investigation agent", defs[0].Description);
        Assert.Equal(10, defs[0].MaxTurns);
        Assert.NotNull(defs[0].AllowedToolPatterns);
        Assert.Equal(3, defs[0].AllowedToolPatterns!.Count);
        Assert.Contains("Read*", defs[0].AllowedToolPatterns!);
        Assert.Contains("You are a focused investigation agent.", defs[0].Instructions!);
    }

    [Fact]
    public void LoadFromDirectory_NoFrontmatter_ReturnsNull()
    {
        File.WriteAllText(Path.Combine(_tempDir, "nofm.md"), "Just some text.");

        var defs = AgentDefinitionLoader.LoadFromDirectory(_tempDir);
        Assert.Empty(defs);
    }

    [Fact]
    public void LoadFromDirectory_EmptyDirectory_ReturnsEmpty()
    {
        var defs = AgentDefinitionLoader.LoadFromDirectory(_tempDir);
        Assert.Empty(defs);
    }

    [Fact]
    public void LoadFromDirectory_NonexistentDirectory_ReturnsEmpty()
    {
        var defs = AgentDefinitionLoader.LoadFromDirectory(Path.Combine(_tempDir, "nonexistent"));
        Assert.Empty(defs);
    }

    [Fact]
    public void LoadFromFile_MissingToolsField_DefaultsToNull()
    {
        var path = Path.Combine(_tempDir, "minimal.md");
        File.WriteAllText(path, """
            ---
            name: minimal
            description: Minimal agent
            ---
            Body text.
            """);

        var def = AgentDefinitionLoader.LoadFromFile(path);
        Assert.NotNull(def);
        Assert.Equal("minimal", def.Name);
        Assert.Null(def.AllowedToolPatterns);
        Assert.Equal(15, def.MaxTurns); // default
    }

    [Fact]
    public void LoadFromFile_NameFromFilename_WhenNotInFrontmatter()
    {
        var path = Path.Combine(_tempDir, "my-agent.md");
        File.WriteAllText(path, """
            ---
            description: Agent without name field
            ---
            Body.
            """);

        var def = AgentDefinitionLoader.LoadFromFile(path);
        Assert.NotNull(def);
        Assert.Equal("my-agent", def.Name);
    }

    [Fact]
    public void LoadFromDirectory_MultipleMdFiles_LoadsAll()
    {
        File.WriteAllText(Path.Combine(_tempDir, "agent-a.md"), """
            ---
            name: agent-a
            description: First agent
            ---
            Body A.
            """);
        File.WriteAllText(Path.Combine(_tempDir, "agent-b.md"), """
            ---
            name: agent-b
            description: Second agent
            ---
            Body B.
            """);

        var defs = AgentDefinitionLoader.LoadFromDirectory(_tempDir);
        Assert.Equal(2, defs.Count);
        Assert.Contains(defs, d => d.Name == "agent-a");
        Assert.Contains(defs, d => d.Name == "agent-b");
    }

    // ── SubagentManager: spawn + cancel ─────────────────────────────

    [Fact]
    public async Task SubagentManager_Spawn_TracksActiveSubagent()
    {
        var mockClient = new MockChatClient("subagent response");
        var options = CreateTestOptions();
        var cache = new ToolAttributeCache();
        var manager = new SubagentManager(mockClient, options, cache);

        var request = new SubagentRequest
        {
            Task = "find the HP address",
            Description = "HP finder",
        };

        var handle = manager.Spawn(request);
        Assert.NotNull(handle);
        Assert.Equal("HP finder", handle.Description);
        Assert.StartsWith("subagent-", handle.Id);

        // Wait for completion
        var result = await handle.Task;
        Assert.True(result.Success);
        Assert.Contains("subagent response", result.Text);
    }

    [Fact]
    public async Task SubagentManager_CancelAll_CancelsActiveSubagents()
    {
        var mockClient = new SlowMockChatClient();
        var options = CreateTestOptions();
        var cache = new ToolAttributeCache();
        var manager = new SubagentManager(mockClient, options, cache);

        var request = new SubagentRequest
        {
            Task = "long running task",
            Description = "slow task",
        };

        var handle = manager.Spawn(request);

        // Small delay to let it start
        await Task.Delay(50);

        manager.CancelAll();

        var result = await handle.Task;
        Assert.False(result.Success);
        Assert.Contains("cancelled", result.Text);
    }

    [Fact]
    public void SubagentHandle_Cancel_CancelsToken()
    {
        var cts = new CancellationTokenSource();
        var handle = new SubagentHandle
        {
            Id = "sub-test",
            Description = "test",
            StartedAt = DateTimeOffset.UtcNow,
            CancellationSource = cts,
        };

        Assert.False(cts.IsCancellationRequested);
        handle.Cancel();
        Assert.True(cts.IsCancellationRequested);

        cts.Dispose();
    }

    // ── Test Helpers ────────────────────────────────────────────────

    private static AgentLoopOptions CreateTestOptions() => new()
    {
        SystemPrompt = "You are a test assistant.",
        Tools = new List<AITool>(),
        Limits = TokenLimits.Balanced,
        ToolResultStore = new ToolResultStore(),
        DangerousToolNames = new HashSet<string>(),
        MaxTurns = 3,
        Log = (level, msg) => { },
    };
}
