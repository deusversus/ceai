using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Tests.Stubs;

public sealed class StubAddressListProvider : ILuaAddressListProvider
{
    private readonly List<LuaMemoryRecord> _records = [];

    public void AddCannedRecord(LuaMemoryRecord record) => _records.Add(record);

    public int GetCount() => _records.Count;

    public LuaMemoryRecord? GetRecord(int index) =>
        index >= 0 && index < _records.Count ? _records[index] : null;

    public LuaMemoryRecord? GetRecordById(string id) =>
        _records.FirstOrDefault(r => r.Id == id);

    public LuaMemoryRecord? GetRecordByDescription(string description) =>
        _records.FirstOrDefault(r => r.Description.Equals(description, StringComparison.OrdinalIgnoreCase));

    public string AddRecord(string address, string dataType, string? description)
    {
        var id = $"addr-{_records.Count + 1}";
        _records.Add(new LuaMemoryRecord(id, description ?? "", address, dataType, "0", false, false));
        return id;
    }

    public void RemoveRecord(string id) =>
        _records.RemoveAll(r => r.Id == id);

    public void SetValue(string id, string value)
    {
        var idx = _records.FindIndex(r => r.Id == id);
        if (idx >= 0)
            _records[idx] = _records[idx] with { Value = value };
    }

    public string? GetValue(string id) =>
        _records.FirstOrDefault(r => r.Id == id)?.Value;

    public string? GetAddress(string id) =>
        _records.FirstOrDefault(r => r.Id == id)?.Address;

    public string? GetDescription(string id) =>
        _records.FirstOrDefault(r => r.Id == id)?.Description;

    public void SetDescription(string id, string description)
    {
        var idx = _records.FindIndex(r => r.Id == id);
        if (idx >= 0)
            _records[idx] = _records[idx] with { Description = description };
    }

    public void SetActive(string id, bool active)
    {
        var idx = _records.FindIndex(r => r.Id == id);
        if (idx >= 0)
            _records[idx] = _records[idx] with { IsActive = active };
    }

    public bool GetActive(string id) =>
        _records.FirstOrDefault(r => r.Id == id)?.IsActive ?? false;

    public void RefreshAll(int processId) { /* no-op in tests */ }
}
