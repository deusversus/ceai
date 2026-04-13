using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Application;

/// <summary>
/// Bridges <see cref="ILuaAddressListProvider"/> to <see cref="AddressTableService"/>,
/// allowing Lua scripts to manipulate the address table without Engine.Lua depending on Application.
/// </summary>
public sealed class LuaAddressListAdapter : ILuaAddressListProvider
{
    private readonly AddressTableService _tableService;
    private readonly IEngineFacade _engineFacade;

    public LuaAddressListAdapter(AddressTableService tableService, IEngineFacade engineFacade)
    {
        _tableService = tableService;
        _engineFacade = engineFacade;
    }

    public int GetCount() => _tableService.Entries.Count;

    public LuaMemoryRecord? GetRecord(int index)
    {
        var entries = _tableService.Entries;
        if (index < 0 || index >= entries.Count) return null;
        return EntryToRecord(entries[index]);
    }

    public LuaMemoryRecord? GetRecordById(string id)
    {
        var node = _tableService.FindNode(id);
        return node is not null ? NodeToRecord(node) : null;
    }

    public LuaMemoryRecord? GetRecordByDescription(string description)
    {
        var node = _tableService.FindNodeByLabel(description);
        return node is not null ? NodeToRecord(node) : null;
    }

    public string AddRecord(string address, string dataType, string? description)
    {
        var dt = Enum.TryParse<MemoryDataType>(dataType, true, out var parsed)
            ? parsed : MemoryDataType.Int32;
        var entry = _tableService.AddEntry(address, dt, "0", description);
        return entry.Id;
    }

    public void RemoveRecord(string id) => _tableService.RemoveEntry(id);

    public void SetValue(string id, string value)
    {
        var node = _tableService.FindNode(id);
        if (node is null) return;
        node.CurrentValue = value;

        // Write to process memory if attached
        var pid = _engineFacade.AttachedProcessId;
        if (pid.HasValue)
        {
            try { _tableService.WriteValueAsync(pid.Value, node).GetAwaiter().GetResult(); }
            catch { /* write may fail if address is invalid */ }
        }
    }

    public string? GetValue(string id) => _tableService.FindNode(id)?.CurrentValue;

    public string? GetAddress(string id) => _tableService.FindNode(id)?.Address;

    public string? GetDescription(string id) => _tableService.FindNode(id)?.Label;

    public void SetDescription(string id, string description)
        => _tableService.UpdateLabel(id, description);

    public void SetActive(string id, bool active)
    {
        var node = _tableService.FindNode(id);
        if (node is null) return;
        if (node.IsLocked != active)
            _tableService.ToggleLock(id);
    }

    public bool GetActive(string id)
        => _tableService.FindNode(id)?.IsLocked ?? false;

    public void RefreshAll(int processId)
        => _tableService.RefreshAllAsync(processId).GetAwaiter().GetResult();

    private static LuaMemoryRecord EntryToRecord(AddressTableEntry entry) => new(
        Id: entry.Id,
        Description: entry.Label ?? "",
        Address: entry.Address ?? "",
        DataType: entry.DataType.ToString(),
        Value: entry.CurrentValue,
        IsActive: entry.IsLocked,
        IsGroupHeader: false);

    private static LuaMemoryRecord NodeToRecord(AddressTableNode node) => new(
        Id: node.Id,
        Description: node.Label ?? "",
        Address: node.Address ?? "",
        DataType: node.DataType.ToString(),
        Value: node.CurrentValue,
        IsActive: node.IsLocked,
        IsGroupHeader: node.IsGroup);
}
