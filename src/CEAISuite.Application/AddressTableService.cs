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

    // Pointer chain fields (CE-style multi-level pointer resolution)
    public bool IsPointer { get; set; }
    /// <summary>CE stores offsets deepest-first. Resolution reverses them.</summary>
    public List<long> PointerOffsets { get; set; } = new();
    /// <summary>The resolved runtime address (set during RefreshAll).</summary>
    public nuint? ResolvedAddress { get; set; }

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
    public string DisplayIcon => IsScriptEntry ? "📜" : IsGroup ? "📁" : (IsPointer ? "🔗" : "");
    public string DisplayAddress => IsGroup ? "" : IsScriptEntry ? "(script)"
        : ResolvedAddress.HasValue ? $"0x{ResolvedAddress.Value:X}"
        : Address;
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
    private IReadOnlyList<ModuleDescriptor> _processModules = Array.Empty<ModuleDescriptor>();
    private bool _is32Bit;

    /// <summary>Observable root nodes for TreeView binding.</summary>
    public ObservableCollection<AddressTableNode> Roots => _roots;

    /// <summary>Flat list of all leaf entries (for export, AI, scripts).</summary>
    public IReadOnlyList<AddressTableEntry> Entries =>
        _roots.SelectMany(n => n.Flatten()).ToList();

    /// <summary>Call after attaching to a process so pointer resolution can find modules.</summary>
    public void SetProcessContext(IReadOnlyList<ModuleDescriptor> modules, bool is32Bit)
    {
        _processModules = modules;
        _is32Bit = is32Bit;
    }

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
        // Ensure we have module info
        if (_processModules.Count == 0)
        {
            try
            {
                var attachment = await engineFacade.AttachAsync(processId, cancellationToken);
                var arch = "x64"; // default
                try { arch = TryDetectArchitecture(processId); } catch { }
                SetProcessContext(attachment.Modules, arch == "x86");
            }
            catch { /* proceed without modules — raw hex addresses may still work */ }
        }
        await RefreshNodes(_roots, processId, cancellationToken);
    }

    private static string TryDetectArchitecture(int processId)
    {
        using var proc = System.Diagnostics.Process.GetProcessById(processId);
        // Check if running under WOW64 (32-bit on 64-bit OS)
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            if (IsWow64Process(proc.Handle, out var isWow64) && isWow64)
                return "x86";
        }
        return "x64";
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(IntPtr hProcess, out bool wow64Process);

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
            if (node.IsScriptEntry && node.Address == "(script)") continue;
            try
            {
                var resolvedAddr = await ResolveAddress(node, processId, cancellationToken);
                if (resolvedAddr == nuint.Zero) continue;
                node.ResolvedAddress = resolvedAddr;

                var typed = await engineFacade.ReadValueAsync(processId, resolvedAddr, node.DataType, cancellationToken);
                node.PreviousValue = node.CurrentValue;
                node.CurrentValue = typed.DisplayValue;

                if (node.IsLocked && node.LockedValue is not null)
                {
                    await engineFacade.WriteValueAsync(processId, resolvedAddr, node.DataType, node.LockedValue, cancellationToken);
                }
            }
            catch
            {
                node.CurrentValue = "???";
            }
            // Also recurse into any children of this entry
            if (node.Children.Count > 0)
                await RefreshNodes(node.Children, processId, cancellationToken);
        }
    }

    /// <summary>
    /// Resolves a CE-style address to a runtime address.
    /// Handles: raw hex, module-relative ("module.dll"+offset), and pointer chains.
    /// </summary>
    private async Task<nuint> ResolveAddress(AddressTableNode node, int processId, CancellationToken ct)
    {
        // Step 1: Resolve the base address (may be module-relative)
        var baseAddr = ResolveBaseAddress(node.Address);
        if (baseAddr == nuint.Zero) return nuint.Zero;

        // Step 2: If not a pointer, the base is the final address
        if (!node.IsPointer || node.PointerOffsets.Count == 0)
            return baseAddr;

        // Step 3: Walk the pointer chain
        // CE stores offsets deepest-first in XML. To resolve, iterate in reverse:
        // For offsets [deepest, ..., shallowest]:
        //   Start at base, for i=count-1 downto 0: read ptr, add offset[i]
        var current = baseAddr;
        for (var i = node.PointerOffsets.Count - 1; i >= 0; i--)
        {
            // Read pointer at current address
            var ptrSize = _is32Bit ? 4 : 8;
            var ptrRead = await engineFacade.ReadMemoryAsync(processId, current, ptrSize, ct);
            var ptrBytes = ptrRead.Bytes.ToArray();
            var ptrValue = _is32Bit
                ? (nuint)BitConverter.ToUInt32(ptrBytes, 0)
                : (nuint)BitConverter.ToUInt64(ptrBytes, 0);

            if (ptrValue == nuint.Zero) return nuint.Zero; // null pointer

            // Add the offset at this level
            current = (nuint)((long)ptrValue + node.PointerOffsets[i]);
        }

        return current;
    }

    /// <summary>
    /// Resolves a base address string. Handles:
    /// - Raw hex: "1A2B3C" or "0x1A2B3C"
    /// - Module-relative: "GameAssembly.dll"+02E9EBB0  or  "module.dll"+offset
    /// - Main-module-relative: "+0" or "+1234"
    /// </summary>
    private nuint ResolveBaseAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address) || address == "(script)") return nuint.Zero;

        var addr = address.Trim().Replace("\"", ""); // strip quotes CE puts around module names

        // Module-relative: "module.dll"+hexOffset
        var plusIdx = addr.IndexOf('+');
        if (plusIdx > 0 && addr[..plusIdx].Contains('.'))
        {
            var moduleName = addr[..plusIdx].Trim();
            var offsetStr = addr[(plusIdx + 1)..].Trim();
            var offset = ParseHexOffset(offsetStr);

            var mod = _processModules.FirstOrDefault(m =>
                string.Equals(m.Name, moduleName, StringComparison.OrdinalIgnoreCase));
            if (mod is null) return nuint.Zero; // module not found

            return (nuint)((long)mod.BaseAddress + offset);
        }

        // Main-module-relative: "+hexOffset"
        if (addr.StartsWith('+'))
        {
            var offsetStr = addr[1..].Trim();
            var offset = ParseHexOffset(offsetStr);
            var mainMod = _processModules.FirstOrDefault();
            if (mainMod is null) return nuint.Zero;
            return (nuint)((long)mainMod.BaseAddress + offset);
        }

        // Raw hex address
        return ParseAddress(addr);
    }

    private static long ParseHexOffset(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return long.Parse(s, System.Globalization.NumberStyles.HexNumber);
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
        var addr = node.ResolvedAddress ?? await ResolveAddress(node, processId, ct);
        if (addr == nuint.Zero) throw new InvalidOperationException("Cannot resolve address for this entry.");
        await engineFacade.WriteValueAsync(processId, addr, node.DataType, node.CurrentValue, ct);
    }
}
