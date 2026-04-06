using System.Globalization;
using CEAISuite.Domain;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Application;

public sealed class WorkspaceDashboardService(
    IEngineFacade engineFacade,
    IInvestigationSessionRepository sessionRepository)
{
    private static readonly string[] s_initSteps = ["create_solution", "create_projects", "build_solution"];

    /// <summary>Most recently built dashboard (set by BuildAsync/InspectProcessAsync).</summary>
    public WorkspaceDashboard? CurrentDashboard { get; private set; }
    public async Task<WorkspaceDashboard> BuildAsync(string dataStorePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataStorePath);

        await sessionRepository.InitializeAsync(cancellationToken);

        var recentSessions = await sessionRepository.ListRecentAsync(5, cancellationToken);
        if (recentSessions.Count == 0)
        {
            await sessionRepository.SaveAsync(CreateInitialSession(), cancellationToken);
            recentSessions = await sessionRepository.ListRecentAsync(5, cancellationToken);
        }

        var processes = await engineFacade.ListProcessesAsync(cancellationToken);

        return CurrentDashboard = new WorkspaceDashboard(
            WorkspaceBootstrap.CreateOverview(),
            processes
                .Select(process => new RunningProcessOverview(process.Id, process.Name, process.Architecture))
                .ToArray(),
            recentSessions
                .Select(
                    session => new RecentSessionOverview(
                        session.Id,
                        session.ProcessName,
                        session.ProcessId,
                        session.CreatedAtUtc,
                        session.AddressEntryCount,
                        session.ScanSessionCount,
                        session.ActionLogCount))
                .ToArray(),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            null,
            dataStorePath,
            $"Loaded {processes.Count} processes and {recentSessions.Count} saved sessions.");
    }

    public async Task<ProcessInspectionOverview> InspectProcessAsync(
        int processId,
        CancellationToken cancellationToken = default)
    {
        var processes = await engineFacade.ListProcessesAsync(cancellationToken);
        var process = processes.FirstOrDefault(candidate => candidate.Id == processId)
            ?? throw new InvalidOperationException($"Process {processId} is no longer available.");

        var inspection = await engineFacade.AttachAsync(processId, cancellationToken);
        var modules = inspection.Modules
            .Take(50)
            .Select(
                module => new ModuleOverview(
                    module.Name,
                    $"0x{module.BaseAddress:X}",
                    $"{module.SizeBytes:N0} bytes"))
            .ToArray();

        MemorySampleOverview? sample = null;
        var statusMessage = $"Attached to {process.Name} ({process.Id}) and loaded {inspection.Modules.Count} modules.";

        var primaryModule = inspection.Modules.Count > 0 ? inspection.Modules[0] : null;
        if (primaryModule is not null)
        {
            try
            {
                var bytes = await engineFacade.ReadMemoryAsync(processId, primaryModule.BaseAddress, 32, cancellationToken);
                var int32Value = await engineFacade.ReadValueAsync(processId, primaryModule.BaseAddress, MemoryDataType.Int32, cancellationToken);
                var pointerValue = await engineFacade.ReadValueAsync(processId, primaryModule.BaseAddress, MemoryDataType.Pointer, cancellationToken);

                sample = new MemorySampleOverview(
                    $"0x{primaryModule.BaseAddress:X}",
                    Convert.ToHexString(bytes.Bytes.ToArray()),
                    int32Value.DisplayValue,
                    pointerValue.DisplayValue);
            }
            catch (Exception exception)
            {
                statusMessage = $"{statusMessage} Memory preview unavailable: {exception.Message}";
            }
        }

        var overview = new ProcessInspectionOverview(
            inspection.ProcessId,
            inspection.ProcessName,
            process.Architecture,
            modules,
            sample,
            null,
            null,
            statusMessage);

        // Keep CurrentDashboard in sync so AI tools see the attached process
        if (CurrentDashboard is not null)
        {
            CurrentDashboard = CurrentDashboard with { CurrentInspection = overview };
        }

        return overview;
    }

    /// <summary>Detach from the current process, clearing all inspection state.</summary>
    public void DetachProcess()
    {
        engineFacade.Detach();

        if (CurrentDashboard is not null)
        {
            CurrentDashboard = CurrentDashboard with
            {
                CurrentInspection = null,
                StatusMessage = "Detached from process."
            };
        }
    }

    public async Task<ManualMemoryProbeOverview> ReadAddressAsync(
        int processId,
        string addressText,
        MemoryDataType dataType,
        CancellationToken cancellationToken = default)
    {
        var address = ParseAddress(addressText);
        var readLen = await GetReadLengthAsync(processId, dataType);
        var raw = await engineFacade.ReadMemoryAsync(processId, address, readLen, cancellationToken);
        var typed = await engineFacade.ReadValueAsync(processId, address, dataType, cancellationToken);

        return new ManualMemoryProbeOverview(
            $"0x{address:X}",
            dataType.ToString(),
            typed.DisplayValue,
            Convert.ToHexString(raw.Bytes.ToArray()));
    }

    public async Task<string> WriteAddressAsync(
        int processId,
        string addressText,
        MemoryDataType dataType,
        string valueText,
        CancellationToken cancellationToken = default)
    {
        var address = ParseAddress(addressText);
        var result = await engineFacade.WriteValueAsync(processId, address, dataType, valueText, cancellationToken);
        return $"Wrote {result.WrittenValue} as {result.DataType} to 0x{result.Address:X} ({result.BytesWritten} bytes).";
    }

    private async Task<int> GetReadLengthAsync(int processId, MemoryDataType dataType)
    {
        if (dataType == MemoryDataType.Pointer)
        {
            var arch = await GetArchitectureAsync(processId);
            return string.Equals(arch, "x86", StringComparison.OrdinalIgnoreCase) ? sizeof(int) : sizeof(long);
        }
        return dataType switch
        {
            MemoryDataType.Int32 => sizeof(int),
            MemoryDataType.Int64 => sizeof(long),
            MemoryDataType.Float => sizeof(float),
            MemoryDataType.Double => sizeof(double),
            _ => throw new ArgumentOutOfRangeException(nameof(dataType), dataType, "Unsupported data type.")
        };
    }

    private async Task<string> GetArchitectureAsync(int processId)
    {
        var processes = await engineFacade.ListProcessesAsync();
        return processes.FirstOrDefault(process => process.Id == processId)?.Architecture ?? "x64";
    }

    private static nuint ParseAddress(string addressText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(addressText);

        var normalized = addressText.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.TryParse(normalized[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexAddress)
                ? (nuint)hexAddress
                : throw new FormatException("Address must be a valid hexadecimal value.");
        }

        return ulong.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var decimalAddress)
            ? (nuint)decimalAddress
            : throw new FormatException("Address must be a valid decimal or hexadecimal value.");
    }

    private static InvestigationSession CreateInitialSession() =>
        new(
            "initial-workspace",
            "workspace-bootstrap",
            null,
            DateTimeOffset.UtcNow,
            Array.Empty<AddressEntry>(),
            Array.Empty<ScanSession>(),
            new[]
            {
                new AIActionLog(
                    "workspace-init",
                    "Initialize workspace",
                    s_initSteps,
                    "Initial workspace scaffold created and validated.",
                    true,
                    "Workspace ready for Milestone 1 development.")
            });
}
