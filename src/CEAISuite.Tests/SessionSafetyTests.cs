using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for Phase 12C session auto-save and safety features:
/// undo depth warning, orphaned operation detection, and settings-driven intervals.
/// </summary>
public class SessionSafetyTests
{
    // ──────────────────────────────────────────────────────────
    // PatchUndoService — depth warning
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task UndoDepthWarning_FiresAtThreshold()
    {
        var facade = new StubEngineFacade();
        var service = new PatchUndoService(facade);

        int? warningCount = null;
        service.UndoDepthWarning += count => warningCount = count;

        // Write 449 patches — should not fire
        for (int i = 0; i < 449; i++)
        {
            facade.WriteMemoryDirect((nuint)(0x1000 + i), new byte[] { 0 });
            await service.WriteWithUndoAsync(1000, (nuint)(0x1000 + i), MemoryDataType.Byte, "1");
        }

        Assert.Null(warningCount);

        // Write the 450th — should fire
        facade.WriteMemoryDirect(0x2000, new byte[] { 0 });
        await service.WriteWithUndoAsync(1000, 0x2000, MemoryDataType.Byte, "1");

        Assert.NotNull(warningCount);
        Assert.Equal(450, warningCount);
    }

    [Fact]
    public async Task UndoDepthWarning_DoesNotFireBelowThreshold()
    {
        var facade = new StubEngineFacade();
        var service = new PatchUndoService(facade);

        bool fired = false;
        service.UndoDepthWarning += _ => fired = true;

        facade.WriteMemoryDirect(0x1000, new byte[] { 0 });
        await service.WriteWithUndoAsync(1000, 0x1000, MemoryDataType.Byte, "42");

        Assert.False(fired);
    }

    // ──────────────────────────────────────────────────────────
    // OperationJournal — orphaned entry detection
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void GetOrphanedEntries_ReturnsEmpty_WhenNoActiveEntries()
    {
        var journal = new OperationJournal();
        var orphaned = journal.GetOrphanedEntries();
        Assert.Empty(orphaned);
    }

    // ──────────────────────────────────────────────────────────
    // Settings-driven interval
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void AutoSaveIntervalMinutes_DefaultIsFive()
    {
        var settingsService = new AppSettingsService();
        Assert.Equal(5, settingsService.Settings.AutoSaveIntervalMinutes);
    }

    [Fact]
    public void AutoSaveIntervalMinutes_CanBeChanged()
    {
        var settingsService = new AppSettingsService();
        settingsService.Settings.AutoSaveIntervalMinutes = 2;
        Assert.Equal(2, settingsService.Settings.AutoSaveIntervalMinutes);
    }
}
