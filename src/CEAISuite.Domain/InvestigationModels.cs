namespace CEAISuite.Domain;

public sealed record ProjectProfile(
    string Id,
    string Name,
    string TargetProcess,
    string TargetPlatform,
    IReadOnlyList<string> DefaultModules);

public sealed record InvestigationSession(
    string Id,
    string ProcessName,
    int? ProcessId,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<AddressEntry> AddressEntries,
    IReadOnlyList<ScanSession> ScanSessions,
    IReadOnlyList<AIActionLog> ActionLog);

public sealed record AddressEntry(
    string Id,
    string Label,
    string AddressExpression,
    string ValueType,
    string? Notes,
    IReadOnlyList<string> Tags);

public sealed record ScanSession(
    string Id,
    string ScanType,
    string InitialConstraints,
    IReadOnlyList<string> RefinementHistory,
    int ResultCount);

public sealed record PatchRecord(
    string Id,
    string Target,
    IReadOnlyList<byte> OriginalBytes,
    IReadOnlyList<byte> PatchedBytes,
    bool IsVerified);

public sealed record AIActionLog(
    string Id,
    string Intent,
    IReadOnlyList<string> ToolCalls,
    string Summary,
    bool UserApproved,
    string Outcome);
