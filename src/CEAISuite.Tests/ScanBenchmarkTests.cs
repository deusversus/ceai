using System.Diagnostics;
using System.Globalization;
using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class ScanBenchmarkTests
{
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

    [Fact]
    public async Task NewScan_CompletesWithinThreshold()
    {
        var engine = CreateEngineWithResults(100);
        var sut = new ScanService(engine);

        var sw = Stopwatch.StartNew();
        await sut.StartScanAsync(1000, MemoryDataType.Int32, ScanType.ExactValue, "100");
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"New scan took {sw.ElapsedMilliseconds}ms, expected < 1000ms");
    }

    [Fact]
    public async Task RefineScan_CompletesWithinThreshold()
    {
        var engine = CreateEngineWithResults(100);
        var sut = new ScanService(engine);
        await sut.StartScanAsync(1000, MemoryDataType.Int32, ScanType.ExactValue, "100");

        var sw = Stopwatch.StartNew();
        await sut.RefineScanAsync(ScanType.ExactValue, "100");
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 500,
            $"Refine scan took {sw.ElapsedMilliseconds}ms, expected < 500ms");
    }

    [Fact]
    public async Task UndoScan_CompletesWithinThreshold()
    {
        var engine = CreateEngineWithResults(100);
        var sut = new ScanService(engine);
        await sut.StartScanAsync(1000, MemoryDataType.Int32, ScanType.ExactValue, "100");
        await sut.RefineScanAsync(ScanType.ExactValue, "100");

        var sw = Stopwatch.StartNew();
        var undone = sut.UndoScan();
        sw.Stop();

        Assert.NotNull(undone);
        Assert.True(sw.ElapsedMilliseconds < 100,
            $"Undo scan took {sw.ElapsedMilliseconds}ms, expected < 100ms");
    }

    [Fact]
    public async Task MultipleSequentialScans_NoDegradation()
    {
        var engine = CreateEngineWithResults(50);
        var sut = new ScanService(engine);

        var times = new List<long>();
        for (int i = 0; i < 10; i++)
        {
            var sw = Stopwatch.StartNew();
            await sut.StartScanAsync(1000, MemoryDataType.Int32, ScanType.ExactValue, (100 + i).ToString(CultureInfo.InvariantCulture));
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

        var sw = Stopwatch.StartNew();
        var overview = await sut.StartScanAsync(1000, MemoryDataType.Int32, ScanType.ExactValue, "100");
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"Large result scan took {sw.ElapsedMilliseconds}ms, expected < 2000ms");
        Assert.Equal(1500, overview.ResultCount);
    }
}
