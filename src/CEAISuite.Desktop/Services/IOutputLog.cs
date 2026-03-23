using System.Collections.ObjectModel;
using CEAISuite.Desktop.Models;

namespace CEAISuite.Desktop.Services;

public interface IOutputLog
{
    ObservableCollection<OutputLogEntry> Entries { get; }
    void Append(string source, string level, string message);
    void Clear();
}
