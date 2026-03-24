using CEAISuite.Domain;

namespace CEAISuite.Tests.Stubs;

public sealed class StubSessionRepository : IInvestigationSessionRepository
{
    private readonly List<SavedInvestigationSession> _sessions = [];

    public void AddCannedSession(string id, string processName, int? processId, int addressCount, int actionCount)
    {
        _sessions.Add(new SavedInvestigationSession(id, processName, processId, DateTimeOffset.UtcNow, addressCount, 0, actionCount));
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SaveAsync(InvestigationSession session, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<InvestigationSession?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) => Task.FromResult<InvestigationSession?>(null);
    public Task<IReadOnlyList<SavedInvestigationSession>> ListRecentAsync(int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SavedInvestigationSession>>(_sessions.Take(limit).ToList());
    public Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        _sessions.RemoveAll(s => s.Id == sessionId);
        return Task.CompletedTask;
    }
}
