using System.Collections.ObjectModel;
using System.Globalization;
using CEAISuite.Desktop.Models;

namespace CEAISuite.Desktop.Services;

public sealed class OutputLogService : IOutputLog
{
    public ObservableCollection<OutputLogEntry> Entries { get; } = new();

    public void Append(string source, string level, string message)
    {
        Entries.Add(new OutputLogEntry
        {
            Timestamp = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            Source = source,
            Level = level,
            Message = message
        });
        if (Entries.Count > 1000)
            Entries.RemoveAt(0);
    }

    public void Clear() => Entries.Clear();
}
