using CEAISuite.Application;
using CEAISuite.Domain;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for low-priority AI tools: DeleteSession, ResumePointerScan, RescanAllPointerPaths.
/// </summary>
public class LowPriorityToolTests
{
    private static AiToolFunctions CreateSut(
        SessionService? sessionService = null,
        PointerScannerService? pointerScannerService = null,
        PointerRescanService? pointerRescanService = null)
    {
        var engine = new StubEngineFacade();
        var repo = new StubSessionRepository();
        var dashboard = new WorkspaceDashboardService(engine, repo);
        var scan = new ScanService(new StubScanEngine());
        var table = new AddressTableService(engine);
        var disasm = new DisassemblyService(new StubDisassemblyEngine());

        return new AiToolFunctions(
            engineFacade: engine,
            dashboardService: dashboard,
            scanService: scan,
            addressTableService: table,
            disassemblyService: disasm,
            scriptGenerationService: null!,
            sessionService: sessionService,
            pointerScannerService: pointerScannerService,
            pointerRescanService: pointerRescanService);
    }

    // ── DeleteSession ──

    [Fact]
    public async Task DeleteSession_Success_ReturnsConfirmation()
    {
        var repo = new InMemorySessionRepository();
        var service = new SessionService(repo);
        var sessionId = await service.SaveSessionAsync(
            "Game.exe", 1000, Array.Empty<AddressTableEntry>(), Array.Empty<AiActionLogEntry>());

        var sut = CreateSut(sessionService: service);

        var result = await sut.DeleteSession(sessionId);

        Assert.Contains("deleted", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(sessionId, result, StringComparison.Ordinal);

        // Verify it was actually deleted
        var sessions = await service.ListSessionsAsync(10);
        Assert.Empty(sessions);
    }

    [Fact]
    public async Task DeleteSession_NullService_ReturnsUnavailable()
    {
        var sut = CreateSut(sessionService: null);

        var result = await sut.DeleteSession("some-id");

        Assert.Equal("Session service not available.", result);
    }

    [Fact]
    public async Task DeleteSession_NonexistentSession_DoesNotThrow()
    {
        var repo = new InMemorySessionRepository();
        var service = new SessionService(repo);
        var sut = CreateSut(sessionService: service);

        // Deleting a nonexistent session should not throw (repository silently ignores)
        var result = await sut.DeleteSession("nonexistent-id");

        Assert.Contains("deleted", result, StringComparison.OrdinalIgnoreCase);
    }

    // ── ResumePointerScan ──

    [Fact]
    public async Task ResumePointerScan_NullService_ReturnsUnavailable()
    {
        var sut = CreateSut(pointerScannerService: null);

        var result = await sut.ResumePointerScan(1000);

        Assert.Equal("Pointer scanner service not available.", result);
    }

    [Fact]
    public async Task ResumePointerScan_NothingToResume_ReturnsNoPaths()
    {
        var engine = new StubEngineFacade();
        var scanner = new PointerScannerService(engine);
        var sut = CreateSut(pointerScannerService: scanner);

        // No prior scan, so resume returns empty partial results
        var result = await sut.ResumePointerScan(1000);

        Assert.Contains("No pointer paths found", result, StringComparison.OrdinalIgnoreCase);
    }

    // ── RescanAllPointerPaths ──

    [Fact]
    public async Task RescanAllPointerPaths_NullService_ReturnsUnavailable()
    {
        var sut = CreateSut(pointerRescanService: null);

        var result = await sut.RescanAllPointerPaths(1000, "game.dll+1000:10,20");

        Assert.Equal("Pointer rescan service not available.", result);
    }

    [Fact]
    public async Task RescanAllPointerPaths_EmptyInput_ReturnsNoPaths()
    {
        var engine = new StubEngineFacade();
        var rescan = new PointerRescanService(engine);
        var sut = CreateSut(pointerRescanService: rescan);

        var result = await sut.RescanAllPointerPaths(1000, "");

        Assert.Equal("No valid pointer paths provided.", result);
    }

    [Fact]
    public async Task RescanAllPointerPaths_InvalidFormat_ReturnsNoPaths()
    {
        var engine = new StubEngineFacade();
        var rescan = new PointerRescanService(engine);
        var sut = CreateSut(pointerRescanService: rescan);

        // No colon separator, so parsing yields empty list
        var result = await sut.RescanAllPointerPaths(1000, "garbage-input");

        Assert.Equal("No valid pointer paths provided.", result);
    }

    [Fact]
    public async Task RescanAllPointerPaths_ValidPaths_ReturnsResults()
    {
        var engine = new StubEngineFacade();
        // Write some memory so the pointer walk produces results (even if invalid)
        engine.WriteMemoryDirect((nuint)0x1000, new byte[64]);
        var rescan = new PointerRescanService(engine);
        var sut = CreateSut(pointerRescanService: rescan);

        var result = await sut.RescanAllPointerPaths(1000, "main.exe+1000:10,20");

        // Should contain rescan result info (valid or invalid)
        Assert.Contains("Rescanned", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RescanAllPointerPaths_WithExpectedValue_IncludesValidation()
    {
        var engine = new StubEngineFacade();
        var rescan = new PointerRescanService(engine);
        var sut = CreateSut(pointerRescanService: rescan);

        var result = await sut.RescanAllPointerPaths(1000, "main.exe+1000:10", "0x42");

        // Should return results (even if no paths are valid)
        Assert.Contains("Rescanned", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RescanAllPointerPaths_MultiplePaths_AllProcessed()
    {
        var engine = new StubEngineFacade();
        var rescan = new PointerRescanService(engine);
        var sut = CreateSut(pointerRescanService: rescan);

        var result = await sut.RescanAllPointerPaths(1000, "main.exe+1000:10,20;main.exe+2000:30");

        Assert.Contains("Rescanned 2 path(s)", result, StringComparison.Ordinal);
    }
}

/// <summary>
/// In-memory session repository for tool tests.
/// </summary>
file sealed class InMemorySessionRepository : IInvestigationSessionRepository
{
    private readonly Dictionary<string, InvestigationSession> _sessions = new();

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SaveAsync(InvestigationSession session, CancellationToken cancellationToken = default)
    {
        _sessions[session.Id] = session;
        return Task.CompletedTask;
    }

    public Task<InvestigationSession?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sessions.TryGetValue(sessionId, out var s) ? s : null);

    public Task<IReadOnlyList<SavedInvestigationSession>> ListRecentAsync(int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SavedInvestigationSession>>(
            _sessions.Values
                .OrderByDescending(s => s.CreatedAtUtc)
                .Take(limit)
                .Select(s => new SavedInvestigationSession(s.Id, s.ProcessName, s.ProcessId, s.CreatedAtUtc,
                    s.AddressEntries.Count, s.ScanSessions.Count, s.ActionLog.Count))
                .ToList());

    public Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        _sessions.Remove(sessionId);
        return Task.CompletedTask;
    }
}
