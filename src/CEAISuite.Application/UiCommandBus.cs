using System.Text.Json;

namespace CEAISuite.Application;

/// <summary>
/// Bus for AI-initiated UI commands. Commands flow from AI tools through
/// the permission engine to ViewModel subscribers.
/// </summary>
public interface IUiCommandBus
{
    /// <summary>Dispatch a command. Returns true if handled by at least one subscriber.</summary>
    bool Dispatch(UiCommand command);

    /// <summary>Raised when a command is dispatched (for ViewModel subscriptions).</summary>
    event Action<UiCommand>? CommandReceived;

    /// <summary>Get the whitelist of allowed command types.</summary>
    IReadOnlyList<string> GetWhitelist();
}

/// <summary>Base for all UI commands dispatched through the bus.</summary>
public abstract record UiCommand(string CommandType);

public sealed record NavigatePanelCommand(string PanelId) : UiCommand("NavigatePanel");

public sealed record PopulateScanFormCommand(
    string? ScanValue, string? ScanType, string? DataType) : UiCommand("PopulateScanForm");

public sealed record AddEntryToTableCommand(
    string Label, string Address, string DataType, string? Value) : UiCommand("AddEntryToTable");

public sealed record SetEntryValueCommand(
    string EntryId, string NewValue) : UiCommand("SetEntryValue");

public sealed record AttachProcessCommand(int ProcessId) : UiCommand("AttachProcess");

/// <summary>
/// Default implementation of the UI command bus with whitelist enforcement.
/// </summary>
public sealed class UiCommandBus : IUiCommandBus
{
    private static readonly HashSet<string> Whitelist = new(StringComparer.OrdinalIgnoreCase)
    {
        "NavigatePanel", "PopulateScanForm", "AddEntryToTable",
        "SetEntryValue", "AttachProcess"
    };

    private readonly AppSettingsService? _settingsService;

    public UiCommandBus(AppSettingsService? settingsService = null)
    {
        _settingsService = settingsService;
    }

    public event Action<UiCommand>? CommandReceived;

    public bool Dispatch(UiCommand command)
    {
#pragma warning disable CA1416 // AppSettingsService is Windows-only; safe because CEAI is Windows-only
        if (_settingsService is not null && !_settingsService.Settings.EnableCoPilot)
#pragma warning restore CA1416
            return false;

        if (!Whitelist.Contains(command.CommandType))
            return false;

        var handler = CommandReceived;
        if (handler is null)
            return false;

        handler.Invoke(command);
        return true;
    }

    public IReadOnlyList<string> GetWhitelist() => [.. Whitelist];

    /// <summary>
    /// Parse a command type + JSON parameters into a concrete UiCommand record.
    /// Returns null if the type is unknown or parameters are invalid.
    /// </summary>
    public static UiCommand? ParseCommand(string commandType, string parametersJson)
    {
        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return commandType.ToUpperInvariant() switch
            {
                "NAVIGATEPANEL" => ParseAs<NavigatePanelCommandDto>(parametersJson, opts, dto => new NavigatePanelCommand(dto.PanelId ?? "")),
                "POPULATESCANFORM" => ParseAs<PopulateScanFormCommandDto>(parametersJson, opts, dto => new PopulateScanFormCommand(dto.ScanValue, dto.ScanType, dto.DataType)),
                "ADDENTRYTOTABLE" => ParseAs<AddEntryToTableCommandDto>(parametersJson, opts, dto => new AddEntryToTableCommand(dto.Label ?? "", dto.Address ?? "", dto.DataType ?? "Int32", dto.Value)),
                "SETENTRYVALUE" => ParseAs<SetEntryValueCommandDto>(parametersJson, opts, dto => new SetEntryValueCommand(dto.EntryId ?? "", dto.NewValue ?? "")),
                "ATTACHPROCESS" => ParseAs<AttachProcessCommandDto>(parametersJson, opts, dto => new AttachProcessCommand(dto.ProcessId)),
                _ => null
            };
        }
        catch { return null; }
    }

    private static UiCommand? ParseAs<TDto>(string json, JsonSerializerOptions opts, Func<TDto, UiCommand> map)
    {
        var dto = JsonSerializer.Deserialize<TDto>(json, opts);
        return dto is not null ? map(dto) : null;
    }

    // DTOs for JSON deserialization
    private sealed record NavigatePanelCommandDto(string? PanelId);
    private sealed record PopulateScanFormCommandDto(string? ScanValue, string? ScanType, string? DataType);
    private sealed record AddEntryToTableCommandDto(string? Label, string? Address, string? DataType, string? Value);
    private sealed record SetEntryValueCommandDto(string? EntryId, string? NewValue);
    private sealed record AttachProcessCommandDto(int ProcessId);
}
