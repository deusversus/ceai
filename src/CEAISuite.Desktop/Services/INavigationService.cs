namespace CEAISuite.Desktop.Services;

/// <summary>
/// Abstracts panel/document navigation so ViewModels can request UI focus changes
/// without coupling to AvalonDock or MainWindow.
/// </summary>
public interface INavigationService
{
    /// <summary>Activate a center-pane document tab by its ContentId.</summary>
    void ShowDocument(string contentId, object? parameter = null);

    /// <summary>Activate/restore a dockable anchorable panel by its ContentId.</summary>
    void ShowAnchorable(string contentId);
}
