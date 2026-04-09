using System.IO;
using CEAISuite.Domain;
using CEAISuite.Persistence.Sqlite;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for SqliteInvestigationSessionRepository using temp database files.
/// Verifies InitializeAsync, SaveAsync, LoadAsync, ListRecentAsync, DeleteAsync round-trips.
/// </summary>
public class SqliteSessionRepositoryTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private readonly SqliteInvestigationSessionRepository _repo;

    public SqliteSessionRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ceai-test-{Guid.NewGuid():N}.db");
        _repo = new SqliteInvestigationSessionRepository(_dbPath);
    }

    public async ValueTask InitializeAsync()
    {
        await _repo.InitializeAsync();
    }

    public ValueTask DisposeAsync()
    {
        try { File.Delete(_dbPath); } catch { /* best-effort */ }
        return ValueTask.CompletedTask;
    }

    private static InvestigationSession CreateTestSession(
        string id = "test-session-1",
        string processName = "Game.exe",
        int? processId = 1234)
    {
        return new InvestigationSession(
            id,
            processName,
            processId,
            DateTimeOffset.UtcNow,
            AddressEntries:
            [
                new AddressEntry("addr-1", "HP", "Game.exe+1A4", "Int32", "100", "Player HP", ["health"]),
            ],
            ScanSessions:
            [
                new ScanSession("scan-1", "ExactValue", "100", ["Changed to 95"], 3),
            ],
            ActionLog:
            [
                new AIActionLog("log-1", "Find HP", ["ReadMemory"], "Found HP at offset", true, "Success"),
            ]);
    }

    // ── InitializeAsync ──

    [Fact]
    public async Task InitializeAsync_CreatesTable_DoesNotThrow()
    {
        // InitializeAsync already called in InitializeAsync lifecycle
        // Calling again should be idempotent (CREATE TABLE IF NOT EXISTS)
        await _repo.InitializeAsync();
    }

    // ── SaveAsync + LoadAsync round-trip ──

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTrips()
    {
        var session = CreateTestSession();
        await _repo.SaveAsync(session);

        var loaded = await _repo.LoadAsync("test-session-1");

        Assert.NotNull(loaded);
        Assert.Equal("test-session-1", loaded.Id);
        Assert.Equal("Game.exe", loaded.ProcessName);
        Assert.Equal(1234, loaded.ProcessId);
        Assert.Single(loaded.AddressEntries);
        Assert.Equal("HP", loaded.AddressEntries[0].Label);
        Assert.Single(loaded.ScanSessions);
        Assert.Single(loaded.ActionLog);
    }

    [Fact]
    public async Task LoadAsync_NonExistent_ReturnsNull()
    {
        var loaded = await _repo.LoadAsync("does-not-exist");
        Assert.Null(loaded);
    }

    [Fact]
    public async Task SaveAsync_NullProcessId_Preserved()
    {
        var session = CreateTestSession(id: "null-pid", processId: null);
        await _repo.SaveAsync(session);

        var loaded = await _repo.LoadAsync("null-pid");
        Assert.NotNull(loaded);
        Assert.Null(loaded.ProcessId);
    }

    // ── Upsert behavior ──

    [Fact]
    public async Task SaveAsync_Twice_UpsertsCorrectly()
    {
        var session1 = CreateTestSession(id: "upsert-test");
        await _repo.SaveAsync(session1);

        // Save again with different data
        var session2 = new InvestigationSession(
            "upsert-test", "Updated.exe", 9999,
            DateTimeOffset.UtcNow,
            [], [], []);
        await _repo.SaveAsync(session2);

        var loaded = await _repo.LoadAsync("upsert-test");
        Assert.NotNull(loaded);
        Assert.Equal("Updated.exe", loaded.ProcessName);
        Assert.Equal(9999, loaded.ProcessId);
    }

    // ── ListRecentAsync ──

    [Fact]
    public async Task ListRecentAsync_ReturnsOrderedByDate()
    {
        // Save three sessions with different timestamps
        var older = new InvestigationSession(
            "old", "Old.exe", 1, DateTimeOffset.UtcNow.AddHours(-2), [], [], []);
        var middle = new InvestigationSession(
            "mid", "Mid.exe", 2, DateTimeOffset.UtcNow.AddHours(-1), [], [], []);
        var newest = new InvestigationSession(
            "new", "New.exe", 3, DateTimeOffset.UtcNow,
            [new AddressEntry("a1", "X", "0x100", "Int32", "0", null, [])],
            [],
            [new AIActionLog("l1", "test", [], "ok", true, "done")]);

        await _repo.SaveAsync(older);
        await _repo.SaveAsync(middle);
        await _repo.SaveAsync(newest);

        var recent = await _repo.ListRecentAsync(10);
        Assert.Equal(3, recent.Count);
        Assert.Equal("new", recent[0].Id); // Most recent first
        Assert.Equal("mid", recent[1].Id);
        Assert.Equal("old", recent[2].Id);
    }

    [Fact]
    public async Task ListRecentAsync_RespectsLimit()
    {
        for (int i = 0; i < 5; i++)
        {
            var s = new InvestigationSession(
                $"sess-{i}", "Game.exe", i,
                DateTimeOffset.UtcNow.AddMinutes(i), [], [], []);
            await _repo.SaveAsync(s);
        }

        var recent = await _repo.ListRecentAsync(2);
        Assert.Equal(2, recent.Count);
    }

    [Fact]
    public async Task ListRecentAsync_ReturnsMetadata()
    {
        var session = CreateTestSession(id: "meta-test");
        await _repo.SaveAsync(session);

        var list = await _repo.ListRecentAsync(10);
        Assert.Single(list);
        var meta = list[0];
        Assert.Equal("meta-test", meta.Id);
        Assert.Equal("Game.exe", meta.ProcessName);
        Assert.Equal(1234, meta.ProcessId);
        Assert.Equal(1, meta.AddressEntryCount);
        Assert.Equal(1, meta.ScanSessionCount);
        Assert.Equal(1, meta.ActionLogCount);
    }

    [Fact]
    public async Task ListRecentAsync_Empty_ReturnsEmptyList()
    {
        var recent = await _repo.ListRecentAsync(10);
        Assert.Empty(recent);
    }

    // ── DeleteAsync ──

    [Fact]
    public async Task DeleteAsync_RemovesSession()
    {
        var session = CreateTestSession(id: "to-delete");
        await _repo.SaveAsync(session);

        await _repo.DeleteAsync("to-delete");

        var loaded = await _repo.LoadAsync("to-delete");
        Assert.Null(loaded);

        var list = await _repo.ListRecentAsync(10);
        Assert.Empty(list);
    }

    [Fact]
    public async Task DeleteAsync_NonExistent_DoesNotThrow()
    {
        await _repo.DeleteAsync("does-not-exist"); // Should be a no-op
    }

    // ── Cancellation ──

    [Fact]
    public async Task SaveAsync_CancellationToken_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var session = CreateTestSession();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _repo.SaveAsync(session, cts.Token));
    }

    // ── Directory creation ──

    [Fact]
    public async Task InitializeAsync_CreatesDirectory()
    {
        var nestedPath = Path.Combine(Path.GetTempPath(), $"ceai-nested-{Guid.NewGuid():N}", "sub", "db.sqlite");
        var repo = new SqliteInvestigationSessionRepository(nestedPath);
        await repo.InitializeAsync();

        var dir = Path.GetDirectoryName(nestedPath)!;
        Assert.True(Directory.Exists(dir));

        // Cleanup
        try { Directory.Delete(Path.Combine(Path.GetTempPath(), Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(nestedPath))!)!), true); } catch { }
    }
}
