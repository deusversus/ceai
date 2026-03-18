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
    /// Returns the field list and the number of integer clusters detected.
    /// </summary>
    public async Task<(IReadOnlyList<StructureField> Fields, int ClustersDetected)> DissectAsync(
        int processId,
        nuint baseAddress,
        int regionSize = 256,
        string typeHint = "auto",
        CancellationToken ct = default)
    {
        var result = await engine.ReadMemoryAsync(processId, baseAddress, regionSize, ct);
        var bytes = result.Bytes.ToArray();
        var fields = new List<StructureField>();

        for (var offset = 0; offset <= bytes.Length - 4; offset += 4)
        {
            var candidates = AnalyzeOffset(bytes, offset, baseAddress, typeHint);
            if (candidates.Count > 0)
            {
                var best = candidates.OrderByDescending(c => c.Confidence).First();
                fields.Add(best);
            }
        }

        int clustersDetected = 0;
        if (typeHint is "auto" or "int32")
        {
            clustersDetected = ApplyIntegerClustering(fields, bytes, baseAddress);
        }

        return (fields, clustersDetected);
    }

    private static List<StructureField> AnalyzeOffset(byte[] bytes, int offset, nuint baseAddr, string typeHint)
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

        // Apply hint-based scoring adjustments
        if (typeHint is not "auto")
            results = ApplyTypeHint(results, typeHint);

        return results;
    }

    private static List<StructureField> ApplyTypeHint(List<StructureField> candidates, string typeHint)
    {
        var adjusted = new List<StructureField>(candidates.Count);
        foreach (var c in candidates)
        {
            var conf = c.Confidence;
            switch (typeHint)
            {
                case "int32":
                    if (c.ProbableType == "Int32")
                        conf = Math.Min(conf * 1.5, 0.95);
                    else if (c.ProbableType == "Float")
                        conf *= 0.5;
                    break;
                case "float":
                    if (c.ProbableType == "Float")
                        conf = Math.Min(conf * 1.3, 0.95);
                    else if (c.ProbableType == "Int32")
                        conf *= 0.6;
                    break;
                case "pointers":
                    if (c.ProbableType == "Pointer")
                        conf = Math.Min(conf * 1.5, 0.95);
                    break;
            }
            adjusted.Add(c with { Confidence = conf });
        }
        return adjusted;
    }

    /// <summary>
    /// Post-processing: detect runs of consecutive Int32 fields with game-stat-like values
    /// and boost their confidence. Also re-evaluate Float fields adjacent to clusters.
    /// Returns the number of clusters detected.
    /// </summary>
    private static int ApplyIntegerClustering(List<StructureField> fields, byte[] bytes, nuint baseAddress)
    {
        if (fields.Count < 3) return 0;

        // Find runs of 3+ consecutive Int32 results (offset stride = 4) in stat range [1, 100_000]
        var clusters = new List<(int startIdx, int endIdx)>();
        int runStart = -1;

        for (int i = 0; i < fields.Count; i++)
        {
            bool isStatInt = fields[i].ProbableType == "Int32"
                && int.TryParse(fields[i].DisplayValue, out var v)
                && v >= 1 && v <= 100_000;

            bool consecutive = i > 0 && fields[i].Offset - fields[i - 1].Offset == 4;

            if (isStatInt && (runStart == -1 || consecutive))
            {
                if (runStart == -1) runStart = i;
            }
            else
            {
                if (runStart != -1 && i - runStart >= 3)
                    clusters.Add((runStart, i - 1));
                runStart = isStatInt ? i : -1;
            }
        }
        // Final run check
        if (runStart != -1 && fields.Count - runStart >= 3)
            clusters.Add((runStart, fields.Count - 1));

        if (clusters.Count == 0) return 0;

        // Build a set of offsets that are within any cluster
        var clusteredOffsets = new HashSet<int>();
        foreach (var (s, e) in clusters)
            for (int i = s; i <= e; i++)
                clusteredOffsets.Add(fields[i].Offset);

        // Boost Int32 fields within clusters
        for (int i = 0; i < fields.Count; i++)
        {
            if (clusteredOffsets.Contains(fields[i].Offset) && fields[i].ProbableType == "Int32")
            {
                fields[i] = fields[i] with { Confidence = Math.Max(fields[i].Confidence, 0.85) };
            }
        }

        // Re-evaluate Float-classified fields within or adjacent to a cluster
        foreach (var (s, e) in clusters)
        {
            int clusterStartOffset = fields[s].Offset;
            int clusterEndOffset = fields[e].Offset;

            for (int i = 0; i < fields.Count; i++)
            {
                if (fields[i].ProbableType != "Float") continue;

                int off = fields[i].Offset;
                bool adjacent = off >= clusterStartOffset - 4 && off <= clusterEndOffset + 4;
                if (!adjacent) continue;

                // Re-check: if the raw int32 value is in game-stat range, replace with Int32
                if (off + 4 <= bytes.Length)
                {
                    int rawInt = BitConverter.ToInt32(bytes, off);
                    if (rawInt >= 1 && rawInt <= 100_000)
                    {
                        fields[i] = new StructureField(off, "Int32", rawInt.ToString(), 0.8);
                    }
                }
            }
        }

        return clusters.Count;
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
