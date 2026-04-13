namespace CEAISuite.Engine.Lua;

/// <summary>
/// Watches script module directories for changes and invalidates the
/// MoonSharpLuaEngine module cache when files are modified, created, or deleted.
/// Enables hot reload: save a .lua file → next require() picks up the change.
/// </summary>
internal sealed class LuaHotReloadWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly MoonSharpLuaEngine _engine;
    private bool _disposed;

    public LuaHotReloadWatcher(MoonSharpLuaEngine engine)
    {
        _engine = engine;
    }

    /// <summary>Start watching the default lib directory and any extra search paths.</summary>
    public void Start(IEnumerable<string> searchPaths)
    {
        Stop();

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var defaultLib = Path.Combine(localAppData, "CEAISuite", "scripts", "lib");

        var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { defaultLib };
        foreach (var p in searchPaths)
            allPaths.Add(p);

        foreach (var dir in allPaths)
        {
            if (!Directory.Exists(dir)) continue;

            var watcher = new FileSystemWatcher(dir, "*.lua")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };

            watcher.Changed += OnFileChanged;
            watcher.Created += OnFileChanged;
            watcher.Deleted += OnFileChanged;
            watcher.Renamed += OnFileRenamed;

            _watchers.Add(watcher);
        }
    }

    /// <summary>Stop all watchers.</summary>
    public void Stop()
    {
        foreach (var w in _watchers)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
        _watchers.Clear();
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        InvalidateModuleForFile(e.FullPath);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        // Invalidate both old and new names
        InvalidateModuleForFile(e.OldFullPath);
        InvalidateModuleForFile(e.FullPath);
    }

    private void InvalidateModuleForFile(string filePath)
    {
        if (_disposed) return;

        // Convert file path back to module name: scripts/lib/utils/math.lua → "utils.math"
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        if (fileName is null) return;

        // Clear the entire module cache — simpler and safer than tracking individual modules
        // since the file path→module name mapping is ambiguous with multiple search paths
        _engine.ClearModuleCache();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
