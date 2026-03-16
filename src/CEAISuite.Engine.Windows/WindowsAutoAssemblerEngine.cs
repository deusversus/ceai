using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Engine.Windows;

/// <summary>
/// Auto Assembler engine for parsing and executing Cheat Engine AA scripts.
/// Uses Keystone assembler for x86/x64 instruction encoding and Windows API for process memory manipulation.
/// </summary>
public sealed partial class WindowsAutoAssemblerEngine : IAutoAssemblerEngine
{
    private const uint ProcessAllAccess = 0x001FFFFF;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageExecuteReadWrite = 0x40;

    static WindowsAutoAssemblerEngine()
    {
        // Keystone NuGet package puts native DLLs in x64/x86 subdirs.
        // Register a resolver so DllImport("keystone") finds the right one.
        NativeLibrary.SetDllImportResolver(typeof(Keystone.Engine).Assembly, (name, assembly, path) =>
        {
            if (!name.Equals("keystone", StringComparison.OrdinalIgnoreCase))
                return IntPtr.Zero;

            var arch = RuntimeInformation.ProcessArchitecture == Architecture.X86 ? "x86" : "x64";
            var baseDir = AppContext.BaseDirectory;
            var candidate = Path.Combine(baseDir, arch, "keystone.dll");
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                return handle;

            // Fallback: try root directory
            candidate = Path.Combine(baseDir, "keystone.dll");
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out handle))
                return handle;

            return IntPtr.Zero;
        });
    }

    public ScriptParseResult Parse(string script)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(script))
        {
            errors.Add("Script is empty.");
            return new ScriptParseResult(false, errors, warnings, null, null);
        }

        var (enableSection, disableSection) = SplitSections(script);

        if (enableSection is null && disableSection is null)
            errors.Add("Script must contain at least an [ENABLE] or [DISABLE] section.");

        if (enableSection is not null)
            ValidateSection(enableSection, "[ENABLE]", errors, warnings);

        if (disableSection is not null)
            ValidateSection(disableSection, "[DISABLE]", errors, warnings);

        return new ScriptParseResult(errors.Count == 0, errors, warnings, enableSection, disableSection);
    }

    public Task<ScriptExecutionResult> EnableAsync(int processId, string script, CancellationToken ct = default) =>
        Task.Run(() => ExecuteSection(processId, script, enable: true, ct), ct);

    public Task<ScriptExecutionResult> DisableAsync(int processId, string script, CancellationToken ct = default) =>
        Task.Run(() => ExecuteSection(processId, script, enable: false, ct), ct);

    private ScriptExecutionResult ExecuteSection(int processId, string script, bool enable, CancellationToken ct)
    {
        var (enableSection, disableSection) = SplitSections(script);
        var section = enable ? enableSection : disableSection;

        if (section is null)
        {
            return new ScriptExecutionResult(
                false,
                $"Script has no [{(enable ? "ENABLE" : "DISABLE")}] section.",
                Array.Empty<ScriptAllocation>(),
                Array.Empty<ScriptPatch>());
        }

        var handle = OpenProcess(ProcessAllAccess, false, processId);
        if (handle == IntPtr.Zero)
        {
            return new ScriptExecutionResult(
                false,
                $"Unable to open process {processId}. Error: {Marshal.GetLastWin32Error()}",
                Array.Empty<ScriptAllocation>(),
                Array.Empty<ScriptPatch>());
        }

        try
        {
            var is64Bit = DetectIs64Bit(handle);
            var modules = GetProcessModules(processId);
            var context = new ExecutionContext(handle, processId, is64Bit, modules);

            return ExecuteSectionCore(context, section, ct);
        }
        catch (Exception ex)
        {
            return new ScriptExecutionResult(
                false,
                ex.Message,
                Array.Empty<ScriptAllocation>(),
                Array.Empty<ScriptPatch>());
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static ScriptExecutionResult ExecuteSectionCore(ExecutionContext ctx, string section, CancellationToken ct)
    {
        var lines = ParseLines(section);
        var allocations = new List<ScriptAllocation>();
        var patches = new List<ScriptPatch>();

        // Collect directives and code blocks in a single pass
        var defines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allocDirectives = new List<(string Name, string SizeExpr, string? NearExpr)>();
        var deallocDirectives = new List<string>();
        var assertDirectives = new List<(string AddrExpr, string BytePattern)>();
        var codeBlocks = new List<CodeBlock>();

        CodeBlock? currentBlock = null;

        foreach (var line in lines)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line))
                continue;

            var trimmed = line.Trim();

            var defineMatch = DefineRegex().Match(trimmed);
            if (defineMatch.Success)
            {
                defines[defineMatch.Groups[1].Value] = defineMatch.Groups[2].Value;
                continue;
            }

            var allocMatch = AllocRegex().Match(trimmed);
            if (allocMatch.Success)
            {
                allocDirectives.Add((
                    allocMatch.Groups[1].Value,
                    allocMatch.Groups[2].Value,
                    allocMatch.Groups[3].Success && allocMatch.Groups[3].Value.Length > 0
                        ? allocMatch.Groups[3].Value
                        : null));
                continue;
            }

            var deallocMatch = DeallocRegex().Match(trimmed);
            if (deallocMatch.Success)
            {
                deallocDirectives.Add(deallocMatch.Groups[1].Value);
                continue;
            }

            var labelMatch = LabelDeclRegex().Match(trimmed);
            if (labelMatch.Success)
            {
                labels.Add(labelMatch.Groups[1].Value);
                continue;
            }

            var assertMatch = AssertRegex().Match(trimmed);
            if (assertMatch.Success)
            {
                assertDirectives.Add((assertMatch.Groups[1].Value, assertMatch.Groups[2].Value));
                continue;
            }

            if (RegisterSymbolRegex().IsMatch(trimmed) || UnregisterSymbolRegex().IsMatch(trimmed))
                continue;

            if (trimmed.StartsWith("LuaCall(", StringComparison.OrdinalIgnoreCase))
                continue;

            // Label definition: "name:"
            var labelDefMatch = LabelDefRegex().Match(trimmed);
            if (labelDefMatch.Success)
            {
                var labelName = labelDefMatch.Groups[1].Value;
                currentBlock = new CodeBlock(labelName);
                codeBlocks.Add(currentBlock);
                continue;
            }

            // db, nop, or assembly instruction — attach to current block
            if (currentBlock is not null)
            {
                var dbMatch = DbRegex().Match(trimmed);
                if (dbMatch.Success)
                {
                    currentBlock.DbDirectives.Add(dbMatch.Groups[1].Value.Trim());
                    continue;
                }

                var nopMatch = NopRegex().Match(trimmed);
                if (nopMatch.Success)
                {
                    currentBlock.NopCount += int.Parse(nopMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                    continue;
                }

                currentBlock.Instructions.Add(trimmed);
            }
        }

        // Phase 1: Resolve define chains (a define may reference another define)
        ResolveDefines(defines);

        // Phase 2: Resolve module+offset addresses in define values
        foreach (var key in defines.Keys.ToArray())
        {
            var resolved = ResolveAddress(defines[key], defines, ctx);
            if (resolved is not null)
                defines[key] = $"0x{resolved.Value:X}";
        }

        // Phase 3: Execute alloc directives — allocate executable memory in the target
        foreach (var (name, sizeExpr, nearExpr) in allocDirectives)
        {
            ct.ThrowIfCancellationRequested();

            var size = ResolveIntValue(ExpandSymbols(sizeExpr, defines));

            nuint nearAddress = 0;
            if (nearExpr is not null)
            {
                var resolvedNear = ResolveAddress(ExpandSymbols(nearExpr, defines), defines, ctx);
                nearAddress = resolvedNear ?? 0;
            }

            var allocated = AllocateMemory(ctx.ProcessHandle, size, nearAddress);
            if (allocated == nuint.Zero)
            {
                return new ScriptExecutionResult(
                    false,
                    $"Failed to allocate {size} bytes for '{name}'. Error: {Marshal.GetLastWin32Error()}",
                    allocations, patches);
            }

            allocations.Add(new ScriptAllocation(name, allocated, size));
            defines[name] = $"0x{allocated:X}";
        }

        // Phase 4: Verify assert directives — check original bytes match expectations
        foreach (var (addrExpr, bytePattern) in assertDirectives)
        {
            ct.ThrowIfCancellationRequested();

            var addr = ResolveAddress(ExpandSymbols(addrExpr, defines), defines, ctx);
            if (addr is null)
            {
                return new ScriptExecutionResult(
                    false, $"Assert failed: unable to resolve address '{addrExpr}'.", allocations, patches);
            }

            var expectedBytes = ParseByteString(bytePattern);
            var actualBytes = new byte[expectedBytes.Length];

            if (!ReadProcessMemory(ctx.ProcessHandle, (IntPtr)addr.Value, actualBytes, actualBytes.Length, out var bytesRead)
                || bytesRead != actualBytes.Length)
            {
                return new ScriptExecutionResult(
                    false,
                    $"Assert failed: unable to read {expectedBytes.Length} bytes at 0x{addr.Value:X}.",
                    allocations, patches);
            }

            if (!actualBytes.AsSpan().SequenceEqual(expectedBytes))
            {
                return new ScriptExecutionResult(
                    false,
                    $"Assert failed at 0x{addr.Value:X}: expected [{Convert.ToHexString(expectedBytes)}], " +
                    $"got [{Convert.ToHexString(actualBytes)}].",
                    allocations, patches);
            }
        }

        // Phase 5: Resolve label addresses from defines (labels that reference known code locations)
        var labelAddresses = new Dictionary<string, nuint>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in codeBlocks)
        {
            if (defines.TryGetValue(block.Label, out var defVal))
            {
                var resolved = ResolveAddress(defVal, defines, ctx);
                if (resolved is not null)
                    labelAddresses[block.Label] = resolved.Value;
            }
        }

        // Also resolve forward-declared labels that didn't get a code block but are used in jumps.
        // For labels declared with label() that correspond to alloc'd regions, they are already in defines.
        foreach (var lbl in labels)
        {
            if (!labelAddresses.ContainsKey(lbl) && defines.TryGetValue(lbl, out var defVal))
            {
                var resolved = ResolveAddress(defVal, defines, ctx);
                if (resolved is not null)
                    labelAddresses[lbl] = resolved.Value;
            }
        }

        // Phase 6: Two-pass assembly — first pass to determine sizes for code-cave blocks,
        // then assign addresses to labels defined inside alloc'd regions, then reassemble.
        AssignAllocBlockAddresses(codeBlocks, labelAddresses, defines, ctx);

        // Phase 7: Assemble and write each code block
        foreach (var block in codeBlocks)
        {
            ct.ThrowIfCancellationRequested();

            if (!labelAddresses.TryGetValue(block.Label, out var blockAddress))
                continue;

            // db directives: write raw bytes and continue
            if (block.DbDirectives.Count > 0)
            {
                var offset = blockAddress;
                foreach (var bytePattern in block.DbDirectives)
                {
                    var bytes = ParseByteString(bytePattern);

                    var origBytes = new byte[bytes.Length];
                    ReadProcessMemory(ctx.ProcessHandle, (IntPtr)offset, origBytes, origBytes.Length, out _);

                    if (!WriteProcessMemory(ctx.ProcessHandle, (IntPtr)offset, bytes, bytes.Length, out var written)
                        || written != bytes.Length)
                    {
                        return new ScriptExecutionResult(
                            false, $"Failed to write {bytes.Length} db bytes at 0x{offset:X}.", allocations, patches);
                    }

                    patches.Add(new ScriptPatch(offset, origBytes, bytes));
                    offset = (nuint)((ulong)offset + (ulong)bytes.Length);
                }

                // If a block has ONLY db directives (no instructions/nops), skip assembly
                if (block.Instructions.Count == 0 && block.NopCount == 0)
                    continue;
            }

            // Assemble instructions
            byte[]? machineCode = null;
            if (block.Instructions.Count > 0)
            {
                var assemblyText = BuildAssemblyText(block.Instructions, defines, labelAddresses);
                machineCode = Assemble(assemblyText, blockAddress, ctx.Is64Bit);

                if (machineCode is null)
                {
                    return new ScriptExecutionResult(
                        false, $"Failed to assemble code block '{block.Label}': {assemblyText}", allocations, patches);
                }
            }

            // Combine machine code + NOPs
            var codeLen = (machineCode?.Length ?? 0) + block.NopCount;
            if (codeLen == 0)
                continue;

            var finalCode = new byte[codeLen];
            machineCode?.CopyTo(finalCode, 0);
            if (block.NopCount > 0)
                Array.Fill(finalCode, (byte)0x90, machineCode?.Length ?? 0, block.NopCount);

            var writeAddr = blockAddress;
            if (block.DbDirectives.Count > 0)
            {
                // Advance past db bytes already written
                var dbTotal = block.DbDirectives.Sum(p => ParseByteString(p).Length);
                writeAddr = (nuint)((ulong)blockAddress + (ulong)dbTotal);
            }

            var origCodeBytes = new byte[finalCode.Length];
            ReadProcessMemory(ctx.ProcessHandle, (IntPtr)writeAddr, origCodeBytes, origCodeBytes.Length, out _);

            if (!WriteProcessMemory(ctx.ProcessHandle, (IntPtr)writeAddr, finalCode, finalCode.Length, out var codeWritten)
                || codeWritten != finalCode.Length)
            {
                return new ScriptExecutionResult(
                    false, $"Failed to write {finalCode.Length} bytes at 0x{writeAddr:X}.", allocations, patches);
            }

            patches.Add(new ScriptPatch(writeAddr, origCodeBytes, finalCode));
        }

        // Phase 8: Execute dealloc directives
        foreach (var name in deallocDirectives)
        {
            ct.ThrowIfCancellationRequested();

            if (!defines.TryGetValue(name, out var addrStr))
                continue;

            var addr = ResolveAddress(addrStr, defines, ctx);
            if (addr is not null)
                VirtualFreeEx(ctx.ProcessHandle, (IntPtr)addr.Value, 0, MemRelease);
        }

        return new ScriptExecutionResult(true, null, allocations, patches);
    }

    /// <summary>
    /// For code blocks that live inside alloc'd regions (code caves), assign sequential
    /// addresses based on estimated instruction sizes so labels within the cave resolve correctly.
    /// </summary>
    private static void AssignAllocBlockAddresses(
        List<CodeBlock> codeBlocks,
        Dictionary<string, nuint> labelAddresses,
        Dictionary<string, string> defines,
        ExecutionContext ctx)
    {
        // Find contiguous sequences of blocks that live inside an alloc'd region
        // (i.e., the first block's label matches an alloc, subsequent blocks without
        // their own address are placed sequentially after it).
        nuint currentOffset = 0;
        var insideAlloc = false;

        foreach (var block in codeBlocks)
        {
            if (labelAddresses.ContainsKey(block.Label))
            {
                // This block already has an assigned address
                insideAlloc = IsAllocAddress(labelAddresses[block.Label], defines);
                if (insideAlloc)
                    currentOffset = labelAddresses[block.Label];

                continue;
            }

            if (insideAlloc && currentOffset != 0)
            {
                // Estimate size of the previous block to advance the offset
                var prevIdx = codeBlocks.IndexOf(block) - 1;
                if (prevIdx >= 0)
                {
                    var prev = codeBlocks[prevIdx];
                    var estimatedSize = EstimateBlockSize(prev, defines, labelAddresses, ctx);
                    currentOffset = (nuint)((ulong)currentOffset + (ulong)estimatedSize);
                }

                labelAddresses[block.Label] = currentOffset;
                defines[block.Label] = $"0x{currentOffset:X}";
            }
        }
    }

    private static bool IsAllocAddress(nuint address, Dictionary<string, string> defines)
    {
        // Check if this address was created by an alloc directive
        foreach (var val in defines.Values)
        {
            if (TryParseHexOrDec(val, out var defAddr) && (nuint)defAddr == address)
                return true;
        }

        return false;
    }

    private static int EstimateBlockSize(
        CodeBlock block,
        Dictionary<string, string> defines,
        Dictionary<string, nuint> labelAddresses,
        ExecutionContext ctx)
    {
        var dbSize = block.DbDirectives.Sum(p => ParseByteString(p).Length);
        var nopSize = block.NopCount;

        if (block.Instructions.Count == 0)
            return dbSize + nopSize;

        // Try to assemble to get exact size; use a temporary base address
        var baseAddr = labelAddresses.TryGetValue(block.Label, out var addr) ? addr : (nuint)0x10000;
        var assemblyText = BuildAssemblyText(block.Instructions, defines, labelAddresses);
        var code = Assemble(assemblyText, baseAddr, ctx.Is64Bit);

        return (code?.Length ?? (block.Instructions.Count * 8)) + dbSize + nopSize;
    }

    #region Parsing Helpers

    private static (string? EnableSection, string? DisableSection) SplitSections(string script)
    {
        string? enableSection = null;
        string? disableSection = null;

        var enableIdx = script.IndexOf("[ENABLE]", StringComparison.OrdinalIgnoreCase);
        var disableIdx = script.IndexOf("[DISABLE]", StringComparison.OrdinalIgnoreCase);

        if (enableIdx >= 0 && disableIdx >= 0)
        {
            if (enableIdx < disableIdx)
            {
                enableSection = script[(enableIdx + "[ENABLE]".Length)..disableIdx].Trim();
                disableSection = script[(disableIdx + "[DISABLE]".Length)..].Trim();
            }
            else
            {
                disableSection = script[(disableIdx + "[DISABLE]".Length)..enableIdx].Trim();
                enableSection = script[(enableIdx + "[ENABLE]".Length)..].Trim();
            }
        }
        else if (enableIdx >= 0)
        {
            enableSection = script[(enableIdx + "[ENABLE]".Length)..].Trim();
        }
        else if (disableIdx >= 0)
        {
            disableSection = script[(disableIdx + "[DISABLE]".Length)..].Trim();
        }

        return (enableSection, disableSection);
    }

    private static List<string> ParseLines(string section)
    {
        var result = new List<string>();
        var inLuaCall = false;
        var luaDepth = 0;

        foreach (var rawLine in section.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            // Handle multiline LuaCall blocks
            if (inLuaCall)
            {
                luaDepth += CountChar(line, '(') - CountChar(line, ')');
                if (luaDepth <= 0)
                    inLuaCall = false;
                continue;
            }

            var trimmed = line.Trim();
            if (trimmed.StartsWith("LuaCall(", StringComparison.OrdinalIgnoreCase))
            {
                luaDepth = CountChar(trimmed, '(') - CountChar(trimmed, ')');
                if (luaDepth > 0)
                    inLuaCall = true;
                result.Add(trimmed);
                continue;
            }

            // Strip // comments
            var commentIdx = trimmed.IndexOf("//", StringComparison.Ordinal);
            if (commentIdx >= 0)
                trimmed = trimmed[..commentIdx].TrimEnd();

            if (!string.IsNullOrWhiteSpace(trimmed))
                result.Add(trimmed);
        }

        return result;
    }

    private static int CountChar(string s, char c)
    {
        var count = 0;
        foreach (var ch in s)
            if (ch == c) count++;
        return count;
    }

    private static void ValidateSection(string section, string sectionName, List<string> errors, List<string> warnings)
    {
        var lines = ParseLines(section);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            if (trimmed.StartsWith("LuaCall(", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"{sectionName}: LuaCall directive will be skipped (CE Lua engine not available).");
                continue;
            }

            // Known directive patterns — no error needed
            if (DefineRegex().IsMatch(trimmed) ||
                AllocRegex().IsMatch(trimmed) ||
                DeallocRegex().IsMatch(trimmed) ||
                LabelDeclRegex().IsMatch(trimmed) ||
                AssertRegex().IsMatch(trimmed) ||
                RegisterSymbolRegex().IsMatch(trimmed) ||
                UnregisterSymbolRegex().IsMatch(trimmed) ||
                LabelDefRegex().IsMatch(trimmed) ||
                DbRegex().IsMatch(trimmed) ||
                NopRegex().IsMatch(trimmed))
            {
                continue;
            }

            // Anything else is assumed to be an assembly instruction (validated at assemble time)
        }
    }

    #endregion

    #region Symbol Resolution

    private static void ResolveDefines(Dictionary<string, string> defines)
    {
        const int maxIterations = 20;
        for (var i = 0; i < maxIterations; i++)
        {
            var changed = false;
            foreach (var key in defines.Keys.ToArray())
            {
                var expanded = ExpandSymbols(defines[key], defines, key);
                if (!string.Equals(expanded, defines[key], StringComparison.Ordinal))
                {
                    defines[key] = expanded;
                    changed = true;
                }
            }

            if (!changed) break;
        }
    }

    private static string ExpandSymbols(string text, Dictionary<string, string> defines, string? excludeKey = null)
    {
        foreach (var (name, value) in defines)
        {
            if (excludeKey is not null && name.Equals(excludeKey, StringComparison.OrdinalIgnoreCase))
                continue;

            text = Regex.Replace(text, $@"\b{Regex.Escape(name)}\b", value, RegexOptions.IgnoreCase);
        }

        return text;
    }

    private static nuint? ResolveAddress(string expression, Dictionary<string, string> defines, ExecutionContext ctx)
    {
        var expanded = ExpandSymbols(expression, defines);

        // Hex or decimal literal
        if (TryParseHexOrDec(expanded, out var directValue))
            return (nuint)directValue;

        // module+offset pattern: "GameAssembly.dll+9A18E8"
        var plusIdx = expanded.IndexOf('+');
        if (plusIdx > 0)
        {
            var modulePart = expanded[..plusIdx].Trim();
            var offsetPart = expanded[(plusIdx + 1)..].Trim();

            if (TryParseHexOrDec(offsetPart, out var offset) && ctx.Modules.TryGetValue(modulePart, out var moduleBase))
                return (nuint)((ulong)moduleBase + offset);
        }

        // Forward to a define that hasn't been expanded yet
        if (defines.TryGetValue(expanded, out var defVal) &&
            !string.Equals(defVal, expanded, StringComparison.OrdinalIgnoreCase))
        {
            return ResolveAddress(defVal, defines, ctx);
        }

        return null;
    }

    private static bool TryParseHexOrDec(string value, out ulong result)
    {
        value = value.Trim();

        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result);

        if (value.StartsWith("$", StringComparison.Ordinal))
            return ulong.TryParse(value[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result);

        // Contains hex chars a-f → parse as hex
        if (value.Any(c => c is (>= 'a' and <= 'f') or (>= 'A' and <= 'F')))
            return ulong.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result);

        return ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    private static int ResolveIntValue(string expression)
    {
        if (expression.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.Parse(expression[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        return int.Parse(expression, CultureInfo.InvariantCulture);
    }

    private static byte[] ParseByteString(string bytePattern)
    {
        var parts = bytePattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = new byte[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            result[i] = byte.Parse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return result;
    }

    #endregion

    #region Assembly

    private static string BuildAssemblyText(
        List<string> instructions,
        Dictionary<string, string> defines,
        Dictionary<string, nuint> labelAddresses)
    {
        var sb = new StringBuilder();

        foreach (var instruction in instructions)
        {
            var expanded = ExpandSymbols(instruction, defines);

            // Replace label references with absolute addresses for Keystone
            foreach (var (label, addr) in labelAddresses)
            {
                expanded = Regex.Replace(
                    expanded,
                    $@"\b{Regex.Escape(label)}\b",
                    $"0x{addr:X}",
                    RegexOptions.IgnoreCase);
            }

            sb.Append(expanded);
            sb.Append("; ");
        }

        return sb.ToString();
    }

    private static byte[]? Assemble(string assemblyText, nuint baseAddress, bool is64Bit)
    {
        try
        {
            using var ks = new Keystone.Engine(Keystone.Architecture.X86, is64Bit ? Keystone.Mode.X64 : Keystone.Mode.X32)
            {
                ThrowOnError = true
            };

            var encoded = ks.Assemble(assemblyText, (ulong)baseAddress);
            return encoded.Buffer;
        }
        catch (Keystone.KeystoneException)
        {
            return null;
        }
    }

    #endregion

    #region Memory Operations

    private static nuint AllocateMemory(IntPtr processHandle, int size, nuint nearAddress)
    {
        if (nearAddress != 0)
        {
            // Allocate within ±2 GB of nearAddress for rel32 jumps
            var searchBase = nearAddress > 0x7FFF0000 ? nearAddress - 0x7FFF0000 : (nuint)0x10000;
            var searchEnd = (nuint)((ulong)nearAddress + 0x7FFF0000);

            for (nuint offset = 0; offset < 0x7FFF0000; offset += 0x10000)
            {
                var tryAddr = (nuint)((ulong)searchBase + offset);
                if (tryAddr > searchEnd) break;

                var result = VirtualAllocEx(
                    processHandle, (IntPtr)tryAddr, (IntPtr)size,
                    MemCommit | MemReserve, PageExecuteReadWrite);

                if (result != IntPtr.Zero)
                    return (nuint)(ulong)result;
            }
        }

        // Fallback: let the OS choose
        var fallback = VirtualAllocEx(
            processHandle, IntPtr.Zero, (IntPtr)size,
            MemCommit | MemReserve, PageExecuteReadWrite);

        return fallback != IntPtr.Zero ? (nuint)(ulong)fallback : nuint.Zero;
    }

    private static Dictionary<string, nuint> GetProcessModules(int processId)
    {
        var result = new Dictionary<string, nuint>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var process = Process.GetProcessById(processId);
            foreach (ProcessModule module in process.Modules)
            {
                var baseAddr = unchecked((nuint)module.BaseAddress.ToInt64());
                result.TryAdd(module.ModuleName, baseAddr);
            }
        }
        catch
        {
            // Module enumeration may fail for protected processes
        }

        return result;
    }

    private static bool DetectIs64Bit(IntPtr processHandle)
    {
        if (IsWow64Process2(processHandle, out var processMachine, out var nativeMachine))
        {
            // processMachine == 0 means process is NOT running under WoW64 → native arch
            if (processMachine == 0)
                return nativeMachine is 0x8664 or 0xAA64;

            return false;
        }

        return IntPtr.Size == 8;
    }

    #endregion

    #region Generated Regexes

    [GeneratedRegex(@"^define\(\s*(\w+)\s*,\s*(.+?)\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex DefineRegex();

    [GeneratedRegex(@"^alloc\(\s*(\w+)\s*,\s*([^,\)]+)\s*(?:,\s*([^,\)]+))?\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex AllocRegex();

    [GeneratedRegex(@"^dealloc\(\s*(\w+)\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex DeallocRegex();

    [GeneratedRegex(@"^label\(\s*(\w+)\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex LabelDeclRegex();

    [GeneratedRegex(@"^assert\(\s*(.+?)\s*,\s*((?:[0-9A-Fa-f]{2}\s*)+)\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex AssertRegex();

    [GeneratedRegex(@"^registersymbol\(\s*\w+\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex RegisterSymbolRegex();

    [GeneratedRegex(@"^unregistersymbol\(\s*\w+\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex UnregisterSymbolRegex();

    [GeneratedRegex(@"^(\w+)\s*:$")]
    private static partial Regex LabelDefRegex();

    [GeneratedRegex(@"^db\s+((?:[0-9A-Fa-f]{2}\s*)+)$", RegexOptions.IgnoreCase)]
    private static partial Regex DbRegex();

    [GeneratedRegex(@"^nop\s+(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex NopRegex();

    #endregion

    #region P/Invoke

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        IntPtr processHandle, IntPtr baseAddress,
        [Out] byte[] buffer, int size, out int numberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(
        IntPtr processHandle, IntPtr baseAddress,
        byte[] buffer, int size, out int numberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(
        IntPtr processHandle, IntPtr address, IntPtr size,
        uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFreeEx(
        IntPtr processHandle, IntPtr address, int size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(
        IntPtr processHandle, out ushort processMachine, out ushort nativeMachine);

    #endregion

    #region Inner Types

    private sealed class ExecutionContext(
        IntPtr processHandle, int processId, bool is64Bit,
        Dictionary<string, nuint> modules)
    {
        public IntPtr ProcessHandle { get; } = processHandle;
        public int ProcessId { get; } = processId;
        public bool Is64Bit { get; } = is64Bit;
        public Dictionary<string, nuint> Modules { get; } = modules;
    }

    private sealed class CodeBlock(string label)
    {
        public string Label { get; } = label;
        public List<string> Instructions { get; } = [];
        public List<string> DbDirectives { get; } = [];
        public int NopCount { get; set; }
    }

    #endregion
}
