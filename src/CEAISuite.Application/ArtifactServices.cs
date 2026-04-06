using System.Globalization;
using System.Text;
using System.Text.Json;
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
        sb.AppendLine("using System.Diagnostics;");
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
}
