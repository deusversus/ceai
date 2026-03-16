using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Application;

public sealed record AddressTableEntry(
    string Id,
    string Label,
    string Address,
    MemoryDataType DataType,
    string CurrentValue,
    string? PreviousValue,
    string? Notes,
    bool IsLocked,
    string? LockedValue);

public sealed class AddressTableService(IEngineFacade engineFacade)
{
    private readonly List<AddressTableEntry> _entries = new();

    public IReadOnlyList<AddressTableEntry> Entries => _entries;

    public AddressTableEntry AddEntry(string address, MemoryDataType dataType, string currentValue, string? label = null)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var entry = new AddressTableEntry(
            id,
            label ?? $"Address_{id}",
            address,
            dataType,
            currentValue,
            null,
            null,
            false,
            null);

        _entries.Add(entry);
        return entry;
    }

    public void AddFromScanResult(ScanResultOverview result, MemoryDataType dataType, string? label = null)
    {
        AddEntry(result.Address, dataType, result.CurrentValue, label);
    }

    public void RemoveEntry(string id)
    {
        _entries.RemoveAll(entry => entry.Id == id);
    }

    public void UpdateLabel(string id, string newLabel)
    {
        var index = _entries.FindIndex(entry => entry.Id == id);
        if (index >= 0)
        {
            _entries[index] = _entries[index] with { Label = newLabel };
        }
    }

    public void UpdateNotes(string id, string? notes)
    {
        var index = _entries.FindIndex(entry => entry.Id == id);
        if (index >= 0)
        {
            _entries[index] = _entries[index] with { Notes = notes };
        }
    }

    public void ToggleLock(string id)
    {
        var index = _entries.FindIndex(entry => entry.Id == id);
        if (index >= 0)
        {
            var entry = _entries[index];
            _entries[index] = entry with
            {
                IsLocked = !entry.IsLocked,
                LockedValue = !entry.IsLocked ? entry.CurrentValue : null
            };
        }
    }

    public async Task RefreshAllAsync(int processId, CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var entry = _entries[i];
                var typed = await engineFacade.ReadValueAsync(processId, ParseAddress(entry.Address), entry.DataType, cancellationToken);
                _entries[i] = entry with
                {
                    PreviousValue = entry.CurrentValue,
                    CurrentValue = typed.DisplayValue
                };

                if (entry.IsLocked && entry.LockedValue is not null)
                {
                    await engineFacade.WriteValueAsync(processId, ParseAddress(entry.Address), entry.DataType, entry.LockedValue, cancellationToken);
                }
            }
            catch
            {
                // Value unreadable — keep last known value
            }
        }
    }

    private static nuint ParseAddress(string addressText)
    {
        var normalized = addressText.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return (nuint)ulong.Parse(normalized[2..], System.Globalization.NumberStyles.HexNumber);
        }

        return (nuint)ulong.Parse(normalized);
    }
}
