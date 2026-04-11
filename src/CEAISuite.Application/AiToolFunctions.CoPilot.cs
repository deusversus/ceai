using System.ComponentModel;
using System.Globalization;
using CEAISuite.Application.AgentLoop;

namespace CEAISuite.Application;

public sealed partial class AiToolFunctions
{
    // ── AI Co-Pilot tools ──

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Get the list of UI commands the AI co-pilot can dispatch to navigate panels, populate forms, or modify entries.")]
    public Task<string> GetUiCommandWhitelist()
    {
        if (uiCommandBus is null)
            return Task.FromResult("Co-pilot not available (UI command bus not wired).");

        var list = uiCommandBus.GetWhitelist();
        var ic = CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(ic, $"Co-Pilot UI commands ({list.Count}):");
        sb.AppendLine("  NavigatePanel        — {\"panelId\": \"scanner\"}");
        sb.AppendLine("  PopulateScanForm     — {\"scanValue\": \"100\", \"scanType\": \"Exact\", \"dataType\": \"Int32\"}");
        sb.AppendLine("  AddEntryToTable      — {\"label\": \"Health\", \"address\": \"0x1234\", \"dataType\": \"Float\", \"value\": \"100\"}");
        sb.AppendLine("  SetEntryValue        — {\"entryId\": \"n_abc\", \"newValue\": \"999\"}");
        sb.AppendLine("  AttachProcess        — {\"processId\": 1234}");
        sb.Append("Use ExecuteUiCommand(commandType, parametersJson) to dispatch.");
        return Task.FromResult(sb.ToString());
    }

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Execute a UI command to navigate panels, populate scan forms, add address table entries, or set values. Requires approval.")]
    public Task<string> ExecuteUiCommand(
        [Description("Command type from GetUiCommandWhitelist (e.g. NavigatePanel, PopulateScanForm)")] string commandType,
        [Description("JSON parameters for the command")] string parametersJson)
    {
        if (uiCommandBus is null)
            return Task.FromResult("Co-pilot not available.");

        var command = UiCommandBus.ParseCommand(commandType, parametersJson);
        if (command is null)
            return Task.FromResult($"Unknown or invalid command: {commandType}. Use GetUiCommandWhitelist to see available commands.");

        var handled = uiCommandBus.Dispatch(command);
        return handled
            ? Task.FromResult($"OK: {commandType} dispatched successfully.")
            : Task.FromResult($"Command {commandType} was not handled. No subscriber is listening for this command type.");
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Get current UI state: attached process, scan status, address table summary.")]
    public Task<string> GetCurrentUiState()
    {
        var ic = CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("UI State:");

        // Address table
        var entries = addressTableService.Entries;
        var locked = entries.Count(e => e.IsLocked);
        var scripts = entries.Count(e => e.Notes?.Contains("script", StringComparison.OrdinalIgnoreCase) == true);
        sb.AppendLine(ic, $"  Address table: {entries.Count} entries ({locked} locked)");

        // Scan undo depth
        sb.AppendLine(ic, $"  Scan undo depth: {scanService.UndoDepth}");

        // Co-pilot status
        sb.AppendLine(ic, $"  Co-pilot: {(uiCommandBus is not null ? "available" : "not wired")}");

        return Task.FromResult(sb.ToString());
    }
}
