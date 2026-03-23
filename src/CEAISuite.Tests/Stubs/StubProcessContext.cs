using CEAISuite.Application;
using CEAISuite.Desktop.Services;

namespace CEAISuite.Tests.Stubs;

public sealed class StubProcessContext : IProcessContext
{
    public int? AttachedProcessId { get; set; }
    public string? AttachedProcessName { get; set; }
    public ProcessInspectionOverview? CurrentInspection { get; set; }
    public event Action? ProcessChanged;

    public void Attach(ProcessInspectionOverview inspection)
    {
        AttachedProcessId = inspection.ProcessId;
        AttachedProcessName = inspection.ProcessName;
        CurrentInspection = inspection;
        ProcessChanged?.Invoke();
    }

    public void Detach()
    {
        AttachedProcessId = null;
        AttachedProcessName = null;
        CurrentInspection = null;
        ProcessChanged?.Invoke();
    }
}
