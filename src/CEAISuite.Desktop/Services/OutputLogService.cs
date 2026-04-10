using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CEAISuite.Desktop.Models;

namespace CEAISuite.Desktop.Services;

public sealed class OutputLogService : IOutputLog
{
    private readonly Dispatcher _dispatcher;

    public OutputLogService(IDispatcherService _)
    {
        // Capture the UI dispatcher at construction time (always on UI thread via DI).
        _dispatcher = System.Windows.Application.Current.Dispatcher;
    }

    public ObservableCollection<OutputLogEntry> Entries { get; } = new();

    public void Append(string source, string level, string message)
    {
        // Filter out Debug-level noise from the user-facing Output panel.
        // Debug messages still go to the Serilog file sink for diagnostics.
        if (level.Equals("Debug", StringComparison.OrdinalIgnoreCase))
            return;

        if (_dispatcher.CheckAccess())
            AppendCore(source, level, message);
        else
            _dispatcher.BeginInvoke(() => AppendCore(source, level, message));
    }

    public void Clear()
    {
        if (_dispatcher.CheckAccess())
            Entries.Clear();
        else
            _dispatcher.BeginInvoke(() => Entries.Clear());
    }

    private void AppendCore(string source, string level, string message)
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
}
