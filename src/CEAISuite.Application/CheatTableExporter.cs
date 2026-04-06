using System.Globalization;
using System.Xml.Linq;

namespace CEAISuite.Application;

/// <summary>
/// Exports AddressTableNode trees back to Cheat Engine .CT (Cheat Table) XML format.
/// Produces XML that is round-trip compatible with <see cref="CheatTableParser"/>.
/// </summary>
public sealed class CheatTableExporter
{
    private int _nextId;

    /// <summary>
    /// Serialize a tree of address table nodes to a CE-format CT XML string.
    /// Optionally accepts preserved table-level elements for round-trip fidelity.
    /// </summary>
    public string ExportToXml(IEnumerable<AddressTableNode> roots, IReadOnlyList<XElement>? preservedTableElements = null)
    {
        _nextId = 0;
        var cheatTable = new XElement("CheatTable",
            new XAttribute("CheatEngineTableVersion", "46"),
            new XElement("CheatEntries",
                roots.Select(BuildEntry)));

        // Append preserved table-level elements (Step 0 passthrough)
        if (preservedTableElements is not null)
        {
            foreach (var el in preservedTableElements)
                cheatTable.Add(new XElement(el));
        }

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            cheatTable);

        return doc.Declaration + Environment.NewLine + doc.Root!.ToString();
    }

    /// <summary>
    /// Save a tree of address table nodes to a .CT file on disk.
    /// </summary>
    public void SaveToFile(IEnumerable<AddressTableNode> roots, string filePath, IReadOnlyList<XElement>? preservedTableElements = null)
    {
        var xml = ExportToXml(roots, preservedTableElements);
        File.WriteAllText(filePath, xml);
    }

    private XElement BuildEntry(AddressTableNode node)
    {
        var id = _nextId++;
        var entry = new XElement("CheatEntry");

        entry.Add(new XElement("ID", id));
        entry.Add(new XElement("Description", $"\"{node.Label}\""));

        if (node.IsGroup)
        {
            entry.Add(new XElement("GroupHeader", "1"));

            // Color
            if (node.UserColor is not null)
            {
                var bgr = CheatTableParser.ConvertRgbToBgr(node.UserColor);
                if (bgr is not null)
                    entry.Add(new XElement("Color", bgr));
            }

            // Options
            EmitOptions(entry, node.Options);

            // Hotkeys
            EmitHotkeys(entry, node.Hotkeys);

            // Comment
            if (!string.IsNullOrEmpty(node.Comment))
                entry.Add(new XElement("Comment", node.Comment));

            if (node.Children.Count > 0)
            {
                entry.Add(new XElement("CheatEntries",
                    node.Children.Select(BuildEntry)));
            }

            // Preserved unknown elements (Step 0)
            AppendPreservedElements(entry, node.PreservedElements);

            return entry;
        }

        // Script-only entries
        if (node.IsScriptEntry)
        {
            entry.Add(new XElement("VariableType", "Auto Assembler Script"));
            var scriptEl = new XElement("AssemblerScript", new XCData(node.AssemblerScript!));
            if (node.ScriptAsync)
                scriptEl.Add(new XAttribute("Async", "1"));
            entry.Add(scriptEl);
            entry.Add(new XElement("Address", "0"));

            // Color
            if (node.UserColor is not null)
            {
                var bgr = CheatTableParser.ConvertRgbToBgr(node.UserColor);
                if (bgr is not null)
                    entry.Add(new XElement("Color", bgr));
            }

            // Options
            EmitOptions(entry, node.Options);

            // Hotkeys
            EmitHotkeys(entry, node.Hotkeys);

            // LastState with RealAddress + Activated
            EmitLastState(entry, node);

            // Comment
            if (!string.IsNullOrEmpty(node.Comment))
                entry.Add(new XElement("Comment", node.Comment));

            if (node.Children.Count > 0)
            {
                entry.Add(new XElement("CheatEntries",
                    node.Children.Select(BuildEntry)));
            }

            // Preserved unknown elements
            AppendPreservedElements(entry, node.PreservedElements);

            return entry;
        }

        // Regular value entries
        var ceType = MapDataTypeToCe(node.DataType);
        entry.Add(new XElement("VariableType", ceType));

        // Address: use the raw address (module-relative or absolute hex)
        var address = node.Address;
        if (string.IsNullOrEmpty(address)) address = "0";
        entry.Add(new XElement("Address", address));

        if (!node.ShowAsSigned)
        {
            entry.Add(new XElement("ShowAsSigned", "0"));
        }

        if (node.ShowAsHex)
        {
            entry.Add(new XElement("ShowAsHex", "1"));
        }

        if (node.DropDownList is { Count: > 0 })
        {
            // CE format: "value:name\r\n" pairs
            var lines = string.Join("\r\n", node.DropDownList.Select(kvp => $"{kvp.Key}:{kvp.Value}"));
            var ddEl = new XElement("DropDownList", lines + "\r\n");
            if (node.DropDownDescriptionOnly)
                ddEl.Add(new XAttribute("DescriptionOnly", "1"));
            if (node.DropDownReadOnly)
                ddEl.Add(new XAttribute("ReadOnly", "1"));
            if (node.DropDownDisplayValueAsItem)
                ddEl.Add(new XAttribute("DisplayValueAsItem", "1"));
            entry.Add(ddEl);
        }

        // Color
        if (node.UserColor is not null)
        {
            var bgr = CheatTableParser.ConvertRgbToBgr(node.UserColor);
            if (bgr is not null)
                entry.Add(new XElement("Color", bgr));
        }

        // Options
        EmitOptions(entry, node.Options);

        // Hotkeys
        EmitHotkeys(entry, node.Hotkeys);

        // Pointer offsets -- CE stores them deepest-first (same order we keep in PointerOffsets)
        if (node.IsPointer && node.PointerOffsets.Count > 0)
        {
            var offsetsEl = new XElement("Offsets");
            for (var i = 0; i < node.PointerOffsets.Count; i++)
            {
                var offsetEl = new XElement("Offset", node.PointerOffsets[i].ToString("X", CultureInfo.InvariantCulture));
                // Emit rich offset attributes if available
                if (node.OriginalOffsets is not null && i < node.OriginalOffsets.Count)
                {
                    var rich = node.OriginalOffsets[i];
                    if (rich.Interval.HasValue)
                        offsetEl.Add(new XAttribute("Interval", rich.Interval.Value));
                    if (rich.UpdateOnFullRefresh)
                        offsetEl.Add(new XAttribute("UpdateOnFullRefresh", "1"));
                }
                offsetsEl.Add(offsetEl);
            }
            entry.Add(offsetsEl);
        }

        // Last known value + RealAddress + Activated
        EmitLastState(entry, node);

        // Assembler script on a value entry (some entries have both address + script)
        if (node.AssemblerScript is not null)
        {
            var scriptEl = new XElement("AssemblerScript", new XCData(node.AssemblerScript));
            if (node.ScriptAsync)
                scriptEl.Add(new XAttribute("Async", "1"));
            entry.Add(scriptEl);
        }

        // Phase 2: String config
        if (node.StringLength.HasValue)
            entry.Add(new XElement("Length", node.StringLength.Value));
        if (node.IsUnicode.HasValue)
            entry.Add(new XElement("Unicode", node.IsUnicode.Value ? "1" : "0"));
        if (node.ZeroTerminate.HasValue)
            entry.Add(new XElement("ZeroTerminate", node.ZeroTerminate.Value ? "1" : "0"));

        // ByteLength
        if (node.ByteLength.HasValue)
            entry.Add(new XElement("ByteLength", node.ByteLength.Value));

        // Bit fields
        if (node.BitStart.HasValue)
            entry.Add(new XElement("BitStart", node.BitStart.Value));
        if (node.BitLength.HasValue)
            entry.Add(new XElement("BitLength", node.BitLength.Value));
        if (node.ShowAsBinary)
            entry.Add(new XElement("ShowAsBinary", "1"));

        // CustomType
        if (!string.IsNullOrEmpty(node.CustomTypeName))
            entry.Add(new XElement("CustomType", node.CustomTypeName));

        // Comment
        if (!string.IsNullOrEmpty(node.Comment))
            entry.Add(new XElement("Comment", node.Comment));

        // Nested children
        if (node.Children.Count > 0)
        {
            entry.Add(new XElement("CheatEntries",
                node.Children.Select(BuildEntry)));
        }

        // Preserved unknown elements (Step 0)
        AppendPreservedElements(entry, node.PreservedElements);

        return entry;
    }

    private static void EmitOptions(XElement entry, CheatEntryOptions options)
    {
        if (options == CheatEntryOptions.None) return;
        var optEl = new XElement("Options");
        if (options.HasFlag(CheatEntryOptions.HideChildren))
            optEl.Add(new XAttribute("moHideChildren", "1"));
        if (options.HasFlag(CheatEntryOptions.ActivateChildrenAsWell))
            optEl.Add(new XAttribute("moActivateChildrenAsWell", "1"));
        if (options.HasFlag(CheatEntryOptions.DeactivateChildrenAsWell))
            optEl.Add(new XAttribute("moDeactivateChildrenAsWell", "1"));
        if (options.HasFlag(CheatEntryOptions.RecursiveSetValue))
            optEl.Add(new XAttribute("moRecursiveSetValue", "1"));
        if (options.HasFlag(CheatEntryOptions.AllowManualCollapseAndExpand))
            optEl.Add(new XAttribute("moAllowManualCollapseAndExpand", "1"));
        entry.Add(optEl);
    }

    private static void EmitHotkeys(XElement entry, List<CheatEntryHotkey>? hotkeys)
    {
        if (hotkeys is null || hotkeys.Count == 0) return;
        var hotkeysEl = new XElement("Hotkeys");
        foreach (var hk in hotkeys)
        {
            var hkEl = new XElement("Hotkey");
            hkEl.Add(new XElement("Action", hk.Action));
            if (hk.Value is not null)
                hkEl.Add(new XElement("Value", hk.Value));
            hkEl.Add(new XElement("ID", hk.Id));
            var keysEl = new XElement("Keys");
            foreach (var key in hk.Keys)
                keysEl.Add(new XElement("Key", key));
            hkEl.Add(keysEl);
            hotkeysEl.Add(hkEl);
        }
        entry.Add(hotkeysEl);
    }

    private static void EmitLastState(XElement entry, AddressTableNode node)
    {
        var hasValue = !string.IsNullOrEmpty(node.CurrentValue) && node.CurrentValue != "0" && node.CurrentValue != "??";
        var realAddr = node.ResolvedAddress.HasValue
            ? node.ResolvedAddress.Value.ToString("X8", CultureInfo.InvariantCulture)
            : node.LastRealAddress;
        var activated = node.IsActive;

        if (!hasValue && realAddr is null && !activated) return;

        var lastState = new XElement("LastState");
        if (hasValue)
            lastState.Add(new XAttribute("Value", node.CurrentValue));
        if (realAddr is not null)
            lastState.Add(new XAttribute("RealAddress", realAddr));
        if (activated)
            lastState.Add(new XAttribute("Activated", "1"));
        entry.Add(lastState);
    }

    private static void AppendPreservedElements(XElement entry, List<XElement>? preserved)
    {
        if (preserved is null) return;
        foreach (var el in preserved)
            entry.Add(new XElement(el));
    }

    private static string MapDataTypeToCe(Engine.Abstractions.MemoryDataType dt) =>
        dt switch
        {
            Engine.Abstractions.MemoryDataType.Byte => "Byte",
            Engine.Abstractions.MemoryDataType.Int16 => "2 Bytes",
            Engine.Abstractions.MemoryDataType.Int32 => "4 Bytes",
            Engine.Abstractions.MemoryDataType.Int64 => "8 Bytes",
            Engine.Abstractions.MemoryDataType.Float => "Float",
            Engine.Abstractions.MemoryDataType.Double => "Double",
            Engine.Abstractions.MemoryDataType.String => "String",
            Engine.Abstractions.MemoryDataType.ByteArray => "Array of Byte",
            _ => "4 Bytes"
        };
}
