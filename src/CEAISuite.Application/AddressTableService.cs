using System.Collections.ObjectModel;
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

/// <summary>
/// A node in the address table tree. Can be a group (has children, no address)
/// or a leaf entry (has address/value, no children).
/// </summary>
public sealed class AddressTableNode
{
    public string Id { get; set; }
    public string Label { get; set; }
    public bool IsGroup { get; set; }

    // Leaf fields
    public string Address { get; set; } = "";
    public MemoryDataType DataType { get; set; }
    public string CurrentValue { get; set; } = "";
    public string? PreviousValue { get; set; }
    public string? Notes { get; set; }
    public bool IsLocked { get; set; }
    public string? LockedValue { get; set; }

    // Script fields
    public string? AssemblerScript { get; set; }
    public bool IsScriptEntry => AssemblerScript is not null;
    public bool IsScriptEnabled { get; set; }
    public string? ScriptStatus { get; set; }

    // Active state (checkbox — for scripts = enabled, for values = frozen/locked)
    public bool IsActive
    {
        get => IsScriptEntry ? IsScriptEnabled : IsLocked;
        set
        {
            if (IsScriptEntry) IsScriptEnabled = value;
            else IsLocked = value;
        }
    }

    // Tree structure
    public ObservableCollection<AddressTableNode> Children { get; } = new();
    public bool IsExpanded { get; set; } = true;

    // Display helpers
    public string DisplayValue => IsGroup ? $"[{Children.Count} items]"
        : IsScriptEntry ? (IsScriptEnabled ? "✅ Enabled" : "❌ Disabled")
        : CurrentValue;
    public string DisplayType => IsGroup ? "Group" : IsScriptEntry ? "Script" : DataType.ToString();
    public string DisplayLock => IsGroup ? "" : IsScriptEntry ? "" : (IsLocked ? "🔒 Frozen" : "");
    public string DisplayIcon => IsScriptEntry ? "📜" : IsGroup ? "📁" : "";
    public string ValueColor => IsLocked ? "#CC4444" : "#000000";

    public AddressTableNode(string id, string label, bool isGroup)
    {
        Id = id;
        Label = label;
        IsGroup = isGroup;
    }

    /// <summary>Flatten this node and its descendants into AddressTableEntry list (leaves only).</summary>
    public IEnumerable<AddressTableEntry> Flatten()
    {
        if (!IsGroup)
        {
            yield return new AddressTableEntry(Id, Label, Address, DataType, CurrentValue, PreviousValue, Notes, IsLocked, LockedValue);
        }
        foreach (var child in Children)
        {
            foreach (var entry in child.Flatten())
                yield return entry;
        }
    }
}

public sealed class AddressTableService(IEngineFacade engineFacade)
{
    private readonly ObservableCollection<AddressTableNode> _roots = new();

    /// <summary>Observable root nodes for TreeView binding.</summary>
    public ObservableCollection<AddressTableNode> Roots => _roots;

    /// <summary>Flat list of all leaf entries (for export, AI, scripts).</summary>
    public IReadOnlyList<AddressTableEntry> Entries =>
        _roots.SelectMany(n => n.Flatten()).ToList();

    public AddressTableEntry AddEntry(string address, MemoryDataType dataType, string currentValue, string? label = null)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var node = new AddressTableNode(id, label ?? $"Address_{id}", false)
        {
            Address = address,
            DataType = dataType,
            CurrentValue = currentValue
        };
        _roots.Add(node);
        return new AddressTableEntry(id, node.Label, address, dataType, currentValue, null, null, false, null);
    }

    /// <summary>Add an entry into a specific group. Creates the group if it doesn't exist.</summary>
    public AddressTableEntry AddEntryToGroup(string groupId, string address, MemoryDataType dataType, string currentValue, string? label = null)
    {
        var group = FindNode(groupId) ?? throw new InvalidOperationException($"Group '{groupId}' not found.");
        if (!group.IsGroup) throw new InvalidOperationException($"'{groupId}' is not a group.");

        var id = Guid.NewGuid().ToString("N")[..8];
        var node = new AddressTableNode(id, label ?? $"Address_{id}", false)
        {
            Address = address,
            DataType = dataType,
            CurrentValue = currentValue
        };
        group.Children.Add(node);
        return new AddressTableEntry(id, node.Label, address, dataType, currentValue, null, null, false, null);
    }

    /// <summary>Create a named group at root level.</summary>
    public AddressTableNode CreateGroup(string label)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var group = new AddressTableNode(id, label, true);
        _roots.Add(group);
        return group;
    }

    /// <summary>Create a named subgroup inside an existing group.</summary>
    public AddressTableNode CreateSubGroup(string parentGroupId, string label)
    {
        var parent = FindNode(parentGroupId) ?? throw new InvalidOperationException($"Group '{parentGroupId}' not found.");
        if (!parent.IsGroup) throw new InvalidOperationException($"'{parentGroupId}' is not a group.");

        var id = Guid.NewGuid().ToString("N")[..8];
        var group = new AddressTableNode(id, label, true);
        parent.Children.Add(group);
        return group;
    }

    public void AddFromScanResult(ScanResultOverview result, MemoryDataType dataType, string? label = null)
    {
        AddEntry(result.Address, dataType, result.CurrentValue, label);
    }

    public void RemoveEntry(string id)
    {
        RemoveFromCollection(_roots, id);
    }

    public void UpdateLabel(string id, string newLabel)
    {
        var node = FindNode(id);
        if (node is not null) node.Label = newLabel;
    }

    public void UpdateNotes(string id, string? notes)
    {
        var node = FindNode(id);
        if (node is not null) node.Notes = notes;
    }

    public void ToggleLock(string id)
    {
        var node = FindNode(id);
        if (node is null || node.IsGroup) return;
        node.IsLocked = !node.IsLocked;
        node.LockedValue = node.IsLocked ? node.CurrentValue : null;
    }

    /// <summary>Move an entry into a group (or to root if groupId is null).</summary>
    public void MoveToGroup(string entryId, string? groupId)
    {
        var node = FindNode(entryId);
        if (node is null) return;

        RemoveFromCollection(_roots, entryId);

        if (groupId is null)
        {
            _roots.Add(node);
        }
        else
        {
            var group = FindNode(groupId);
            if (group is not null && group.IsGroup)
                group.Children.Add(node);
            else
                _roots.Add(node); // fallback to root
        }
    }

    public async Task RefreshAllAsync(int processId, CancellationToken cancellationToken = default)
    {
        await RefreshNodes(_roots, processId, cancellationToken);
    }

    private async Task RefreshNodes(ObservableCollection<AddressTableNode> nodes, int processId, CancellationToken cancellationToken)
    {
        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node.IsGroup)
            {
                await RefreshNodes(node.Children, processId, cancellationToken);
                continue;
            }
            try
            {
                var typed = await engineFacade.ReadValueAsync(processId, ParseAddress(node.Address), node.DataType, cancellationToken);
                node.PreviousValue = node.CurrentValue;
                node.CurrentValue = typed.DisplayValue;

                if (node.IsLocked && node.LockedValue is not null)
                {
                    await engineFacade.WriteValueAsync(processId, ParseAddress(node.Address), node.DataType, node.LockedValue, cancellationToken);
                }
            }
            catch
            {
                // Value unreadable — keep last known
            }
        }
    }

    /// <summary>Import a flat list of entries as root nodes (for backward compat).</summary>
    public void ImportFlat(IEnumerable<AddressTableEntry> entries)
    {
        foreach (var e in entries)
            AddEntry(e.Address, e.DataType, e.CurrentValue, e.Label);
    }

    /// <summary>Import nodes from CT parser with hierarchy preserved.</summary>
    public void ImportNodes(IEnumerable<AddressTableNode> nodes)
    {
        foreach (var node in nodes)
            _roots.Add(node);
    }

    public AddressTableNode? FindNode(string id) => FindInCollection(_roots, id);

    private static AddressTableNode? FindInCollection(ObservableCollection<AddressTableNode> nodes, string id)
    {
        foreach (var node in nodes)
        {
            if (node.Id == id) return node;
            var found = FindInCollection(node.Children, id);
            if (found is not null) return found;
        }
        return null;
    }

    private static bool RemoveFromCollection(ObservableCollection<AddressTableNode> nodes, string id)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].Id == id) { nodes.RemoveAt(i); return true; }
            if (RemoveFromCollection(nodes[i].Children, id)) return true;
        }
        return false;
    }

    public static nuint ParseAddress(string addressText)
    {
        var normalized = addressText.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return (nuint)ulong.Parse(normalized[2..], System.Globalization.NumberStyles.HexNumber);
        return (nuint)ulong.Parse(normalized);
    }

    /// <summary>Write a node's current value to process memory.</summary>
    public async Task WriteValueAsync(int processId, AddressTableNode node, CancellationToken ct = default)
    {
        var addr = ParseAddress(node.Address);
        await engineFacade.WriteValueAsync(processId, addr, node.DataType, node.CurrentValue, ct);
    }
}
