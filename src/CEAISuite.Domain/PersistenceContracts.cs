namespace CEAISuite.Domain;

public sealed record SavedInvestigationSession(
    string Id,
    string ProcessName,
    int? ProcessId,
    DateTimeOffset CreatedAtUtc,
    int AddressEntryCount,
    int ScanSessionCount,
    int ActionLogCount);

public interface IInvestigationSessionRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(InvestigationSession session, CancellationToken cancellationToken = default);

    Task<InvestigationSession?> LoadAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SavedInvestigationSession>> ListRecentAsync(int limit, CancellationToken cancellationToken = default);
}
