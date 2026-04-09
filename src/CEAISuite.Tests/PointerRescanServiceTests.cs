using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Tests;

public class PointerRescanServiceTests
{
    private static StubEngineFacade CreateFacadeWithPointerChain(nuint moduleBase, long moduleOffset, nuint[] pointerValues, long[] offsets)
    {
        var facade = new StubEngineFacade();
        facade.AttachModules = new[] { new ModuleDescriptor("game.exe", moduleBase, 0x10000) };

        // Set up the pointer chain:
        // moduleBase + moduleOffset => read pointer => pointerValues[0]
        // pointerValues[0] + offsets[0] => read pointer => pointerValues[1]
        // ...
        nuint currentAddr = (nuint)((long)moduleBase + moduleOffset);

        for (int i = 0; i < pointerValues.Length; i++)
        {
            // Write pointer value at current address
            facade.WriteMemoryDirect(currentAddr, BitConverter.GetBytes((ulong)pointerValues[i]));
            if (i < offsets.Length)
                currentAddr = (nuint)((long)pointerValues[i] + offsets[i]);
        }

        return facade;
    }

    [Fact]
    public async Task RescanPathAsync_ValidChain_ReturnsValid()
    {
        // Chain: game.exe+0x100 -> [+0x10] -> final address 0x50010
        var facade = new StubEngineFacade();
        facade.AttachModules = new[] { new ModuleDescriptor("game.exe", (nuint)0x400000, 0x10000) };

        // At game.exe+0x100 (= 0x400100), store a pointer to 0x500000
        facade.WriteMemoryDirect((nuint)0x400100, BitConverter.GetBytes((ulong)0x500000));
        // Final address will be 0x500000 + 0x10 = 0x500010

        var path = new PointerPath("game.exe", (nuint)0x400000, 0x100, new long[] { 0x10 }, (nuint)0x500010);
        var svc = new PointerRescanService(facade);

        var result = await svc.RescanPathAsync(1, path, (nuint)0x500010);

        Assert.True(result.IsValid);
        Assert.Equal((nuint)0x500010, result.NewResolvedAddress);
        Assert.Equal(1.0, result.StabilityScore);
        Assert.Equal("Valid", result.Status);
    }

    [Fact]
    public async Task RescanPathAsync_ModuleNotFound_ReturnsInvalid()
    {
        var facade = new StubEngineFacade();
        facade.AttachModules = new[] { new ModuleDescriptor("game.exe", (nuint)0x400000, 0x10000) };

        var path = new PointerPath("missing.dll", (nuint)0, 0x100, new long[] { 0x10 }, (nuint)0x500010);
        var svc = new PointerRescanService(facade);

        var result = await svc.RescanPathAsync(1, path);

        Assert.False(result.IsValid);
        Assert.Null(result.NewResolvedAddress);
        Assert.Contains("not found", result.Status);
    }

    [Fact]
    public async Task RescanPathAsync_NullPointer_ReturnsInvalid()
    {
        var facade = new StubEngineFacade();
        facade.AttachModules = new[] { new ModuleDescriptor("game.exe", (nuint)0x400000, 0x10000) };

        // Write zero pointer at the start of the chain
        facade.WriteMemoryDirect((nuint)0x400100, BitConverter.GetBytes((ulong)0));

        var path = new PointerPath("game.exe", (nuint)0x400000, 0x100, new long[] { 0x10 }, (nuint)0x500010);
        var svc = new PointerRescanService(facade);

        var result = await svc.RescanPathAsync(1, path);

        Assert.False(result.IsValid);
        Assert.Contains("Null pointer", result.Status);
    }

    [Fact]
    public async Task RescanPathAsync_ShortChain_GetsStabilityBonus()
    {
        var facade = new StubEngineFacade();
        facade.AttachModules = new[] { new ModuleDescriptor("game.exe", (nuint)0x400000, 0x10000) };

        // Single-offset chain (short = more stable)
        facade.WriteMemoryDirect((nuint)0x400100, BitConverter.GetBytes((ulong)0x500000));

        var path = new PointerPath("game.exe", (nuint)0x400000, 0x100, new long[] { 0x10 }, (nuint)0x500010);
        var svc = new PointerRescanService(facade);

        var result = await svc.RescanPathAsync(1, path, (nuint)0x500010);

        // Short chain (1 offset <= 2) gets +0.1, and .exe module gets +0.05
        Assert.True(result.StabilityScore >= 1.0);
    }

    [Fact]
    public async Task RescanAllAsync_MultiplePathsSortedByStability()
    {
        var facade = new StubEngineFacade();
        facade.AttachModules = new[] { new ModuleDescriptor("game.exe", (nuint)0x400000, 0x10000) };

        // Path 1: valid chain
        facade.WriteMemoryDirect((nuint)0x400100, BitConverter.GetBytes((ulong)0x500000));

        // Path 2: will have module not found
        var path1 = new PointerPath("game.exe", (nuint)0x400000, 0x100, new long[] { 0x10 }, (nuint)0x500010);
        var path2 = new PointerPath("missing.dll", (nuint)0, 0x200, new long[] { 0x20 }, (nuint)0x600020);

        var svc = new PointerRescanService(facade);
        var results = await svc.RescanAllAsync(1, new[] { path1, path2 }, (nuint)0x500010);

        Assert.Equal(2, results.Count);
        // First result should be the valid one (higher stability)
        Assert.True(results[0].IsValid);
        Assert.False(results[1].IsValid);
    }

    [Fact]
    public async Task ValidateAndRecoverAsync_AllInvalid_NeedsFreshScan()
    {
        var facade = new StubEngineFacade();
        facade.AttachModules = new[] { new ModuleDescriptor("game.exe", (nuint)0x400000, 0x10000) };

        // Null pointer chain => all invalid
        var path1 = new PointerPath("game.exe", (nuint)0x400000, 0x100, new long[] { 0x10 }, (nuint)0x500010);
        var path2 = new PointerPath("game.exe", (nuint)0x400000, 0x200, new long[] { 0x20 }, (nuint)0x600020);

        var svc = new PointerRescanService(facade);
        var (validated, needsFresh) = await svc.ValidateAndRecoverAsync(1, new[] { path1, path2 }, (nuint)0x500010);

        Assert.True(needsFresh);
        Assert.Equal(2, validated.Count);
    }

    [Fact]
    public async Task ValidateAndRecoverAsync_SomeValid_DoesNotNeedFreshScan()
    {
        var facade = new StubEngineFacade();
        facade.AttachModules = new[] { new ModuleDescriptor("game.exe", (nuint)0x400000, 0x10000) };

        // One valid chain
        facade.WriteMemoryDirect((nuint)0x400100, BitConverter.GetBytes((ulong)0x500000));

        var path1 = new PointerPath("game.exe", (nuint)0x400000, 0x100, new long[] { 0x10 }, (nuint)0x500010);
        var path2 = new PointerPath("game.exe", (nuint)0x400000, 0x200, new long[] { 0x20 }, (nuint)0x600020);

        var svc = new PointerRescanService(facade);
        var (validated, needsFresh) = await svc.ValidateAndRecoverAsync(1, new[] { path1, path2 }, (nuint)0x500010);

        Assert.False(needsFresh);
    }

    [Fact]
    public async Task RescanPathAsync_NoOffsets_ResolvesToModuleBaseOffset()
    {
        var facade = new StubEngineFacade();
        facade.AttachModules = new[] { new ModuleDescriptor("game.exe", (nuint)0x400000, 0x10000) };

        // Empty offset list means the resolved address is simply moduleBase + moduleOffset
        var path = new PointerPath("game.exe", (nuint)0x400000, 0x100, Array.Empty<long>(), (nuint)0x400100);
        var svc = new PointerRescanService(facade);

        var result = await svc.RescanPathAsync(1, path, (nuint)0x400100);

        Assert.True(result.IsValid);
        Assert.Equal((nuint)0x400100, result.NewResolvedAddress);
    }
}
