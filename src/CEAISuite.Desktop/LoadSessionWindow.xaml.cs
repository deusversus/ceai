using System.Windows;
using CEAISuite.Domain;

namespace CEAISuite.Desktop;

/// <summary>
/// Modal dialog for selecting a saved investigation session to load or delete.
/// </summary>
public sealed partial class LoadSessionWindow : Window
{
    private List<SavedInvestigationSession> _sessions;
    private readonly Func<string, Task> _deleteSession;
    private readonly Func<Task<IReadOnlyList<SavedInvestigationSession>>> _refreshSessions;

    public string? SelectedSessionId { get; private set; }

    public LoadSessionWindow(
        IReadOnlyList<SavedInvestigationSession> sessions,
        Func<string, Task> deleteSession,
        Func<Task<IReadOnlyList<SavedInvestigationSession>>> refreshSessions)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowChromeHelper.EnableRoundedCorners(this);
        _sessions = sessions.ToList();
        _deleteSession = deleteSession;
        _refreshSessions = refreshSessions;
        PopulateList();
    }

    private void PopulateList()
    {
        SessionListBox.Items.Clear();
        foreach (var s in _sessions)
            SessionListBox.Items.Add($"{s.Id}  \u2014  {s.ProcessName}  ({s.AddressEntryCount} addresses, {s.CreatedAtUtc:g})");
        if (_sessions.Count > 0)
            SessionListBox.SelectedIndex = 0;
    }

    private void Load_Click(object sender, RoutedEventArgs e)
    {
        if (SessionListBox.SelectedIndex < 0) return;
        SelectedSessionId = _sessions[SessionListBox.SelectedIndex].Id;
        DialogResult = true;
        Close();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SessionListBox.SelectedIndex < 0) return;
        var target = _sessions[SessionListBox.SelectedIndex];
        var confirm = MessageBox.Show(
            $"Delete session \"{target.Id}\"?\nThis cannot be undone.",
            "Delete Session", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        await _deleteSession(target.Id);
        _sessions = (await _refreshSessions()).ToList();
        PopulateList();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void CaptionClose_Click(object sender, RoutedEventArgs e) => Close();
}
