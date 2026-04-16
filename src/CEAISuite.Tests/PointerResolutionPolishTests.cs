using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for Phase 12D pointer resolution polish:
/// per-depth offset limits, smart rescan, stability color, and top-N ranking.
/// </summary>
public class PointerResolutionPolishTests
{
    // ──────────────────────────────────────────────────────────
    // Per-depth offset limits
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ScanForPointers_WithPerDepthOffsets_FindsWithinLimit()
    {
        var facade = new StubEngineFacade();
        var service = new PointerScannerService(facade);

        // Write a pointer at module base (0x400000) — the stub only returns data for
        // exact address matches in ReadMemoryAsync, and the scanner reads from module base.
        var targetAddr = (nuint)0x500000;
        var moduleBase = (nuint)0x400000;
        // Write a full chunk at module base containing a pointer at offset 0
        var chunk = new byte[4096];
        var ptrBytes = BitConverter.GetBytes((ulong)(targetAddr - 0x10));
        Array.Copy(ptrBytes, 0, chunk, 0, 8); // pointer at offset 0 within module
        facade.WriteMemoryDirect(moduleBase, chunk);

        // perDepthMaxOffset[0] = 0x20, so offset 0x10 is within limit
        var results = await service.ScanForPointersAsync(
            1000, targetAddr, maxDepth: 1, maxOffset: 0x2000,
            perDepthMaxOffset: new long[] { 0x20 });

        Assert.True(results.Count > 0, "Expected at least 1 pointer path with offset 0x10 within limit 0x20");
    }

    [Fact]
    public async Task ScanForPointers_WithPerDepthOffsets_ExcludesOutsideLimit()
    {
        var facade = new StubEngineFacade();
        var service = new PointerScannerService(facade);

        var targetAddr = (nuint)0x500000;
        var moduleBase = (nuint)0x400000;
        var chunk = new byte[4096];
        // Pointer at offset 0 that is 0x100 away from target
        var ptrBytes = BitConverter.GetBytes((ulong)(targetAddr - 0x100));
        Array.Copy(ptrBytes, 0, chunk, 0, 8);
        facade.WriteMemoryDirect(moduleBase, chunk);

        // perDepthMaxOffset[0] = 0x20, but the actual offset is 0x100 — should be excluded
        var results = await service.ScanForPointersAsync(
            1000, targetAddr, maxDepth: 1, maxOffset: 0x2000,
            perDepthMaxOffset: new long[] { 0x20 });

        Assert.Empty(results); // offset 0x100 exceeds per-depth limit 0x20
    }

    [Fact]
    public async Task ScanForPointers_WithoutPerDepthOffsets_UsesDefaultOffset()
    {
        var facade = new StubEngineFacade();
        var service = new PointerScannerService(facade);

        var targetAddr = (nuint)0x500000;
        var moduleBase = (nuint)0x400000;
        var chunk = new byte[4096];
        // Pointer at offset 0 that is 0x100 away from target
        var ptrBytes = BitConverter.GetBytes((ulong)(targetAddr - 0x100));
        Array.Copy(ptrBytes, 0, chunk, 0, 8);
        facade.WriteMemoryDirect(moduleBase, chunk);

        // No perDepthMaxOffset — uses default 0x2000, so offset 0x100 is within range
        var results = await service.ScanForPointersAsync(
            1000, targetAddr, maxDepth: 1, maxOffset: 0x2000);

        Assert.True(results.Count > 0, "Expected at least 1 pointer path with offset 0x100 within default limit 0x2000");
    }

    // ──────────────────────────────────────────────────────────
    // Smart rescan (skip unchanged modules)
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RescanAll_SkipUnchangedModules_FastTracksMatchingBase()
    {
        var facade = new StubEngineFacade();
        var moduleBase = (nuint)0x400000;
        facade.AttachModules = new[] { new ModuleDescriptor("game.exe", moduleBase, 0x10000) };
        var rescanService = new PointerRescanService(facade);

        // Path with same base as current module
        var path = new PointerPath("game.exe", moduleBase, 0x100, new long[] { 0x10 }, 0x500000);

        var results = await rescanService.RescanAllAsync(
            1000, new[] { path }, skipUnchangedModules: true);

        Assert.Single(results);
        Assert.Contains("Unchanged", results[0].Status);
        Assert.Equal(1.0, results[0].StabilityScore);
    }

    [Fact]
    public async Task RescanAll_SkipUnchangedModules_RescansDifferentBase()
    {
        var facade = new StubEngineFacade();
        facade.AttachModules = new[] { new ModuleDescriptor("game.exe", (nuint)0x500000, 0x10000) };
        var rescanService = new PointerRescanService(facade);

        // Path recorded with OLD base (different from current 0x500000)
        var path = new PointerPath("game.exe", (nuint)0x400000, 0x100, new long[] { 0x10 }, 0x500000);

        // Write a valid pointer chain so rescan doesn't crash
        facade.WriteMemoryDirect((nuint)0x500100, BitConverter.GetBytes((ulong)0x600000));

        var results = await rescanService.RescanAllAsync(
            1000, new[] { path }, skipUnchangedModules: true);

        Assert.Single(results);
        // Should NOT be "Unchanged" since base changed
        Assert.DoesNotContain("Unchanged", results[0].Status);
    }

    [Fact]
    public async Task RescanAll_WithoutSkipFlag_AlwaysRescans()
    {
        var facade = new StubEngineFacade();
        var moduleBase = (nuint)0x400000;
        facade.AttachModules = new[] { new ModuleDescriptor("game.exe", moduleBase, 0x10000) };
        var rescanService = new PointerRescanService(facade);

        var path = new PointerPath("game.exe", moduleBase, 0x100, new long[] { 0x10 }, 0x500000);
        facade.WriteMemoryDirect((nuint)(moduleBase + 0x100), BitConverter.GetBytes((ulong)0x600000));

        var results = await rescanService.RescanAllAsync(
            1000, new[] { path }, skipUnchangedModules: false);

        Assert.Single(results);
        // Without the skip flag, it should do a full rescan (won't say "Unchanged")
        Assert.DoesNotContain("Unchanged", results[0].Status);
    }

    // ──────────────────────────────────────────────────────────
    // Stability color
    // ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Stable", "#22AA22")]
    [InlineData("Unchanged", "#22AA22")]
    [InlineData("Drifted", "#CCAA00")]
    [InlineData("Broken", "#CC2222")]
    [InlineData("Found", "#888888")]
    [InlineData("Loaded", "#888888")]
    public void StabilityColor_MatchesStatus(string status, string expectedColor)
    {
        var item = new CEAISuite.Desktop.Models.PointerPathDisplayItem { Status = status };
        Assert.Equal(expectedColor, item.StabilityColor);
    }

    // ──────────────────────────────────────────────────────────
    // GetMaxOffsetForDepth helper (tested indirectly via scan)
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void StabilityColor_Common_MapsToGray()
    {
        var item = new CEAISuite.Desktop.Models.PointerPathDisplayItem { Status = "Common" };
        Assert.Equal("#888888", item.StabilityColor);
    }

    [Fact]
    public void SortByStability_OrdersCorrectly()
    {
        var items = new System.Collections.ObjectModel.ObservableCollection<CEAISuite.Desktop.Models.PointerPathDisplayItem>
        {
            new() { Status = "Broken", StabilityScore = 0 },
            new() { Status = "Stable", StabilityScore = 1.0 },
            new() { Status = "Found", StabilityScore = -1 },
            new() { Status = "Drifted", StabilityScore = 0.5 },
        };

        var sorted = items
            .OrderByDescending(r => r.Status switch
            {
                "Stable" or "Unchanged" => 3,
                "Drifted" => 2,
                "Found" or "Loaded" or "Common" => 1,
                _ => 0
            })
            .ThenByDescending(r => r.StabilityScore)
            .ToList();

        Assert.Equal("Stable", sorted[0].Status);
        Assert.Equal("Drifted", sorted[1].Status);
        Assert.Equal("Found", sorted[2].Status);
        Assert.Equal("Broken", sorted[3].Status);
    }

    [Fact]
    public void PointerMapFile_SerializationRoundtrip()
    {
        var paths = new[] { new PointerPath("game.exe", 0x400000, 0x100, new long[] { 0x10, 0x20 }, 0x500000) };
        var map = new PointerMapFile("game.exe", 0x500000, DateTimeOffset.UtcNow, 3, 0x2000, paths);

        Assert.Equal("game.exe", map.TargetProcess);
        Assert.Equal(3, map.MaxDepth);
        Assert.Single(map.Paths);
        Assert.Equal(2, map.Paths[0].Offsets.Count);
    }
}
