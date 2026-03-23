using CEAISuite.Application;

namespace CEAISuite.Desktop.Services;

public interface IProcessContext
{
    int? AttachedProcessId { get; }
    string? AttachedProcessName { get; }
    ProcessInspectionOverview? CurrentInspection { get; }
    event Action? ProcessChanged;
    void Attach(ProcessInspectionOverview inspection);
    void Detach();
}
