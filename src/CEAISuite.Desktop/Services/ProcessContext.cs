using CEAISuite.Application;

namespace CEAISuite.Desktop.Services;

public sealed class ProcessContext : IProcessContext
{
    public int? AttachedProcessId => CurrentInspection?.ProcessId;
    public string? AttachedProcessName => CurrentInspection?.ProcessName;
    public ProcessInspectionOverview? CurrentInspection { get; private set; }

    public event Action? ProcessChanged;

    public void Attach(ProcessInspectionOverview inspection)
    {
        CurrentInspection = inspection;
        ProcessChanged?.Invoke();
    }

    public void Detach()
    {
        CurrentInspection = null;
        ProcessChanged?.Invoke();
    }
}
