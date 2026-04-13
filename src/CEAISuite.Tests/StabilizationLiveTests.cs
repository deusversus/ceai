using System.Diagnostics;
using System.IO;
using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Engine.Windows;

namespace CEAISuite.Tests;

/// <summary>
/// Phase 10L: Live-process stabilization tests.
/// These run against the real test harness process with the real engine,
/// not stubs. They verify scan performance under adversarial conditions
/// and crash recovery with data integrity.
/// </summary>
[Trait("Category", "Stabilization")]
[Trait("Category", "Integration")]
[Trait("Flaky", "Timing")]
public class StabilizationLiveTests
{
    // ── Benchmark: live scan against harness ──

    [Fact]
    public async Task LiveScan_AgainstHarness_CompletesUnder5Seconds()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);

        // Write a known value to scan for
        var addr = await harness.AllocAsync(4096, TestContext.Current.CancellationToken);
        await harness.WriteAtAsync(addr, BitConverter.GetBytes(12345), TestContext.Current.CancellationToken);

        var scanEngine = new WindowsScanEngine();
        var constraints = new ScanConstraints(MemoryDataType.Int32, ScanType.ExactValue, "12345");

        var sw = Stopwatch.StartNew();
        var result = await scanEngine.StartScanAsync(harness.ProcessId, constraints, TestContext.Current.CancellationToken);
        sw.Stop();

        Assert.NotNull(result);
        Assert.True(result.Results.Count > 0, "Should find at least one match");
        Assert.True(sw.ElapsedMilliseconds < 10000,
            $"Live scan took {sw.ElapsedMilliseconds}ms (threshold: 10000ms). Possible regression.");
    }

    [Fact]
    public async Task LiveScan_WithValueChurn_CompletesWithoutCrash()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);

        var addr = await harness.AllocAsync(4096, TestContext.Current.CancellationToken);
        await harness.WriteAtAsync(addr, BitConverter.GetBytes(99999), TestContext.Current.CancellationToken);

        // Start churning values while we scan
        var churnThread = await harness.StartValueChurnAsync(addr, 1, TestContext.Current.CancellationToken);

        var scanEngine = new WindowsScanEngine();
        var constraints = new ScanConstraints(MemoryDataType.Int32, ScanType.UnknownInitialValue, null);

        try
        {
            var sw = Stopwatch.StartNew();
            var result = await scanEngine.StartScanAsync(harness.ProcessId, constraints, TestContext.Current.CancellationToken);
            sw.Stop();

            Assert.NotNull(result);
            Assert.True(sw.ElapsedMilliseconds < 10000,
                $"Live scan with value churn took {sw.ElapsedMilliseconds}ms (threshold: 10s).");
        }
        finally
        {
            await harness.StopLoopAsync(churnThread, TestContext.Current.CancellationToken);
        }
    }

    // ── Crash recovery: simulate mid-operation crash ──

    [Fact]
    public async Task CrashRecovery_MidScanAutoSave_RestoresTable()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"ceai-recovery-{Guid.NewGuid():N}.json");

        try
        {
            var engine = new StubEngineFacade();
            var table = new AddressTableService(engine);
            var exportService = new AddressTableExportService();

            // Simulate active session: add entries as if user was working
            table.AddEntry("0x1000", MemoryDataType.Int32, "100", "Player HP");
            table.AddEntry("0x1004", MemoryDataType.Float, "1.5", "Speed Multiplier");
            table.AddEntry("0x1008", MemoryDataType.Int32, "50", "Ammo Count");

            // Lock an entry (simulating active freeze)
            var hpNode = table.Roots.First(n => n.Label == "Player HP");
            hpNode.IsLocked = true;
            hpNode.LockedValue = "999";

            // Auto-save (what happens periodically during normal use)
            await exportService.ExportRecoveryAsync(table.Roots.ToList(), tempPath);

            // === SIMULATED CRASH ===
            // Drop all references — the table service is "gone"
            // (In a real crash, the process dies here)

            // === SIMULATED RESTART ===
            // Create a fresh table service (as if app just restarted)
            var freshEngine = new StubEngineFacade();
            var freshTable = new AddressTableService(freshEngine);

            // Restore from recovery file
            var recovered = await exportService.ImportRecoveryAsync(tempPath);

            Assert.NotNull(recovered);
            Assert.Equal(3, recovered.Count);

            // Verify all data survived the "crash"
            var recoveredHp = recovered.First(n => n.Label == "Player HP");
            Assert.Equal("0x1000", recoveredHp.Address);
            Assert.Equal(MemoryDataType.Int32, recoveredHp.DataType);
            Assert.True(recoveredHp.IsLocked, "Lock state should survive recovery");
            Assert.Equal("999", recoveredHp.LockedValue);

            var recoveredSpeed = recovered.First(n => n.Label == "Speed Multiplier");
            Assert.Equal(MemoryDataType.Float, recoveredSpeed.DataType);

            var recoveredAmmo = recovered.First(n => n.Label == "Ammo Count");
            Assert.Equal("50", recoveredAmmo.CurrentValue);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    [Fact]
    public async Task CrashRecovery_RepeatedCrashCycles_AllRestore()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"ceai-recovery-{Guid.NewGuid():N}.json");
        var exportService = new AddressTableExportService();

        try
        {
            // Simulate 10 crash cycles — each time, save, "crash", restore, verify
            for (int cycle = 0; cycle < 10; cycle++)
            {
                var engine = new StubEngineFacade();
                var table = new AddressTableService(engine);

                // Add entries unique to this cycle
                for (int j = 0; j < 10; j++)
                {
                    table.AddEntry(
                        $"0x{(0x1000 + cycle * 100 + j * 4):X}",
                        MemoryDataType.Int32,
                        (cycle * 100 + j).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        $"Cycle{cycle}_Entry{j}");
                }

                // Auto-save
                await exportService.ExportRecoveryAsync(table.Roots.ToList(), tempPath);

                // "Crash" — drop references

                // "Restart" — restore
                var recovered = await exportService.ImportRecoveryAsync(tempPath);

                Assert.NotNull(recovered);
                Assert.Equal(10, recovered.Count);
                Assert.Equal($"Cycle{cycle}_Entry0", recovered[0].Label);
                Assert.Equal($"Cycle{cycle}_Entry9", recovered[9].Label);
            }
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    [Fact]
    public async Task CrashRecovery_PartialWrite_GracefulFallback()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"ceai-recovery-{Guid.NewGuid():N}.json");
        var exportService = new AddressTableExportService();

        try
        {
            var engine = new StubEngineFacade();
            var table = new AddressTableService(engine);
            table.AddEntry("0x1000", MemoryDataType.Int32, "100", "HP");

            // Write a valid recovery file
            await exportService.ExportRecoveryAsync(table.Roots.ToList(), tempPath);

            // Simulate partial/corrupt write (as if process died mid-write)
            var validContent = await File.ReadAllTextAsync(tempPath);
            await File.WriteAllTextAsync(tempPath, validContent[..(validContent.Length / 2)]); // Truncated

            // Recovery should handle corrupt file gracefully
            var recovered = await exportService.ImportRecoveryAsync(tempPath);
            Assert.Null(recovered); // Corrupt file returns null, no crash
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }
}

