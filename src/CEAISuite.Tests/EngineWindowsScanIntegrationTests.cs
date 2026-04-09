using System.Globalization;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Engine.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace CEAISuite.Tests;

/// <summary>
/// Integration tests for <see cref="WindowsScanEngine"/> against the real
/// CEAISuite.Tests.Harness process.
/// </summary>
[Trait("Category", "Integration")]
public class EngineWindowsScanIntegrationTests
{
    [Fact]
    public async Task EnumerateRegionsAsync_HarnessProcess_ReturnsRegions()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);
        var scanner = new WindowsScanEngine();

        var regions = await scanner.EnumerateRegionsAsync(harness.ProcessId, TestContext.Current.CancellationToken);

        Assert.NotEmpty(regions);
        // A running process always has readable regions (code, stack, heap)
        Assert.Contains(regions, r => r.IsReadable);
    }

    [Fact]
    public async Task EnumerateRegionsAsync_IncludesAllocatedRegion()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);
        var scanner = new WindowsScanEngine();

        // Allocate RWX memory in the harness
        var allocResp = await harness.SendCommandAsync("ALLOC 4096");
        Assert.NotNull(allocResp);
        Assert.StartsWith("ALLOC_OK:", allocResp);
        var address = nuint.Parse(allocResp.Split(':')[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        var regions = await scanner.EnumerateRegionsAsync(harness.ProcessId, TestContext.Current.CancellationToken);

        // The allocated region should appear in the enumeration
        var matchingRegion = regions.FirstOrDefault(r =>
            address >= r.BaseAddress && address < r.BaseAddress + (nuint)r.RegionSize);
        Assert.NotNull(matchingRegion);
        Assert.True(matchingRegion.IsReadable, "Allocated region should be readable");
        Assert.True(matchingRegion.IsWritable, "Allocated region should be writable");
        Assert.True(matchingRegion.IsExecutable, "Allocated RWX region should be executable");
    }

    [Fact]
    public async Task StartScanAsync_ExactInt32_FindsAllocatedValue()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);
        var scanner = new WindowsScanEngine();
        var facade = new WindowsEngineFacade(NullLogger<WindowsEngineFacade>.Instance);
        await facade.AttachAsync(harness.ProcessId, TestContext.Current.CancellationToken);

        // Allocate memory and write a distinctive Int32 value
        var allocResp = await harness.SendCommandAsync("ALLOC 4096");
        Assert.NotNull(allocResp);
        var address = nuint.Parse(allocResp!.Split(':')[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        // Write a unique sentinel value unlikely to appear naturally
        const int sentinel = 0x13_37_BE_EF;
        await facade.WriteValueAsync(
            harness.ProcessId, address, MemoryDataType.Int32,
            sentinel.ToString(CultureInfo.InvariantCulture),
            TestContext.Current.CancellationToken);

        // Scan for it
        var constraints = new ScanConstraints(MemoryDataType.Int32, ScanType.ExactValue, sentinel.ToString(CultureInfo.InvariantCulture));
        var results = await scanner.StartScanAsync(harness.ProcessId, constraints, TestContext.Current.CancellationToken);

        Assert.NotEmpty(results.Results);
        // The exact address we wrote to must be among the results
        Assert.Contains(results.Results, r => r.Address == address);
    }
}
