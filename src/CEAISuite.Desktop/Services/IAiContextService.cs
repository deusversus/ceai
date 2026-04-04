namespace CEAISuite.Desktop.Services;

/// <summary>
/// Allows any ViewModel to send context to the AI Operator panel
/// without coupling to AiOperatorViewModel directly.
/// </summary>
public interface IAiContextService
{
    /// <summary>Send a labeled context snippet to the AI Operator chat input.</summary>
    void SendContext(string label, string context);

    /// <summary>Raised when context is sent — MainWindow subscribes to route to AI Operator.</summary>
    event Action<string, string>? ContextRequested;
}
