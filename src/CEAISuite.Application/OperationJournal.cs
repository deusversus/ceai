using System.Collections.Concurrent;

namespace CEAISuite.Application;

/// <summary>
/// Tracks all breakpoint/hook operations as journal entries.
/// Supports rolling back groups of related operations.
/// </summary>
public sealed class OperationJournal
{
    private readonly ConcurrentDictionary<string, JournalEntry> _entries = new();
    private readonly ConcurrentDictionary<string, List<string>> _groups = new();

    public string RecordOperation(
        string operationId,
        string operationType,
        nuint address,
        string mode,
        string? groupId,
        Func<Task<bool>> rollbackAction)
    {
        var entry = new JournalEntry(
            operationId, operationType, address, mode, groupId,
            rollbackAction, DateTimeOffset.UtcNow, JournalEntryStatus.Active);

        _entries[operationId] = entry;

        if (groupId is not null)
        {
            _groups.GetOrAdd(groupId, _ => new List<string>()).Add(operationId);
        }

        return operationId;
    }

    public async Task<JournalRollbackResult> RollbackOperationAsync(string operationId)
    {
        if (!_entries.TryGetValue(operationId, out var entry))
            return new JournalRollbackResult(false, 0, 0, "Operation not found in journal.");

        if (entry.Status == JournalEntryStatus.RolledBack)
            return new JournalRollbackResult(true, 0, 0, "Already rolled back.");

        bool success = false;
        try { success = await entry.RollbackAction(); }
        catch { /* rollback failed */ }

        _entries[operationId] = entry with { Status = success ? JournalEntryStatus.RolledBack : JournalEntryStatus.RollbackFailed };
        return new JournalRollbackResult(success, 1, success ? 1 : 0, success ? "Rolled back." : "Rollback failed.");
    }

    public async Task<JournalRollbackResult> RollbackGroupAsync(string groupId)
    {
        if (!_groups.TryGetValue(groupId, out var ids))
            return new JournalRollbackResult(false, 0, 0, $"Group '{groupId}' not found.");

        int total = 0, succeeded = 0;
        // Rollback in reverse order
        foreach (var id in ids.AsEnumerable().Reverse())
        {
            total++;
            var result = await RollbackOperationAsync(id);
            if (result.Success) succeeded++;
        }

        return new JournalRollbackResult(succeeded == total, total, succeeded,
            $"Rolled back {succeeded}/{total} operations in group '{groupId}'.");
    }

    public IReadOnlyList<JournalEntry> GetEntries() => _entries.Values.OrderByDescending(e => e.Timestamp).ToArray();

    public IReadOnlyList<JournalEntry> GetGroupEntries(string groupId) =>
        _groups.TryGetValue(groupId, out var ids)
            ? ids.Select(id => _entries.GetValueOrDefault(id)).OfType<JournalEntry>().ToArray()
            : [];
}

public enum JournalEntryStatus { Active, RolledBack, RollbackFailed }

public sealed record JournalEntry(
    string OperationId,
    string OperationType,
    nuint Address,
    string Mode,
    string? GroupId,
    Func<Task<bool>> RollbackAction,
    DateTimeOffset Timestamp,
    JournalEntryStatus Status);

public sealed record JournalRollbackResult(
    bool Success,
    int TotalOperations,
    int SucceededRollbacks,
    string Message);
