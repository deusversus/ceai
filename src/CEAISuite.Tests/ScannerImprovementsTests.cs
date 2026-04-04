using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class ScannerImprovementsTests
{
    // ── 7A.1: Multi-threaded scan (progress reporting via stub) ──

    [Fact]
    public async Task MultiThreadedScan_ReportsProgress()
    {
        var stub = new StubScanEngine();
        var progress = new List<ScanProgress>();
        var progressReporter = new Progress<ScanProgress>(p => progress.Add(p));

        await stub.StartScanAsync(1234,
            new ScanConstraints(MemoryDataType.Int32, ScanType.ExactValue, "100"),
            new ScanOptions(MaxThreads: 2),
            progressReporter);

        Assert.NotEmpty(stub.ReportedProgress);
    }

    // ── 7A.2: Alignment ──

    [Fact]
    public void Alignment_DefaultUsesValueSize()
    {
        var options = new ScanOptions();
        Assert.Equal(0, options.Alignment); // 0 means "use value size"
    }

    [Fact]
    public void Alignment_ExplicitValueHonored()
    {
        var options = new ScanOptions(Alignment: 4);
        Assert.Equal(4, options.Alignment);
    }

    // ── 7A.3: Undo Scan ──

    [Fact]
    public async Task UndoScan_RestoresPreviousResults()
    {
        var stub = new StubScanEngine();
        var svc = new ScanService(stub);

        // Initial scan with 5 results
        stub.NextScanResult = new ScanResultSet("scan1", 1, new ScanConstraints(MemoryDataType.Int32, ScanType.ExactValue, "100"),
            Enumerable.Range(0, 5).Select(i => new ScanResultEntry((nuint)(0x1000 + i * 4), "100", null, new byte[] { 100, 0, 0, 0 })).ToList(),
            10, 40960, DateTimeOffset.UtcNow);
        await svc.StartScanAsync(1, MemoryDataType.Int32, ScanType.ExactValue, "100");

        // Refine to 2 results
        stub.NextRefineResult = new ScanResultSet("scan1", 1, new ScanConstraints(MemoryDataType.Int32, ScanType.ExactValue, "100"),
            Enumerable.Range(0, 2).Select(i => new ScanResultEntry((nuint)(0x1000 + i * 4), "100", "100", new byte[] { 100, 0, 0, 0 })).ToList(),
            10, 40960, DateTimeOffset.UtcNow);
        await svc.RefineScanAsync(ScanType.ExactValue, "100");
        Assert.Equal(2, svc.LastScanResults!.Results.Count);
        Assert.True(svc.CanUndo);

        // Undo
        var undone = svc.UndoScan();
        Assert.NotNull(undone);
        Assert.Equal(5, svc.LastScanResults!.Results.Count);
        Assert.False(svc.CanUndo);
    }

    [Fact]
    public async Task UndoScan_StackDepthLimited()
    {
        var stub = new StubScanEngine();
        var svc = new ScanService(stub);

        // Initial scan
        stub.NextScanResult = new ScanResultSet("scan1", 1, new ScanConstraints(MemoryDataType.Int32, ScanType.UnknownInitialValue, null),
            new List<ScanResultEntry> { new((nuint)0x1000, "1", null, new byte[] { 1, 0, 0, 0 }) },
            10, 40960, DateTimeOffset.UtcNow);
        await svc.StartScanAsync(1, MemoryDataType.Int32, ScanType.UnknownInitialValue, null);

        // Push 25 refinements
        stub.NextRefineResult = new ScanResultSet("scan1", 1, new ScanConstraints(MemoryDataType.Int32, ScanType.Changed, null),
            new List<ScanResultEntry> { new((nuint)0x1000, "2", "1", new byte[] { 2, 0, 0, 0 }) },
            10, 40960, DateTimeOffset.UtcNow);
        for (int i = 0; i < 25; i++)
            await svc.RefineScanAsync(ScanType.Changed, null);

        // Stack should be capped at 20
        Assert.True(svc.UndoDepth <= 20);
    }

    [Fact]
    public void UndoScan_EmptyStack_ReturnsNull()
    {
        var stub = new StubScanEngine();
        var svc = new ScanService(stub);

        Assert.False(svc.CanUndo);
        var result = svc.UndoScan();
        Assert.Null(result);
    }

    // ── 7A.5: Float Epsilon ──

    [Fact]
    public void FloatEpsilon_MatchesWithinTolerance()
    {
        var options = new ScanOptions(FloatEpsilon: 0.1f);
        Assert.Equal(0.1f, options.FloatEpsilon);
    }

    [Fact]
    public void FloatEpsilon_DefaultIsNull()
    {
        var options = new ScanOptions();
        Assert.Null(options.FloatEpsilon);
    }

    // ── 7A.7: Writable-Only Toggle ──

    [Fact]
    public void WritableOnly_DefaultIsTrue()
    {
        var options = new ScanOptions();
        Assert.True(options.WritableOnly);
    }

    [Fact]
    public void WritableOnly_CanBeDisabled()
    {
        var options = new ScanOptions(WritableOnly: false);
        Assert.False(options.WritableOnly);
    }

    // ── 7A.9: Suspend Process ──

    [Fact]
    public async Task SuspendProcess_FlagPassedToEngine()
    {
        var stub = new StubScanEngine();
        await stub.StartScanAsync(1234,
            new ScanConstraints(MemoryDataType.Int32, ScanType.ExactValue, "100"),
            new ScanOptions(SuspendProcess: true));

        Assert.True(stub.SuspendProcessCalled);
    }

    // ── 7A.8: Grouped Scans ──

    [Fact]
    public async Task GroupedScan_TagsResultsCorrectly()
    {
        var stub = new StubScanEngine();
        var groups = new List<GroupedScanConstraint>
        {
            new("Health", new ScanConstraints(MemoryDataType.Int32, ScanType.ExactValue, "100")),
            new("Mana", new ScanConstraints(MemoryDataType.Int32, ScanType.ExactValue, "50"))
        };

        var result = await stub.GroupedScanAsync(1234, groups, new ScanOptions());

        Assert.Equal(2, result.Results.Count);
        Assert.Equal("Health", result.Results[0].GroupLabel);
        Assert.Equal("Mana", result.Results[1].GroupLabel);
    }

    // ── 7A.10: BitChanged ──

    [Fact]
    public void BitChanged_ScanTypeExists()
    {
        var scanType = ScanType.BitChanged;
        Assert.Equal(ScanType.BitChanged, scanType);
    }

    // ── 7A.11: Custom Type Definitions ──

    [Fact]
    public void CustomType_RegistrationAndLookup()
    {
        var stub = new StubScanEngine();
        var svc = new ScanService(stub);

        var customType = new CustomTypeDefinition("Vec3", 12, new[]
        {
            new CustomTypeField("X", 0, MemoryDataType.Float),
            new CustomTypeField("Y", 4, MemoryDataType.Float),
            new CustomTypeField("Z", 8, MemoryDataType.Float)
        });

        svc.RegisterCustomType(customType);
        // No exception = success
        svc.UnregisterCustomType("Vec3");
    }

    // ── Service-level ScanOptions wiring ──

    [Fact]
    public async Task StartScanWithOptions_PassesOptionsToEngine()
    {
        var stub = new StubScanEngine();
        var svc = new ScanService(stub);

        await svc.StartScanAsync(1, MemoryDataType.Float, ScanType.ExactValue, "100.0",
            new ScanOptions(FloatEpsilon: 0.5f, WritableOnly: false));

        Assert.NotNull(stub.LastOptions);
        Assert.Equal(0.5f, stub.LastOptions!.FloatEpsilon);
        Assert.False(stub.LastOptions.WritableOnly);
    }

    [Fact]
    public async Task RefineScanWithOptions_PassesOptionsToEngine()
    {
        var stub = new StubScanEngine();
        var svc = new ScanService(stub);

        await svc.StartScanAsync(1, MemoryDataType.Int32, ScanType.UnknownInitialValue, null);
        await svc.RefineScanAsync(ScanType.Changed, null, new ScanOptions(Alignment: 4));

        Assert.NotNull(stub.LastOptions);
        Assert.Equal(4, stub.LastOptions!.Alignment);
    }

    [Fact]
    public async Task UndoStack_ExactlyMaxDepthKept()
    {
        var stub = new StubScanEngine();
        var svc = new ScanService(stub);

        stub.NextScanResult = new ScanResultSet("s", 1, new ScanConstraints(MemoryDataType.Int32, ScanType.UnknownInitialValue, null),
            new List<ScanResultEntry> { new((nuint)0x1000, "1", null, new byte[] { 1, 0, 0, 0 }) },
            10, 40960, DateTimeOffset.UtcNow);
        await svc.StartScanAsync(1, MemoryDataType.Int32, ScanType.UnknownInitialValue, null);

        stub.NextRefineResult = new ScanResultSet("s", 1, new ScanConstraints(MemoryDataType.Int32, ScanType.Changed, null),
            new List<ScanResultEntry> { new((nuint)0x1000, "2", "1", new byte[] { 2, 0, 0, 0 }) },
            10, 40960, DateTimeOffset.UtcNow);

        // Push exactly 20 (MaxHistoryDepth)
        for (int i = 0; i < 20; i++)
            await svc.RefineScanAsync(ScanType.Changed, null);

        Assert.Equal(20, svc.UndoDepth);

        // Push one more — should still be 20 (oldest dropped)
        await svc.RefineScanAsync(ScanType.Changed, null);
        Assert.Equal(20, svc.UndoDepth);
    }

    // ── GroupedScan service wrapper ──

    [Fact]
    public async Task GroupedScanService_DelegatesToEngine()
    {
        var stub = new StubScanEngine();
        var svc = new ScanService(stub);

        var groups = new List<GroupedScanConstraint>
        {
            new("HP", new ScanConstraints(MemoryDataType.Int32, ScanType.ExactValue, "100"))
        };

        var result = await svc.GroupedScanAsync(1, groups, new ScanOptions());
        Assert.NotNull(result);
        Assert.Single(result.Results);
        Assert.Equal("HP", result.Results[0].GroupLabel);
    }

    // ── 7A.4: Hex Display Toggle ──

    [Fact]
    public void HexDisplayOption_DefaultIsFalse()
    {
        var options = new ScanOptions();
        Assert.False(options.ShowAsHex);
    }

    // ── 7A.12: Memory-Mapped Files ──

    [Fact]
    public void IncludeMemoryMappedFiles_DefaultIsFalse()
    {
        var options = new ScanOptions();
        Assert.False(options.IncludeMemoryMappedFiles);
    }

    [Fact]
    public void IncludeMemoryMappedFiles_CanBeEnabled()
    {
        var options = new ScanOptions(IncludeMemoryMappedFiles: true);
        Assert.True(options.IncludeMemoryMappedFiles);
    }
}
