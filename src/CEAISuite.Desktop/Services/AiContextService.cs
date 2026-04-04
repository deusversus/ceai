namespace CEAISuite.Desktop.Services;

public sealed class AiContextService : IAiContextService
{
    public event Action<string, string>? ContextRequested;

    public void SendContext(string label, string context)
    {
        ContextRequested?.Invoke(label, context);
    }
}
