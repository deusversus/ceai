using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using CEAISuite.Engine.Abstractions;
using Microsoft.Extensions.Logging;

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
/// Implements INotifyPropertyChanged so WPF bindings update live.
/// </summary>
public sealed class AddressTableNode : INotifyPropertyChanged
{
    private const string ChangedFlashColor = "#FF4444";
    private const string ScriptEnabledColor = "#22AA22";
    private const string ScriptDisabledColor = "#AA2222";
    private const string FrozenColor = "#CC4444";
    private const string UnresolvedColor = "#999999";
    private const string DefaultValueColor = "#CCCCCC";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? prop = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
    private void NotifyDisplayProperties()
    {
        Notify(nameof(DisplayValue));
        Notify(nameof(DisplayValueWeight));
        Notify(nameof(DisplayAddress));
        Notify(nameof(DisplayType));
        Notify(nameof(DisplayIcon));
        Notify(nameof(StatusTooltip));
        Notify(nameof(ValueForeground));
    }

    public string Id { get; set; }

    private string _label = "";
    public string Label
    {
        get => _label;
        set { if (_label != value) { _label = value; Notify(); } }
    }

    public bool IsGroup { get; set; }

    // Leaf fields
    private string _address = "";
    public string Address
    {
        get => _address;
        set { if (_address != value) { _address = value; Notify(); Notify(nameof(DisplayAddress)); } }
    }

    private MemoryDataType _dataType;
    public MemoryDataType DataType
    {
        get => _dataType;
        set { if (_dataType != value) { _dataType = value; Notify(); Notify(nameof(DisplayType)); } }
    }

    private string _currentValue = "";
    public string CurrentValue
    {
        get => _currentValue;
        set
        {
            if (_currentValue != value)
            {
                // Track change for UI highlighting (Phase 6)
                if (!string.IsNullOrEmpty(_currentValue) && !string.IsNullOrEmpty(value))
                    ValueJustChanged = true;
                _currentValue = value;
                Notify();
                NotifyDisplayProperties();
            }
        }
    }

    private bool _valueJustChanged;
    /// <summary>Briefly true when CurrentValue changes during auto-refresh. UI uses this for flash highlighting.</summary>
    public bool ValueJustChanged
    {
        get => _valueJustChanged;
        set { if (_valueJustChanged != value) { _valueJustChanged = value; Notify(); Notify(nameof(ValueForeground)); } }
    }

    public string? PreviousValue { get; set; }
    public string? Notes { get; set; }

    private string? _userColor;
    /// <summary>User-selected color for this row (Phase 6). Hex string like "#FF4444".</summary>
    public string? UserColor
    {
        get => _userColor;
        set { if (_userColor != value) { _userColor = value; Notify(); Notify(nameof(RowBackground)); } }
    }

    /// <summary>Row background: user color at reduced opacity, or transparent.</summary>
    public string RowBackground => UserColor is not null ? UserColor + "33" : "#00000000";

    private bool _isLocked;
    public bool IsLocked
    {
        get => _isLocked;
        set { if (_isLocked != value) { _isLocked = value; Notify(); Notify(nameof(IsActive)); NotifyDisplayProperties(); } }
    }

    public string? LockedValue { get; set; }

    // Pointer chain fields (CE-style multi-level pointer resolution)
    public bool IsPointer { get; set; }
    /// <summary>CE stores offsets deepest-first. Resolution reverses them.</summary>
    public List<long> PointerOffsets { get; set; } = new();
    /// <summary>True if address starts with + or - (relative to parent's resolved address, per CE 7.5).</summary>
    public bool IsOffset { get; set; }
    /// <summary>Parent node reference for CE-style offset resolution.</summary>
    public AddressTableNode? Parent { get; set; }
    /// <summary>CE ShowAsSigned flag. false = display as unsigned (default for CE).</summary>
    private bool _showAsSigned;
    public bool ShowAsSigned
    {
        get => _showAsSigned;
        set { if (_showAsSigned != value) { _showAsSigned = value; Notify(); NotifyDisplayProperties(); } }
    }

    /// <summary>CE ShowAsHex flag. true = display integer values in hexadecimal.</summary>
    private bool _showAsHex;
    public bool ShowAsHex
    {
        get => _showAsHex;
        set { if (_showAsHex != value) { _showAsHex = value; Notify(); Notify(nameof(DisplayType)); NotifyDisplayProperties(); } }
    }
    /// <summary>CE DropDownList: value-to-name mapping (e.g., item ID → item name).</summary>
    public Dictionary<int, string>? DropDownList { get; set; }

    private nuint? _resolvedAddress;
    /// <summary>The resolved runtime address (set during RefreshAll).</summary>
    public nuint? ResolvedAddress
    {
        get => _resolvedAddress;
        set { if (_resolvedAddress != value) { _resolvedAddress = value; Notify(); Notify(nameof(DisplayAddress)); } }
    }

    // Script fields
    public string? AssemblerScript { get; set; }
    public bool IsScriptEntry => AssemblerScript is not null;

    private bool _isScriptEnabled;
    public bool IsScriptEnabled
    {
        get => _isScriptEnabled;
        set { if (_isScriptEnabled != value) { _isScriptEnabled = value; Notify(); Notify(nameof(IsActive)); NotifyDisplayProperties(); } }
    }

    public string? ScriptStatus { get; set; }

    // Active state (checkbox — for scripts = enabled, for values = frozen/locked)
    public bool IsActive
    {
        get => IsScriptEntry ? IsScriptEnabled : IsLocked;
        set
        {
            if (IsScriptEntry) IsScriptEnabled = value;
            else IsLocked = value;
            Notify();
        }
    }

    // CT format gap closure: preserved unknown XML elements for round-trip fidelity
    /// <summary>Unknown XML elements preserved from the original CT file for lossless round-tripping.</summary>
    public List<XElement>? PreservedElements { get; set; }

    // CT format gap closure: Options flags
    /// <summary>CE Options flags (moHideChildren, moActivateChildrenAsWell, etc.).</summary>
    public CheatEntryOptions Options { get; set; }

    // CT format gap closure: Hotkeys
    /// <summary>Hotkey bindings from the CT file.</summary>
    public List<CheatEntryHotkey> Hotkeys { get; set; } = new();

    // CT format gap closure: Rich offsets with Interval/UpdateOnFullRefresh attributes
    /// <summary>Original CE offset elements with attributes, for export fidelity.</summary>
    public List<CeOffset>? OriginalOffsets { get; set; }

    // CT format gap closure: LastState attributes
    /// <summary>Last known real address from the CT file's LastState element.</summary>
    public string? LastRealAddress { get; set; }

    // CT format gap closure: String config
    /// <summary>CE String length.</summary>
    public int? StringLength { get; set; }
    /// <summary>CE Unicode flag for string entries.</summary>
    public bool? IsUnicode { get; set; }
    /// <summary>CE ZeroTerminate flag for string entries.</summary>
    public bool? ZeroTerminate { get; set; }

    // CT format gap closure: ByteLength
    /// <summary>CE ByteLength for array-of-byte entries.</summary>
    public int? ByteLength { get; set; }

    // CT format gap closure: Bit fields
    /// <summary>CE BitStart for binary/bit display.</summary>
    public int? BitStart { get; set; }
    /// <summary>CE BitLength for binary/bit display.</summary>
    public int? BitLength { get; set; }
    /// <summary>CE ShowAsBinary flag.</summary>
    public bool ShowAsBinary { get; set; }

    // CT format gap closure: CustomType
    /// <summary>CE CustomType name (user-defined types).</summary>
    public string? CustomTypeName { get; set; }

    // CT format gap closure: DropDownList attributes
    /// <summary>CE DropDownList DescriptionOnly attribute.</summary>
    public bool DropDownDescriptionOnly { get; set; }
    /// <summary>CE DropDownList ReadOnly attribute.</summary>
    public bool DropDownReadOnly { get; set; }
    /// <summary>CE DropDownList DisplayValueAsItem attribute.</summary>
    public bool DropDownDisplayValueAsItem { get; set; }

    // CT format gap closure: Script Async
    /// <summary>CE AssemblerScript Async attribute.</summary>
    public bool ScriptAsync { get; set; }

    // CT format gap closure: Comment
    /// <summary>CE Comment element text.</summary>
    public string? Comment { get; set; }

    // Tree structure
    public ObservableCollection<AddressTableNode> Children { get; } = new();
    public bool IsExpanded { get; set; }

    // Display helpers (computed — fire from setters above)
    public string DisplayValue => IsGroup ? $"[{Children.Count} items]"
        : IsScriptEntry ? (IsScriptEnabled ? "[ENABLED]" : "[DISABLED]")
        : string.IsNullOrEmpty(CurrentValue) ? "??" : (IsLocked ? $"{CurrentValue} 🔒" : CurrentValue);
    public string DisplayType => IsGroup ? "Group" : IsScriptEntry ? "AA Script"
        : ShowAsHex ? $"{DataType} (Hex)" : DataType.ToString();
    public string DisplayIcon => IsScriptEntry ? "📜" : IsGroup ? "📁" : (IsPointer ? "🔗" : "");
    public string DisplayAddress => IsGroup ? "" : IsScriptEntry ? "(script)"
        : ResolvedAddress.HasValue ? $"0x{ResolvedAddress.Value:X}"
        : Address;

    /// <summary>Bold weight for active scripts and frozen values.</summary>
    public string DisplayValueWeight => (IsScriptEntry || IsLocked) ? "Bold" : "Normal";

    /// <summary>Tooltip with full status info.</summary>
    public string StatusTooltip => IsScriptEntry
        ? $"Script: {Label}\nStatus: {(IsScriptEnabled ? "ENABLED" : "DISABLED")}\n{ScriptStatus ?? ""}\n\nCheck the box or use context menu to toggle."
        : IsGroup ? $"Group: {Label} ({Children.Count} items)"
        : $"{Label}\nAddress: {DisplayAddress}\nType: {DataType}\nValue: {CurrentValue}" +
          (IsLocked ? $"\n🔒 FROZEN at {LockedValue}" : "") +
          (IsPointer ? $"\nPointer: {PointerOffsets.Count} level(s)" : "") +
          (!string.IsNullOrEmpty(Notes) ? $"\n\n{Notes}" : "");

    /// <summary>Foreground for value column: red flash when changed, then normal color.</summary>
    public string ValueForeground => ValueJustChanged ? ChangedFlashColor
        : IsScriptEntry ? (IsScriptEnabled ? ScriptEnabledColor : ScriptDisabledColor)
        : IsLocked ? FrozenColor
        : CurrentValue == "???" ? UnresolvedColor
        : DefaultValueColor;

    public AddressTableNode(string id, string label, bool isGroup)
    {
        Id = id;
        _label = label;
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

public sealed class AddressTableService(IEngineFacade engineFacade, ILogger<AddressTableService>? logger = null)
{
    /// <summary>Optional diagnostic trace callback for debugging address table refresh issues.</summary>
    public Action<string, string, string>? DiagnosticLog { get; set; }

    private void Log(string level, string message)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("[{Level}] {Message}", level, message);
        DiagnosticLog?.Invoke("AddressTable", level, message);
    }

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

        // Clear cached resolved addresses — ASLR bases change on every process restart,
        // so stale cached addresses from the old process would cause wrong reads.
        ClearResolvedAddressCache(_roots);
    }

    private static void ClearResolvedAddressCache(IEnumerable<AddressTableNode> nodes)
    {
        foreach (var node in nodes)
        {
            node.ResolvedAddress = null;
            if (node.Children.Count > 0)
                ClearResolvedAddressCache(node.Children);
        }
    }

    public AddressTableEntry AddEntry(string address, MemoryDataType dataType, string currentValue, string? label = null)
    {
        var id = $"addr-{Guid.NewGuid().ToString("N")[..8]}";
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

        var id = $"addr-{Guid.NewGuid().ToString("N")[..8]}";
        var node = new AddressTableNode(id, label ?? $"Address_{id}", false)
        {
            Address = address,
            DataType = dataType,
            CurrentValue = currentValue,
            Parent = group
        };
        group.Children.Add(node);
        return new AddressTableEntry(id, node.Label, address, dataType, currentValue, null, null, false, null);
    }

    /// <summary>Create a named group at root level.</summary>
    public AddressTableNode CreateGroup(string label)
    {
        var id = $"group-{Guid.NewGuid().ToString("N")[..8]}";
        var group = new AddressTableNode(id, label, true);
        _roots.Add(group);
        return group;
    }

    /// <summary>Create a named subgroup inside an existing group.</summary>
    public AddressTableNode CreateSubGroup(string parentGroupId, string label)
    {
        var parent = FindNode(parentGroupId) ?? throw new InvalidOperationException($"Group '{parentGroupId}' not found.");
        if (!parent.IsGroup) throw new InvalidOperationException($"'{parentGroupId}' is not a group.");

        var id = $"group-{Guid.NewGuid().ToString("N")[..8]}";
        var group = new AddressTableNode(id, label, true) { Parent = parent };
        parent.Children.Add(group);
        return group;
    }

    public void AddFromScanResult(ScanResultOverview result, MemoryDataType dataType, string? label = null)
    {
        AddEntry(result.Address, dataType, result.CurrentValue, label);
    }

    /// <summary>Add an existing node to a group by ID.</summary>
    public void AddEntryToGroup(AddressTableNode node, string groupId)
    {
        var group = FindNode(groupId) ?? throw new InvalidOperationException($"Group '{groupId}' not found.");
        if (!group.IsGroup) throw new InvalidOperationException($"'{groupId}' is not a group.");
        node.Parent = group;
        group.Children.Add(node);
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
        // Always refresh module list — base addresses change due to ASLR on every process restart
        try
        {
            var attachment = await engineFacade.AttachAsync(processId, cancellationToken).ConfigureAwait(false);
            var arch = "x64"; // default
            try { arch = TryDetectArchitecture(processId); }
            catch (Exception archEx) { Log("Warning", $"Architecture detection failed for PID {processId}: {archEx.Message}"); }
            SetProcessContext(attachment.Modules, arch == "x86");
        }
        catch (Exception ex)
        {
            // AttachAsync failed — process may be dead or still loading.
            // Clear stale modules so we don't resolve with wrong ASLR bases.
            Log("Warning", $"RefreshAllAsync: AttachAsync failed for PID {processId}: {ex.GetType().Name}: {ex.Message}");
            if (_processModules.Count > 0)
            {
                _processModules = Array.Empty<ModuleDescriptor>();
                ClearResolvedAddressCache(_roots);
                Log("Warning", "Cleared stale module cache due to attach failure.");
            }
            return;
        }
        await RefreshNodes(_roots, processId, cancellationToken).ConfigureAwait(false);
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
                // CE 7.5: Groups can have their own address + pointer chain.
                // Children use the parent's resolved address as their base.
                if (!string.IsNullOrEmpty(node.Address) && node.Address != "0" && node.Address != "(script)")
                {
                    try
                    {
                        var resolvedAddr = await ResolveAddress(node, processId, cancellationToken).ConfigureAwait(false);
                        node.ResolvedAddress = resolvedAddr != nuint.Zero ? resolvedAddr : null;
                    }
                    catch (Exception ex) { if (logger is not null && logger.IsEnabled(LogLevel.Debug)) logger.LogDebug(ex, "Failed to resolve address for node {Label}", node.Label); node.ResolvedAddress = null; }
                }
                // If group has a pointer chain but failed to resolve, skip children
                // (they depend on this group's address and would read garbage)
                if (node.IsPointer && node.ResolvedAddress is null)
                {
                    SetChildrenUnresolved(node);
                    continue;
                }
                await RefreshNodes(node.Children, processId, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (node.IsScriptEntry && node.Address == "(script)") continue;

            try
            {
                var resolvedAddr = await ResolveAddress(node, processId, cancellationToken).ConfigureAwait(false);
                if (resolvedAddr == nuint.Zero)
                {
                    node.ResolvedAddress = null;
                    node.CurrentValue = "??";
                    continue;
                }
                node.ResolvedAddress = resolvedAddr;

                var typed = await engineFacade.ReadValueAsync(processId, resolvedAddr, node.DataType, cancellationToken).ConfigureAwait(false);
                node.PreviousValue = node.CurrentValue;

                // Format the display value: hex mode, unsigned mode, or default
                string displayValue;
                if (node.ShowAsHex && typed.RawBytes is { Count: > 0 })
                    displayValue = FormatHex(typed.RawBytes, node.DataType) ?? typed.DisplayValue;
                else if (!node.ShowAsSigned && typed.RawBytes is { Count: > 0 })
                    displayValue = FormatUnsigned(typed.RawBytes, node.DataType) ?? typed.DisplayValue;
                else
                    displayValue = typed.DisplayValue;

                // Apply dropdown list name lookup if available
                if (node.DropDownList is not null && TryParseDisplayInt(displayValue, node.ShowAsHex, out var intVal)
                    && node.DropDownList.TryGetValue(intVal, out var lookupName))
                    displayValue = $"{displayValue} : {lookupName}";

                node.CurrentValue = displayValue;

                if (node.IsLocked && node.LockedValue is not null)
                {
                    await engineFacade.WriteValueAsync(processId, resolvedAddr, node.DataType, node.LockedValue, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                node.CurrentValue = "???";
                // Only log occasionally to avoid flooding — read failures are common when process exits
                Log("Debug", $"Read failed for \"{node.Label}\" at {node.Address}: {ex.GetType().Name}: {ex.Message}");
            }
            // Also recurse into any children of this entry
            if (node.Children.Count > 0)
                await RefreshNodes(node.Children, processId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Recursively mark all descendant leaves as unresolved ("??").</summary>
    private static void SetChildrenUnresolved(AddressTableNode parent)
    {
        foreach (var child in parent.Children)
        {
            if (child.IsGroup)
            {
                child.ResolvedAddress = null;
                SetChildrenUnresolved(child);
            }
            else if (!child.IsScriptEntry)
            {
                child.ResolvedAddress = null;
                child.CurrentValue = "??";
            }
        }
    }

    /// <summary>
    /// Resolves a CE-style address to a runtime address.
    /// Handles: raw hex, module-relative ("module.dll"+offset), and pointer chains.
    /// </summary>
    private async Task<nuint> ResolveAddress(AddressTableNode node, int processId, CancellationToken ct)
    {
        // Step 1: Resolve the base address (may be module-relative or parent-relative)
        var baseAddr = ResolveBaseAddress(node);
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
            var ptrRead = await engineFacade.ReadMemoryAsync(processId, current, ptrSize, ct).ConfigureAwait(false);
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

    /// <summary>Format raw bytes as unsigned for CE's ShowAsSigned=0.</summary>
    private static string? FormatUnsigned(IReadOnlyList<byte> raw, MemoryDataType dt)
    {
        if (raw.Count == 0) return null;
        var buf = raw is byte[] arr ? arr : raw.ToArray();
        return dt switch
        {
            MemoryDataType.Byte => buf[0].ToString(CultureInfo.InvariantCulture),
            MemoryDataType.Int16 => BitConverter.ToUInt16(buf, 0).ToString(CultureInfo.InvariantCulture),
            MemoryDataType.Int32 => BitConverter.ToUInt32(buf, 0).ToString(CultureInfo.InvariantCulture),
            MemoryDataType.Int64 => BitConverter.ToUInt64(buf, 0).ToString(CultureInfo.InvariantCulture),
            _ => null // Float/Double/String — no signed/unsigned distinction
        };
    }

    private static string? FormatHex(IReadOnlyList<byte> raw, MemoryDataType dt)
    {
        if (raw.Count == 0) return null;
        var buf = raw is byte[] arr ? arr : raw.ToArray();
        return dt switch
        {
            MemoryDataType.Byte => buf[0].ToString("X2", CultureInfo.InvariantCulture),
            MemoryDataType.Int16 => BitConverter.ToUInt16(buf, 0).ToString("X", CultureInfo.InvariantCulture),
            MemoryDataType.Int32 => BitConverter.ToUInt32(buf, 0).ToString("X", CultureInfo.InvariantCulture),
            MemoryDataType.Int64 => BitConverter.ToUInt64(buf, 0).ToString("X", CultureInfo.InvariantCulture),
            _ => null // Float/Double/String — hex doesn't apply
        };
    }

    private static bool TryParseDisplayInt(string displayValue, bool isHex, out int result)
    {
        // Strip any existing lookup suffix ("2904 : Dagger" → "2904")
        var valueStr = displayValue.Contains(" : ", StringComparison.Ordinal) ? displayValue[..displayValue.IndexOf(" : ", StringComparison.Ordinal)] : displayValue;
        valueStr = valueStr.Trim();

        if (isHex)
            return int.TryParse(valueStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result);
        return int.TryParse(valueStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    /// <summary>
    /// Resolves a base address string using CE 7.5's algorithm:
    /// - If address starts with +/- and has a parent with a resolved address, use parent.RealAddress + offset
    /// - Module-relative: "GameAssembly.dll"+hexOffset
    /// - Raw hex: 1A2B3C or 0x1A2B3C
    /// </summary>
    private nuint ResolveBaseAddress(AddressTableNode node)
    {
        var address = node.Address;
        if (string.IsNullOrWhiteSpace(address) || address == "(script)") return nuint.Zero;

        var addr = address.Trim().Replace("\"", ""); // strip quotes CE puts around module names

        // CE 7.5 parent-relative offset: fIsOffset && hasParent
        // Walk up parent chain to find first ancestor with a real interpretable address
        if (node.IsOffset && node.Parent is not null)
        {
            var offset = ParseHexOffset(addr.TrimStart('+', '-'));
            if (addr.TrimStart().StartsWith('-'))
                offset = -offset;

            // Walk up to find first parent with resolved address
            var ancestor = node.Parent;
            while (ancestor is not null)
            {
                if (ancestor.ResolvedAddress.HasValue && ancestor.ResolvedAddress.Value != nuint.Zero)
                    return (nuint)((long)ancestor.ResolvedAddress.Value + offset);

                // Also try if ancestor has a real address (not just "+0")
                if (!string.IsNullOrEmpty(ancestor.Address) &&
                    !ancestor.Address.TrimStart().StartsWith('+') &&
                    !ancestor.Address.TrimStart().StartsWith('-') &&
                    ancestor.Address != "0")
                    break; // ancestor has its own address but not yet resolved — can't continue

                ancestor = ancestor.Parent;
            }

            // No ancestor resolved — parent pointer chain is broken (e.g. character slot not loaded)
            return nuint.Zero;
        }

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

        // Main-module-relative: "+hexOffset" without parent (top-level)
        if (addr.StartsWith('+'))
        {
            var offsetStr = addr[1..].Trim();
            var offset = ParseHexOffset(offsetStr);
            var mainMod = _processModules.Count > 0 ? _processModules[0] : null;
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
        return long.TryParse(s, System.Globalization.NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var result) ? result : 0;
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

    /// <summary>Remove all entries and groups from the address table.</summary>
    public void ClearAll()
    {
        _roots.Clear();
    }

    public AddressTableNode? FindNode(string id) => FindInCollection(_roots, id);

    public AddressTableNode? FindNodeByLabel(string label) => FindByLabelInCollection(_roots, label);

    private static AddressTableNode? FindByLabelInCollection(ObservableCollection<AddressTableNode> nodes, string label)
    {
        foreach (var node in nodes)
        {
            if (string.Equals(node.Label, label, StringComparison.OrdinalIgnoreCase)) return node;
            var found = FindByLabelInCollection(node.Children, label);
            if (found is not null) return found;
        }
        return null;
    }

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
        ArgumentException.ThrowIfNullOrWhiteSpace(addressText);

        var normalized = addressText.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.TryParse(normalized[2..], System.Globalization.NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexAddress)
                ? (nuint)hexAddress
                : throw new FormatException("Address must be a valid hexadecimal value.");
        }

        return ulong.TryParse(normalized, System.Globalization.NumberStyles.Integer, CultureInfo.InvariantCulture, out var decimalAddress)
            ? (nuint)decimalAddress
            : throw new FormatException("Address must be a valid decimal or hexadecimal value.");
    }

    /// <summary>Write a node's current value to process memory.</summary>
    public async Task WriteValueAsync(int processId, AddressTableNode node, CancellationToken ct = default)
    {
        var addr = node.ResolvedAddress ?? await ResolveAddress(node, processId, ct).ConfigureAwait(false);
        if (addr == nuint.Zero) throw new InvalidOperationException("Cannot resolve address for this entry.");
        await engineFacade.WriteValueAsync(processId, addr, node.DataType, node.CurrentValue, ct).ConfigureAwait(false);
    }
}
