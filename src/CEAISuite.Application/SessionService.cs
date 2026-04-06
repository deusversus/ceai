using CEAISuite.Domain;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Application;

public sealed class SessionService(IInvestigationSessionRepository repository)
{
    private static readonly string[] s_lockedTags = ["locked"];
    public async Task<string> SaveSessionAsync(
        string? processName,
        int? processId,
        IReadOnlyList<AddressTableEntry> addressEntries,
        IReadOnlyList<AiActionLogEntry> actionLog,
        string? chatId = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = $"session-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";

        var domainEntries = addressEntries.Select(e => new AddressEntry(
            e.Id,
            e.Label,
            e.Address,
            e.DataType.ToString(),
            e.CurrentValue,
            e.Notes,
            e.IsLocked ? s_lockedTags : Array.Empty<string>()
        )).ToArray();

        var domainActions = actionLog.Select(a => new AIActionLog(
            Guid.NewGuid().ToString("N")[..8],
            a.ToolName,
            new[] { $"{a.ToolName}({a.Arguments})" },
            a.Result,
            true,
            "completed"
        )).ToArray();

        var session = new InvestigationSession(
            sessionId,
            processName ?? "unknown",
            processId,
            DateTimeOffset.UtcNow,
            domainEntries,
            Array.Empty<ScanSession>(),
            domainActions,
            chatId);

        await repository.SaveAsync(session, cancellationToken);
        return sessionId;
    }

    public async Task<IReadOnlyList<SavedInvestigationSession>> ListSessionsAsync(
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        return await repository.ListRecentAsync(limit, cancellationToken);
    }

    public async Task<(IReadOnlyList<AddressTableEntry> Entries, string ProcessName, int? ProcessId, string? ChatId)?> LoadSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var session = await repository.LoadAsync(sessionId, cancellationToken);
        if (session is null) return null;

        var entries = session.AddressEntries.Select(e => new AddressTableEntry(
            e.Id,
            e.Label,
            e.AddressExpression,
            Enum.TryParse<MemoryDataType>(e.ValueType, true, out var dt) ? dt : MemoryDataType.Int32,
            e.CurrentValue ?? "?",
            null,
            e.Notes,
            e.Tags.Contains("locked"),
            null
        )).ToArray();

        return (entries, session.ProcessName, session.ProcessId, session.ChatId);
    }

    public async Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await repository.DeleteAsync(sessionId, cancellationToken);
    }
}
