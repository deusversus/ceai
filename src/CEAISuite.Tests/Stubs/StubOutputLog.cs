using System.Collections.ObjectModel;
using CEAISuite.Desktop.Models;
using CEAISuite.Desktop.Services;

namespace CEAISuite.Tests.Stubs;

public sealed class StubOutputLog : IOutputLog
{
    public ObservableCollection<OutputLogEntry> Entries { get; } = new();
    public List<(string Source, string Level, string Message)> LoggedMessages { get; } = new();

    public void Append(string source, string level, string message) =>
        LoggedMessages.Add((source, level, message));

    public void Clear()
    {
        Entries.Clear();
        LoggedMessages.Clear();
    }
}
