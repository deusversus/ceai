using CEAISuite.Engine.Lua;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for structure definition Lua bindings: createStructure, addElement,
/// getElement, autoGuess, toCStruct.
/// </summary>
public sealed class LuaStructureBindingsTests : IDisposable
{
    private readonly StubEngineFacade _facade = new();
    private readonly StubStructureProvider _provider = new();
    private readonly MoonSharpLuaEngine _engine;

    public LuaStructureBindingsTests()
    {
        _facade.AttachAsync(1234).GetAwaiter().GetResult();
        _engine = new MoonSharpLuaEngine(
            _facade,
            structureProvider: _provider,
            executionTimeout: TimeSpan.FromSeconds(5));
    }

    public void Dispose() => _engine.Dispose();

    [Fact]
    public async Task CreateStructure_ReturnsTable()
    {
        var result = await _engine.ExecuteAsync("""
            local s = createStructure("PlayerData")
            return s.Name
            """);

        Assert.True(result.Success, result.Error);
        Assert.Equal("PlayerData", result.ReturnValue);
    }

    [Fact]
    public async Task AddElement_IncreasesCount()
    {
        var result = await _engine.ExecuteAsync("""
            local s = createStructure("Test")
            s.addElement(0, "Int32", "Health")
            s.addElement(4, "Float", "Speed")
            return s.getElementCount()
            """);

        Assert.True(result.Success, result.Error);
        Assert.Equal("2", result.ReturnValue);
    }

    [Fact]
    public async Task GetElement_ReturnsFieldData()
    {
        var result = await _engine.ExecuteAsync("""
            local s = createStructure("Test")
            s.addElement(0, "Int32", "Health")
            local field = s.getElement(0)
            return field.Name .. ":" .. field.Type
            """);

        Assert.True(result.Success, result.Error);
        Assert.Equal("Health:Int32", result.ReturnValue);
    }

    [Fact]
    public async Task GetElement_Offset()
    {
        var result = await _engine.ExecuteAsync("""
            local s = createStructure("Test")
            s.addElement(16, "Pointer", "NextPtr")
            local field = s.getElement(0)
            return field.Offset
            """);

        Assert.True(result.Success, result.Error);
        Assert.Equal("16", result.ReturnValue);
    }

    [Fact]
    public async Task ToCStruct_ReturnsFormattedText()
    {
        var result = await _engine.ExecuteAsync("""
            local s = createStructure("GameState")
            s.addElement(0, "Int32", "score")
            s.addElement(4, "Float", "time")
            return s.toCStruct()
            """);

        Assert.True(result.Success, result.Error);
        Assert.Contains("GameState", result.ReturnValue);
        Assert.Contains("score", result.ReturnValue);
        Assert.Contains("time", result.ReturnValue);
    }

    [Fact]
    public async Task AutoGuess_PopulatesFields()
    {
        var result = await _engine.ExecuteAsync("""
            local s = createStructure("AutoStruct")
            s.autoGuess(0x1000, 256)
            return s.getElementCount()
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        // StubStructureProvider.DissectMemory returns 3 canned fields
        Assert.Equal("3", result.ReturnValue);
    }

    [Fact]
    public async Task Destroy_RemovesStructure()
    {
        var result = await _engine.ExecuteAsync("""
            local s = createStructure("Temp")
            s.addElement(0, "Int32", "x")
            s.destroy()
            local list = listStructures()
            return #list
            """);

        Assert.True(result.Success, result.Error);
        Assert.Equal("0", result.ReturnValue);
    }

    [Fact]
    public async Task ListStructures_ReturnsAll()
    {
        var result = await _engine.ExecuteAsync("""
            createStructure("A")
            createStructure("B")
            createStructure("C")
            local list = listStructures()
            return #list
            """);

        Assert.True(result.Success, result.Error);
        Assert.Equal("3", result.ReturnValue);
    }

    [Fact]
    public async Task Standalone_StructureGetName()
    {
        var result = await _engine.ExecuteAsync("""
            local s = createStructure("MyStruct")
            return structure_getName(s)
            """);

        Assert.True(result.Success, result.Error);
        Assert.Equal("MyStruct", result.ReturnValue);
    }

    [Fact]
    public async Task Standalone_StructureAddAndGetElement()
    {
        var result = await _engine.ExecuteAsync("""
            local s = createStructure("Test")
            structure_addElement(s, 8, "Double", "velocity")
            return structure_getElementCount(s)
            """);

        Assert.True(result.Success, result.Error);
        Assert.Equal("1", result.ReturnValue);
    }
}
