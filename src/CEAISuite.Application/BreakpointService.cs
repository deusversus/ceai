using System.Globalization;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Application;

public sealed record BreakpointOverview(
    string Id,
    string Address,
    string Type,
    string HitAction,
    bool IsEnabled,
    int HitCount);

public sealed record BreakpointHitOverview(
    string BreakpointId,
    string Address,
    int ThreadId,
    string Timestamp,
    IReadOnlyDictionary<string, string> Registers);

/// <summary>
/// Application-level service wrapping IBreakpointEngine for the UI and AI operator.
/// </summary>
public sealed class BreakpointService(IBreakpointEngine? breakpointEngine)
{
    private bool IsAvailable => breakpointEngine is not null;

    public async Task<BreakpointOverview> SetBreakpointAsync(
        int processId,
        string addressText,
        BreakpointType type = BreakpointType.Software,
        BreakpointHitAction action = BreakpointHitAction.LogAndContinue,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        var address = ParseAddress(addressText);
        var bp = await breakpointEngine!.SetBreakpointAsync(processId, address, type, action, cancellationToken);
        return ToOverview(bp);
    }

    public async Task<BreakpointOverview> SetBreakpointAsync(
        int processId,
        string addressText,
        BreakpointType type,
        BreakpointMode mode,
        BreakpointHitAction action = BreakpointHitAction.LogAndContinue,
        bool singleHit = false,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        var address = ParseAddress(addressText);
        var bp = await breakpointEngine!.SetBreakpointAsync(processId, address, type, mode, action, cancellationToken);
        if (singleHit)
            _singleHitBreakpoints.Add(bp.Id);
        return ToOverview(bp);
    }

    /// <summary>IDs of breakpoints that should auto-remove after first hit.</summary>
    internal HashSet<string> SingleHitBreakpoints => _singleHitBreakpoints;
    private readonly HashSet<string> _singleHitBreakpoints = new();

    public async Task<bool> RemoveBreakpointAsync(
        int processId,
        string breakpointId,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        return await breakpointEngine!.RemoveBreakpointAsync(processId, breakpointId, cancellationToken);
    }

    public async Task<IReadOnlyList<BreakpointOverview>> ListBreakpointsAsync(
        int processId,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        var breakpoints = await breakpointEngine!.ListBreakpointsAsync(processId, cancellationToken);
        return breakpoints.Select(ToOverview).ToArray();
    }

    public async Task<IReadOnlyList<BreakpointHitOverview>> GetHitLogAsync(
        string breakpointId,
        int maxEntries = 50,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        var hits = await breakpointEngine!.GetHitLogAsync(breakpointId, maxEntries, cancellationToken);
        return hits.Select(h => new BreakpointHitOverview(
            h.BreakpointId,
            $"0x{h.Address:X}",
            h.ThreadId,
            h.TimestampUtc.ToString("HH:mm:ss.fff"),
            h.RegisterSnapshot)).ToArray();
    }

    private void EnsureAvailable()
    {
        if (!IsAvailable)
            throw new InvalidOperationException(
                "Breakpoint engine is not available. Ensure a process is attached and the debug engine is initialized.");
    }

    private static BreakpointOverview ToOverview(BreakpointDescriptor bp) =>
        new(bp.Id, $"0x{bp.Address:X}", bp.Type.ToString(), bp.HitAction.ToString(), bp.IsEnabled, bp.HitCount);

    private static nuint ParseAddress(string addressText)
    {
        var normalized = addressText.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return (nuint)ulong.Parse(normalized[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return (nuint)ulong.Parse(normalized, CultureInfo.InvariantCulture);
    }
}
