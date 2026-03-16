using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Application;

/// <summary>
/// Identifies probable field types in a memory region around a base address.
/// CE calls this "Structure Dissection" — it reads a block of memory and
/// tries to guess what data types live at each offset.
/// </summary>
public sealed class StructureDissectorService(IEngineFacade engine)
{
    public sealed record StructureField(
        int Offset,
        string ProbableType,
        string DisplayValue,
        double Confidence);

    /// <summary>
    /// Analyze a region of memory starting at baseAddress and identify probable fields.
    /// </summary>
    public async Task<IReadOnlyList<StructureField>> DissectAsync(
        int processId,
        nuint baseAddress,
        int regionSize = 256,
        CancellationToken ct = default)
    {
        var result = await engine.ReadMemoryAsync(processId, baseAddress, regionSize, ct);
        var bytes = result.Bytes.ToArray();
        var fields = new List<StructureField>();

        for (var offset = 0; offset <= bytes.Length - 4; offset += 4)
        {
            var candidates = AnalyzeOffset(bytes, offset, baseAddress);
            if (candidates.Count > 0)
            {
                var best = candidates.OrderByDescending(c => c.Confidence).First();
                fields.Add(best);
            }
        }

        return fields;
    }

    private static List<StructureField> AnalyzeOffset(byte[] bytes, int offset, nuint baseAddr)
    {
        var results = new List<StructureField>();

        // Try Int32
        if (offset + 4 <= bytes.Length)
        {
            var val = BitConverter.ToInt32(bytes, offset);
            var confidence = ClassifyInt32(val);
            if (confidence > 0.1)
                results.Add(new StructureField(offset, "Int32", val.ToString(), confidence));
        }

        // Try Float
        if (offset + 4 <= bytes.Length)
        {
            var val = BitConverter.ToSingle(bytes, offset);
            var confidence = ClassifyFloat(val);
            if (confidence > 0.1)
                results.Add(new StructureField(offset, "Float", $"{val:G6}", confidence));
        }

        // Try pointer (8 bytes)
        if (offset + 8 <= bytes.Length)
        {
            var val = BitConverter.ToUInt64(bytes, offset);
            var confidence = ClassifyPointer(val);
            if (confidence > 0.3)
                results.Add(new StructureField(offset, "Pointer", $"0x{val:X16}", confidence));
        }

        // Try Int64
        if (offset + 8 <= bytes.Length)
        {
            var val = BitConverter.ToInt64(bytes, offset);
            var confidence = ClassifyInt64(val);
            if (confidence > 0.1)
                results.Add(new StructureField(offset, "Int64", val.ToString(), confidence));
        }

        // Try Double
        if (offset + 8 <= bytes.Length)
        {
            var val = BitConverter.ToDouble(bytes, offset);
            var confidence = ClassifyDouble(val);
            if (confidence > 0.1)
                results.Add(new StructureField(offset, "Double", $"{val:G6}", confidence));
        }

        // Zero region
        if (offset + 4 <= bytes.Length)
        {
            var allZero = true;
            for (var i = offset; i < offset + 4; i++)
                if (bytes[i] != 0) { allZero = false; break; }
            if (allZero)
                results.Add(new StructureField(offset, "Padding/Zero", "0", 0.3));
        }

        return results;
    }

    private static double ClassifyInt32(int value)
    {
        if (value == 0) return 0.05;
        // Common game values: HP, mana, level, count, etc.
        if (value is > 0 and <= 10_000) return 0.7;
        if (value is > 10_000 and <= 1_000_000) return 0.5;
        if (value is > 1_000_000 and <= 100_000_000) return 0.4;
        // Negative values less common but possible (signed offsets, delta)
        if (value is < 0 and > -10_000) return 0.4;
        return 0.2;
    }

    private static double ClassifyFloat(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value)) return 0.0;
        if (value == 0f) return 0.05;
        // Common game floats: coordinates, speeds, scales, percentages
        if (value is > -10_000f and < 10_000f && value != (int)value) return 0.8;
        // Exactly integer floats still possible
        if (value is > -10_000f and < 10_000f) return 0.4;
        return 0.15;
    }

    private static double ClassifyPointer(ulong value)
    {
        // User-space virtual address ranges
        // Windows x64: typically 0x00007FF... range for modules, 0x0000... for heap
        if (value is > 0x10000 and < 0x00007FFFFFFFFFFF) return 0.6;
        // 32-bit process in WoW64
        if (value is > 0x10000 and < 0xFFFFFFFF) return 0.5;
        return 0.0;
    }

    private static double ClassifyInt64(long value)
    {
        if (value == 0) return 0.05;
        if (value is > 0 and <= 1_000_000) return 0.5;
        return 0.15;
    }

    private static double ClassifyDouble(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return 0.0;
        if (value == 0d) return 0.05;
        if (value is > -100_000d and < 100_000d && value != (long)value) return 0.7;
        return 0.1;
    }
}
