using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace CEAISuite.Application.AgentLoop;

/// <summary>
/// Plugin interface for extending CEAI with custom tools, skills, and hooks.
///
/// Plugins are discovered from DLL files in the plugins directory, loaded
/// into isolated <see cref="AssemblyLoadContext"/>s, and initialized at startup.
/// Plugin tools are prefixed with <c>plugin_{name}_</c> to avoid naming collisions.
///
/// Modeled after Claude Code's plugin system with plugin-provided skills,
/// hooks, and MCP servers.
/// </summary>
public interface ICeaiPlugin
{
    /// <summary>Unique plugin name.</summary>
    string Name { get; }

    /// <summary>Plugin version string.</summary>
    string Version { get; }

    /// <summary>Short description of what this plugin provides.</summary>
    string Description { get; }

    /// <summary>Get tools provided by this plugin.</summary>
    IReadOnlyList<AIFunction> GetTools();

    /// <summary>Get skill definitions provided by this plugin (optional).</summary>
    IReadOnlyList<SkillDefinition>? GetSkills() => null;

    /// <summary>
    /// Initialize the plugin with the host context. Called once at load time.
    /// </summary>
    Task InitializeAsync(PluginContext context, CancellationToken ct);

    /// <summary>
    /// Shutdown the plugin. Called when the plugin is unloaded or the host exits.
    /// </summary>
    Task ShutdownAsync();
}

/// <summary>
/// Context provided to plugins during initialization.
/// </summary>
public sealed record PluginContext
{
    /// <summary>Logging callback: (category, message).</summary>
    public required Action<string, string> Log { get; init; }

    /// <summary>Token limits for the current session.</summary>
    public required TokenLimits Limits { get; init; }

    /// <summary>Tool result store for spilling large outputs.</summary>
    public required ToolResultStore ResultStore { get; init; }

    /// <summary>Plugin's own storage directory (created if needed).</summary>
    public required string StorageDirectory { get; init; }
}

/// <summary>
/// Manages plugin lifecycle: discovery, loading, initialization, and unloading.
/// Each plugin is loaded in an isolated <see cref="AssemblyLoadContext"/> so it
/// can be unloaded independently without affecting the host.
/// </summary>
public sealed class PluginHost : IAsyncDisposable
{
    private readonly string _pluginDirectory;
    private readonly Action<string, string>? _log;
    private readonly List<LoadedPlugin> _plugins = [];
    private readonly object _lock = new();

    public PluginHost(string? pluginDirectory = null, Action<string, string>? log = null)
    {
        _pluginDirectory = pluginDirectory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CEAISuite", "plugins");
        _log = log;
    }

    /// <summary>The directory where plugins are stored.</summary>
    public string PluginDirectory => _pluginDirectory;

    /// <summary>All loaded plugins (snapshot).</summary>
    public IReadOnlyList<LoadedPlugin> Plugins
    {
        get { lock (_lock) return _plugins.ToList(); }
    }

    /// <summary>
    /// Scan the plugin directory and load all valid plugin DLLs.
    /// Returns the total number of tools discovered across all plugins.
    /// </summary>
    public async Task<int> LoadAllAsync(PluginContext context, CancellationToken ct = default)
    {
        if (!Directory.Exists(_pluginDirectory))
        {
            _log?.Invoke("PLUGIN", $"Plugin directory not found: {_pluginDirectory}");
            return 0;
        }

        int totalTools = 0;

        foreach (var dllPath in Directory.EnumerateFiles(_pluginDirectory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var loaded = await LoadPluginAsync(dllPath, context, ct).ConfigureAwait(false);
                if (loaded is not null)
                {
                    lock (_lock) _plugins.Add(loaded);
                    totalTools += loaded.Tools.Count;
                    _log?.Invoke("PLUGIN", $"Loaded: {loaded.Plugin.Name} v{loaded.Plugin.Version} — {loaded.Tools.Count} tools");
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke("PLUGIN", $"Failed to load {Path.GetFileName(dllPath)}: {ex.Message}");
            }
        }

        _log?.Invoke("PLUGIN", $"Loaded {_plugins.Count} plugins with {totalTools} total tools");
        return totalTools;
    }

    /// <summary>
    /// Get all tools from all loaded plugins, prefixed with <c>plugin_{name}_</c>.
    /// </summary>
    public List<AIFunction> GetAllTools()
    {
        lock (_lock) return _plugins.SelectMany(p => p.Tools).ToList();
    }

    /// <summary>
    /// Get all skills from all loaded plugins.
    /// </summary>
    public List<SkillDefinition> GetAllSkills()
    {
        lock (_lock)
        {
            return _plugins
                .SelectMany(p => p.Plugin.GetSkills() ?? [])
                .ToList();
        }
    }

    /// <summary>Unload a specific plugin by name.</summary>
    public async Task UnloadPluginAsync(string name)
    {
        LoadedPlugin? target;
        lock (_lock)
        {
            target = _plugins.FirstOrDefault(p =>
                string.Equals(p.Plugin.Name, name, StringComparison.OrdinalIgnoreCase));
            if (target is null) return;
            _plugins.Remove(target);
        }

        try
        {
            await target.Plugin.ShutdownAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke("PLUGIN", $"Error shutting down {name}: {ex.Message}");
        }

        target.LoadContext.Unload();
        _log?.Invoke("PLUGIN", $"Unloaded: {name}");
    }

    /// <summary>Get a formatted status summary.</summary>
    public string GetStatusSummary()
    {
        lock (_lock)
        {
            if (_plugins.Count == 0) return "No plugins loaded.";

            var lines = _plugins.Select(p =>
                $"  ✓ {p.Plugin.Name} v{p.Plugin.Version} — {p.Tools.Count} tools — {p.Plugin.Description}");
            return $"Plugins ({_plugins.Count}):\n{string.Join("\n", lines)}";
        }
    }

    /// <summary>
    /// Verify a plugin DLL's SHA256 against the manifest file (plugin-checksums.json).
    /// Returns true if verified, false if rejected. Logs warnings/errors.
    /// </summary>
    private bool VerifyPluginChecksum(string dllPath)
    {
        var manifestPath = Path.Combine(_pluginDirectory, "plugin-checksums.json");
        if (!File.Exists(manifestPath))
        {
            _log?.Invoke("PLUGIN", $"WARNING: No plugin-checksums.json manifest found. " +
                $"Loading unverified DLL: {Path.GetFileName(dllPath)}. " +
                "Create a manifest or use the online catalog for verified installs.");
            return true; // Allow loading with warning when no manifest exists
        }

        try
        {
            var json = File.ReadAllText(manifestPath);
            var checksums = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (checksums is null)
            {
                _log?.Invoke("PLUGIN", $"WARNING: plugin-checksums.json is empty or invalid. Allowing load of {Path.GetFileName(dllPath)}.");
                return true;
            }

            var fileName = Path.GetFileName(dllPath);
            if (!checksums.TryGetValue(fileName, out var expectedHash))
            {
                _log?.Invoke("PLUGIN", $"REJECTED: {fileName} is not listed in plugin-checksums.json. " +
                    "Add its SHA256 hash to the manifest to allow loading.");
                return false;
            }

            using var stream = File.OpenRead(dllPath);
            var actualHash = Convert.ToHexStringLower(SHA256.HashData(stream));
            var expected = expectedHash.Trim().ToLowerInvariant();

            if (!string.Equals(actualHash, expected, StringComparison.Ordinal))
            {
                _log?.Invoke("PLUGIN", $"REJECTED: {fileName} checksum mismatch. " +
                    $"Expected {expected}, got {actualHash}. The DLL may have been tampered with.");
                return false;
            }

            _log?.Invoke("PLUGIN", $"Verified: {fileName} checksum OK.");
            return true;
        }
        catch (Exception ex)
        {
            _log?.Invoke("PLUGIN", $"WARNING: Failed to verify {Path.GetFileName(dllPath)}: {ex.Message}. Allowing load.");
            return true; // Fail-open on manifest read errors (graceful degradation)
        }
    }

    private async Task<LoadedPlugin?> LoadPluginAsync(
        string dllPath, PluginContext baseContext, CancellationToken ct)
    {
        // Security: verify DLL checksum against manifest if available
        if (!VerifyPluginChecksum(dllPath))
            return null;

        // Create isolated assembly load context
        var alc = new PluginAssemblyLoadContext(dllPath);
        var assembly = alc.LoadFromAssemblyPath(dllPath);

        // Find ICeaiPlugin implementations
        var pluginTypes = assembly.GetExportedTypes()
            .Where(t => !t.IsAbstract && t.IsClass && typeof(ICeaiPlugin).IsAssignableFrom(t))
            .ToList();

        if (pluginTypes.Count == 0)
        {
            alc.Unload();
            return null;
        }

        // Use the first plugin type found
        var pluginType = pluginTypes[0];
        if (Activator.CreateInstance(pluginType) is not ICeaiPlugin plugin)
        {
            alc.Unload();
            return null;
        }

        // Create plugin-specific context with isolated storage
        var pluginStorageDir = Path.Combine(_pluginDirectory, plugin.Name, "data");
        if (!Directory.Exists(pluginStorageDir))
            Directory.CreateDirectory(pluginStorageDir);

        var pluginContext = baseContext with { StorageDirectory = pluginStorageDir };

        await plugin.InitializeAsync(pluginContext, ct).ConfigureAwait(false);

        // Collect and prefix tools
        var rawTools = plugin.GetTools();
        var prefixedTools = rawTools.Select(tool =>
        {
            var prefixedName = $"plugin_{plugin.Name}_{tool.Name}";
            return AIFunctionFactory.Create(
                async (IDictionary<string, object?> args) =>
                {
                    var result = await tool.InvokeAsync(new AIFunctionArguments(args)).ConfigureAwait(false);
                    return result?.ToString() ?? "(no output)";
                },
                prefixedName,
                tool.Description ?? prefixedName);
        }).Cast<AIFunction>().ToList();

        return new LoadedPlugin(plugin, alc, prefixedTools);
    }

    public async ValueTask DisposeAsync()
    {
        List<LoadedPlugin> snapshot;
        lock (_lock)
        {
            snapshot = _plugins.ToList();
            _plugins.Clear();
        }

        foreach (var loaded in snapshot)
        {
            try { await loaded.Plugin.ShutdownAsync().ConfigureAwait(false); } catch (Exception ex) { _log?.Invoke("PLUGIN", $"Shutdown error for {loaded.Plugin.Name}: {ex.Message}"); }
            loaded.LoadContext.Unload();
        }
    }
}

/// <summary>A loaded plugin with its tools and assembly context.</summary>
public sealed record LoadedPlugin(
    ICeaiPlugin Plugin,
    AssemblyLoadContext LoadContext,
    IReadOnlyList<AIFunction> Tools);

/// <summary>
/// Isolated assembly load context for plugin DLLs. Allows plugins to be
/// loaded and unloaded independently of the host application.
/// </summary>
internal sealed class PluginAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginAssemblyLoadContext(string pluginPath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Try plugin-local resolution first, fall back to host
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is not null ? LoadFromAssemblyPath(path) : null;
    }
}
