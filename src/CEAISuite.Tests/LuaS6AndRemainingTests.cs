using CEAISuite.Engine.Abstractions;
using CEAISuite.Engine.Lua;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for S6A AI-assisted scripting, S5 generateCallBytes/generateJmpBytes,
/// and remaining coverage items.
/// </summary>
public sealed class LuaS6AndRemainingTests : IDisposable
{
    private readonly StubEngineFacade _facade = new();
    private readonly StubAutoAssemblerEngine _assembler = new();
    private readonly StubDisassemblyEngine _disassembly = new();
    private readonly StubAiAssistant _aiAssistant = new();
    private readonly MoonSharpLuaEngine _engine;

    public LuaS6AndRemainingTests()
    {
        _facade.AttachAsync(1234).GetAwaiter().GetResult();
        _engine = new MoonSharpLuaEngine(
            _facade,
            _assembler,
            executionTimeout: TimeSpan.FromSeconds(5),
            disassemblyEngine: _disassembly,
            aiAssistant: _aiAssistant);
    }

    public void Dispose() => _engine.Dispose();

    // ── S6A: AI-assisted scripting ──

    [Fact]
    public async Task Ai_Suggest_ReturnsSuggestion()
    {
        _aiAssistant.NextResponse = "Use readInteger to read the health value";

        var result = await _engine.ExecuteAsync("""
            return ai.suggest("How do I read health in this game?")
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Contains("readInteger", result.ReturnValue);
    }

    [Fact]
    public async Task Ai_Explain_ReturnsExplanation()
    {
        _aiAssistant.NextResponse = "This function saves the player state";

        var result = await _engine.ExecuteAsync("""
            return ai.explain(0x7FF00100)
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Contains("player state", result.ReturnValue);
    }

    [Fact]
    public async Task Ai_FindPattern_ReturnsPattern()
    {
        _aiAssistant.NextResponse = "48 8B 05 ?? ?? ?? ?? 48 89 44 24";

        var result = await _engine.ExecuteAsync("""
            return ai.findPattern("health decrement function")
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Contains("48 8B 05", result.ReturnValue);
    }

    [Fact]
    public async Task Ai_Analyze_Works()
    {
        _aiAssistant.NextResponse = "This code sets up a stack frame";

        var result = await _engine.ExecuteAsync("""
            return ai.analyze(0x7FF00100)
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Contains("stack frame", result.ReturnValue);
    }

    [Fact]
    public async Task Ai_WithoutProcess_Throws()
    {
        using var bareEngine = new MoonSharpLuaEngine(
            _facade, _assembler, aiAssistant: _aiAssistant);

        var result = await bareEngine.ExecuteAsync("""
            return ai.explain(0x100)
            """);

        Assert.False(result.Success);
        Assert.Contains("No process attached", result.Error);
    }

    // ── S5: generateCallBytes / generateJmpBytes ──

    [Fact]
    public async Task GenerateCallBytes_ProducesCorrectOpcode()
    {
        var result = await _engine.ExecuteAsync("""
            local bytes = generateCallBytes(0x1000, 0x2000)
            return string.sub(bytes, 1, 2)
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("E8", result.ReturnValue); // CALL opcode
    }

    [Fact]
    public async Task GenerateJmpBytes_ProducesCorrectOpcode()
    {
        var result = await _engine.ExecuteAsync("""
            local bytes = generateJmpBytes(0x1000, 0x2000)
            return string.sub(bytes, 1, 2)
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("E9", result.ReturnValue); // JMP opcode
    }

    [Fact]
    public async Task GenerateCallBytes_CorrectOffset()
    {
        // CALL from 0x1000 to 0x2000: offset = 0x2000 - 0x1000 - 5 = 0x0FFB
        var result = await _engine.ExecuteAsync("""
            return generateCallBytes(0x1000, 0x2000)
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        // E8 FB 0F 00 00 (little-endian 0x00000FFB)
        Assert.Equal("E8 FB 0F 00 00", result.ReturnValue);
    }

    // ── Stub AI assistant ──

    private sealed class StubAiAssistant : ILuaAiAssistant
    {
        public string NextResponse { get; set; } = "AI response";

        public Task<string> SuggestAsync(string context, CancellationToken ct = default)
            => Task.FromResult(NextResponse);

        public Task<string> ExplainAsync(nuint address, int processId, CancellationToken ct = default)
            => Task.FromResult(NextResponse);

        public Task<string> FindPatternAsync(string description, int processId, CancellationToken ct = default)
            => Task.FromResult(NextResponse);
    }
}
