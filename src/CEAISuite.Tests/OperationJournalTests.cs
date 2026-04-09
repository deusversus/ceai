using System.IO;
using CEAISuite.Application;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for <see cref="OperationJournal"/>: recording, rollback, groups, persistence.
/// </summary>
public class OperationJournalTests : IDisposable
{
    private readonly string _tempDir;

    public OperationJournalTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ceai-journal-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // ── RecordOperation ─────────────────────────────────────────────

    [Fact]
    public void RecordOperation_SingleEntry_AppearsInGetEntries()
    {
        var journal = new OperationJournal();
        journal.RecordOperation("op-1", "breakpoint", (nuint)0x1000, "hardware", null, () => Task.FromResult(true));

        var entries = journal.GetEntries();
        Assert.Single(entries);
        Assert.Equal("op-1", entries[0].OperationId);
        Assert.Equal("breakpoint", entries[0].OperationType);
        Assert.Equal((nuint)0x1000, entries[0].Address);
        Assert.Equal("hardware", entries[0].Mode);
        Assert.Equal(JournalEntryStatus.Active, entries[0].Status);
    }

    [Fact]
    public void RecordOperation_MultipleEntries_AllTracked()
    {
        var journal = new OperationJournal();
        journal.RecordOperation("op-1", "bp", (nuint)0x1000, "hw", null, () => Task.FromResult(true));
        journal.RecordOperation("op-2", "hook", (nuint)0x2000, "sw", null, () => Task.FromResult(true));

        var entries = journal.GetEntries();
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public void RecordOperation_WithGroup_GroupTracked()
    {
        var journal = new OperationJournal();
        journal.RecordOperation("op-1", "bp", (nuint)0x1000, "hw", "group-A", () => Task.FromResult(true));
        journal.RecordOperation("op-2", "bp", (nuint)0x2000, "hw", "group-A", () => Task.FromResult(true));

        var groupEntries = journal.GetGroupEntries("group-A");
        Assert.Equal(2, groupEntries.Count);
    }

    [Fact]
    public void GetGroupEntries_UnknownGroup_ReturnsEmpty()
    {
        var journal = new OperationJournal();
        var entries = journal.GetGroupEntries("nonexistent");
        Assert.Empty(entries);
    }

    // ── RollbackOperationAsync ──────────────────────────────────────

    [Fact]
    public async Task RollbackOperationAsync_Success_MarksRolledBack()
    {
        var journal = new OperationJournal();
        journal.RecordOperation("op-1", "bp", (nuint)0x1000, "hw", null, () => Task.FromResult(true));

        var result = await journal.RollbackOperationAsync("op-1");
        Assert.True(result.Success);
        Assert.Equal(1, result.TotalOperations);
        Assert.Equal(1, result.SucceededRollbacks);

        var entry = journal.GetEntries().First(e => e.OperationId == "op-1");
        Assert.Equal(JournalEntryStatus.RolledBack, entry.Status);
    }

    [Fact]
    public async Task RollbackOperationAsync_Failure_MarksRollbackFailed()
    {
        var journal = new OperationJournal();
        journal.RecordOperation("op-1", "bp", (nuint)0x1000, "hw", null, () => Task.FromResult(false));

        var result = await journal.RollbackOperationAsync("op-1");
        Assert.False(result.Success);

        var entry = journal.GetEntries().First(e => e.OperationId == "op-1");
        Assert.Equal(JournalEntryStatus.RollbackFailed, entry.Status);
    }

    [Fact]
    public async Task RollbackOperationAsync_NotFound_ReturnsFalse()
    {
        var journal = new OperationJournal();
        var result = await journal.RollbackOperationAsync("no-such-op");
        Assert.False(result.Success);
        Assert.Contains("not found", result.Message);
    }

    [Fact]
    public async Task RollbackOperationAsync_AlreadyRolledBack_SkipsGracefully()
    {
        var journal = new OperationJournal();
        journal.RecordOperation("op-1", "bp", (nuint)0x1000, "hw", null, () => Task.FromResult(true));

        await journal.RollbackOperationAsync("op-1");
        var second = await journal.RollbackOperationAsync("op-1");
        Assert.True(second.Success);
        Assert.Contains("Already", second.Message);
    }

    [Fact]
    public async Task RollbackOperationAsync_ThrowingAction_HandledGracefully()
    {
        var journal = new OperationJournal();
        journal.RecordOperation("op-1", "bp", (nuint)0x1000, "hw", null,
            () => throw new InvalidOperationException("boom"));

        var result = await journal.RollbackOperationAsync("op-1");
        Assert.False(result.Success);
    }

    // ── RollbackGroupAsync ──────────────────────────────────────────

    [Fact]
    public async Task RollbackGroupAsync_AllSucceed_ReportsSuccess()
    {
        var journal = new OperationJournal();
        journal.RecordOperation("op-1", "bp", (nuint)0x1000, "hw", "grp", () => Task.FromResult(true));
        journal.RecordOperation("op-2", "bp", (nuint)0x2000, "hw", "grp", () => Task.FromResult(true));

        var result = await journal.RollbackGroupAsync("grp");
        Assert.True(result.Success);
        Assert.Equal(2, result.TotalOperations);
        Assert.Equal(2, result.SucceededRollbacks);
    }

    [Fact]
    public async Task RollbackGroupAsync_PartialFailure_ReportsFailures()
    {
        var journal = new OperationJournal();
        journal.RecordOperation("op-1", "bp", (nuint)0x1000, "hw", "grp", () => Task.FromResult(true));
        journal.RecordOperation("op-2", "bp", (nuint)0x2000, "hw", "grp", () => Task.FromResult(false));

        var result = await journal.RollbackGroupAsync("grp");
        Assert.False(result.Success);
        Assert.Equal(2, result.TotalOperations);
        Assert.Equal(1, result.SucceededRollbacks);
        Assert.NotNull(result.FailedOperations);
        Assert.Single(result.FailedOperations);
    }

    [Fact]
    public async Task RollbackGroupAsync_UnknownGroup_ReturnsFailure()
    {
        var journal = new OperationJournal();
        var result = await journal.RollbackGroupAsync("no-such-group");
        Assert.False(result.Success);
        Assert.Contains("not found", result.Message);
    }

    [Fact]
    public async Task RollbackGroupAsync_RollsBackInReverseOrder()
    {
        var journal = new OperationJournal();
        var rollbackOrder = new List<string>();

        journal.RecordOperation("op-1", "bp", (nuint)0x1000, "hw", "grp",
            () => { rollbackOrder.Add("op-1"); return Task.FromResult(true); });
        journal.RecordOperation("op-2", "bp", (nuint)0x2000, "hw", "grp",
            () => { rollbackOrder.Add("op-2"); return Task.FromResult(true); });

        await journal.RollbackGroupAsync("grp");

        Assert.Equal(2, rollbackOrder.Count);
        Assert.Equal("op-2", rollbackOrder[0]); // Last recorded, first rolled back
        Assert.Equal("op-1", rollbackOrder[1]);
    }

    // ── Persistence ─────────────────────────────────────────────────

    [Fact]
    public void Persistence_RecordedEntries_SurvivedRestart()
    {
        // First "session" — write
        var journal1 = new OperationJournal(_tempDir);
        journal1.RecordOperation("op-persist", "bp", (nuint)0xDEAD, "hw", "grp-persist",
            () => Task.FromResult(true));

        // Second "session" — read
        var journal2 = new OperationJournal(_tempDir);
        var entries = journal2.GetEntries();
        Assert.Single(entries);
        Assert.Equal("op-persist", entries[0].OperationId);
        Assert.Equal((nuint)0xDEAD, entries[0].Address);
    }

    [Fact]
    public void Persistence_OrphanedEntries_DetectedCorrectly()
    {
        var journal1 = new OperationJournal(_tempDir);
        journal1.RecordOperation("op-orphan", "bp", (nuint)0xBEEF, "hw", null,
            () => Task.FromResult(true));

        // Reload — orphaned because no rollback action survives serialization
        var journal2 = new OperationJournal(_tempDir);
        var orphans = journal2.GetOrphanedEntries();
        Assert.Single(orphans);
        Assert.Equal("op-orphan", orphans[0].OperationId);
    }

    [Fact]
    public async Task Persistence_OrphanedEntry_CannotRollback_ReturnsMessage()
    {
        var journal1 = new OperationJournal(_tempDir);
        journal1.RecordOperation("op-orphan", "bp", (nuint)0xBEEF, "hw", null,
            () => Task.FromResult(true));

        var journal2 = new OperationJournal(_tempDir);
        var result = await journal2.RollbackOperationAsync("op-orphan");
        Assert.False(result.Success);
        Assert.Contains("no rollback action", result.Message);
    }

    [Fact]
    public void Persistence_NullDirectory_WorksInMemoryOnly()
    {
        var journal = new OperationJournal(journalDirectory: null);
        journal.RecordOperation("op-mem", "bp", (nuint)0x1000, "hw", null,
            () => Task.FromResult(true));

        Assert.Single(journal.GetEntries());
    }

    // ── GetEntries ordering ─────────────────────────────────────────

    [Fact]
    public void GetEntries_OrderedByTimestampDescending()
    {
        var journal = new OperationJournal();
        journal.RecordOperation("op-first", "bp", (nuint)0x1000, "hw", null, () => Task.FromResult(true));
        // Ensure a small time gap
        Thread.Sleep(10);
        journal.RecordOperation("op-second", "bp", (nuint)0x2000, "hw", null, () => Task.FromResult(true));

        var entries = journal.GetEntries();
        Assert.Equal(2, entries.Count);
        Assert.Equal("op-second", entries[0].OperationId); // Most recent first
        Assert.Equal("op-first", entries[1].OperationId);
    }
}
