using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace CEAISuite.Application;

/// <summary>
/// Tracks all breakpoint/hook operations as journal entries.
/// Supports rolling back groups of related operations.
/// 3F: Appends to a .jsonl file on each RecordOperation for persistence across restarts.
/// </summary>
public sealed class OperationJournal
{
    private readonly ConcurrentDictionary<string, JournalEntry> _entries = new();
    private readonly ConcurrentDictionary<string, List<string>> _groups = new();
    private readonly string? _journalFilePath;

    public OperationJournal(string? journalDirectory = null)
    {
        if (journalDirectory is not null)
        {
            try
            {
                Directory.CreateDirectory(journalDirectory);
                _journalFilePath = Path.Combine(journalDirectory, "operations.jsonl");
                LoadPersistedEntries();
            }
            catch (Exception ex)
            {
                // Persistence is best-effort — don't prevent journal from functioning
                System.Diagnostics.Trace.TraceWarning($"[OperationJournal] Journal directory init failed: {ex.Message}");
                _journalFilePath = null;
            }
        }
    }

    // Parameterless constructor for backward compatibility
    public OperationJournal() : this(journalDirectory: null) { }

    /// <summary>Get persisted (orphaned) operation IDs that were active when the app last exited.</summary>
    public IReadOnlyList<PersistedJournalEntry> GetOrphanedEntries() =>
        _entries.Values
            .Where(e => e.Status == JournalEntryStatus.Active && e.RollbackAction is null)
            .Select(e => new PersistedJournalEntry(e.OperationId, e.OperationType, e.Address, e.Mode, e.GroupId, e.Timestamp))
            .ToArray();

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

        // 3F: Persist to disk
        AppendToJournal(entry);

        return operationId;
    }

    private void AppendToJournal(JournalEntry entry)
    {
        if (_journalFilePath is null) return;
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                operationId = entry.OperationId,
                operationType = entry.OperationType,
                address = entry.Address.ToString(CultureInfo.InvariantCulture),
                mode = entry.Mode,
                groupId = entry.GroupId,
                timestamp = entry.Timestamp,
                status = entry.Status.ToString()
            });
            File.AppendAllText(_journalFilePath, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            // Persistence is best-effort
            System.Diagnostics.Trace.TraceWarning($"[OperationJournal] Journal append failed: {ex.Message}");
        }
    }

    private void LoadPersistedEntries()
    {
        if (_journalFilePath is null || !File.Exists(_journalFilePath)) return;
        try
        {
            foreach (var line in File.ReadAllLines(_journalFilePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var opId = root.GetProperty("operationId").GetString()!;
                    var opType = root.GetProperty("operationType").GetString()!;
                    var addrStr = root.GetProperty("address").GetString()!;
                    var addr = nuint.Parse(addrStr, CultureInfo.InvariantCulture);
                    var mode = root.GetProperty("mode").GetString()!;
                    var groupId = root.TryGetProperty("groupId", out var gp) ? gp.GetString() : null;
                    var ts = root.GetProperty("timestamp").GetDateTimeOffset();
                    var statusStr = root.TryGetProperty("status", out var sp) ? sp.GetString() : "Active";
                    var status = Enum.TryParse<JournalEntryStatus>(statusStr, out var s) ? s : JournalEntryStatus.Active;

                    // Load as orphaned (no rollback action — those can't be serialized)
                    var entry = new JournalEntry(opId, opType, addr, mode, groupId, null!, ts, status);
                    _entries.TryAdd(opId, entry);
                    if (groupId is not null)
                        _groups.GetOrAdd(groupId, _ => new List<string>()).Add(opId);
                }
                catch (Exception ex)
                {
                    // Skip malformed lines
                    System.Diagnostics.Trace.TraceWarning($"[OperationJournal] Skipping malformed journal line: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            // File read failed — start fresh
            System.Diagnostics.Trace.TraceWarning($"[OperationJournal] Journal file read failed: {ex.Message}");
        }
    }

    public async Task<JournalRollbackResult> RollbackOperationAsync(string operationId)
    {
        if (!_entries.TryGetValue(operationId, out var entry))
            return new JournalRollbackResult(false, 0, 0, "Operation not found in journal.");

        if (entry.Status == JournalEntryStatus.RolledBack)
            return new JournalRollbackResult(true, 0, 0, "Already rolled back.");

        // Orphaned entries loaded from disk have no rollback delegate — guard against NRE
        if (entry.RollbackAction is null)
            return new JournalRollbackResult(false, 1, 0,
                $"Operation '{operationId}' was loaded from a persisted journal and has no rollback action. " +
                "Re-attach to the target process and re-create the operation to enable rollback.");

        bool success = false;
        try { success = await entry.RollbackAction(); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"[Journal] Rollback failed for {operationId}: {ex.GetType().Name}: {ex.Message}"); }

        _entries[operationId] = entry with { Status = success ? JournalEntryStatus.RolledBack : JournalEntryStatus.RollbackFailed };
        return new JournalRollbackResult(success, 1, success ? 1 : 0, success ? "Rolled back." : "Rollback failed.");
    }

    public async Task<JournalRollbackResult> RollbackGroupAsync(string groupId)
    {
        if (!_groups.TryGetValue(groupId, out var ids))
            return new JournalRollbackResult(false, 0, 0, $"Group '{groupId}' not found.");

        int total = 0, succeeded = 0;
        var failures = new List<RollbackFailureDetail>();

        // Rollback in reverse order
        foreach (var id in ids.AsEnumerable().Reverse())
        {
            total++;
            var result = await RollbackOperationAsync(id);
            if (result.Success)
            {
                succeeded++;
            }
            else
            {
                // 3E: Track which operations failed during group rollback
                failures.Add(new RollbackFailureDetail(id, result.Message));
            }
        }

        var message = $"Rolled back {succeeded}/{total} operations in group '{groupId}'.";
        if (failures.Count > 0)
        {
            message += $" Failures: {string.Join("; ", failures.Select(f => $"{f.OperationId}: {f.ErrorMessage}"))}";
        }

        return new JournalRollbackResult(succeeded == total, total, succeeded, message, failures);
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

/// <summary>3F: Lightweight representation of a persisted journal entry (no rollback action).</summary>
public sealed record PersistedJournalEntry(
    string OperationId,
    string OperationType,
    nuint Address,
    string Mode,
    string? GroupId,
    DateTimeOffset Timestamp);

/// <summary>3E: Details about a single operation that failed during group rollback.</summary>
public sealed record RollbackFailureDetail(string OperationId, string ErrorMessage);

public sealed record JournalRollbackResult(
    bool Success,
    int TotalOperations,
    int SucceededRollbacks,
    string Message,
    IReadOnlyList<RollbackFailureDetail>? FailedOperations = null);
