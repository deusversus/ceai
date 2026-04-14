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
    private readonly Func<ILuaScriptEngine?> _luaEngineFactory;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, nuint> _symbolTable = new(StringComparer.OrdinalIgnoreCase);

    public WindowsAutoAssemblerEngine(Func<ILuaScriptEngine?>? luaEngineFactory = null)
    {
        _luaEngineFactory = luaEngineFactory ?? (() => null);
    }

    public IReadOnlyList<RegisteredSymbol> GetRegisteredSymbols() =>
        _symbolTable.Select(kv => new RegisteredSymbol(kv.Key, kv.Value)).ToList();

    public nuint? ResolveSymbol(string name) =>
        _symbolTable.TryGetValue(name, out var addr) ? addr : null;

    public void RegisterSymbol(string name, nuint address) =>
        _symbolTable[name] = address;

    public void UnregisterSymbol(string name) =>
        _symbolTable.TryRemove(name, out _);

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
            ValidateSection(enableSection, "[ENABLE]", errors, warnings, _luaEngineFactory() is not null);

        if (disableSection is not null)
            ValidateSection(disableSection, "[DISABLE]", errors, warnings, _luaEngineFactory() is not null);

        return new ScriptParseResult(errors.Count == 0, errors, warnings, enableSection, disableSection);
    }

    public Task<ScriptExecutionResult> EnableAsync(int processId, string script, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var processed = PreprocessConditionals(PreprocessIncludes(script));
            return ExecuteSection(processId, processed, enable: true, ct);
        }, ct);

    public Task<ScriptExecutionResult> DisableAsync(int processId, string script, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var processed = PreprocessConditionals(PreprocessIncludes(script));
            return ExecuteSection(processId, processed, enable: false, ct);
        }, ct);

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

            if (modules.Count == 0)
            {
                // Module enumeration failed — module-relative addresses will all be unresolvable.
                // This usually means the process is protected, still starting, or has exited.
                return new ScriptExecutionResult(
                    false,
                    $"Unable to enumerate modules for process {processId}. " +
                    "The process may be protected, still loading, or no longer running.",
                    Array.Empty<ScriptAllocation>(),
                    Array.Empty<ScriptPatch>());
            }

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

    private ScriptExecutionResult ExecuteSectionCore(ExecutionContext ctx, string section, CancellationToken ct)
    {
        var lines = ParseLines(section);
        var allocations = new List<ScriptAllocation>();
        var patches = new List<ScriptPatch>();
        var registeredSymbols = new List<RegisteredSymbol>();
        var strictMode = false;
        var warnings = new List<string>();

        // Collect directives and code blocks in a single pass
        var defines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allocDirectives = new List<(string Name, string SizeExpr, string? NearExpr)>();
        var deallocDirectives = new List<string>();
        var assertDirectives = new List<(string AddrExpr, string BytePattern)>();
        var registerSymbolNames = new List<string>();
        var unregisterSymbolNames = new List<string>();
        var createThreadDirectives = new List<string>();
        var readMemDirectives = new List<(string SourceAddr, int Length)>();
        var writeMemDirectives = new List<(string DestAddr, string BytePattern)>();
        var loadLibraryDirectives = new List<string>();
        var codeBlocks = new List<CodeBlock>();

        CodeBlock? currentBlock = null;
        var insideLuaBlock = false;
        var luaBlockBuilder = new StringBuilder();
        var luaBlockIndex = 0;

        foreach (var line in lines)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line))
                continue;

            var trimmed = line.Trim();

            // {$strict} pragma
            if (trimmed.Equals("{$strict}", StringComparison.OrdinalIgnoreCase))
            {
                strictMode = true;
                continue;
            }

            // {$luacode} / {$lua} ... {$asm} block handling
            // CE uses {$lua} as the primary directive; {$luacode} is an alias
            if (IsLuaBlockStart(trimmed))
            {
                insideLuaBlock = true;
                luaBlockBuilder.Clear();
                luaBlockIndex++;
                continue;
            }
            if (trimmed.Equals("{$asm}", StringComparison.OrdinalIgnoreCase))
            {
                if (insideLuaBlock && luaBlockBuilder.Length > 0)
                {
                    var luaEngineInBlock = _luaEngineFactory();
                    if (luaEngineInBlock is not null)
                    {
                        var luaResult = luaEngineInBlock.ExecuteAsync(luaBlockBuilder.ToString(), ctx.ProcessId, ct)
                            .GetAwaiter().GetResult();
                        if (!luaResult.Success)
                            return new ScriptExecutionResult(false, $"Lua block #{luaBlockIndex} error: {luaResult.Error}", [], []);
                    }
                    else
                    {
                        warnings.Add("Lua code block skipped (Lua engine not available).");
                    }
                }
                insideLuaBlock = false;
                continue;
            }
            if (insideLuaBlock)
            {
                luaBlockBuilder.AppendLine(line);
                continue;
            }

            var defineMatch = DefineRegex().Match(trimmed);
            if (defineMatch.Success)
            {
                defines[defineMatch.Groups[1].Value] = defineMatch.Groups[2].Value;
                continue;
            }

            // var name = value (syntactic sugar for define)
            var varMatch = VarDeclRegex().Match(trimmed);
            if (varMatch.Success)
            {
                defines[varMatch.Groups[1].Value] = varMatch.Groups[2].Value;
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

            // registersymbol / unregistersymbol — collect names for post-resolution execution
            var regSymMatch = RegisterSymbolRegex().Match(trimmed);
            if (regSymMatch.Success)
            {
                registerSymbolNames.Add(regSymMatch.Groups[1].Value);
                continue;
            }
            var unregSymMatch = UnregisterSymbolRegex().Match(trimmed);
            if (unregSymMatch.Success)
            {
                unregisterSymbolNames.Add(unregSymMatch.Groups[1].Value);
                continue;
            }

            // createthread(address)
            var ctMatch = CreateThreadRegex().Match(trimmed);
            if (ctMatch.Success)
            {
                createThreadDirectives.Add(ctMatch.Groups[1].Value.Trim());
                continue;
            }

            // readmem(sourceAddr, length)
            var rmMatch = ReadMemRegex().Match(trimmed);
            if (rmMatch.Success)
            {
                readMemDirectives.Add((rmMatch.Groups[1].Value.Trim(), int.Parse(rmMatch.Groups[2].Value.Trim(), CultureInfo.InvariantCulture)));
                continue;
            }

            // writemem(destAddr, bytePattern)
            var wmMatch = WriteMemRegex().Match(trimmed);
            if (wmMatch.Success)
            {
                writeMemDirectives.Add((wmMatch.Groups[1].Value.Trim(), wmMatch.Groups[2].Value.Trim()));
                continue;
            }

            // loadlibrary(dllPath)
            var llMatch = LoadLibraryRegex().Match(trimmed);
            if (llMatch.Success)
            {
                loadLibraryDirectives.Add(llMatch.Groups[1].Value.Trim());
                continue;
            }

            // aobscanmodule(symbolName, moduleName, bytePattern)
            var aobMatch = AobScanModuleRegex().Match(trimmed);
            if (aobMatch.Success)
            {
                var symbolName = aobMatch.Groups[1].Value;
                var moduleName = aobMatch.Groups[2].Value;
                var pattern = aobMatch.Groups[3].Value.Trim();

                var found = AobScanModule(ctx, moduleName, pattern, ct);
                if (found is null)
                {
                    return new ScriptExecutionResult(
                        false,
                        $"aobscanmodule failed: pattern not found for '{symbolName}' in module '{moduleName}'. " +
                        $"Pattern: {pattern}. The game version may have changed.",
                        allocations, patches);
                }

                defines[symbolName] = $"0x{found.Value:X}";
                continue;
            }

            // aobscan(symbolName, bytePattern) — scan all readable memory
            var aobGlobalMatch = AobScanGlobalRegex().Match(trimmed);
            if (aobGlobalMatch.Success)
            {
                var symbolName = aobGlobalMatch.Groups[1].Value;
                var pattern = aobGlobalMatch.Groups[2].Value.Trim();

                var found = AobScanAll(ctx, pattern, ct);
                if (found is null)
                {
                    return new ScriptExecutionResult(
                        false,
                        $"aobscan failed: pattern not found for '{symbolName}'. Pattern: {pattern}.",
                        allocations, patches);
                }

                defines[symbolName] = $"0x{found.Value:X}";
                continue;
            }

            if (trimmed.StartsWith("LuaCall(", StringComparison.OrdinalIgnoreCase))
            {
                var luaCallEngine = _luaEngineFactory();
                if (luaCallEngine is not null)
                {
                    // Extract content between balanced LuaCall( ... )
                    var inner = trimmed["LuaCall(".Length..];
                    // Find the matching closing paren (skip nested parens)
                    var depth = 1;
                    var endIdx = 0;
                    for (int ci = 0; ci < inner.Length && depth > 0; ci++)
                    {
                        if (inner[ci] == '(') depth++;
                        else if (inner[ci] == ')') { depth--; if (depth == 0) { endIdx = ci; break; } }
                    }
                    var funcExpr = endIdx > 0 ? inner[..endIdx].Trim() : inner.TrimEnd(')', ' ');
                    if (!funcExpr.Contains('('))
                        funcExpr += "()";
                    var luaResult = luaCallEngine.EvaluateAsync(funcExpr, ct).GetAwaiter().GetResult();
                    if (!luaResult.Success)
                        return new ScriptExecutionResult(false, $"LuaCall error: {luaResult.Error}", [], []);
                }
                continue;
            }

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
            else if (TryExecuteCustomCommand(trimmed))
            {
                // S5: Custom AA command handled
                continue;
            }
            else if (strictMode)
            {
                // Strict mode: unrecognized lines outside code blocks are errors
                return new ScriptExecutionResult(
                    false,
                    $"Strict mode: unrecognized directive or instruction outside code block: '{trimmed}'",
                    allocations, patches);
            }
        }

        // Execute any trailing {$lua}/{$luacode} block that wasn't closed with {$asm}
        if (insideLuaBlock && luaBlockBuilder.Length > 0)
        {
            var luaEngine = _luaEngineFactory();
            if (luaEngine is not null)
            {
                var luaResult = luaEngine.ExecuteAsync(luaBlockBuilder.ToString(), ctx.ProcessId, ct)
                    .GetAwaiter().GetResult();
                if (!luaResult.Success)
                    return new ScriptExecutionResult(false, $"Lua error: {luaResult.Error}", allocations, patches);
            }
            else
            {
                warnings.Add("Trailing Lua code block skipped (Lua engine not available).");
            }
        }

        // Phase 1: Resolve define chains (a define may reference another define)
        ResolveDefines(defines);

        // Also add symbol table entries as define fallbacks for address resolution
        foreach (var (symName, symAddr) in _symbolTable)
        {
            if (!defines.ContainsKey(symName))
                defines[symName] = $"0x{symAddr:X}";
        }

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

                    // Opcode safety validation for db directive bytes
                    var dbOpcodeResult = OpcodeValidator.ValidateBytes(bytes, ctx.Is64Bit);
                    if (!dbOpcodeResult.IsValid)
                    {
                        var errMsg = string.Format(
                            CultureInfo.InvariantCulture,
                            "Opcode validation failed for db directive in block '{0}': {1}",
                            block.Label, string.Join("; ", dbOpcodeResult.Errors));
                        return new ScriptExecutionResult(false, errMsg, allocations, patches);
                    }
                    if (dbOpcodeResult.Warnings.Count > 0)
                        warnings.AddRange(dbOpcodeResult.Warnings);

                    var origBytes = new byte[bytes.Length];
                    if (!ReadProcessMemory(ctx.ProcessHandle, (IntPtr)offset, origBytes, origBytes.Length, out var origRead)
                        || origRead != origBytes.Length)
                    {
                        return new ScriptExecutionResult(
                            false, $"Failed to read original {bytes.Length} bytes at 0x{offset:X} for backup. " +
                                   $"Address may be invalid or unreadable (error {Marshal.GetLastWin32Error()}).", allocations, patches);
                    }

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
                    var detail = _lastAssemblyError ?? assemblyText;
                    return new ScriptExecutionResult(
                        false, $"Failed to assemble code block '{block.Label}': {detail}", allocations, patches);
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

            // Opcode safety validation — block dangerous instructions before writing
            var opcodeResult = OpcodeValidator.ValidateBytes(finalCode, ctx.Is64Bit);
            if (!opcodeResult.IsValid)
            {
                var errMsg = string.Format(
                    CultureInfo.InvariantCulture,
                    "Opcode validation failed for block '{0}': {1}",
                    block.Label, string.Join("; ", opcodeResult.Errors));
                return new ScriptExecutionResult(false, errMsg, allocations, patches);
            }
            if (opcodeResult.Warnings.Count > 0)
                warnings.AddRange(opcodeResult.Warnings);

            var writeAddr = blockAddress;
            if (block.DbDirectives.Count > 0)
            {
                // Advance past db bytes already written
                var dbTotal = block.DbDirectives.Sum(p => ParseByteString(p).Length);
                writeAddr = (nuint)((ulong)blockAddress + (ulong)dbTotal);
            }

            var origCodeBytes = new byte[finalCode.Length];
            if (!ReadProcessMemory(ctx.ProcessHandle, (IntPtr)writeAddr, origCodeBytes, origCodeBytes.Length, out var origCodeRead)
                || origCodeRead != origCodeBytes.Length)
            {
                return new ScriptExecutionResult(
                    false, $"Failed to read original {finalCode.Length} bytes at 0x{writeAddr:X} for backup. " +
                           $"Address may be invalid or unreadable (error {Marshal.GetLastWin32Error()}).", allocations, patches);
            }

            if (!WriteProcessMemory(ctx.ProcessHandle, (IntPtr)writeAddr, finalCode, finalCode.Length, out var codeWritten)
                || codeWritten != finalCode.Length)
            {
                return new ScriptExecutionResult(
                    false, $"Failed to write {finalCode.Length} bytes at 0x{writeAddr:X}.", allocations, patches);
            }

            patches.Add(new ScriptPatch(writeAddr, origCodeBytes, finalCode));
        }

        // Phase 8: Execute dealloc directives
        var deallocErrors = new List<string>();
        foreach (var name in deallocDirectives)
        {
            ct.ThrowIfCancellationRequested();

            if (!defines.TryGetValue(name, out var addrStr))
                continue;

            var addr = ResolveAddress(addrStr, defines, ctx);
            if (addr is not null)
            {
                // Flush instruction cache before freeing — reduces UAF risk if code was recently executing
                FlushInstructionCache(ctx.ProcessHandle, (IntPtr)addr.Value, 0);

                if (!VirtualFreeEx(ctx.ProcessHandle, (IntPtr)addr.Value, 0, MemRelease))
                    deallocErrors.Add($"Failed to free '{name}' at 0x{addr.Value:X} (error {Marshal.GetLastWin32Error()})");
            }
        }

        var deallocWarning = deallocErrors.Count > 0
            ? $" Warning: {string.Join("; ", deallocErrors)}"
            : null;

        // Phase 9: Execute registersymbol / unregistersymbol
        foreach (var symName in registerSymbolNames)
        {
            if (defines.TryGetValue(symName, out var symVal))
            {
                var resolved = ResolveAddress(symVal, defines, ctx);
                if (resolved is not null)
                {
                    _symbolTable[symName] = resolved.Value;
                    registeredSymbols.Add(new RegisteredSymbol(symName, resolved.Value));
                }
            }
        }
        foreach (var symName in unregisterSymbolNames)
        {
            _symbolTable.TryRemove(symName, out _);
        }

        // Phase 10: Execute createthread directives
        foreach (var addrExpr in createThreadDirectives)
        {
            ct.ThrowIfCancellationRequested();
            var addr = ResolveAddress(ExpandSymbols(addrExpr, defines), defines, ctx);
            if (addr is null)
            {
                return new ScriptExecutionResult(false,
                    $"createthread failed: cannot resolve address '{addrExpr}'.",
                    allocations, patches, registeredSymbols);
            }

            var threadHandle = CreateRemoteThread(ctx.ProcessHandle, IntPtr.Zero, 0,
                (IntPtr)addr.Value, IntPtr.Zero, 0, out _);
            if (threadHandle == IntPtr.Zero)
            {
                return new ScriptExecutionResult(false,
                    $"createthread failed at 0x{addr.Value:X}: error {Marshal.GetLastWin32Error()}",
                    allocations, patches, registeredSymbols);
            }
            _ = WaitForSingleObject(threadHandle, 1000);
            _ = CloseHandle(threadHandle);
        }

        // Phase 11: Execute readmem directives
        foreach (var (srcExpr, length) in readMemDirectives)
        {
            var addr = ResolveAddress(ExpandSymbols(srcExpr, defines), defines, ctx);
            if (addr is null) continue;
            var buf = new byte[length];
            ReadProcessMemory(ctx.ProcessHandle, (IntPtr)addr.Value, buf, length, out _);
            // readmem results are stored for use by writemem or db; here just log
        }

        // Phase 12: Execute writemem directives
        foreach (var (destExpr, bytePattern) in writeMemDirectives)
        {
            var addr = ResolveAddress(ExpandSymbols(destExpr, defines), defines, ctx);
            if (addr is null) continue;
            var bytes = ParseByteString(bytePattern);

            // Opcode safety validation for writemem directive bytes
            var wmOpcodeResult = OpcodeValidator.ValidateBytes(bytes, ctx.Is64Bit);
            if (!wmOpcodeResult.IsValid)
            {
                var errMsg = string.Format(
                    CultureInfo.InvariantCulture,
                    "Opcode validation failed for writemem directive at '{0}': {1}",
                    destExpr, string.Join("; ", wmOpcodeResult.Errors));
                return new ScriptExecutionResult(false, errMsg, allocations, patches, registeredSymbols);
            }
            if (wmOpcodeResult.Warnings.Count > 0)
                warnings.AddRange(wmOpcodeResult.Warnings);

            var origBytes = new byte[bytes.Length];
            ReadProcessMemory(ctx.ProcessHandle, (IntPtr)addr.Value, origBytes, origBytes.Length, out _);
            WriteProcessMemory(ctx.ProcessHandle, (IntPtr)addr.Value, bytes, bytes.Length, out _);
            patches.Add(new ScriptPatch(addr.Value, origBytes, bytes));
        }

        // Phase 13: Execute loadlibrary directives
        foreach (var dllPath in loadLibraryDirectives)
        {
            ct.ThrowIfCancellationRequested();
            var loadResult = LoadLibraryInTarget(ctx.ProcessHandle, dllPath);
            if (!loadResult)
            {
                warnings.Add($"loadlibrary('{dllPath}') may have failed (error {Marshal.GetLastWin32Error()}).");
            }
        }

        var allWarnings = deallocWarning;
        if (warnings.Count > 0)
            allWarnings = (allWarnings ?? "") + " " + string.Join("; ", warnings);

        return new ScriptExecutionResult(true, string.IsNullOrWhiteSpace(allWarnings) ? null : allWarnings.Trim(),
            allocations, patches, registeredSymbols);
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
            if (labelAddresses.TryGetValue(block.Label, out var blockAddr))
            {
                // This block already has an assigned address
                insideAlloc = IsAllocAddress(blockAddr, defines);
                if (insideAlloc)
                    currentOffset = blockAddr;

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

    /// <summary>
    /// Check if a trimmed line starts a Lua code block.
    /// CE uses {$lua} as the primary directive; {$luacode} is an alias.
    /// </summary>
    private static bool IsLuaBlockStart(string trimmedLine) =>
        trimmedLine.Equals("{$luacode}", StringComparison.OrdinalIgnoreCase) ||
        trimmedLine.Equals("{$lua}", StringComparison.OrdinalIgnoreCase);

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

    private static void ValidateSection(string section, string sectionName, List<string> errors, List<string> warnings, bool luaAvailable = false)
    {
        var lines = ParseLines(section);
        var insideLuaBlock = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            // Track {$lua}/{$luacode}...{$asm} blocks — skip Lua lines during AA validation
            if (IsLuaBlockStart(trimmed))
            {
                insideLuaBlock = true;
                if (!luaAvailable)
                    warnings.Add($"{sectionName}: Lua code block will be skipped (Lua engine not available).");
                continue;
            }
            if (trimmed.Equals("{$asm}", StringComparison.OrdinalIgnoreCase))
            {
                insideLuaBlock = false;
                continue;
            }
            if (insideLuaBlock)
                continue;

            if (trimmed.StartsWith("LuaCall(", StringComparison.OrdinalIgnoreCase))
            {
                if (!luaAvailable)
                    warnings.Add($"{sectionName}: LuaCall directive will be skipped (Lua engine not available).");
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
                AobScanModuleRegex().IsMatch(trimmed) ||
                AobScanGlobalRegex().IsMatch(trimmed) ||
                AobReplaceRegex().IsMatch(trimmed) ||
                AobReplaceModuleRegex().IsMatch(trimmed) ||
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
        var plusIdx = expanded.IndexOf('+', StringComparison.Ordinal);
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

        if (value.StartsWith('$'))
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
        catch (Keystone.KeystoneException ex)
        {
            _lastAssemblyError = $"Keystone: {ex.Message} — input: {(assemblyText.Length > 120 ? assemblyText[..120] + "..." : assemblyText)}";
            return null;
        }
    }

    [ThreadStatic]
    private static string? _lastAssemblyError;

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
        catch (Exception ex)
        {
            // Module enumeration may fail for protected processes
            System.Diagnostics.Trace.TraceWarning($"[WindowsAutoAssemblerEngine] Module enumeration failed: {ex.Message}");
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

    // ── S5: Custom AA Command Execution ──

    private bool TryExecuteCustomCommand(string line)
    {
        // Custom commands look like: commandName(arg1, arg2) or commandName
        var parenIdx = line.IndexOf('(');
        string commandName;
        string[] args;

        if (parenIdx > 0 && line.EndsWith(')'))
        {
            commandName = line[..parenIdx].Trim();
            var argsStr = line[(parenIdx + 1)..^1];
            args = argsStr.Split(',', StringSplitOptions.TrimEntries);
        }
        else
        {
            commandName = line.Trim();
            args = [];
        }

        if (_customCommands.TryGetValue(commandName, out var handler))
        {
            return handler(args);
        }

        return false;
    }

    // ── S5: Custom AA Command Registration ──

    private readonly Dictionary<string, Func<string[], bool>> _customCommands = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Register a custom AA directive that can be invoked from scripts.</summary>
    public void RegisterCustomCommand(string name, Func<string[], bool> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);
        _customCommands[name] = handler;
    }

    /// <summary>Unregister a previously registered custom AA directive.</summary>
    public void UnregisterCustomCommand(string name) => _customCommands.Remove(name);

    /// <summary>List all registered custom AA commands.</summary>
    public IReadOnlyList<string> GetCustomCommands() => _customCommands.Keys.ToList();

    // ── S5: Conditional Compilation Preprocessor ──

    /// <summary>
    /// Process {$ifdef SYMBOL}, {$ifndef SYMBOL}, {$else}, {$endif} directives.
    /// Uses the symbol table + built-in defines (WIN32, WIN64) to decide which blocks to include.
    /// </summary>
    private string PreprocessConditionals(string script)
    {
        var sb = new StringBuilder();
        var stack = new Stack<(bool Active, bool ElseSeen)>();
        var active = true; // whether current lines should be included
        var insideLuaBlock = false; // skip conditionals inside {$lua}/{$luacode} blocks

        foreach (var line in script.Split('\n'))
        {
            var trimmed = line.Trim();

            // Track {$lua}/{$luacode}...{$asm} boundaries — pass these through unchanged
            if (IsLuaBlockStart(trimmed))
            {
                insideLuaBlock = true;
                if (active) sb.AppendLine(line);
                continue;
            }
            if (trimmed.Equals("{$asm}", StringComparison.OrdinalIgnoreCase))
            {
                insideLuaBlock = false;
                if (active) sb.AppendLine(line);
                continue;
            }
            if (insideLuaBlock)
            {
                if (active) sb.AppendLine(line);
                continue;
            }

            if (trimmed.StartsWith("{$ifdef ", StringComparison.OrdinalIgnoreCase) && trimmed.EndsWith('}'))
            {
                var symbol = trimmed[8..^1].Trim();
                var defined = IsSymbolDefined(symbol);
                stack.Push((active, false));
                active = active && defined;
                continue;
            }

            if (trimmed.StartsWith("{$ifndef ", StringComparison.OrdinalIgnoreCase) && trimmed.EndsWith('}'))
            {
                var symbol = trimmed[9..^1].Trim();
                var defined = IsSymbolDefined(symbol);
                stack.Push((active, false));
                active = active && !defined;
                continue;
            }

            if (trimmed.Equals("{$else}", StringComparison.OrdinalIgnoreCase))
            {
                if (stack.Count > 0)
                {
                    var (parentActive, _) = stack.Pop();
                    // If parent was active and current block was NOT active, switch to active
                    active = parentActive && !active;
                    stack.Push((parentActive, true));
                }
                continue;
            }

            if (trimmed.Equals("{$endif}", StringComparison.OrdinalIgnoreCase))
            {
                if (stack.Count > 0)
                {
                    var (parentActive, _) = stack.Pop();
                    active = parentActive;
                }
                continue;
            }

            if (active)
                sb.AppendLine(line);
        }

        return sb.ToString();
    }

    private bool IsSymbolDefined(string symbol)
    {
        // Built-in defines
        if (symbol.Equals("WIN64", StringComparison.OrdinalIgnoreCase))
            return IntPtr.Size == 8;
        if (symbol.Equals("WIN32", StringComparison.OrdinalIgnoreCase))
            return IntPtr.Size == 4;

        // Check registered symbols
        return _symbolTable.ContainsKey(symbol);
    }

    /// <summary>Recursively inline {$include filepath} directives. Max depth 10.</summary>
    private static string PreprocessIncludes(string script, int depth = 0)
    {
        if (depth > 10)
            throw new InvalidOperationException("Include depth exceeded 10 — possible circular include.");

        var sb = new StringBuilder();
        foreach (var line in script.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("{$include ", StringComparison.OrdinalIgnoreCase) && trimmed.EndsWith('}'))
            {
                var path = trimmed[10..^1].Trim();
                if (File.Exists(path))
                {
                    var included = File.ReadAllText(path);
                    sb.AppendLine(PreprocessIncludes(included, depth + 1));
                }
                else
                {
                    sb.Append("// Include not found: ").AppendLine(path);
                }
            }
            else
            {
                sb.AppendLine(line);
            }
        }
        return sb.ToString();
    }

    /// <summary>Load a DLL into the target process using CreateRemoteThread + LoadLibraryW.</summary>
    private static bool LoadLibraryInTarget(IntPtr processHandle, string dllPath)
    {
        var pathBytes = Encoding.Unicode.GetBytes(dllPath + '\0');
        var pathAlloc = VirtualAllocEx(processHandle, IntPtr.Zero, (IntPtr)pathBytes.Length, MemCommit | MemReserve, 0x04 /* PAGE_READWRITE */);
        if (pathAlloc == IntPtr.Zero) return false;

        try
        {
            if (!WriteProcessMemory(processHandle, pathAlloc, pathBytes, pathBytes.Length, out _))
                return false;

            var kernel32 = GetModuleHandleW("kernel32.dll");
            if (kernel32 == IntPtr.Zero) return false;

            var loadLibAddr = GetProcAddress(kernel32, "LoadLibraryW");
            if (loadLibAddr == IntPtr.Zero) return false;

            var threadHandle = CreateRemoteThread(processHandle, IntPtr.Zero, 0, loadLibAddr, pathAlloc, 0, out _);
            if (threadHandle == IntPtr.Zero) return false;

            _ = WaitForSingleObject(threadHandle, 5000);
            _ = CloseHandle(threadHandle);
            return true;
        }
        finally
        {
            VirtualFreeEx(processHandle, pathAlloc, 0, MemRelease);
        }
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

    [GeneratedRegex(@"^registersymbol\(\s*(\w+)\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex RegisterSymbolRegex();

    [GeneratedRegex(@"^unregistersymbol\(\s*(\w+)\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex UnregisterSymbolRegex();

    [GeneratedRegex(@"^var\s+(\w+)\s*=\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex VarDeclRegex();

    [GeneratedRegex(@"^createthread\s*\(\s*(.+?)\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex CreateThreadRegex();

    [GeneratedRegex(@"^readmem\s*\(\s*(.+?)\s*,\s*(\d+)\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex ReadMemRegex();

    [GeneratedRegex(@"^writemem\s*\(\s*(.+?)\s*,\s*((?:[0-9A-Fa-f]{2}\s*)+)\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex WriteMemRegex();

    [GeneratedRegex(@"^loadlibrary\s*\(\s*(.+?)\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex LoadLibraryRegex();

    [GeneratedRegex(@"^(\w+)\s*:$")]
    private static partial Regex LabelDefRegex();

    [GeneratedRegex(@"^db\s+((?:[0-9A-Fa-f]{2}\s*)+)$", RegexOptions.IgnoreCase)]
    private static partial Regex DbRegex();

    [GeneratedRegex(@"^nop\s+(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex NopRegex();

    [GeneratedRegex(@"^aobscanmodule\(\s*(\w+)\s*,\s*([^,]+?)\s*,\s*((?:[0-9A-Fa-f?]{1,2}\s*)+)\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex AobScanModuleRegex();

    [GeneratedRegex(@"^aobscan\(\s*(\w+)\s*,\s*((?:[0-9A-Fa-f?]{1,2}\s*)+)\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex AobScanGlobalRegex();

    [GeneratedRegex(@"^aobreplace\(\s*((?:[0-9A-Fa-f?]{1,2}\s*)+)\s*,\s*((?:[0-9A-Fa-f]{1,2}\s*)+)\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex AobReplaceRegex();

    [GeneratedRegex(@"^aobreplacemodule\(\s*([^,]+?)\s*,\s*((?:[0-9A-Fa-f?]{1,2}\s*)+)\s*,\s*((?:[0-9A-Fa-f]{1,2}\s*)+)\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex AobReplaceModuleRegex();

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
    private static extern bool FlushInstructionCache(
        IntPtr processHandle, IntPtr baseAddress, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(
        IntPtr processHandle, out ushort processMachine, out ushort nativeMachine);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(
        IntPtr hProcess, IntPtr lpThreadAttributes, uint dwStackSize,
        IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, out uint lpThreadId);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int VirtualQueryEx(
        IntPtr hProcess, IntPtr lpAddress, out MemoryBasicInformation lpBuffer, int dwLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    #endregion

    #region AOB Scanning

    /// <summary>Parse a CE-style byte pattern (e.g. "48 8B 05 ?? ?? ?? ?? 48 85 C0") into bytes + mask.</summary>
    private static (byte[] pattern, bool[] mask) ParseAobPattern(string patternStr)
    {
        var parts = patternStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bytes = new byte[parts.Length];
        var mask = new bool[parts.Length]; // true = must match, false = wildcard

        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i] is "?" or "??")
            {
                mask[i] = false;
                bytes[i] = 0;
            }
            else
            {
                mask[i] = true;
                bytes[i] = byte.Parse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
        }

        return (bytes, mask);
    }

    /// <summary>Scan a specific module's memory for an AOB pattern.</summary>
    private static nuint? AobScanModule(ExecutionContext ctx, string moduleName, string patternStr, CancellationToken ct)
    {
        if (!ctx.Modules.TryGetValue(moduleName, out var moduleBase))
            return null;

        int moduleSize;
        try
        {
            using var proc = Process.GetProcessById(ctx.ProcessId);
            var mod = proc.Modules.Cast<ProcessModule>()
                .FirstOrDefault(m => string.Equals(m.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase));
            moduleSize = mod?.ModuleMemorySize ?? 0;
        }
        catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"[WindowsAutoAssemblerEngine] Failed to get module size for AOB scan: {ex.Message}"); moduleSize = 0; }

        if (moduleSize == 0) return null;

        var (pattern, mask) = ParseAobPattern(patternStr);
        return ScanRegion(ctx.ProcessHandle, moduleBase, (nuint)moduleSize, pattern, mask, ct);
    }

    /// <summary>Scan all readable committed memory for an AOB pattern.</summary>
    private static nuint? AobScanAll(ExecutionContext ctx, string patternStr, CancellationToken ct)
    {
        var (pattern, mask) = ParseAobPattern(patternStr);
        var mbiSize = Marshal.SizeOf<MemoryBasicInformation>();
        var address = (nuint)0x10000;
        var maxAddr = ctx.Is64Bit ? unchecked((nuint)0x7FFFFFFEFFFF) : (nuint)0x7FFEFFFF;

        while (address < maxAddr)
        {
            ct.ThrowIfCancellationRequested();

            if (VirtualQueryEx(ctx.ProcessHandle, (IntPtr)address, out var mbi, mbiSize) == 0)
                break;

            var regionSize = (nuint)(ulong)mbi.RegionSize;
            if (regionSize == 0) break;

            if (mbi.State == MemCommit && IsReadable(mbi.Protect))
            {
                var found = ScanRegion(ctx.ProcessHandle, address, regionSize, pattern, mask, ct);
                if (found is not null) return found;
            }

            address = (nuint)((ulong)address + (ulong)regionSize);
        }

        return null;
    }

    private static bool IsReadable(uint protect) =>
        protect is 0x02 or 0x04 or 0x08    // PAGE_READONLY, PAGE_READWRITE, PAGE_WRITECOPY
            or 0x20 or 0x40 or 0x80;       // PAGE_EXECUTE_READ, PAGE_EXECUTE_READWRITE, PAGE_EXECUTE_WRITECOPY

    private static nuint? ScanRegion(IntPtr processHandle, nuint baseAddr, nuint regionSize, byte[] pattern, bool[] mask, CancellationToken ct)
    {
        const int chunkSize = 0x10000; // 64 KB chunks
        var overlap = pattern.Length - 1;

        for (nuint offset = 0; offset < regionSize; offset += (nuint)(chunkSize - overlap))
        {
            ct.ThrowIfCancellationRequested();

            var readSize = (int)Math.Min(chunkSize, (ulong)(regionSize - offset));
            if (readSize < pattern.Length) break;

            var buffer = new byte[readSize];
            if (!ReadProcessMemory(processHandle, (IntPtr)(baseAddr + offset), buffer, readSize, out var bytesRead)
                || bytesRead < pattern.Length)
                continue;

            var matchIdx = FindPattern(buffer, bytesRead, pattern, mask);
            if (matchIdx >= 0)
                return baseAddr + offset + (nuint)matchIdx;
        }

        return null;
    }

    private static int FindPattern(byte[] data, int dataLen, byte[] pattern, bool[] mask)
    {
        var end = dataLen - pattern.Length;
        for (var i = 0; i <= end; i++)
        {
            var found = true;
            for (var j = 0; j < pattern.Length; j++)
            {
                if (mask[j] && data[i + j] != pattern[j])
                {
                    found = false;
                    break;
                }
            }

            if (found) return i;
        }

        return -1;
    }

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
