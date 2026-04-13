using CEAISuite.Engine.Abstractions;
using MoonSharp.Interpreter;

namespace CEAISuite.Engine.Lua;

/// <summary>
/// CE-compatible memory scanning Lua functions: AOBScan, AOBScanModule, createMemScan.
/// </summary>
internal static class LuaScanBindings
{
    public static void Register(
        Script script,
        MoonSharpLuaEngine engine,
        IScanEngine scanEngine,
        IEngineFacade engineFacade,
        IAutoAssemblerEngine? autoAssembler)
    {
        // AOBScan(pattern, [protectionFlags]) → table of address strings, or nil
        script.Globals["AOBScan"] = (Func<string, DynValue>)(pattern =>
        {
            var pid = RequireProcess(engine);
            var constraints = new ScanConstraints(MemoryDataType.ByteArray, ScanType.ArrayOfBytes, pattern);
            var options = new ScanOptions(Alignment: 1, WritableOnly: false);
            var results = scanEngine.StartScanAsync(pid, constraints, options).GetAwaiter().GetResult();

            if (results.Results.Count == 0)
                return DynValue.Nil;

            var table = new Table(script);
            for (int i = 0; i < results.Results.Count; i++)
                table[i + 1] = FormatAddress(results.Results[i].Address);
            return DynValue.NewTable(table);
        });

        // AOBScanModule(module, pattern) → table of address strings, or nil
        script.Globals["AOBScanModule"] = (Func<string, string, DynValue>)((moduleName, pattern) =>
        {
            var pid = RequireProcess(engine);

            // Find module base and size
            var moduleBase = LuaAddressResolver.FindModuleBaseAsync(moduleName, pid, engineFacade, CancellationToken.None)
                .GetAwaiter().GetResult();
            if (!moduleBase.HasValue)
                throw new ScriptRuntimeException($"Module '{moduleName}' not found");

            // Scan within module range using AOB scan
            var constraints = new ScanConstraints(MemoryDataType.ByteArray, ScanType.ArrayOfBytes, pattern);
            var options = new ScanOptions(Alignment: 1, WritableOnly: false);
            var results = scanEngine.StartScanAsync(pid, constraints, options).GetAwaiter().GetResult();

            // Filter results to module range — get module size from regions
            var regions = scanEngine.EnumerateRegionsAsync(pid).GetAwaiter().GetResult();
            var moduleRegions = regions.Where(r =>
                (ulong)r.BaseAddress >= (ulong)moduleBase.Value);

            // Find the module's end address by summing contiguous regions from base
            ulong moduleEnd = (ulong)moduleBase.Value;
            foreach (var region in moduleRegions.OrderBy(r => (ulong)r.BaseAddress))
            {
                if ((ulong)region.BaseAddress <= moduleEnd)
                    moduleEnd = (ulong)region.BaseAddress + (ulong)region.RegionSize;
                else
                    break;
            }

            var filtered = results.Results
                .Where(r => (ulong)r.Address >= (ulong)moduleBase.Value && (ulong)r.Address < moduleEnd)
                .ToList();

            if (filtered.Count == 0)
                return DynValue.Nil;

            var table = new Table(script);
            for (int i = 0; i < filtered.Count; i++)
                table[i + 1] = FormatAddress(filtered[i].Address);
            return DynValue.NewTable(table);
        });

        // createMemScan() → MemScan object (table with methods)
        script.Globals["createMemScan"] = (Func<DynValue>)(() =>
        {
            ScanResultSet? currentResults = null;

            var memScan = new Table(script);

            // firstScan(scanType, dataType, value) — initiate a new scan
            memScan["firstScan"] = (Action<DynValue, string, string, DynValue>)((self, scanTypeStr, dataTypeStr, value) =>
            {
                var pid = RequireProcess(engine);
                var scanType = ParseScanType(scanTypeStr);
                var dataType = ParseDataType(dataTypeStr);
                var valueStr = value.IsNil() ? null : value.CastToString();
                var constraints = new ScanConstraints(dataType, scanType, valueStr);
                var options = new ScanOptions(WritableOnly: false);
                currentResults = scanEngine.StartScanAsync(pid, constraints, options).GetAwaiter().GetResult();
            });

            // nextScan(scanType, dataType, value) — refine results
            memScan["nextScan"] = (Action<DynValue, string, string, DynValue>)((self, scanTypeStr, dataTypeStr, value) =>
            {
                if (currentResults is null)
                    throw new ScriptRuntimeException("No active scan. Call firstScan() first.");

                var scanType = ParseScanType(scanTypeStr);
                var dataType = ParseDataType(dataTypeStr);
                var valueStr = value.IsNil() ? null : value.CastToString();
                var refinement = new ScanConstraints(dataType, scanType, valueStr);
                var options = new ScanOptions(WritableOnly: false);
                currentResults = scanEngine.RefineScanAsync(currentResults, refinement, options).GetAwaiter().GetResult();
            });

            // getResultCount() → number
            memScan["getResultCount"] = (Func<DynValue, double>)(self =>
            {
                return currentResults?.Results.Count ?? 0;
            });

            // getResults(maxCount?) → table of {address, value}
            memScan["getResults"] = (Func<DynValue, DynValue, DynValue>)((self, maxCountArg) =>
            {
                if (currentResults is null)
                    return DynValue.Nil;

                var maxCount = maxCountArg.IsNil() ? 100 : (int)maxCountArg.Number;
                var resultTable = new Table(script);
                var count = Math.Min(currentResults.Results.Count, maxCount);

                for (int i = 0; i < count; i++)
                {
                    var entry = new Table(script);
                    entry["address"] = FormatAddress(currentResults.Results[i].Address);
                    entry["value"] = currentResults.Results[i].CurrentValue;
                    resultTable[i + 1] = DynValue.NewTable(entry);
                }

                return DynValue.NewTable(resultTable);
            });

            // destroy() — release scan resources
            memScan["destroy"] = (Action<DynValue>)(self =>
            {
                currentResults = null;
            });

            return DynValue.NewTable(memScan);
        });
    }

    private static ScanType ParseScanType(string scanType) => scanType.ToLowerInvariant() switch
    {
        "exact" or "exactvalue" => ScanType.ExactValue,
        "unknown" or "unknowninitialvalue" => ScanType.UnknownInitialValue,
        "increased" => ScanType.Increased,
        "decreased" => ScanType.Decreased,
        "changed" => ScanType.Changed,
        "unchanged" => ScanType.Unchanged,
        "aob" or "arrayofbytes" => ScanType.ArrayOfBytes,
        "biggerthan" => ScanType.BiggerThan,
        "smallerthan" => ScanType.SmallerThan,
        "between" or "valuebetween" => ScanType.ValueBetween,
        _ => throw new ScriptRuntimeException($"Unknown scan type: '{scanType}'")
    };

    private static MemoryDataType ParseDataType(string dataType) => dataType.ToLowerInvariant() switch
    {
        "byte" => MemoryDataType.Byte,
        "int16" or "2bytes" or "short" => MemoryDataType.Int16,
        "int32" or "4bytes" or "int" or "integer" => MemoryDataType.Int32,
        "int64" or "8bytes" or "long" or "qword" => MemoryDataType.Int64,
        "float" => MemoryDataType.Float,
        "double" => MemoryDataType.Double,
        "string" => MemoryDataType.String,
        "aob" or "arrayofbytes" => MemoryDataType.ByteArray,
        _ => throw new ScriptRuntimeException($"Unknown data type: '{dataType}'")
    };

    private static int RequireProcess(MoonSharpLuaEngine engine)
        => LuaBindingHelpers.RequireProcess(engine);

    private static string FormatAddress(nuint address)
        => LuaBindingHelpers.FormatAddress(address);
}
