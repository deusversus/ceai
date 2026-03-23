namespace CEAISuite.Desktop.Services;

/// <summary>
/// Concrete navigation service whose callbacks are wired by MainWindow at startup.
/// Registered as both <see cref="NavigationService"/> (for wiring) and <see cref="INavigationService"/> (for injection).
/// </summary>
public sealed class NavigationService : INavigationService
{
    private Action<string, object?>? _showDocument;
    private Action<string>? _showAnchorable;

    /// <summary>Called once by MainWindow to connect AvalonDock navigation logic.</summary>
    public void Configure(Action<string, object?> showDocument, Action<string> showAnchorable)
    {
        _showDocument = showDocument;
        _showAnchorable = showAnchorable;
    }

    public void ShowDocument(string contentId, object? parameter = null) =>
        _showDocument?.Invoke(contentId, parameter);

    public void ShowAnchorable(string contentId) =>
        _showAnchorable?.Invoke(contentId);
}
