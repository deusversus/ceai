using System.Collections.Concurrent;
using System.Windows.Threading;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Desktop.Services;

/// <summary>
/// Desktop implementation of <see cref="IMainFormProxy"/> that bridges Lua script
/// requests to WPF navigation, theme, and status bar services.
/// Thread-safe: all UI-affecting operations are dispatched to the WPF dispatcher.
/// </summary>
public sealed class MainFormProxyService : IMainFormProxy, IDisposable
{
    private readonly INavigationService _navigationService;
    private readonly Dispatcher _dispatcher;
    private readonly ConcurrentDictionary<string, (string Caption, Action Callback)> _menuItems = new();
    private int _nextMenuItemId;
    private volatile string _statusText = string.Empty;
    private bool _disposed;

    /// <summary>All known panel/document ContentIds from MainWindow.xaml.</summary>
    private static readonly IReadOnlyList<string> KnownPanelIds =
    [
        "processes", "modules", "threads", "memoryRegions", "workspace", "plugins",
        "addressTable", "inspection", "memoryBrowser", "disassembler",
        "structureDissector", "pointerScanner", "scriptEditor", "debugger", "luaConsole",
        "scanner", "output", "breakpoints", "scripts", "snapshots",
        "findResults", "hotkeys", "journal", "speedHack", "vehDebugger", "aiOperator"
    ];

    /// <summary>
    /// Document tabs live in the center LayoutDocumentPane; anchorables live in sidebar/bottom.
    /// This set identifies which ContentIds are documents (shown via ShowDocument) vs anchorables.
    /// </summary>
    private static readonly HashSet<string> DocumentIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "addressTable", "inspection", "memoryBrowser", "disassembler",
        "structureDissector", "pointerScanner", "scriptEditor", "debugger", "luaConsole"
    };

    public MainFormProxyService(INavigationService navigationService, Dispatcher dispatcher)
    {
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        // Subscribe to ThemeManager.ThemeChanged to relay to Lua scripts
        ThemeManager.ThemeChanged += OnThemeChanged;
    }

    public bool IsPanelVisible(string panelId)
    {
        // Panel visibility tracking would require AvalonDock layout queries.
        // For now, return true for all known panels (they exist in the layout).
        return KnownPanelIds.Contains(panelId);
    }

    public void SetPanelVisible(string panelId, bool visible)
    {
        if (string.IsNullOrWhiteSpace(panelId))
            return;

        if (!visible)
        {
            // Hiding panels programmatically in AvalonDock is complex — gracefully ignore.
            return;
        }

        // Show the panel via the navigation service on the UI thread
        _dispatcher.BeginInvoke(() =>
        {
            if (DocumentIds.Contains(panelId))
                _navigationService.ShowDocument(panelId);
            else
                _navigationService.ShowAnchorable(panelId);
        });
    }

    public IReadOnlyList<string> GetPanelIds() => KnownPanelIds;

    public void NavigateDisassembler(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return;

        _dispatcher.BeginInvoke(() =>
            _navigationService.ShowDocument("disassembler", address));
    }

    public void NavigateMemoryBrowser(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return;

        _dispatcher.BeginInvoke(() =>
            _navigationService.ShowDocument("memoryBrowser", address));
    }

    public void SetStatus(string text)
    {
        _statusText = text ?? string.Empty;
    }

    public string GetStatus() => _statusText;

    public bool IsDarkTheme() => ThemeManager.ResolvedTheme == AppTheme.Dark;

    public string GetThemeName() => ThemeManager.ResolvedTheme.ToString();

    public event Action<string>? ThemeChanged;

    public string AddMenuItem(string caption, Action callback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caption);
        ArgumentNullException.ThrowIfNull(callback);

        var id = $"luamenu_{Interlocked.Increment(ref _nextMenuItemId)}";
        _menuItems[id] = (caption, callback);
        return id;
    }

    public void RemoveMenuItem(string menuItemId)
    {
        if (!string.IsNullOrWhiteSpace(menuItemId))
            _menuItems.TryRemove(menuItemId, out _);
    }

    /// <summary>
    /// Returns the currently registered Lua menu items.
    /// Can be consumed by the MainWindow to render dynamic menu entries.
    /// </summary>
    public IReadOnlyDictionary<string, (string Caption, Action Callback)> GetMenuItems() =>
        _menuItems;

    private void OnThemeChanged(AppTheme theme)
    {
        ThemeChanged?.Invoke(theme.ToString());
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ThemeManager.ThemeChanged -= OnThemeChanged;
        _menuItems.Clear();
    }
}
