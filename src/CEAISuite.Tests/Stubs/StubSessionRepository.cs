using CEAISuite.Domain;

namespace CEAISuite.Tests.Stubs;

public sealed class StubSessionRepository : IInvestigationSessionRepository
{
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SaveAsync(InvestigationSession session, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<InvestigationSession?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) => Task.FromResult<InvestigationSession?>(null);
    public Task<IReadOnlyList<SavedInvestigationSession>> ListRecentAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SavedInvestigationSession>>([]);
    public Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
