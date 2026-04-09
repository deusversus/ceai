using CEAISuite.Application.AgentLoop;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CEAISuite.Engine.Abstractions;
using Iced.Intel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CEAISuite.Application;

public sealed partial class AiToolFunctions
{
    // ── Static Analysis Tools ──

    private const int DefaultChunkSize = 0x10000;

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Search a module's code for instructions that write to a specific offset (e.g., [reg+0x38]). Returns all MOV/ADD/SUB/XOR/INC/DEC/IMUL instructions targeting [any_register + offset]. Essential for finding what code writes to a known data field. Set includeReads=true to also find reads and LEA address computations.")]
    public async Task<string> FindWritersToOffset(
        [Description("Process ID")] int processId,
        [Description("Module name (e.g., 'GameAssembly.dll') or 'all' to scan loaded modules")] string moduleName,
        [Description("The displacement/offset to search for (hex), e.g., '0x38' or '38'")] string offset,
        [Description("Max results to return")] int maxResults = 0,
        [Description("Also include instructions that READ from [reg+offset] (useful for tracing data flow)")] bool includeReads = false)
    {
        if (maxResults <= 0) maxResults = _limits.MaxCodeSearchResults;
        var dispValue = (long)(ulong)ParseAddress(offset);
        var attachment = await engineFacade.AttachAsync(processId).ConfigureAwait(false);
        var targetModules = moduleName.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? attachment.Modules.ToList()
            : attachment.Modules.Where(m => m.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase)).ToList();

        if (targetModules.Count == 0) return $"Module '{moduleName}' not found.";

        var results = new List<string>();
        var formatter = new MasmFormatter();
        var output = new StringOutput();

        foreach (var mod in targetModules)
        {
            if (results.Count >= maxResults) break;

            var chunkSize = DefaultChunkSize;
            for (long off = 0; off < mod.SizeBytes && results.Count < maxResults; off += chunkSize)
            {
                var readAddr = (nuint)((ulong)mod.BaseAddress + (ulong)off);
                var readLen = (int)Math.Min(chunkSize, mod.SizeBytes - off);

                MemoryReadResult memResult;
                try { memResult = await engineFacade.ReadMemoryAsync(processId, readAddr, readLen).ConfigureAwait(false); }
                catch (Exception ex) { logger?.LogDebug(ex, "FindWritersToOffset: Memory read failed at chunk offset"); continue; }

                var bytes = memResult.Bytes is byte[] arr ? arr : memResult.Bytes.ToArray();
                if (bytes.Length == 0) continue;

                var reader = new ByteArrayCodeReader(bytes);
                var decoder = Decoder.Create(64, reader, (ulong)readAddr);

                while (decoder.IP < (ulong)readAddr + (ulong)bytes.Length)
                {
                    var instr = decoder.Decode();
                    if (instr.IsInvalid) continue;

                    bool isLea = instr.Mnemonic == Mnemonic.Lea;

                    for (int opIdx = 0; opIdx < instr.OpCount; opIdx++)
                    {
                        if (instr.GetOpKind(opIdx) == OpKind.Memory &&
                            (long)instr.MemoryDisplacement64 == dispValue &&
                            instr.MemoryBase != Register.None)
                        {
                            bool isWrite = opIdx == 0 && IsWriteInstruction(instr);

                            string classification;
                            if (isLea)
                                classification = "LEA (address computation)";
                            else if (isWrite)
                                classification = "WRITE";
                            else
                                classification = "READ";

                            // Default mode: only include writes. With includeReads: include all.
                            if (!isWrite && !isLea && !includeReads) continue;

                            formatter.Format(instr, output);
                            results.Add($"  0x{instr.IP:X} | {output.ToStringAndReset()} | {classification} | base={instr.MemoryBase} | in {mod.Name}");
                            if (results.Count >= maxResults) break;
                        }
                    }
                }
            }
        }

        var label = includeReads ? "Accessors of" : "Writers to";
        return results.Count > 0
            ? $"{label} offset 0x{(ulong)(nuint)ParseAddress(offset):X} ({results.Count} found):\n{string.Join('\n', results)}"
            : $"No instructions {(includeReads ? "accessing" : "writing to")} offset 0x{(ulong)(nuint)ParseAddress(offset):X} found in {moduleName}.";
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Find function boundaries around a given address by scanning for prologue/epilogue patterns (push rbp, sub rsp, ret, int3 padding). Returns the likely function start, end, and size.")]
    public async Task<string> FindFunctionBoundaries(
        [Description("Process ID")] int processId,
        [Description("Address inside the function (hex)")] string address,
        [Description("How far to search backward/forward (bytes)")] int searchRange = 4096)
    {
        var targetAddr = (ulong)ParseAddress(address);
        var startAddr = targetAddr > (ulong)searchRange ? targetAddr - (ulong)searchRange : 0UL;
        var totalLen = (long)Math.Min((ulong)searchRange * 2, 0x100000UL);

        // Read in chunks (engine limits per-call reads)
        const int chunkSize = 0x10000;
        var allBytes = new List<byte>();
        for (long off = 0; off < totalLen; off += chunkSize)
        {
            var readAddr = (nuint)(startAddr + (ulong)off);
            var readLen = (int)Math.Min(chunkSize, totalLen - off);
            try
            {
                var memResult = await engineFacade.ReadMemoryAsync(processId, readAddr, readLen).ConfigureAwait(false);
                var chunk = memResult.Bytes is byte[] arr ? arr : memResult.Bytes.ToArray();
                allBytes.AddRange(chunk);
            }
            catch (Exception ex) { logger?.LogDebug(ex, "FindFunctionBoundaries: Memory read failed"); break; }
        }

        var bytes = allBytes.ToArray();
        if (bytes.Length == 0) return $"Failed to read memory around 0x{targetAddr:X}.";

        var reader = new ByteArrayCodeReader(bytes);
        var decoder = Decoder.Create(64, reader, startAddr);

        var instructions = new List<Iced.Intel.Instruction>();
        while (decoder.IP < startAddr + (ulong)bytes.Length)
        {
            var instr = decoder.Decode();
            if (instr.IsInvalid) { continue; } // skip invalid bytes, don't break
            instructions.Add(instr);
        }

        if (instructions.Count == 0) return $"Could not decode any instructions around 0x{targetAddr:X}.";

        // Scan backward from target for prologue patterns
        ulong funcStart = 0;
        bool foundStart = false;
        for (int i = instructions.Count - 1; i >= 0; i--)
        {
            if (instructions[i].IP > targetAddr) continue;

            var instr = instructions[i];

            // Pattern 1: push rbp (classic frame pointer prologue)
            if (instr.Mnemonic == Mnemonic.Push && instr.Op0Register == Register.RBP)
            {
                funcStart = instr.IP;
                foundStart = true;
                break;
            }

            // Pattern 2: sub rsp, imm (frameless prologue) preceded by a boundary
            if (instr.Mnemonic == Mnemonic.Sub && instr.Op0Register == Register.RSP &&
                (instr.GetOpKind(1) == OpKind.Immediate8 || instr.GetOpKind(1) == OpKind.Immediate32))
            {
                // Accept if preceded by ret, int3, or a push sequence
                if (i > 0)
                {
                    var prev = instructions[i - 1];
                    if (prev.Mnemonic is Mnemonic.Ret or Mnemonic.Int3)
                    {
                        funcStart = instr.IP;
                        foundStart = true;
                        break;
                    }
                    // Accept push rbp/rdi/rsi/rbx before sub rsp (common IL2CPP pattern)
                    if (prev.Mnemonic == Mnemonic.Push)
                    {
                        // Walk back through consecutive push instructions to find the function start
                        int pushStart = i - 1;
                        while (pushStart > 0 && instructions[pushStart - 1].Mnemonic == Mnemonic.Push)
                            pushStart--;
                        // Verify boundary: before the push sequence should be ret/int3/start of buffer
                        if (pushStart == 0 ||
                            instructions[pushStart - 1].Mnemonic is Mnemonic.Ret or Mnemonic.Int3 or Mnemonic.Jmp)
                        {
                            funcStart = instructions[pushStart].IP;
                            foundStart = true;
                            break;
                        }
                    }
                }
            }

            // Pattern 3: int3 boundary followed by real code
            if (instr.Mnemonic == Mnemonic.Int3 && i + 1 < instructions.Count && instructions[i + 1].IP <= targetAddr)
            {
                // Skip consecutive int3 padding
                int nextReal = i + 1;
                while (nextReal < instructions.Count && instructions[nextReal].Mnemonic == Mnemonic.Int3)
                    nextReal++;
                if (nextReal < instructions.Count && instructions[nextReal].IP <= targetAddr)
                {
                    funcStart = instructions[nextReal].IP;
                    foundStart = true;
                    break;
                }
            }
        }

        // Scan forward from target for epilogue
        ulong funcEnd = 0;
        bool foundEnd = false;
        int retCount = 0;
        foreach (var instr in instructions)
        {
            if (instr.IP < targetAddr) continue;

            if (instr.Mnemonic == Mnemonic.Ret)
            {
                funcEnd = instr.IP + (ulong)instr.Length;
                foundEnd = true;
                retCount++;
                // Continue to find int3 padding after ret (true function end)
                continue;
            }
            // int3 after ret confirms we're past the function
            if (foundEnd && instr.Mnemonic == Mnemonic.Int3)
                break;
            // Non-int3 after ret means there are more paths (e.g., multiple returns)
            if (foundEnd && instr.Mnemonic != Mnemonic.Int3)
            {
                foundEnd = false; // reset, keep looking for final ret
            }
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Function boundary analysis around 0x{targetAddr:X}:");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Start: {(foundStart ? $"0x{funcStart:X}" : $"not found (searched {searchRange} bytes back)")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  End:   {(foundEnd ? $"0x{funcEnd:X}" : $"not found (searched {searchRange} bytes forward)")}");
        if (foundStart && foundEnd)
        {
            var size = funcEnd - funcStart;
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Size:  {size} bytes (0x{size:X})");
        }
        if (retCount > 1)
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Note:  {retCount} return instructions found (multiple exit paths)");
        return sb.ToString().TrimEnd();
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Find all CALL and JMP instructions that target a specific function address. Scans module code for direct calls (call 0xABCD), indirect calls (call [rip+disp] resolved to target), and tail-call jumps. Useful for tracing who calls a known function.")]
    public async Task<string> GetCallerGraph(
        [Description("Process ID")] int processId,
        [Description("Target function address (hex)")] string targetAddress,
        [Description("Module to scan (or 'all')")] string moduleName,
        [Description("Max results")] int maxResults = 0)
    {
        if (maxResults <= 0) maxResults = _limits.MaxCodeSearchResults;
        var target = (ulong)ParseAddress(targetAddress);
        var attachment = await engineFacade.AttachAsync(processId).ConfigureAwait(false);
        var targetModules = moduleName.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? attachment.Modules.ToList()
            : attachment.Modules.Where(m => m.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase)).ToList();

        if (targetModules.Count == 0) return $"Module '{moduleName}' not found.";

        var results = new List<string>();
        var formatter = new MasmFormatter();
        var output = new StringOutput();

        foreach (var mod in targetModules)
        {
            if (results.Count >= maxResults) break;

            var chunkSize = DefaultChunkSize;
            for (long off = 0; off < mod.SizeBytes && results.Count < maxResults; off += chunkSize)
            {
                var readAddr = (nuint)((ulong)mod.BaseAddress + (ulong)off);
                var readLen = (int)Math.Min(chunkSize, mod.SizeBytes - off);

                MemoryReadResult memResult;
                try { memResult = await engineFacade.ReadMemoryAsync(processId, readAddr, readLen).ConfigureAwait(false); }
                catch (Exception ex) { if (logger is not null && logger.IsEnabled(LogLevel.Debug)) logger.LogDebug(ex, "FindCallsTo: Failed to read memory at {Address}", readAddr); continue; }

                var bytes = memResult.Bytes is byte[] arr ? arr : memResult.Bytes.ToArray();
                if (bytes.Length == 0) continue;

                var reader = new ByteArrayCodeReader(bytes);
                var decoder = Decoder.Create(64, reader, (ulong)readAddr);

                while (decoder.IP < (ulong)readAddr + (ulong)bytes.Length)
                {
                    var instr = decoder.Decode();
                    if (instr.IsInvalid) continue;

                    bool isCall = instr.Mnemonic == Mnemonic.Call;
                    bool isJmp = instr.Mnemonic == Mnemonic.Jmp;
                    if (!isCall && !isJmp) continue;

                    bool matches = false;
                    string callType = isCall ? "CALL" : "JMP (tail call)";

                    // Direct near branch (E8 rel32 for call, E9 rel32 for jmp)
                    if (instr.NearBranchTarget == target)
                    {
                        matches = true;
                    }
                    // Indirect RIP-relative: call [rip+disp] → try to resolve the pointer
                    else if (instr.IsIPRelativeMemoryOperand && instr.IPRelativeMemoryAddress != 0)
                    {
                        // The instruction reads a pointer from [rip+disp]; read 8 bytes at that address
                        try
                        {
                            var ptrResult = await engineFacade.ReadMemoryAsync(processId,
                                (nuint)instr.IPRelativeMemoryAddress, 8).ConfigureAwait(false);
                            var ptrBytes = ptrResult.Bytes is byte[] pb ? pb : ptrResult.Bytes.ToArray();
                            if (ptrBytes.Length == 8)
                            {
                                var resolvedTarget = BitConverter.ToUInt64(ptrBytes, 0);
                                if (resolvedTarget == target)
                                {
                                    matches = true;
                                    callType = isCall ? "CALL [indirect, resolved]" : "JMP [indirect, resolved]";
                                }
                            }
                        }
                        catch (Exception ex) { logger?.LogDebug(ex, "GetCallerGraph pointer read failed"); }
                    }

                    if (matches)
                    {
                        formatter.Format(instr, output);
                        results.Add($"  0x{instr.IP:X} | {output.ToStringAndReset()} | {callType} | in {mod.Name}");
                        if (results.Count >= maxResults) break;
                    }
                }
            }
        }

        return results.Count > 0
            ? $"Callers of 0x{target:X} ({results.Count} found):\n{string.Join('\n', results)}"
            : $"No CALL/JMP instructions targeting 0x{target:X} found in {moduleName}.\n" +
              "Note: If the target is called through vtables or register-indirect calls (call rax), those cannot be resolved statically.";
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Search for instruction patterns in a module. Find specific mnemonics, register usage, or memory access patterns. Examples: 'mov.*\\\\[rsi\\\\+0x38\\\\]', 'call.*GameAssembly', 'imul.*ebx'.")]
    public async Task<string> SearchInstructionPattern(
        [Description("Process ID")] int processId,
        [Description("Module name (or 'all')")] string moduleName,
        [Description("Regex pattern to match against formatted instruction text (MASM syntax)")] string pattern,
        [Description("Max results")] int maxResults = 0)
    {
        if (maxResults <= 0) maxResults = _limits.MaxCodeSearchResults;
        Regex regex;
        try { regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(5)); }
        catch (ArgumentException ex) { return $"Invalid regex pattern: {ex.Message}"; }

        var attachment = await engineFacade.AttachAsync(processId).ConfigureAwait(false);
        var targetModules = moduleName.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? attachment.Modules.ToList()
            : attachment.Modules.Where(m => m.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase)).ToList();

        if (targetModules.Count == 0) return $"Module '{moduleName}' not found.";

        var results = new List<string>();
        var formatter = new MasmFormatter();
        var output = new StringOutput();

        foreach (var mod in targetModules)
        {
            if (results.Count >= maxResults) break;

            var chunkSize = DefaultChunkSize;
            for (long off = 0; off < mod.SizeBytes && results.Count < maxResults; off += chunkSize)
            {
                var readAddr = (nuint)((ulong)mod.BaseAddress + (ulong)off);
                var readLen = (int)Math.Min(chunkSize, mod.SizeBytes - off);

                MemoryReadResult memResult;
                try { memResult = await engineFacade.ReadMemoryAsync(processId, readAddr, readLen).ConfigureAwait(false); }
                catch (Exception ex) { if (logger is not null && logger.IsEnabled(LogLevel.Debug)) logger.LogDebug(ex, "SearchInstructionPattern: Failed to read memory at {Address}", readAddr); continue; }

                var bytes = memResult.Bytes is byte[] arr ? arr : memResult.Bytes.ToArray();
                if (bytes.Length == 0) continue;

                var reader = new ByteArrayCodeReader(bytes);
                var decoder = Decoder.Create(64, reader, (ulong)readAddr);

                while (decoder.IP < (ulong)readAddr + (ulong)bytes.Length)
                {
                    var instr = decoder.Decode();
                    if (instr.IsInvalid) continue;

                    formatter.Format(instr, output);
                    var text = output.ToStringAndReset();

                    if (regex.IsMatch(text))
                    {
                        results.Add($"  0x{instr.IP:X} | {text} | in {mod.Name}");
                        if (results.Count >= maxResults) break;
                    }
                }
            }
        }

        return results.Count > 0
            ? $"Pattern matches for '{pattern}' ({results.Count} found):\n{string.Join('\n', results)}"
            : $"No instructions matching '{pattern}' found in {moduleName}.\nNote: MASM formatter uses hex suffixed with 'h' (e.g., [rax+38h] not [rax+0x38]). Consider using FindByMemoryOperand for offset-based searches.";
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Search for instructions with a specific memory operand displacement and optional base register. More reliable than regex pattern search since it uses structured operand data, not text matching.")]
    public async Task<string> FindByMemoryOperand(
        [Description("Process ID")] int processId,
        [Description("Module name (or 'all')")] string moduleName,
        [Description("Memory displacement/offset to match (hex), e.g., '0x38'")] string displacement,
        [Description("Optional base register filter, e.g., 'rsi', 'rax', or 'any'")] string baseRegister = "any",
        [Description("Filter: 'writes', 'reads', 'all'")] string filter = "all",
        [Description("Max results")] int maxResults = 0)
    {
        if (maxResults <= 0) maxResults = _limits.MaxSearchResults;
        var dispValue = (long)(ulong)ParseAddress(displacement);
        bool filterAnyBase = baseRegister.Equals("any", StringComparison.OrdinalIgnoreCase);

        var attachment = await engineFacade.AttachAsync(processId).ConfigureAwait(false);
        var targetModules = moduleName.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? attachment.Modules.ToList()
            : attachment.Modules.Where(m => m.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase)).ToList();

        if (targetModules.Count == 0) return $"Module '{moduleName}' not found.";

        var results = new List<string>();
        var formatter = new MasmFormatter();
        var output = new StringOutput();

        foreach (var mod in targetModules)
        {
            if (results.Count >= maxResults) break;

            var chunkSize = DefaultChunkSize;
            for (long off = 0; off < mod.SizeBytes && results.Count < maxResults; off += chunkSize)
            {
                var readAddr = (nuint)((ulong)mod.BaseAddress + (ulong)off);
                var readLen = (int)Math.Min(chunkSize, mod.SizeBytes - off);

                MemoryReadResult memResult;
                try { memResult = await engineFacade.ReadMemoryAsync(processId, readAddr, readLen).ConfigureAwait(false); }
                catch (Exception ex) { logger?.LogDebug(ex, "FindByMemoryOperand: Memory read failed at chunk offset"); continue; }

                var bytes = memResult.Bytes is byte[] arr ? arr : memResult.Bytes.ToArray();
                if (bytes.Length == 0) continue;

                var reader = new ByteArrayCodeReader(bytes);
                var decoder = Decoder.Create(64, reader, (ulong)readAddr);

                while (decoder.IP < (ulong)readAddr + (ulong)bytes.Length)
                {
                    var instr = decoder.Decode();
                    if (instr.IsInvalid) continue;

                    bool isLea = instr.Mnemonic == Mnemonic.Lea;

                    for (int opIdx = 0; opIdx < instr.OpCount; opIdx++)
                    {
                        if (instr.GetOpKind(opIdx) != OpKind.Memory) continue;
                        if ((long)instr.MemoryDisplacement64 != dispValue) continue;
                        if (instr.MemoryBase == Register.None) continue;
                        if (!filterAnyBase && !instr.MemoryBase.ToString().Equals(baseRegister, StringComparison.OrdinalIgnoreCase)) continue;

                        string classification;
                        if (isLea)
                            classification = "LEA";
                        else if (opIdx == 0 && IsWriteInstruction(instr))
                            classification = "WRITE";
                        else
                            classification = "READ";

                        if (filter.Equals("writes", StringComparison.OrdinalIgnoreCase) && classification != "WRITE") continue;
                        if (filter.Equals("reads", StringComparison.OrdinalIgnoreCase) && classification != "READ") continue;

                        formatter.Format(instr, output);
                        results.Add($"  0x{instr.IP:X} | {output.ToStringAndReset()} | {classification} | base={instr.MemoryBase} | in {mod.Name}");
                        if (results.Count >= maxResults) break;
                    }
                }
            }
        }

        var baseDesc = filterAnyBase ? "any base" : $"base={baseRegister}";
        return results.Count > 0
            ? $"Memory operand matches for displacement 0x{(ulong)(nuint)ParseAddress(displacement):X}, {baseDesc}, filter={filter} ({results.Count} found):\n{string.Join('\n', results)}"
            : $"No instructions with displacement 0x{(ulong)(nuint)ParseAddress(displacement):X} ({baseDesc}, filter={filter}) found in {moduleName}.";
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("High-level tool that bridges from a known data field to the code that writes it. " +
        "Given an address table entry (by ID or label), extracts its structure offset, finds the containing module's code, " +
        "and searches for instructions referencing that offset. Combines table metadata + FindByMemoryOperand + caller analysis. " +
        "This is the recommended starting point for 'find what writes to this field' investigations.")]
    public async Task<string> TraceFieldWriters(
        [Description("Process ID")] int processId,
        [Description("Address table entry ID or label (e.g., 'EXP' or 'ct-75')")] string entryIdOrLabel,
        [Description("Module to search (e.g., 'GameAssembly.dll'). If empty, searches all code modules.")] string moduleName = "",
        [Description("Max results per search strategy")] int maxResults = 0)
    {
        if (maxResults <= 0) maxResults = _limits.MaxTraceFieldResults;
        // Step 1: Resolve the table entry
        var node = ResolveNode(entryIdOrLabel);
        if (node is null)
            return $"Address table entry '{entryIdOrLabel}' not found.";
        if (!node.ResolvedAddress.HasValue)
            return $"Entry '{node.Label}' ({node.Id}) has no resolved address — refresh the table first.";

        var resolvedAddr = node.ResolvedAddress.Value;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"═══ TraceFieldWriters: {node.Label} ({node.Id}) ═══");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Resolved address: 0x{resolvedAddr:X}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Data type: {node.DataType}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Current value: {node.CurrentValue}");

        // Step 2: Determine structure offset from parent or address pattern
        long? structOffset = null;
        string offsetSource = "unknown";

        if (node.IsOffset && node.Parent is not null && node.Parent.ResolvedAddress.HasValue)
        {
            // Parent-relative offset: the offset IS the displacement we need
            structOffset = (long)((ulong)resolvedAddr - (ulong)node.Parent.ResolvedAddress.Value);
            offsetSource = $"parent-relative ({node.Parent.Label} + 0x{structOffset:X})";
        }
        else if (node.Address.StartsWith('+') || node.Address.StartsWith('-'))
        {
            // Symbolic offset address like "+38"
            if (long.TryParse(node.Address.TrimStart('+'), System.Globalization.NumberStyles.HexNumber, null, out var parsed))
            {
                structOffset = parsed;
                offsetSource = $"symbolic offset ({node.Address})";
            }
        }

        if (structOffset.HasValue)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Structure offset: 0x{structOffset.Value:X} (from {offsetSource})");
        }
        else
        {
            sb.AppendLine("⚠️ Could not determine structure offset — no parent-relative relationship found.");
            sb.AppendLine("   Falling back to absolute address search only.");
        }

        // Step 3: Determine search module(s)
        var attachment = await engineFacade.AttachAsync(processId).ConfigureAwait(false);
        var searchModules = string.IsNullOrWhiteSpace(moduleName) || moduleName.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? attachment.Modules.Where(m => m.SizeBytes > 0x1000).ToList()
            : attachment.Modules.Where(m => m.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase)).ToList();

        if (searchModules.Count == 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"⚠️ Module '{moduleName}' not found.");
            return sb.ToString();
        }

        var modNames = string.Join(", ", searchModules.Select(m => m.Name).Take(5));
        if (searchModules.Count > 5) modNames += $" (+{searchModules.Count - 5} more)";
        sb.AppendLine(CultureInfo.InvariantCulture, $"Searching: {modNames}");
        sb.AppendLine();

        var allResults = new List<(string Strategy, string Line)>();

        // Strategy A: If we have a structure offset, search for displacement-based memory operands
        if (structOffset.HasValue)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"── Strategy A: Instructions with memory displacement 0x{structOffset.Value:X} ──");
            var formatter = new MasmFormatter();
            var output = new StringOutput();
            int stratACount = 0;

            foreach (var mod in searchModules)
            {
                if (stratACount >= maxResults) break;

                var chunkSize = DefaultChunkSize;
                for (long off = 0; off < mod.SizeBytes && stratACount < maxResults; off += chunkSize)
                {
                    var readAddr = (nuint)((ulong)mod.BaseAddress + (ulong)off);
                    var readLen = (int)Math.Min(chunkSize, mod.SizeBytes - off);

                    MemoryReadResult memResult;
                    try { memResult = await engineFacade.ReadMemoryAsync(processId, readAddr, readLen).ConfigureAwait(false); }
                    catch (Exception ex) { logger?.LogDebug(ex, "FindStructureAccess: Strategy A memory read failed"); continue; }

                    var bytes = memResult.Bytes is byte[] arr ? arr : memResult.Bytes.ToArray();
                    if (bytes.Length == 0) continue;

                    var reader = new ByteArrayCodeReader(bytes);
                    var decoder = Decoder.Create(64, reader, (ulong)readAddr);

                    while (decoder.IP < (ulong)readAddr + (ulong)bytes.Length)
                    {
                        var instr = decoder.Decode();
                        if (instr.IsInvalid) continue;

                        bool isLea = instr.Mnemonic == Mnemonic.Lea;

                        for (int opIdx = 0; opIdx < instr.OpCount; opIdx++)
                        {
                            if (instr.GetOpKind(opIdx) != OpKind.Memory) continue;
                            if ((long)instr.MemoryDisplacement64 != structOffset.Value) continue;
                            if (instr.MemoryBase == Register.None) continue;

                            string classification;
                            if (isLea) classification = "LEA";
                            else if (opIdx == 0 && IsWriteInstruction(instr)) classification = "WRITE";
                            else classification = "READ";

                            formatter.Format(instr, output);
                            var line = $"  0x{instr.IP:X} | {output.ToStringAndReset()} | {classification} | base={instr.MemoryBase} | {mod.Name}";
                            allResults.Add(("A", line));
                            sb.AppendLine(line);
                            stratACount++;
                            if (stratACount >= maxResults) break;
                        }
                    }
                }
            }

            if (stratACount == 0)
                sb.AppendLine("  (no results — the field may be accessed through helper functions or computed offsets)");
            sb.AppendLine();
        }

        // Strategy B: Search for nearby small offsets if offset is small (common in IL2CPP)
        // If the primary offset search found nothing and offset < 0x200, also try adjacent offsets
        if (structOffset.HasValue && allResults.Count == 0 && structOffset.Value < 0x200)
        {
            var adjacentOffsets = new[] { structOffset.Value - 4, structOffset.Value + 4, structOffset.Value - 8, structOffset.Value + 8 };
            sb.AppendLine(CultureInfo.InvariantCulture, $"── Strategy B: Adjacent offsets (±4, ±8 from 0x{structOffset.Value:X}) ──");
            int stratBCount = 0;

            foreach (var adjOff in adjacentOffsets.Where(o => o > 0))
            {
                if (stratBCount >= 10) break;
                foreach (var mod in searchModules.Take(3))
                {
                    if (stratBCount >= 10) break;

                    var chunkSize = DefaultChunkSize;
                    var formatter = new MasmFormatter();
                    var output = new StringOutput();

                    for (long off = 0; off < mod.SizeBytes && stratBCount < 10; off += chunkSize)
                    {
                        var readAddr = (nuint)((ulong)mod.BaseAddress + (ulong)off);
                        var readLen = (int)Math.Min(chunkSize, mod.SizeBytes - off);

                        MemoryReadResult memResult;
                        try { memResult = await engineFacade.ReadMemoryAsync(processId, readAddr, readLen).ConfigureAwait(false); }
                        catch (Exception ex) { logger?.LogDebug(ex, "FindStructureAccess: Strategy B memory read failed"); continue; }

                        var bytes = memResult.Bytes is byte[] arr ? arr : memResult.Bytes.ToArray();
                        if (bytes.Length == 0) continue;

                        var reader = new ByteArrayCodeReader(bytes);
                        var decoder = Decoder.Create(64, reader, (ulong)readAddr);

                        while (decoder.IP < (ulong)readAddr + (ulong)bytes.Length)
                        {
                            var instr = decoder.Decode();
                            if (instr.IsInvalid) continue;

                            for (int opIdx = 0; opIdx < instr.OpCount; opIdx++)
                            {
                                if (instr.GetOpKind(opIdx) != OpKind.Memory) continue;
                                if ((long)instr.MemoryDisplacement64 != adjOff) continue;
                                if (instr.MemoryBase == Register.None) continue;
                                if (opIdx != 0 || !IsWriteInstruction(instr)) continue;

                                formatter.Format(instr, output);
                                var line = $"  0x{instr.IP:X} | {output.ToStringAndReset()} | WRITE to +0x{adjOff:X} (adjacent) | base={instr.MemoryBase} | {mod.Name}";
                                allResults.Add(("B", line));
                                sb.AppendLine(line);
                                stratBCount++;
                                if (stratBCount >= 10) break;
                            }
                        }
                    }
                }
            }

            if (stratBCount == 0)
                sb.AppendLine("  (no adjacent offset writers found either)");
            sb.AppendLine();
        }

        // Strategy C: If we found writers, try to identify their containing functions
        if (allResults.Count > 0)
        {
            sb.AppendLine("── Strategy C: Function context for top writer candidates ──");
            var writerAddresses = allResults
                .Where(r => r.Line.Contains("WRITE"))
                .Select(r =>
                {
                    var addrStr = r.Line.TrimStart().Split('|')[0].Trim();
                    return ParseAddress(addrStr);
                })
                .Distinct()
                .Take(5);

            foreach (var writerAddr in writerAddresses)
            {
                // Find function boundaries around this writer
                var containingMod = searchModules.FirstOrDefault(m =>
                    (ulong)writerAddr >= (ulong)m.BaseAddress &&
                    (ulong)writerAddr < (ulong)m.BaseAddress + (ulong)m.SizeBytes);

                if (containingMod is null) continue;

                // Quick backward scan for function prologue (push rbp / sub rsp)
                const int scanBack = 0x200;
                var scanStart = (nuint)Math.Max((long)writerAddr - scanBack, (long)containingMod.BaseAddress);
                var scanLen = (int)((ulong)writerAddr - (ulong)scanStart + 0x20);

                MemoryReadResult scanMem;
                try { scanMem = await engineFacade.ReadMemoryAsync(processId, scanStart, scanLen).ConfigureAwait(false); }
                catch (Exception ex) { logger?.LogDebug(ex, "FindStructureAccess: Strategy C memory read failed"); continue; }

                var scanBytes = scanMem.Bytes is byte[] sa ? sa : scanMem.Bytes.ToArray();
                var scanReader = new ByteArrayCodeReader(scanBytes);
                var scanDecoder = Decoder.Create(64, scanReader, (ulong)scanStart);

                nuint funcStart = 0;
                while (scanDecoder.IP < (ulong)writerAddr)
                {
                    var ins = scanDecoder.Decode();
                    if (ins.IsInvalid) continue;
                    // push rbp or sub rsp,imm
                    if ((ins.Mnemonic == Mnemonic.Push && ins.Op0Register == Register.RBP) ||
                        (ins.Mnemonic == Mnemonic.Sub && ins.Op0Register == Register.RSP))
                    {
                        funcStart = (nuint)ins.IP;
                    }
                }

                if (funcStart != 0)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  Writer 0x{writerAddr:X} → likely function at 0x{funcStart:X} in {containingMod.Name}");
                    sb.AppendLine($"    (use GetCallerGraph with this function address to find call sites)");
                }
            }
            sb.AppendLine();
        }

        // Summary
        sb.AppendLine("── Summary ──");
        int writes = allResults.Count(r => r.Line.Contains("WRITE"));
        int reads = allResults.Count(r => r.Line.Contains("READ") && !r.Line.Contains("READWRITE"));
        int leas = allResults.Count(r => r.Line.Contains("LEA"));
        sb.AppendLine(CultureInfo.InvariantCulture, $"Total: {allResults.Count} results ({writes} writes, {reads} reads, {leas} LEAs)");

        if (allResults.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("💡 No direct offset references found. Possible reasons:");
            sb.AppendLine("   • Field is accessed through IL2CPP/Unity accessor functions (indirect calls)");
            sb.AppendLine("   • Offset is computed at runtime rather than embedded in displacement");
            sb.AppendLine("   • Writer code is in a different module (try moduleName='all')");
            sb.AppendLine("   • Field uses a different base+offset decomposition than expected");
            sb.AppendLine();
            sb.AppendLine("🔧 Next steps:");
            sb.AppendLine("   1. Try searching adjacent offsets manually with FindByMemoryOperand");
            sb.AppendLine("   2. Use function analysis around the known reward hook addresses");
            sb.AppendLine("   3. Install a stealth code-cave hook on a nearby known function and capture register snapshots");
            sb.AppendLine("   4. Use SampledWriteTrace on the data address to confirm write activity");
        }

        return sb.ToString();
    }

}
