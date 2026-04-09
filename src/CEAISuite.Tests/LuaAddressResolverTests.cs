using CEAISuite.Engine.Lua;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for LuaAddressResolver.TryParseHex and ResolveAsync logic.
/// Pointer chain resolution requires full IEngineFacade integration and is
/// covered more thoroughly in LuaCeApiTests.
/// </summary>
public sealed class LuaAddressResolverTests
{
    // ── TryParseHex ──

    [Theory]
    [InlineData("0x1234", 0x1234UL)]
    [InlineData("0X1234", 0x1234UL)]
    [InlineData("DEADBEEF", 0xDEADBEEFUL)]
    [InlineData("0xDEADBEEF", 0xDEADBEEFUL)]
    [InlineData("0", 0UL)]
    [InlineData("0x0", 0UL)]
    [InlineData("FF", 0xFFUL)]
    [InlineData("0xFFFFFFFFFFFFFFFF", 0xFFFFFFFFFFFFFFFFUL)]
    public void TryParseHex_ValidInput_Succeeds(string input, ulong expected)
    {
        Assert.True(LuaAddressResolver.TryParseHex(input, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("XYZ")]
    [InlineData("0xGHIJ")]
    [InlineData("hello")]
    public void TryParseHex_InvalidInput_ReturnsFalse(string input)
    {
        Assert.False(LuaAddressResolver.TryParseHex(input, out _));
    }

    // ── ResolveAsync: plain hex ──

    [Fact]
    public async Task ResolveAsync_PlainHex_ReturnsAddress()
    {
        var facade = new StubEngineFacade();
        var result = await LuaAddressResolver.ResolveAsync("0x1234", 1, facade, null);
        Assert.Equal((nuint)0x1234, result);
    }

    [Fact]
    public async Task ResolveAsync_PlainHexWithout0x_ReturnsAddress()
    {
        var facade = new StubEngineFacade();
        var result = await LuaAddressResolver.ResolveAsync("ABCD", 1, facade, null);
        Assert.Equal((nuint)0xABCD, result);
    }

    // ── ResolveAsync: null/empty ──

    [Fact]
    public async Task ResolveAsync_NullExpression_ReturnsNull()
    {
        var facade = new StubEngineFacade();
        var result = await LuaAddressResolver.ResolveAsync(null!, 1, facade, null);
        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_EmptyExpression_ReturnsNull()
    {
        var facade = new StubEngineFacade();
        var result = await LuaAddressResolver.ResolveAsync("", 1, facade, null);
        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_WhitespaceExpression_ReturnsNull()
    {
        var facade = new StubEngineFacade();
        var result = await LuaAddressResolver.ResolveAsync("   ", 1, facade, null);
        Assert.Null(result);
    }

    // ── ResolveAsync: module+offset ──

    [Fact]
    public async Task ResolveAsync_ModulePlusOffset_ResolvesCorrectly()
    {
        var facade = new StubEngineFacade();
        facade.AttachModules = [new("game.exe", (nuint)0x400000, 4096)];
        await facade.AttachAsync(1);

        var result = await LuaAddressResolver.ResolveAsync("game.exe+0x100", 1, facade, null);
        Assert.Equal((nuint)0x400100, result);
    }

    [Fact]
    public async Task ResolveAsync_UnknownModule_ReturnsNull()
    {
        var facade = new StubEngineFacade();
        facade.AttachModules = [new("other.exe", (nuint)0x400000, 4096)];
        await facade.AttachAsync(1);

        var result = await LuaAddressResolver.ResolveAsync("notfound.dll+0x100", 1, facade, null);
        Assert.Null(result);
    }

    // ── ResolveAsync: registered symbol ──

    [Fact]
    public async Task ResolveAsync_RegisteredSymbol_ResolvesFromAutoAssembler()
    {
        var facade = new StubEngineFacade();
        var aa = new Stubs.StubAutoAssemblerEngine();
        aa.RegisterSymbol("mySymbol", (nuint)0xBEEF);

        var result = await LuaAddressResolver.ResolveAsync("mySymbol", 1, facade, aa);
        Assert.Equal((nuint)0xBEEF, result);
    }

    [Fact]
    public async Task ResolveAsync_UnknownSymbol_NoModule_ReturnsNull()
    {
        var facade = new StubEngineFacade();
        var aa = new Stubs.StubAutoAssemblerEngine();

        var result = await LuaAddressResolver.ResolveAsync("unknownSymbol", 1, facade, aa);
        Assert.Null(result);
    }

    // ── FindModuleBaseAsync ──

    [Fact]
    public async Task FindModuleBaseAsync_ExistingModule_ReturnsBase()
    {
        var facade = new StubEngineFacade();
        facade.AttachModules = [new("kernel32.dll", (nuint)0x76000000, 0x100000)];

        var result = await LuaAddressResolver.FindModuleBaseAsync("kernel32.dll", 1, facade, CancellationToken.None);
        Assert.Equal((nuint)0x76000000, result);
    }

    [Fact]
    public async Task FindModuleBaseAsync_CaseInsensitive()
    {
        var facade = new StubEngineFacade();
        facade.AttachModules = [new("Game.EXE", (nuint)0x400000, 4096)];

        var result = await LuaAddressResolver.FindModuleBaseAsync("game.exe", 1, facade, CancellationToken.None);
        Assert.Equal((nuint)0x400000, result);
    }

    [Fact]
    public async Task FindModuleBaseAsync_NonExistent_ReturnsNull()
    {
        var facade = new StubEngineFacade();
        facade.AttachModules = [new("main.exe", (nuint)0x400000, 4096)];

        var result = await LuaAddressResolver.FindModuleBaseAsync("nothere.dll", 1, facade, CancellationToken.None);
        Assert.Null(result);
    }

    // ── ResolveAsync: whitespace trimming ──

    [Fact]
    public async Task ResolveAsync_TrimmedInput_ResolveCorrectly()
    {
        var facade = new StubEngineFacade();
        var result = await LuaAddressResolver.ResolveAsync("  0x1234  ", 1, facade, null);
        Assert.Equal((nuint)0x1234, result);
    }
}
