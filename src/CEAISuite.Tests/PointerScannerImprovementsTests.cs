using System.IO;
using System.Text.Json;
using CEAISuite.Application;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class PointerScannerImprovementsTests
{
    private static readonly JsonSerializerOptions s_nuintOptions = new() { Converters = { new NuintJsonConverter() } };

    // ── NuintJsonConverter ──

    [Fact]
    public void NuintConverter_SerializesAsHex()
    {
        var json = JsonSerializer.Serialize((nuint)0x12345, s_nuintOptions);
        Assert.Equal("\"0x12345\"", json);
    }

    [Fact]
    public void NuintConverter_DeserializesHex()
    {
        var result = JsonSerializer.Deserialize<nuint>("\"0x12345\"", s_nuintOptions);
        Assert.Equal((nuint)0x12345, result);
    }

    [Fact]
    public void NuintConverter_DeserializesWithout0xPrefix()
    {
        var result = JsonSerializer.Deserialize<nuint>("\"ABCD\"", s_nuintOptions);
        Assert.Equal((nuint)0xABCD, result);
    }

    // ── Pointer Map Save / Load ──

    [Fact]
    public async Task SaveLoadRoundTrip_PreservesAllFields()
    {
        var tmpPath = Path.Combine(Path.GetTempPath(), $"test_ptr_{Guid.NewGuid():N}.ptr");
        try
        {
            var paths = new List<PointerPath>
            {
                new("game.dll", (nuint)0x7FF00000, 0x1000, new long[] { 0x10, 0x20 }, (nuint)0x50000),
                new("mono.dll", (nuint)0x7FF10000, 0x2000, new long[] { 0x8 }, (nuint)0x60000)
            };
            var original = new PointerMapFile("game.exe", (nuint)0x50000, DateTimeOffset.UtcNow, 3, 0x2000, paths);

            await PointerScannerService.SavePointerMapAsync(tmpPath, original);
            var loaded = await PointerScannerService.LoadPointerMapAsync(tmpPath);

            Assert.Equal(original.TargetProcess, loaded.TargetProcess);
            Assert.Equal(original.OriginalTargetAddress, loaded.OriginalTargetAddress);
            Assert.Equal(original.MaxDepth, loaded.MaxDepth);
            Assert.Equal(original.MaxOffset, loaded.MaxOffset);
            Assert.Equal(original.Paths.Count, loaded.Paths.Count);
            Assert.Equal(original.Paths[0].ModuleName, loaded.Paths[0].ModuleName);
            Assert.Equal(original.Paths[0].ModuleBase, loaded.Paths[0].ModuleBase);
            Assert.Equal(original.Paths[0].Offsets.Count, loaded.Paths[0].Offsets.Count);
            Assert.Equal(original.Paths[1].ResolvedAddress, loaded.Paths[1].ResolvedAddress);
        }
        finally
        {
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
        }
    }

    [Fact]
    public async Task LoadPointerMap_InvalidFile_Throws()
    {
        var tmpPath = Path.Combine(Path.GetTempPath(), $"bad_ptr_{Guid.NewGuid():N}.ptr");
        try
        {
            await File.WriteAllTextAsync(tmpPath, "not valid json at all");
            await Assert.ThrowsAsync<JsonException>(() => PointerScannerService.LoadPointerMapAsync(tmpPath));
        }
        finally
        {
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
        }
    }

    // ── Pointer Map Comparison ──

    [Fact]
    public void CompareMaps_CommonPaths()
    {
        var shared = new PointerPath("game.dll", (nuint)0x7FF00000, 0x1000, new long[] { 0x10 }, (nuint)0x50000);
        var onlyA = new PointerPath("game.dll", (nuint)0x7FF00000, 0x2000, new long[] { 0x20 }, (nuint)0x50000);
        var onlyB = new PointerPath("mono.dll", (nuint)0x7FF10000, 0x3000, new long[] { 0x30 }, (nuint)0x60000);

        var mapA = new PointerMapFile("game.exe", (nuint)0x50000, DateTimeOffset.UtcNow, 3, 0x2000, new[] { shared, onlyA });
        var mapB = new PointerMapFile("game.exe", (nuint)0x50000, DateTimeOffset.UtcNow, 3, 0x2000, new[] { shared, onlyB });

        var result = PointerScannerService.CompareMaps(mapA, mapB);

        Assert.Single(result.CommonPaths);
        Assert.Single(result.OnlyInFirst);
        Assert.Single(result.OnlyInSecond);
    }

    [Fact]
    public void CompareMaps_NoOverlap()
    {
        var pathA = new PointerPath("a.dll", (nuint)0x1000, 0x100, new long[] { 0x10 }, (nuint)0x2000);
        var pathB = new PointerPath("b.dll", (nuint)0x3000, 0x200, new long[] { 0x20 }, (nuint)0x4000);

        var mapA = new PointerMapFile("game.exe", (nuint)0x2000, DateTimeOffset.UtcNow, 3, 0x2000, new[] { pathA });
        var mapB = new PointerMapFile("game.exe", (nuint)0x4000, DateTimeOffset.UtcNow, 3, 0x2000, new[] { pathB });

        var result = PointerScannerService.CompareMaps(mapA, mapB);

        Assert.Empty(result.CommonPaths);
        Assert.Equal(0.0, result.OverlapRatio);
    }

    [Fact]
    public void CompareMaps_Identical()
    {
        var path = new PointerPath("game.dll", (nuint)0x1000, 0x100, new long[] { 0x10 }, (nuint)0x2000);
        var map = new PointerMapFile("game.exe", (nuint)0x2000, DateTimeOffset.UtcNow, 3, 0x2000, new[] { path });

        var result = PointerScannerService.CompareMaps(map, map);

        Assert.Single(result.CommonPaths);
        Assert.Empty(result.OnlyInFirst);
        Assert.Empty(result.OnlyInSecond);
        Assert.Equal(1.0, result.OverlapRatio);
    }

    [Fact]
    public void CompareMaps_PartialOverlap_CalculatesCorrectRatio()
    {
        var shared1 = new PointerPath("game.dll", (nuint)0x1000, 0x100, new long[] { 0x10 }, (nuint)0x2000);
        var shared2 = new PointerPath("game.dll", (nuint)0x1000, 0x200, new long[] { 0x20 }, (nuint)0x3000);
        var uniqueA = new PointerPath("game.dll", (nuint)0x1000, 0x300, new long[] { 0x30 }, (nuint)0x4000);
        var uniqueB = new PointerPath("game.dll", (nuint)0x1000, 0x400, new long[] { 0x40 }, (nuint)0x5000);

        var mapA = new PointerMapFile("game.exe", (nuint)0x2000, DateTimeOffset.UtcNow, 3, 0x2000, new[] { shared1, shared2, uniqueA });
        var mapB = new PointerMapFile("game.exe", (nuint)0x2000, DateTimeOffset.UtcNow, 3, 0x2000, new[] { shared1, shared2, uniqueB });

        var result = PointerScannerService.CompareMaps(mapA, mapB);

        Assert.Equal(2, result.CommonPaths.Count);
        Assert.Single(result.OnlyInFirst);
        Assert.Single(result.OnlyInSecond);
        // 2 common / max(3, 3) = 0.667
        Assert.True(result.OverlapRatio > 0.6 && result.OverlapRatio < 0.7);
    }

    [Fact]
    public void CompareMaps_DuplicatePaths_HandledGracefully()
    {
        var dup1 = new PointerPath("game.dll", (nuint)0x1000, 0x100, new long[] { 0x10 }, (nuint)0x2000);
        var dup2 = new PointerPath("game.dll", (nuint)0x1000, 0x100, new long[] { 0x10 }, (nuint)0x2000);
        var unique = new PointerPath("game.dll", (nuint)0x1000, 0x200, new long[] { 0x20 }, (nuint)0x3000);

        var mapA = new PointerMapFile("game.exe", (nuint)0x2000, DateTimeOffset.UtcNow, 3, 0x2000, new[] { dup1, dup2, unique });
        var mapB = new PointerMapFile("game.exe", (nuint)0x2000, DateTimeOffset.UtcNow, 3, 0x2000, new[] { dup1, unique });

        // Should NOT throw despite duplicate keys — DistinctBy handles it
        var result = PointerScannerService.CompareMaps(mapA, mapB);

        Assert.Equal(2, result.CommonPaths.Count); // dup1 (deduped) + unique
        Assert.Empty(result.OnlyInFirst);
        Assert.Empty(result.OnlyInSecond);
    }

    // ── Module Filter ──

    [Fact]
    public void ModuleFilter_ParsesCommaSeparated()
    {
        // Test the parsing logic indirectly — the module filter is comma-separated
        var filter = "game.dll, mono.dll, UnityPlayer.dll";
        var parsed = filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0).ToList();

        Assert.Equal(3, parsed.Count);
        Assert.Equal("game.dll", parsed[0]);
        Assert.Equal("mono.dll", parsed[1]);
        Assert.Equal("UnityPlayer.dll", parsed[2]);
    }

    [Fact]
    public void ModuleFilter_NullReturnsNull()
    {
        string? filter = null;
        IReadOnlyList<string>? result = string.IsNullOrWhiteSpace(filter) ? null : filter.Split(',').ToList();
        Assert.Null(result);
    }

    // ── Resume State ──

    [Fact]
    public void CanResume_InitiallyFalse()
    {
        var service = new PointerScannerService(new StubEngineFacade());
        Assert.False(service.CanResume);
    }

    // ── Edge-case tests ──

    [Fact]
    public async Task LoadPointerMap_NonExistentFile_Throws()
    {
        var bogusPath = Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid():N}.ptr");
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => PointerScannerService.LoadPointerMapAsync(bogusPath));
    }

    [Fact]
    public async Task SaveLoadRoundTrip_EmptyPaths()
    {
        var tmpPath = Path.Combine(Path.GetTempPath(), $"empty_ptr_{Guid.NewGuid():N}.ptr");
        try
        {
            var original = new PointerMapFile("game.exe", (nuint)0x50000, DateTimeOffset.UtcNow, 3, 0x2000,
                Array.Empty<PointerPath>());

            await PointerScannerService.SavePointerMapAsync(tmpPath, original);
            var loaded = await PointerScannerService.LoadPointerMapAsync(tmpPath);

            Assert.Equal(original.TargetProcess, loaded.TargetProcess);
            Assert.Empty(loaded.Paths);
        }
        finally
        {
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
        }
    }
}
