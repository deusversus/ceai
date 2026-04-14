using CEAISuite.Desktop.Services;

namespace CEAISuite.Tests.Stubs;

public sealed class StubNavigationService : INavigationService
{
    public List<(string ContentId, object? Parameter)> DocumentsShown { get; } = new();
    public List<string> AnchorablesShown { get; } = new();

    public void ShowDocument(string contentId, object? parameter = null) =>
        DocumentsShown.Add((contentId, parameter));

    public void ShowAnchorable(string contentId) =>
        AnchorablesShown.Add(contentId);

    public bool IsPanelVisible(string contentId) => true;
}
