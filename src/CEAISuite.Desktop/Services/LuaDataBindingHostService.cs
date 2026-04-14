using System.Windows.Threading;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Desktop.Services;

/// <summary>
/// WPF-side host for reactive Lua data bindings. Fires a refresh cycle event
/// every 250ms via a DispatcherTimer so bindings can poll record values.
/// </summary>
public sealed class LuaDataBindingHostService : ILuaDataBindingHost, IDisposable
{
    private readonly DispatcherTimer _refreshTimer;

    public event Action? RefreshCycleCompleted;

    public LuaDataBindingHostService()
    {
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _refreshTimer.Tick += OnTick;
        _refreshTimer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        RefreshCycleCompleted?.Invoke();
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnTick;
    }
}
