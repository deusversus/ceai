using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Application;

public sealed class ScriptGenerationService
{
    public static string GenerateTrainerScript(IReadOnlyList<AddressTableEntry> entries, string processName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// CE AI Suite - Auto-generated Trainer Script");
        sb.AppendLine(CultureInfo.InvariantCulture, $"// Target: {processName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"// Generated: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine(CultureInfo.InvariantCulture, $"// Entries: {entries.Count}");
        sb.AppendLine();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Diagnostics;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine("using System.Runtime.InteropServices;");
        sb.AppendLine();
        sb.AppendLine("namespace CEAISuite.GeneratedTrainer;");
        sb.AppendLine();
        sb.AppendLine("public static class Trainer");
        sb.AppendLine("{");
        sb.AppendLine("    [DllImport(\"kernel32.dll\")]");
        sb.AppendLine("    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);");
        sb.AppendLine();
        sb.AppendLine("    [DllImport(\"kernel32.dll\")]");
        sb.AppendLine("    private static extern bool WriteProcessMemory(IntPtr handle, IntPtr address, byte[] buffer, int size, out int written);");
        sb.AppendLine();
        sb.AppendLine("    [DllImport(\"kernel32.dll\")]");
        sb.AppendLine("    private static extern bool CloseHandle(IntPtr handle);");
        sb.AppendLine();
        sb.AppendLine("    public static void Apply()");
        sb.AppendLine("    {");
        sb.AppendLine(CultureInfo.InvariantCulture, $"        var process = Process.GetProcessesByName(\"{processName.Replace(".exe", "")}\").FirstOrDefault();");
        sb.AppendLine("        if (process is null) { Console.WriteLine(\"Process not found.\"); return; }");
        sb.AppendLine();
        sb.AppendLine("        var handle = OpenProcess(0x0028, false, process.Id); // VM_WRITE | VM_OPERATION");
        sb.AppendLine("        if (handle == IntPtr.Zero) { Console.WriteLine(\"Cannot open process.\"); return; }");
        sb.AppendLine();

        foreach (var entry in entries)
        {
            if (entry.IsLocked && entry.LockedValue is not null)
            {
                var bytes = GetValueBytes(entry.DataType.ToString(), entry.LockedValue);
                sb.AppendLine(CultureInfo.InvariantCulture, $"        // {entry.Label}: {entry.DataType} = {entry.LockedValue}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"        WriteValue(handle, (IntPtr){entry.Address}, new byte[] {{ {bytes} }});");
                sb.AppendLine();
            }
        }

        sb.AppendLine("        CloseHandle(handle);");
        sb.AppendLine("        Console.WriteLine(\"Trainer applied.\");");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private static void WriteValue(IntPtr handle, IntPtr address, byte[] value)");
        sb.AppendLine("    {");
        sb.AppendLine("        WriteProcessMemory(handle, address, value, value.Length, out _);");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public static void Main()");
        sb.AppendLine("    {");
        sb.AppendLine("        Apply();");
        sb.AppendLine("        Console.WriteLine(\"Press any key to exit...\");");
        sb.AppendLine("        Console.ReadKey(true);");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    public static string GenerateAutoAssemblerScript(IReadOnlyList<AddressTableEntry> entries, string processName)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"// CE AI Suite - Auto Assembler Script");
        sb.AppendLine(CultureInfo.InvariantCulture, $"// Target: {processName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"// Generated: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();
        sb.AppendLine("[ENABLE]");
        sb.AppendLine();

        foreach (var entry in entries.Where(e => e.IsLocked && e.LockedValue is not null))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"// {entry.Label}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"alloc(newmem_{entry.Id},2048)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"label(returnhere_{entry.Id})");
            sb.AppendLine(CultureInfo.InvariantCulture, $"label(originalcode_{entry.Id})");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"newmem_{entry.Id}:");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  // overwrite with {entry.LockedValue} ({entry.DataType})");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  mov [{entry.Address}],{FormatAAValue(entry.DataType, entry.LockedValue!)}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  jmp returnhere_{entry.Id}");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"originalcode_{entry.Id}:");
            sb.AppendLine($"  // original bytes go here");
            sb.AppendLine();
        }

        sb.AppendLine("[DISABLE]");
        sb.AppendLine();
        foreach (var entry in entries.Where(e => e.IsLocked && e.LockedValue is not null))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"dealloc(newmem_{entry.Id})");
        }

        return sb.ToString();
    }

    public static string GenerateLuaScript(IReadOnlyList<AddressTableEntry> entries, string processName)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"-- CE AI Suite - Lua Script");
        sb.AppendLine(CultureInfo.InvariantCulture, $"-- Target: {processName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"-- Generated: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"local processName = \"{processName.Replace(".exe", "")}\"");
        sb.AppendLine();
        sb.AppendLine("-- Open the process");
        sb.AppendLine("local pid = getProcessIDFromProcessName(processName .. \".exe\")");
        sb.AppendLine("if pid == nil then");
        sb.AppendLine("  print(\"Process not found: \" .. processName)");
        sb.AppendLine("  return");
        sb.AppendLine("end");
        sb.AppendLine("openProcess(pid)");
        sb.AppendLine("print(\"Attached to \" .. processName .. \" (PID: \" .. pid .. \")\")");
        sb.AppendLine();

        foreach (var entry in entries.Where(e => e.IsLocked && e.LockedValue is not null))
        {
            var writeFunc = entry.DataType switch
            {
                MemoryDataType.Int32 => "writeInteger",
                MemoryDataType.Int64 => "writeQword",
                MemoryDataType.Float => "writeFloat",
                MemoryDataType.Double => "writeDouble",
                _ => "writeInteger"
            };
            sb.AppendLine(CultureInfo.InvariantCulture, $"-- {entry.Label}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{writeFunc}(\"{entry.Address}\", {entry.LockedValue})");
        }

        sb.AppendLine();
        sb.AppendLine("print(\"Trainer applied.\")");

        return sb.ToString();
    }

    public static string SummarizeInvestigation(
        string processName,
        int processId,
        IReadOnlyList<AddressTableEntry> addressEntries,
        IReadOnlyList<ScanResultOverview>? scanResults,
        DisassemblyOverview? disassembly)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Investigation Summary");
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Target:** {processName} (PID {processId})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Date:** {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();

        sb.AppendLine("## Address Table");
        if (addressEntries.Count == 0)
        {
            sb.AppendLine("No entries recorded.");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{addressEntries.Count} entries tracked:");
            foreach (var e in addressEntries)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- **{e.Label}** @ `{e.Address}` ({e.DataType}) = {e.CurrentValue}{(e.IsLocked ? " [LOCKED]" : "")}");
            }
        }
        sb.AppendLine();

        sb.AppendLine("## Scan Results");
        if (scanResults is null || scanResults.Count == 0)
        {
            sb.AppendLine("No scan results in current session.");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{scanResults.Count} results from last scan.");
        }
        sb.AppendLine();

        if (disassembly is not null)
        {
            sb.AppendLine("## Disassembly");
            sb.AppendLine(CultureInfo.InvariantCulture, $"From `{disassembly.StartAddress}` — {disassembly.Lines.Count} instructions:");
            foreach (var line in disassembly.Lines.Take(10))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  `{line.Address}` {line.Mnemonic} {line.Operands}");
            }
        }

        return sb.ToString();
    }

    private static string FormatAAValue(MemoryDataType dt, string value) =>
        dt switch
        {
            MemoryDataType.Float => $"(float){value}",
            MemoryDataType.Double => $"(double){value}",
            _ => value
        };

    private static string GetValueBytes(string dataType, string value)
    {
        try
        {
            byte[] bytes = dataType switch
            {
                "Int32" => BitConverter.GetBytes(int.Parse(value, CultureInfo.InvariantCulture)),
                "Int64" => BitConverter.GetBytes(long.Parse(value, CultureInfo.InvariantCulture)),
                "Float" => BitConverter.GetBytes(float.Parse(value, CultureInfo.InvariantCulture)),
                "Double" => BitConverter.GetBytes(double.Parse(value, CultureInfo.InvariantCulture)),
                _ => BitConverter.GetBytes(int.Parse(value, CultureInfo.InvariantCulture))
            };
            return string.Join(", ", bytes.Select(b => $"0x{b:X2}"));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"[ArtifactServices] Value-to-bytes conversion failed: {ex.Message}");
            return "0x00";
        }
    }
}

public sealed class AddressTableExportService
{
    private static readonly JsonSerializerOptions s_indentedJsonOptions = new() { WriteIndented = true };
    public static string ExportToJson(IReadOnlyList<AddressTableEntry> entries)
    {
        var exportEntries = entries.Select(e => new
        {
            e.Label,
            e.Address,
            DataType = e.DataType.ToString(),
            e.CurrentValue,
            e.Notes,
            e.IsLocked,
            e.LockedValue
        });

        return JsonSerializer.Serialize(exportEntries, s_indentedJsonOptions);
    }

    public static IReadOnlyList<AddressTableEntry> ImportFromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var entries = new List<AddressTableEntry>();

        foreach (var element in doc.RootElement.EnumerateArray())
        {
            entries.Add(new AddressTableEntry(
                $"addr-{Guid.NewGuid().ToString("N")[..8]}",
                element.GetProperty("Label").GetString() ?? "imported",
                element.GetProperty("Address").GetString() ?? "0x0",
                Enum.Parse<Engine.Abstractions.MemoryDataType>(element.GetProperty("DataType").GetString() ?? "Int32"),
                element.GetProperty("CurrentValue").GetString() ?? "0",
                null,
                element.TryGetProperty("Notes", out var notes) ? notes.GetString() : null,
                element.TryGetProperty("IsLocked", out var locked) && locked.GetBoolean(),
                element.TryGetProperty("LockedValue", out var lv) ? lv.GetString() : null));
        }

        return entries;
    }

    // ── Recovery (crash auto-save / restore) ─────────────────────────────

    private static readonly JsonSerializerOptions s_recoveryOptions = new()
    {
        WriteIndented = false, // compact for speed
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Serialize the full address-table tree to a lightweight JSON file for crash recovery.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static")]
    public async Task ExportRecoveryAsync(IReadOnlyList<AddressTableNode> roots, string filePath)
    {
        var dtos = roots.Select(RecoveryNodeDto.FromNode).ToList();
        var dir = Path.GetDirectoryName(filePath);
        if (dir is not null) Directory.CreateDirectory(dir);
        await using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await JsonSerializer.SerializeAsync(fs, dtos, s_recoveryOptions).ConfigureAwait(false);
    }

    /// <summary>
    /// Deserialize a recovery JSON file back into address-table nodes.
    /// Returns null if the file does not exist or is corrupt.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static")]
    public async Task<IReadOnlyList<AddressTableNode>?> ImportRecoveryAsync(string filePath)
    {
        if (!File.Exists(filePath)) return null;
        try
        {
            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            var dtos = await JsonSerializer.DeserializeAsync<List<RecoveryNodeDto>>(fs, s_recoveryOptions).ConfigureAwait(false);
            if (dtos is null) return null;
            return dtos.Select(d => d.ToNode()).ToList();
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    /// <summary>DTO for JSON round-tripping AddressTableNode trees during crash recovery.</summary>
    internal sealed class RecoveryNodeDto
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public bool IsGroup { get; set; }
        public string Address { get; set; } = "";
        public MemoryDataType DataType { get; set; }
        public string CurrentValue { get; set; } = "";
        public string? AssemblerScript { get; set; }
        public bool ShowAsHex { get; set; }
        public bool ShowAsSigned { get; set; }
        public bool IsLocked { get; set; }
        public string? LockedValue { get; set; }
        public string? UserColor { get; set; }
        public bool IsPointer { get; set; }
        public List<long>? PointerOffsets { get; set; }
        public bool IsOffset { get; set; }
        public string? Notes { get; set; }
        public List<RecoveryNodeDto>? Children { get; set; }

        public static RecoveryNodeDto FromNode(AddressTableNode n) => new()
        {
            Id = n.Id,
            Label = n.Label,
            IsGroup = n.IsGroup,
            Address = n.Address,
            DataType = n.DataType,
            CurrentValue = n.CurrentValue,
            AssemblerScript = n.AssemblerScript,
            ShowAsHex = n.ShowAsHex,
            ShowAsSigned = n.ShowAsSigned,
            IsLocked = n.IsLocked,
            LockedValue = n.LockedValue,
            UserColor = n.UserColor,
            IsPointer = n.IsPointer,
            PointerOffsets = n.PointerOffsets.Count > 0 ? n.PointerOffsets : null,
            IsOffset = n.IsOffset,
            Notes = n.Notes,
            Children = n.Children.Count > 0
                ? n.Children.Select(FromNode).ToList()
                : null
        };

        public AddressTableNode ToNode()
        {
            var node = new AddressTableNode(Id, Label, IsGroup)
            {
                Address = Address,
                DataType = DataType,
                CurrentValue = CurrentValue,
                AssemblerScript = AssemblerScript,
                ShowAsHex = ShowAsHex,
                ShowAsSigned = ShowAsSigned,
                IsLocked = IsLocked,
                LockedValue = LockedValue,
                UserColor = UserColor,
                IsPointer = IsPointer,
                IsOffset = IsOffset,
                Notes = Notes
            };
            if (PointerOffsets is not null)
                node.PointerOffsets = PointerOffsets;
            if (Children is not null)
            {
                foreach (var child in Children)
                {
                    var childNode = child.ToNode();
                    childNode.Parent = node;
                    node.Children.Add(childNode);
                }
            }
            return node;
        }
    }
}
