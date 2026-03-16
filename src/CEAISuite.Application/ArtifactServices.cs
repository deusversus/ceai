using System.Text;
using System.Text.Json;

namespace CEAISuite.Application;

public sealed class ScriptGenerationService
{
    public string GenerateTrainerScript(IReadOnlyList<AddressTableEntry> entries, string processName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// CE AI Suite - Auto-generated Trainer Script");
        sb.AppendLine($"// Target: {processName}");
        sb.AppendLine($"// Generated: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"// Entries: {entries.Count}");
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
        sb.AppendLine($"        var process = Process.GetProcessesByName(\"{processName.Replace(".exe", "")}\").FirstOrDefault();");
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
                sb.AppendLine($"        // {entry.Label}: {entry.DataType} = {entry.LockedValue}");
                sb.AppendLine($"        WriteValue(handle, (IntPtr){entry.Address}, new byte[] {{ {bytes} }});");
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

    private static string GetValueBytes(string dataType, string value)
    {
        try
        {
            byte[] bytes = dataType switch
            {
                "Int32" => BitConverter.GetBytes(int.Parse(value)),
                "Int64" => BitConverter.GetBytes(long.Parse(value)),
                "Float" => BitConverter.GetBytes(float.Parse(value)),
                "Double" => BitConverter.GetBytes(double.Parse(value)),
                _ => BitConverter.GetBytes(int.Parse(value))
            };
            return string.Join(", ", bytes.Select(b => $"0x{b:X2}"));
        }
        catch
        {
            return "0x00";
        }
    }
}

public sealed class AddressTableExportService
{
    public string ExportToJson(IReadOnlyList<AddressTableEntry> entries)
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

        return JsonSerializer.Serialize(exportEntries, new JsonSerializerOptions { WriteIndented = true });
    }

    public IReadOnlyList<AddressTableEntry> ImportFromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var entries = new List<AddressTableEntry>();

        foreach (var element in doc.RootElement.EnumerateArray())
        {
            entries.Add(new AddressTableEntry(
                Guid.NewGuid().ToString("N")[..8],
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
