using System.ComponentModel;
using System.Text.Json;
using CEAISuite.Application.AgentLoop;

namespace CEAISuite.Application;

#pragma warning disable CA1416 // Platform compatibility — this is a Windows-only application
public sealed partial class AiToolFunctions
{
    private static readonly HashSet<string> SettingsWriteWhitelist = new(StringComparer.OrdinalIgnoreCase)
    {
        "RefreshIntervalMs", "Theme", "DensityPreset", "MaxConversationMessages",
        "ScanThreadCount", "DefaultScanDataType", "LuaExecutionTimeoutSeconds",
        "TokenProfile", "EnableCoPilot", "MemoryBrowserBytesPerRow",
        "AutoOpenMemoryBrowser", "UseStreaming", "EnableEarlyToolExecution",
        "AutoSaveIntervalMinutes", "LogRetentionDays", "ShowUnresolvedAsQuestionMarks",
        "EnableAgentMemory", "MaxMemoryEntries", "RateLimitSeconds"
    };

    private static readonly string[] SensitivePatterns =
        ["Key", "Token", "Secret", "Password", "Encrypted", "OAuth", "Sensitive"];

    [ReadOnlyTool]
    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [SearchHint("settings", "config", "preferences", "theme", "refresh rate")]
    [Description("Get current application settings (non-sensitive only). API keys and tokens are never exposed.")]
    public Task<string> GetSettings(
        [Description("Section filter: all, general, scanning, display, lua, tokens, copilot")] string section = "all")
    {
        if (appSettingsService is null) return Task.FromResult("Settings service not available.");

        var s = appSettingsService.Settings;
        var result = new Dictionary<string, object?>();

        bool all = section.Equals("all", StringComparison.OrdinalIgnoreCase);

        if (all || section.Equals("general", StringComparison.OrdinalIgnoreCase))
        {
            result["provider"] = s.Provider;
            result["model"] = s.Model;
            result["refreshIntervalMs"] = s.RefreshIntervalMs;
            result["autoSaveIntervalMinutes"] = s.AutoSaveIntervalMinutes;
            result["logRetentionDays"] = s.LogRetentionDays;
            result["enableAgentMemory"] = s.EnableAgentMemory;
            result["maxMemoryEntries"] = s.MaxMemoryEntries;
            result["rateLimitSeconds"] = s.RateLimitSeconds;
            result["rateLimitWait"] = s.RateLimitWait;
            result["useStreaming"] = s.UseStreaming;
            result["enableEarlyToolExecution"] = s.EnableEarlyToolExecution;
            result["maxConversationMessages"] = s.MaxConversationMessages;
            result["permissionMode"] = s.PermissionMode;
        }

        if (all || section.Equals("scanning", StringComparison.OrdinalIgnoreCase))
        {
            result["scanThreadCount"] = s.ScanThreadCount;
            result["defaultScanDataType"] = s.DefaultScanDataType;
        }

        if (all || section.Equals("display", StringComparison.OrdinalIgnoreCase))
        {
            result["theme"] = s.Theme;
            result["densityPreset"] = s.DensityPreset;
            result["showUnresolvedAsQuestionMarks"] = s.ShowUnresolvedAsQuestionMarks;
            result["memoryBrowserBytesPerRow"] = s.MemoryBrowserBytesPerRow;
            result["autoOpenMemoryBrowser"] = s.AutoOpenMemoryBrowser;
        }

        if (all || section.Equals("lua", StringComparison.OrdinalIgnoreCase))
        {
            result["luaExecutionTimeoutSeconds"] = s.LuaExecutionTimeoutSeconds;
        }

        if (all || section.Equals("tokens", StringComparison.OrdinalIgnoreCase))
        {
            result["tokenProfile"] = s.TokenProfile;
            result["maxSessionCostDollars"] = s.MaxSessionCostDollars;
            result["inputPricePerMillion"] = s.InputPricePerMillion;
            result["outputPricePerMillion"] = s.OutputPricePerMillion;
        }

        if (all || section.Equals("copilot", StringComparison.OrdinalIgnoreCase))
        {
            result["enableCoPilot"] = s.EnableCoPilot;
            result["requirePlanForDestructive"] = s.RequirePlanForDestructive;
        }

        return Task.FromResult(ToJson(result));
    }

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [SearchHint("settings", "config", "update", "change setting")]
    [Description("Update an application setting by key. Only non-sensitive settings can be changed. Use GetSettings to see available keys.")]
    public Task<string> UpdateSetting(
        [Description("Setting key (e.g., 'Theme', 'RefreshIntervalMs', 'ScanThreadCount')")] string key,
        [Description("New value (will be parsed to the correct type)")] string value)
    {
        if (appSettingsService is null) return Task.FromResult("Settings service not available.");

        // Security: block sensitive keys
        if (SensitivePatterns.Any(p => key.Contains(p, StringComparison.OrdinalIgnoreCase)))
            return Task.FromResult($"Cannot modify '{key}' — this is a sensitive setting. Change it in the Settings dialog.");

        if (!SettingsWriteWhitelist.Contains(key))
            return Task.FromResult($"Unknown or non-modifiable setting '{key}'. Use GetSettings to see available keys.");

        var s = appSettingsService.Settings;
        try
        {
            switch (key)
            {
                case "RefreshIntervalMs": s.RefreshIntervalMs = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "Theme": s.Theme = value; break;
                case "DensityPreset": s.DensityPreset = value; break;
                case "MaxConversationMessages": s.MaxConversationMessages = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "ScanThreadCount": s.ScanThreadCount = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "DefaultScanDataType": s.DefaultScanDataType = value; break;
                case "LuaExecutionTimeoutSeconds": s.LuaExecutionTimeoutSeconds = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "TokenProfile": s.TokenProfile = value; break;
                case "EnableCoPilot": s.EnableCoPilot = bool.Parse(value); break;
                case "MemoryBrowserBytesPerRow": s.MemoryBrowserBytesPerRow = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "AutoOpenMemoryBrowser": s.AutoOpenMemoryBrowser = bool.Parse(value); break;
                case "UseStreaming": s.UseStreaming = bool.Parse(value); break;
                case "EnableEarlyToolExecution": s.EnableEarlyToolExecution = bool.Parse(value); break;
                case "AutoSaveIntervalMinutes": s.AutoSaveIntervalMinutes = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "LogRetentionDays": s.LogRetentionDays = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "ShowUnresolvedAsQuestionMarks": s.ShowUnresolvedAsQuestionMarks = bool.Parse(value); break;
                case "EnableAgentMemory": s.EnableAgentMemory = bool.Parse(value); break;
                case "MaxMemoryEntries": s.MaxMemoryEntries = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "RateLimitSeconds": s.RateLimitSeconds = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                default: return Task.FromResult($"Setting '{key}' not handled.");
            }

            appSettingsService.Save();
            return Task.FromResult($"Setting '{key}' updated to '{value}'.");
        }
        catch (FormatException)
        {
            return Task.FromResult($"Cannot parse '{value}' for setting '{key}'.");
        }
    }
}
