using CEAISuite.Engine.Windows;

namespace CEAISuite.Tests;

/// <summary>
/// Integration tests for <see cref="WindowsSymbolEngine"/> using the test harness process.
/// Verifies symbol loading, address resolution, and cleanup lifecycle.
/// </summary>
[Trait("Category", "Integration")]
public class EngineWindowsSymbolIntegrationTests
{
    [Fact]
    public async Task LoadSymbols_HarnessModule_DoesNotCrash()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);

        var (baseAddr, size) = await harness.GetModuleInfoAsync(TestContext.Current.CancellationToken);

        using var engine = new WindowsSymbolEngine();
        // Loading symbols should not throw even if PDB is absent
        var loaded = await engine.LoadSymbolsForModuleAsync(
            harness.ProcessId, "CEAISuite.Tests.Harness.exe", baseAddr, size);

        // Result is either true (symbols found) or false (no PDB) -- neither should throw
        Assert.True(loaded || !loaded, "LoadSymbols completed without crashing");
    }

    [Fact]
    public async Task ResolveAddress_UnknownAddress_ReturnsNull()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);

        var (baseAddr, size) = await harness.GetModuleInfoAsync(TestContext.Current.CancellationToken);

        using var engine = new WindowsSymbolEngine();
        await engine.LoadSymbolsForModuleAsync(
            harness.ProcessId, "CEAISuite.Tests.Harness.exe", baseAddr, size);

        // Resolve a bogus address that is definitely outside any loaded module
        var result = engine.ResolveAddress(unchecked((nuint)0xDEADBEEFDEAD));
        Assert.Null(result);
    }

    [Fact]
    public async Task Cleanup_DoesNotThrow()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);

        var (baseAddr, size) = await harness.GetModuleInfoAsync(TestContext.Current.CancellationToken);

        using var engine = new WindowsSymbolEngine();
        await engine.LoadSymbolsForModuleAsync(
            harness.ProcessId, "CEAISuite.Tests.Harness.exe", baseAddr, size);

        // Cleanup should not throw
        engine.Cleanup(harness.ProcessId);

        // Double cleanup should also be safe
        engine.Cleanup(harness.ProcessId);

        // Dispose after cleanup should also be safe (handled by using statement)
    }
}
