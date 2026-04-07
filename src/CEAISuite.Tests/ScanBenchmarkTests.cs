using System.Diagnostics;
using System.Globalization;
using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

/// <summary>
/// Phase 9D: Scan engine performance benchmarks with warmup, multi-iteration, and memory tracking.
/// </summary>
public class ScanBenchmarkTests
{
    private const int Iterations = 5;

    private static StubScanEngine CreateEngineWithResults(int resultCount = 0)
    {
        var engine = new StubScanEngine();
        if (resultCount > 0)
        {
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
                "bench-scan", 1000,
                new ScanConstraints(MemoryDataType.Int32, ScanType.ExactValue, "100"),
                results, 100, 1024 * 1024, DateTimeOffset.UtcNow);
            engine.NextRefineResult = new ScanResultSet(
                "bench-scan", 1000,
                new ScanConstraints(MemoryDataType.Int32, ScanType.ExactValue, "100"),
                results.Take(resultCount / 2).ToList(), 50, 512 * 1024, DateTimeOffset.UtcNow);
        }
        return engine;
    }

    /// <summary>Runs <paramref name="action"/> with warmup and returns the median time in ms.</summary>
    private static async Task<(long MedianMs, long AllocatedBytes)> MeasureAsync(Func<Task> action)
    {
        // Warmup: run once, discard
        await action();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var times = new List<long>();
        long totalAllocated = 0;

        for (int i = 0; i < Iterations; i++)
        {
            var allocBefore = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            await action();
            sw.Stop();
            var allocAfter = GC.GetAllocatedBytesForCurrentThread();

            times.Add(sw.ElapsedMilliseconds);
            totalAllocated += allocAfter - allocBefore;
        }

        times.Sort();
        var median = times[Iterations / 2];
        return (median, totalAllocated / Iterations);
    }

    [Fact]
    public async Task NewScan_CompletesWithinThreshold()
    {
        var engine = CreateEngineWithResults(100);
        var sut = new ScanService(engine);

        var (median, allocBytes) = await MeasureAsync(() =>
            sut.StartScanAsync(1000, MemoryDataType.Int32, ScanType.ExactValue, "100"));

        Assert.True(median < 1000,
            $"New scan median: {median}ms (expected < 1000ms), avg alloc: {allocBytes:N0} bytes");
    }

    [Fact]
    public async Task RefineScan_CompletesWithinThreshold()
    {
        var engine = CreateEngineWithResults(100);
        var sut = new ScanService(engine);
        await sut.StartScanAsync(1000, MemoryDataType.Int32, ScanType.ExactValue, "100");

        var (median, allocBytes) = await MeasureAsync(() =>
            sut.RefineScanAsync(ScanType.ExactValue, "100"));

        Assert.True(median < 500,
            $"Refine scan median: {median}ms (expected < 500ms), avg alloc: {allocBytes:N0} bytes");
    }

    [Fact]
    public async Task UndoScan_CompletesWithinThreshold()
    {
        var engine = CreateEngineWithResults(100);
        var sut = new ScanService(engine);
        await sut.StartScanAsync(1000, MemoryDataType.Int32, ScanType.ExactValue, "100");
        await sut.RefineScanAsync(ScanType.ExactValue, "100");

        // Undo is sync, but we still measure with warmup pattern
        var times = new List<long>();
        // Warmup (need to rebuild state each time since undo consumes the stack)
        sut.UndoScan();
        await sut.RefineScanAsync(ScanType.ExactValue, "100");

        for (int i = 0; i < Iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            var undone = sut.UndoScan();
            sw.Stop();
            times.Add(sw.ElapsedMilliseconds);
            Assert.NotNull(undone);

            // Rebuild stack for next iteration
            if (i < Iterations - 1)
                await sut.RefineScanAsync(ScanType.ExactValue, "100");
        }

        times.Sort();
        var median = times[Iterations / 2];
        Assert.True(median < 100,
            $"Undo scan median: {median}ms (expected < 100ms)");
    }

    [Fact]
    public async Task MultipleSequentialScans_NoDegradation()
    {
        var engine = CreateEngineWithResults(50);
        var sut = new ScanService(engine);

        // Warmup
        await sut.StartScanAsync(1000, MemoryDataType.Int32, ScanType.ExactValue, "100");

        var times = new List<long>();
        for (int i = 0; i < 10; i++)
        {
            var sw = Stopwatch.StartNew();
            await sut.StartScanAsync(1000, MemoryDataType.Int32, ScanType.ExactValue,
                (100 + i).ToString(CultureInfo.InvariantCulture));
            sw.Stop();
            times.Add(sw.ElapsedMilliseconds);
        }

        // No single scan should exceed the threshold
        Assert.All(times, t => Assert.True(t < 1000,
            $"Sequential scan took {t}ms, expected < 1000ms"));

        // Last scan should not be significantly slower than first
        // (allow 5x tolerance for runtime variability)
        if (times[0] > 0)
        {
            Assert.True(times[^1] <= times[0] * 5 + 50,
                $"Last scan ({times[^1]}ms) significantly slower than first ({times[0]}ms)");
        }
    }

    [Fact]
    public async Task LargeResultSet_CompletesWithinThreshold()
    {
        var engine = CreateEngineWithResults(1500);
        var sut = new ScanService(engine);

        var (median, allocBytes) = await MeasureAsync(() =>
            sut.StartScanAsync(1000, MemoryDataType.Int32, ScanType.ExactValue, "100"));

        Assert.True(median < 2000,
            $"Large result scan median: {median}ms (expected < 2000ms), avg alloc: {allocBytes:N0} bytes");
    }

    [Fact]
    public async Task NewScan_MemoryAllocationBounded()
    {
        var engine = CreateEngineWithResults(500);
        var sut = new ScanService(engine);

        // Warmup
        await sut.StartScanAsync(1000, MemoryDataType.Int32, ScanType.ExactValue, "100");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = GC.GetAllocatedBytesForCurrentThread();

        await sut.StartScanAsync(1000, MemoryDataType.Int32, ScanType.ExactValue, "100");

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // 500 results @ ~100 bytes each ≈ 50KB; allow 10x overhead for framework allocations
        Assert.True(allocated < 5_000_000,
            $"Scan allocated {allocated:N0} bytes, expected < 5MB for 500 results");
    }
}
