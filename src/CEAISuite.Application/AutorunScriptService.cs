using CEAISuite.Engine.Abstractions;
using Microsoft.Extensions.Logging;

namespace CEAISuite.Application;

/// <summary>
/// Manages autorun Lua scripts that execute on app startup.
/// Scripts in %LOCALAPPDATA%/CEAISuite/scripts/autorun/ are executed alphabetically.
/// Errors are logged but don't block startup.
/// </summary>
public sealed class AutorunScriptService
{
    private readonly ILuaScriptEngine _luaEngine;
    private readonly ILogger<AutorunScriptService> _logger;
    private readonly string _autorunDir;
    private readonly HashSet<string> _disabledScripts = new(StringComparer.OrdinalIgnoreCase);

    public AutorunScriptService(ILuaScriptEngine luaEngine, ILogger<AutorunScriptService> logger)
    {
        _luaEngine = luaEngine;
        _logger = logger;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _autorunDir = Path.Combine(localAppData, "CEAISuite", "scripts", "autorun");
    }

    /// <summary>Execute all enabled autorun scripts. Call once at app startup.</summary>
#pragma warning disable CA1873 // Logging args are cheap string/int operations
    public async Task ExecuteAllAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_autorunDir))
        {
            _logger.LogDebug("Autorun directory does not exist: {Dir}", _autorunDir);
            return;
        }

        var scripts = Directory.GetFiles(_autorunDir, "*.lua")
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger.LogInformation("Found {Count} autorun scripts in {Dir}", scripts.Count, _autorunDir);

        foreach (var scriptPath in scripts)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(scriptPath);

            if (_disabledScripts.Contains(name))
            {
                _logger.LogDebug("Skipping disabled autorun script: {Name}", name);
                continue;
            }

            try
            {
                var source = await File.ReadAllTextAsync(scriptPath, ct);
                var result = await _luaEngine.ExecuteAsync(source, ct);

                if (result.Success)
                    _logger.LogInformation("Autorun script executed: {Name}", name);
                else
                    _logger.LogWarning("Autorun script failed: {Name} — {Error}", name, result.Error);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Autorun script error: {Name}", name);
            }
        }
    }
#pragma warning restore CA1873

    /// <summary>List all autorun scripts with their enabled status.</summary>
    public IReadOnlyList<AutorunScriptInfo> ListScripts()
    {
        if (!Directory.Exists(_autorunDir))
            return [];

        return Directory.GetFiles(_autorunDir, "*.lua")
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .Select(f =>
            {
                var name = Path.GetFileName(f);
                return new AutorunScriptInfo(name, f, !_disabledScripts.Contains(name));
            })
            .ToList();
    }

    /// <summary>Enable or disable an autorun script by filename.</summary>
    public void SetEnabled(string scriptName, bool enabled)
    {
        if (enabled)
            _disabledScripts.Remove(scriptName);
        else
            _disabledScripts.Add(scriptName);
    }

    /// <summary>Get the autorun scripts directory path (creates if needed).</summary>
    public string GetAutorunDirectory()
    {
        Directory.CreateDirectory(_autorunDir);
        return _autorunDir;
    }
}

public sealed record AutorunScriptInfo(string Name, string FullPath, bool Enabled);
