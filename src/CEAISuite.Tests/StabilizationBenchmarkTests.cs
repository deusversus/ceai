using System.Diagnostics;
using System.Globalization;
using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

/// <summary>
/// Phase 10L: Stabilization benchmark regression tests.
/// Verifies scan performance does not regress when speed hack is active,
/// multiplier updates are fast, apply/remove cycles are bounded, concurrent
/// scans don't deadlock, and memory allocation stays within budget.
/// </summary>
[Trait("Category", "Stabilization")]
public class StabilizationBenchmarkTests
{
    private const int Iterations = 5;
    private const int TestProcessId = 1000;

    private static (ScanService scan, SpeedHackService speedHack) CreateServices()
    {
        var scanEngine = new StubScanEngine();
        var speedHackEngine = new StubSpeedHackEngine();
        return (new ScanService(scanEngine), new SpeedHackService(speedHackEngine));
    }

    private static StubScanEngine CreateEngineWithResults(int resultCount)
    {
        var engine = new StubScanEngine();
        var results = new List<ScanResultEntry>();
        for (int i = 0; i < resultCount; i++)
        {
            results.Add(new ScanResultEntry(
                (nuint)(0x1000 + i * 4),
                (100 + i).ToString(CultureInfo.InvariantCulture),
                null,
                BitConverter.GetBytes(100 + i),
                null));
        }
        engine.NextScanResult = new ScanResultSet(
            "stab-scan", TestProcessId,
            new ScanConstraints(MemoryDataType.Int32, ScanType.ExactValue, "100"),
            results, 100, 1024 * 1024, DateTimeOffset.UtcNow);
        engine.NextRefineResult = new ScanResultSet(
            "stab-scan", TestProcessId,
            new ScanConstraints(MemoryDataType.Int32, ScanType.ExactValue, "100"),
            results.Take(resultCount / 2).ToList(), 50, 512 * 1024, DateTimeOffset.UtcNow);
        return engine;
    }

    /// <summary>Runs <paramref name="action"/> with warmup and returns the median time in ms.</summary>
    private static async Task<long> MeasureMedianAsync(Func<Task> action)
    {
        // Warmup: run once, discard
        await action();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var times = new List<long>();
        for (int i = 0; i < Iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            await action();
            sw.Stop();
            times.Add(sw.ElapsedMilliseconds);
        }

        times.Sort();
        return times[Iterations / 2];
    }

    [Fact]
    [Trait("Category", "Stabilization")]
    public async Task ScanWithSpeedHackActive_DoesNotRegress()
    {
        var scanEngine = CreateEngineWithResults(100);
        var speedHackEngine = new StubSpeedHackEngine();
        var scan = new ScanService(scanEngine);
        var speedHack = new SpeedHackService(speedHackEngine);

        // Apply speed hack first
        var applyResult = await speedHack.ApplyAsync(TestProcessId, 2.0);
        Assert.True(applyResult.Success, "Speed hack apply should succeed");

        var median = await MeasureMedianAsync(() =>
            scan.StartScanAsync(TestProcessId, MemoryDataType.Int32, ScanType.ExactValue, "100"));

        Assert.True(median < 1200,
            $"Scan with speed hack active median: {median}ms (expected < 1200ms)");
    }

    [Fact]
    [Trait("Category", "Stabilization")]
    public async Task RefineScanWithSpeedHackActive_DoesNotRegress()
    {
        var scanEngine = CreateEngineWithResults(100);
        var speedHackEngine = new StubSpeedHackEngine();
        var scan = new ScanService(scanEngine);
        var speedHack = new SpeedHackService(speedHackEngine);

        // Apply speed hack
        var applyResult = await speedHack.ApplyAsync(TestProcessId, 2.0);
        Assert.True(applyResult.Success, "Speed hack apply should succeed");

        // Initial scan for refine to work against
        await scan.StartScanAsync(TestProcessId, MemoryDataType.Int32, ScanType.ExactValue, "100");

        var median = await MeasureMedianAsync(() =>
            scan.RefineScanAsync(ScanType.ExactValue, "100"));

        Assert.True(median < 600,
            $"Refine scan with speed hack active median: {median}ms (expected < 600ms)");
    }

    [Fact]
    [Trait("Category", "Stabilization")]
    public async Task MultiplierUpdate_Latency_Under10ms()
    {
        var (_, speedHack) = CreateServices();

        // Apply speed hack
        var applyResult = await speedHack.ApplyAsync(TestProcessId, 2.0);
        Assert.True(applyResult.Success, "Speed hack apply should succeed");

        // Warmup
        await speedHack.UpdateMultiplierAsync(TestProcessId, 1.5);

        const int updateCount = 100;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < updateCount; i++)
        {
            var multiplier = 1.0 + (i % 70) * 0.1; // Varies 1.0–8.0
            var result = await speedHack.UpdateMultiplierAsync(TestProcessId, multiplier);
            Assert.True(result.Success, $"UpdateMultiplier iteration {i} should succeed");
        }
        sw.Stop();

        var averageMs = (double)sw.ElapsedMilliseconds / updateCount;
        Assert.True(averageMs < 10,
            $"Average multiplier update: {averageMs:F2}ms (expected < 10ms)");
    }

    [Fact]
    [Trait("Category", "Stabilization")]
    public async Task SpeedHackApplyRemoveCycle_CompletesInTime()
    {
        var (_, speedHack) = CreateServices();

        // Warmup
        await speedHack.ApplyAsync(TestProcessId, 2.0);
        await speedHack.RemoveAsync(TestProcessId);

        const int cycles = 10;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < cycles; i++)
        {
            var applyResult = await speedHack.ApplyAsync(TestProcessId, 2.0 + i * 0.1);
            Assert.True(applyResult.Success, $"Apply cycle {i} should succeed");

            var removeResult = await speedHack.RemoveAsync(TestProcessId);
            Assert.True(removeResult.Success, $"Remove cycle {i} should succeed");
        }
        sw.Stop();

        var perCycleMs = (double)sw.ElapsedMilliseconds / cycles;
        Assert.True(perCycleMs < 500,
            $"Per-cycle time: {perCycleMs:F2}ms (expected < 500ms per apply+remove)");
    }

    [Fact]
    [Trait("Category", "Stabilization")]
    public async Task ConcurrentScanWithSpeedHack_NoDeadlock()
    {
        var scanEngine = CreateEngineWithResults(50);
        var speedHackEngine = new StubSpeedHackEngine();
        var scan = new ScanService(scanEngine);
        var speedHack = new SpeedHackService(speedHackEngine);

        // Apply speed hack
        var applyResult = await speedHack.ApplyAsync(TestProcessId, 3.0);
        Assert.True(applyResult.Success, "Speed hack apply should succeed");

        // Run 5 concurrent scans — must all complete without deadlock
        var tasks = Enumerable.Range(0, 5).Select(i =>
            scan.StartScanAsync(TestProcessId, MemoryDataType.Int32, ScanType.ExactValue,
                (100 + i).ToString(CultureInfo.InvariantCulture)));

        var completed = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(5, completed.Length);
        Assert.All(completed, result => Assert.NotNull(result));
    }

    [Fact]
    [Trait("Category", "Stabilization")]
    public async Task MemoryAllocation_SpeedHackCycle_Bounded()
    {
        var (_, speedHack) = CreateServices();

        // Warmup
        await speedHack.ApplyAsync(TestProcessId, 2.0);
        await speedHack.RemoveAsync(TestProcessId);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = GC.GetAllocatedBytesForCurrentThread();

        const int cycles = 10;
        for (int i = 0; i < cycles; i++)
        {
            await speedHack.ApplyAsync(TestProcessId, 2.0 + i * 0.1);
            await speedHack.RemoveAsync(TestProcessId);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // 10 cycles with stubs should allocate well under 500KB
        Assert.True(allocated < 500_000,
            $"Speed hack cycles allocated {allocated:N0} bytes, expected < 500,000 bytes (500KB)");
    }
}
