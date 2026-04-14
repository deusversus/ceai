namespace CEAISuite.Desktop.Services;

/// <summary>
/// Concrete navigation service whose callbacks are wired by MainWindow at startup.
/// Registered as both <see cref="NavigationService"/> (for wiring) and <see cref="INavigationService"/> (for injection).
/// </summary>
public sealed class NavigationService : INavigationService
{
    private Action<string, object?>? _showDocument;
    private Action<string>? _showAnchorable;
    private Func<string, bool>? _isPanelVisible;

    /// <summary>Called once by MainWindow to connect AvalonDock navigation logic.</summary>
    public void Configure(Action<string, object?> showDocument, Action<string> showAnchorable,
        Func<string, bool>? isPanelVisible = null)
    {
        _showDocument = showDocument;
        _showAnchorable = showAnchorable;
        _isPanelVisible = isPanelVisible;
    }

    public void ShowDocument(string contentId, object? parameter = null) =>
        _showDocument?.Invoke(contentId, parameter);

    public void ShowAnchorable(string contentId) =>
        _showAnchorable?.Invoke(contentId);

    public bool IsPanelVisible(string contentId) =>
        _isPanelVisible?.Invoke(contentId) ?? false;
}
