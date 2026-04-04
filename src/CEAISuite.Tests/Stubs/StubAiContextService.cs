using CEAISuite.Desktop.Services;

namespace CEAISuite.Tests.Stubs;

public sealed class StubAiContextService : IAiContextService
{
    public event Action<string, string>? ContextRequested;
    public string? LastLabel { get; private set; }
    public string? LastContext { get; private set; }

    public void SendContext(string label, string context)
    {
        LastLabel = label;
        LastContext = context;
        ContextRequested?.Invoke(label, context);
    }
}
