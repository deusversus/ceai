using System.ComponentModel;
using System.Globalization;
using CEAISuite.Application.AgentLoop;

namespace CEAISuite.Application;

public sealed partial class AiToolFunctions
{
    // ── Plugin management tools ──

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("List all loaded plugins with name, version, description, and tool count.")]
    public Task<string> ListPlugins()
    {
        if (pluginHost is null)
            return Task.FromResult("Plugin system not available.");

        var plugins = pluginHost.Plugins;
        if (plugins.Count == 0)
            return Task.FromResult("No plugins loaded. Place .dll files in %LOCALAPPDATA%/CEAISuite/plugins/");

        var ic = CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(ic, $"{plugins.Count} plugin(s) loaded:");
        foreach (var p in plugins)
            sb.AppendLine(ic, $"  {p.Plugin.Name} v{p.Plugin.Version} — {p.Tools.Count} tools — {p.Plugin.Description}");
        return Task.FromResult(sb.ToString());
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Get the list of tools provided by a specific plugin.")]
    public Task<string> GetPluginTools(
        [Description("Name of the plugin")] string pluginName)
    {
        if (pluginHost is null)
            return Task.FromResult("Plugin system not available.");

        var plugin = pluginHost.Plugins
            .FirstOrDefault(p => string.Equals(p.Plugin.Name, pluginName, StringComparison.OrdinalIgnoreCase));

        if (plugin is null)
            return Task.FromResult($"Plugin '{pluginName}' not found. Use ListPlugins to see loaded plugins.");

        if (plugin.Tools.Count == 0)
            return Task.FromResult($"Plugin '{pluginName}' has no tools.");

        var ic = CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(ic, $"Tools from {plugin.Plugin.Name} v{plugin.Plugin.Version} ({plugin.Tools.Count}):");
        foreach (var tool in plugin.Tools)
            sb.AppendLine(ic, $"  {tool.Name} — {tool.Description ?? "(no description)"}");
        return Task.FromResult(sb.ToString());
    }
}
