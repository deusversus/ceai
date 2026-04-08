using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class SymbolToolTests
{
    private static AiToolFunctions CreateSut(
        StubEngineFacade? engine = null,
        StubSymbolEngine? symbolEngine = null)
    {
        engine ??= new StubEngineFacade();
        var sessionRepo = new StubSessionRepository();
        var dashboard = new WorkspaceDashboardService(engine, sessionRepo);
        var scanEngine = new StubScanEngine();
        var scanService = new ScanService(scanEngine);
        var addressTable = new AddressTableService(engine);
        var disassembly = new DisassemblyService(new StubDisassemblyEngine());
        var scriptGen = new ScriptGenerationService();
        return new AiToolFunctions(
            engine, dashboard, scanService, addressTable, disassembly, scriptGen,
            symbolEngine: symbolEngine);
    }

    // ── LoadSymbolsForModule ──

    [Fact]
    public async Task LoadSymbolsForModule_ReturnsSuccess_WhenSymbolsLoaded()
    {
        var engine = new StubEngineFacade();
        engine.AttachModules = [new ModuleDescriptor("GameAssembly.dll", (nuint)0x7FF00000, 0x100000)];
        var symbolEngine = new StubSymbolEngine();
        var sut = CreateSut(engine, symbolEngine);

        var result = await sut.LoadSymbolsForModule(1000, "GameAssembly.dll");

        Assert.Contains("Symbols loaded for GameAssembly.dll", result);
        Assert.Contains("0x7FF00000", result);
    }

    [Fact]
    public async Task LoadSymbolsForModule_ReturnsNotFound_WhenModuleMissing()
    {
        var engine = new StubEngineFacade();
        engine.AttachModules = [new ModuleDescriptor("main.exe", (nuint)0x400000, 4096)];
        var symbolEngine = new StubSymbolEngine();
        var sut = CreateSut(engine, symbolEngine);

        var result = await sut.LoadSymbolsForModule(1000, "NonExistent.dll");

        Assert.Contains("Module 'NonExistent.dll' not found", result);
    }

    [Fact]
    public async Task LoadSymbolsForModule_ReturnsUnavailable_WhenSymbolEngineNull()
    {
        var sut = CreateSut(symbolEngine: null);

        var result = await sut.LoadSymbolsForModule(1000, "game.dll");

        Assert.Equal("Symbol engine not available.", result);
    }

    // ── ResolveAddressToSymbol ──

    [Fact]
    public void ResolveAddressToSymbol_ReturnsDisplayName_WhenSymbolFound()
    {
        var symbolEngine = new StubSymbolEngine();
        symbolEngine.AddSymbol((nuint)0x7FF00100, "Player::TakeDamage", "GameAssembly.dll", 0x1A);
        var sut = CreateSut(symbolEngine: symbolEngine);

        var result = sut.ResolveAddressToSymbol(1000, "0x7FF00100");

        Assert.Equal("GameAssembly.dll!Player::TakeDamage+0x1A", result);
    }

    [Fact]
    public void ResolveAddressToSymbol_ReturnsNoSymbol_WhenNotFound()
    {
        var symbolEngine = new StubSymbolEngine();
        var sut = CreateSut(symbolEngine: symbolEngine);

        var result = sut.ResolveAddressToSymbol(1000, "0xDEADBEEF");

        Assert.Contains("No symbol found for address 0xDEADBEEF", result);
    }

    [Fact]
    public void ResolveAddressToSymbol_ReturnsUnavailable_WhenSymbolEngineNull()
    {
        var sut = CreateSut(symbolEngine: null);

        var result = sut.ResolveAddressToSymbol(1000, "0x7FF00100");

        Assert.Equal("Symbol engine not available.", result);
    }
}
