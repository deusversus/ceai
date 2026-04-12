using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using CEAISuite.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CEAISuite.Engine.Windows;

/// <summary>
/// IAT-patching speed hack engine for x64 Windows processes.
/// Intercepts timing APIs (timeGetTime, QueryPerformanceCounter, GetTickCount64) by:
/// 1. Walking the target process PE import tables to locate IAT slots
/// 2. Allocating code caves with scaling trampolines (fixed-point 16.16 arithmetic)
/// 3. Overwriting IAT entries to redirect calls through our trampolines
///
/// The trampolines capture a base value at apply time and scale the delta:
///   result = base + (real_result - base) * multiplier
///
/// Multiplier updates are lock-free: 8-byte aligned writes are atomic on x64,
/// so UpdateMultiplierAsync just overwrites the data section without suspending threads.
/// </summary>
public sealed class WindowsSpeedHackEngine : ISpeedHackEngine, IDisposable
{
    private readonly ILogger<WindowsSpeedHackEngine> _logger;
    private readonly ConcurrentDictionary<int, ProcessSpeedHackState> _states = new();
    private bool _disposed;

    public WindowsSpeedHackEngine(ILogger<WindowsSpeedHackEngine>? logger = null)
    {
        _logger = logger ?? NullLoggerFactory.Instance.CreateLogger<WindowsSpeedHackEngine>();
    }

    // ─── Constants ──────────────────────────────────────────────────

    private const uint ProcessAllAccess = 0x001FFFFF;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageExecuteReadWrite = 0x40;
    private const uint PageReadWrite = 0x04;

    private const uint TH32CS_SNAPMODULE = 0x00000008;
    private const uint TH32CS_SNAPTHREAD = 0x00000004;

    // PE constants
    private const ushort IMAGE_DOS_SIGNATURE = 0x5A4D;
    private const uint IMAGE_NT_SIGNATURE = 0x00004550;
    private const int IMAGE_DIRECTORY_ENTRY_IMPORT = 1;

    // Cave layout: 3 data slots (8 bytes each) + code per function
    private const int DataSlotSize = 8;
    private const int DataSectionSize = 3 * DataSlotSize; // real_func_addr, base_value, multiplier
    private const int MaxTrampolineCodeSize = 64; // generous upper bound per trampoline
    private const int PerFunctionCaveSize = DataSectionSize + MaxTrampolineCodeSize;

    // ─── Internal State ─────────────────────────────────────────────

    private sealed class ProcessSpeedHackState : IDisposable
    {
        public IntPtr ProcessHandle;
        public double Multiplier;
        public List<IatPatchInfo> Patches = new();
        public IntPtr CaveBase;
        public int CaveSize;

        public void Dispose()
        {
            if (ProcessHandle != IntPtr.Zero)
            {
                CloseHandle(ProcessHandle);
                ProcessHandle = IntPtr.Zero;
            }
        }
    }

    private sealed record IatPatchInfo(
        string FunctionName,
        nuint IatSlotAddress,
        nuint OriginalValue,
        nuint TrampolineAddress,
        nuint MultiplierDataAddress);

    // ─── Target function descriptors ────────────────────────────────

    private sealed record TimingFunction(string DllName, string FunctionName, bool IsQpc);

    private static readonly TimingFunction[] AllTimingFunctions =
    [
        new("winmm.dll", "timeGetTime", false),
        new("kernel32.dll", "QueryPerformanceCounter", true),
        new("kernel32.dll", "GetTickCount64", false),
    ];

    // ─── ISpeedHackEngine ───────────────────────────────────────────

    public Task<SpeedHackResult> ApplyAsync(
        int processId, double multiplier, SpeedHackOptions options,
        CancellationToken ct = default) =>
        Task.Run(() => ApplyCore(processId, multiplier, options), ct);

    public Task<SpeedHackResult> RemoveAsync(
        int processId, CancellationToken ct = default) =>
        Task.Run(() => RemoveCore(processId), ct);

    public SpeedHackState GetState(int processId)
    {
        if (_states.TryGetValue(processId, out var state))
        {
            var funcs = state.Patches.Select(p => p.FunctionName).ToList();
            return new SpeedHackState(true, state.Multiplier, funcs);
        }
        return new SpeedHackState(false, 1.0, []);
    }

    public Task<SpeedHackResult> UpdateMultiplierAsync(
        int processId, double newMultiplier, CancellationToken ct = default) =>
        Task.Run(() => UpdateMultiplierCore(processId, newMultiplier), ct);

    // ─── Core Implementation ────────────────────────────────────────

    private SpeedHackResult ApplyCore(int processId, double multiplier, SpeedHackOptions options)
    {
        if (_states.ContainsKey(processId))
            return new SpeedHackResult(false, $"Speed hack already active on process {processId}");

        if (multiplier <= 0.0)
            return new SpeedHackResult(false, "Multiplier must be positive");

        // Open process
        var hProcess = OpenProcess(ProcessAllAccess, false, processId);
        if (hProcess == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            return new SpeedHackResult(false, $"Failed to open process {processId} (Win32 error {err})");
        }

        try
        {
            // Validate x64
            if (!IsProcess64Bit(hProcess))
            {
                CloseHandle(hProcess);
                return new SpeedHackResult(false, "Speed hack only supports x64 processes");
            }

            // Determine which functions to patch
            var targets = new List<TimingFunction>();
            if (options.PatchTimeGetTime) targets.Add(AllTimingFunctions[0]);
            if (options.PatchQueryPerformanceCounter) targets.Add(AllTimingFunctions[1]);
            if (options.PatchGetTickCount64) targets.Add(AllTimingFunctions[2]);

            if (targets.Count == 0)
            {
                CloseHandle(hProcess);
                return new SpeedHackResult(false, "No timing functions selected for patching");
            }

            // Get main module base address
            var moduleBase = GetMainModuleBase(processId);
            if (moduleBase == nuint.Zero)
            {
                CloseHandle(hProcess);
                return new SpeedHackResult(false, "Failed to get main module base address");
            }

            // Allocate a single code cave for all trampolines
            int totalCaveSize = targets.Count * PerFunctionCaveSize;
            var caveBase = VirtualAllocEx(
                hProcess, IntPtr.Zero, (UIntPtr)totalCaveSize,
                MemCommit | MemReserve, PageExecuteReadWrite);
            if (caveBase == IntPtr.Zero)
            {
                CloseHandle(hProcess);
                return new SpeedHackResult(false, "Failed to allocate code cave in target process");
            }

            var patchedFunctions = new List<string>();
            var patches = new List<IatPatchInfo>();
            long fixedPointMultiplier = ToFixedPoint(multiplier);

            // For each target function: resolve, find IAT slot, build trampoline, patch
            for (int i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                var caveOffset = (nuint)(i * PerFunctionCaveSize);
                var functionCaveAddr = (nuint)(long)caveBase + caveOffset;

                try
                {
                    var patch = PatchFunction(
                        hProcess, processId, moduleBase,
                        target, functionCaveAddr, fixedPointMultiplier);

                    if (patch != null)
                    {
                        patches.Add(patch);
                        patchedFunctions.Add(target.FunctionName);
                        if (_logger.IsEnabled(LogLevel.Information))
                            _logger.LogInformation(
                                "Patched {Function} IAT slot at 0x{IatSlot:X} -> trampoline at 0x{Trampoline:X}",
                                target.FunctionName, patch.IatSlotAddress, patch.TrampolineAddress);
                    }
                    else
                    {
                        if (_logger.IsEnabled(LogLevel.Warning))
                            _logger.LogWarning("Skipped {Function}: IAT slot not found in main module", target.FunctionName);
                    }
                }
                catch (Exception ex)
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning(ex, "Failed to patch {Function}, skipping", target.FunctionName);
                }
            }

            if (patches.Count == 0)
            {
                VirtualFreeEx(hProcess, caveBase, UIntPtr.Zero, MemRelease);
                CloseHandle(hProcess);
                return new SpeedHackResult(false, "No timing functions could be patched (IAT entries not found in main module)");
            }

            var state = new ProcessSpeedHackState
            {
                ProcessHandle = hProcess,
                Multiplier = multiplier,
                Patches = patches,
                CaveBase = caveBase,
                CaveSize = totalCaveSize,
            };

            if (!_states.TryAdd(processId, state))
            {
                // Race: another thread applied first — undo everything
                RestoreAllPatches(hProcess, processId, patches);
                VirtualFreeEx(hProcess, caveBase, UIntPtr.Zero, MemRelease);
                CloseHandle(hProcess);
                return new SpeedHackResult(false, "Speed hack was applied by another thread concurrently");
            }

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation(
                    "Speed hack applied to process {ProcessId}: {Multiplier}x, {Count} function(s) patched",
                    processId, multiplier, patches.Count);

            return new SpeedHackResult(true, PatchedFunctions: patchedFunctions);
        }
        catch (Exception ex)
        {
            CloseHandle(hProcess);
            if (_logger.IsEnabled(LogLevel.Error))
                _logger.LogError(ex, "Failed to apply speed hack to process {ProcessId}", processId);
            return new SpeedHackResult(false, $"Unexpected error: {ex.Message}");
        }
    }

    private SpeedHackResult RemoveCore(int processId)
    {
        if (!_states.TryRemove(processId, out var state))
            return new SpeedHackResult(false, $"No active speed hack on process {processId}");

        try
        {
            RestoreAllPatches(state.ProcessHandle, processId, state.Patches);

            if (state.CaveBase != IntPtr.Zero)
                VirtualFreeEx(state.ProcessHandle, state.CaveBase, UIntPtr.Zero, MemRelease);

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Speed hack removed from process {ProcessId}", processId);
            return new SpeedHackResult(true);
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Error))
                _logger.LogError(ex, "Error removing speed hack from process {ProcessId}", processId);
            return new SpeedHackResult(false, $"Error during removal: {ex.Message}");
        }
        finally
        {
            state.Dispose();
        }
    }

    private SpeedHackResult UpdateMultiplierCore(int processId, double newMultiplier)
    {
        if (!_states.TryGetValue(processId, out var state))
            return new SpeedHackResult(false, $"No active speed hack on process {processId}");

        if (newMultiplier <= 0.0)
            return new SpeedHackResult(false, "Multiplier must be positive");

        long fixedPoint = ToFixedPoint(newMultiplier);
        var fpBytes = BitConverter.GetBytes(fixedPoint);

        foreach (var patch in state.Patches)
        {
            if (!WriteProcessMemory(
                    state.ProcessHandle, (IntPtr)patch.MultiplierDataAddress,
                    fpBytes, fpBytes.Length, out _))
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    var err = Marshal.GetLastWin32Error();
                    _logger.LogWarning(
                        "Failed to update multiplier for {Function} at 0x{Addr:X} (Win32 error {Error})",
                        patch.FunctionName, patch.MultiplierDataAddress, err);
                }
            }
        }

        state.Multiplier = newMultiplier;
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "Speed hack multiplier updated to {Multiplier}x for process {ProcessId}",
                newMultiplier, processId);

        return new SpeedHackResult(true);
    }

    // ─── PE IAT Walking ─────────────────────────────────────────────

    /// <summary>
    /// Patch a single timing function by walking the target PE's import table,
    /// building a trampoline, and overwriting the IAT slot.
    /// </summary>
    private IatPatchInfo? PatchFunction(
        IntPtr hProcess, int processId, nuint moduleBase,
        TimingFunction target, nuint caveAddr, long fixedPointMultiplier)
    {
        // Resolve the real function address in our own process.
        // System DLLs are mapped at the same base address across all processes on Windows.
        var dllHandle = GetModuleHandleW(target.DllName);
        if (dllHandle == IntPtr.Zero)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("GetModuleHandleW({Dll}) returned null", target.DllName);
            return null;
        }

        var realFuncAddr = GetProcAddress(dllHandle, target.FunctionName);
        if (realFuncAddr == IntPtr.Zero)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("GetProcAddress({Dll}, {Func}) returned null", target.DllName, target.FunctionName);
            return null;
        }

        // Walk the PE import table to find the IAT slot
        var iatSlot = FindIatSlot(hProcess, moduleBase, target.DllName, target.FunctionName);
        if (iatSlot == nuint.Zero)
            return null;

        // Capture the base value by calling the real function now.
        // For timeGetTime/GetTickCount64 we call them locally (same system DLLs).
        // For QPC we call QueryPerformanceCounter locally.
        long baseValue = CaptureBaseValue(target);

        // Write trampoline data section
        nuint dataAddr = caveAddr;
        nuint codeAddr = caveAddr + (nuint)DataSectionSize;
        nuint multiplierAddr = caveAddr + 2 * DataSlotSize;

        var dataBytes = new byte[DataSectionSize];
        BitConverter.GetBytes((long)(nint)realFuncAddr).CopyTo(dataBytes, 0);     // [+0x00] real_func_addr
        BitConverter.GetBytes(baseValue).CopyTo(dataBytes, 8);                     // [+0x08] base_value
        BitConverter.GetBytes(fixedPointMultiplier).CopyTo(dataBytes, 16);         // [+0x10] multiplier_fp

        if (!WriteProcessMemory(hProcess, (IntPtr)dataAddr, dataBytes, dataBytes.Length, out _))
        {
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning("Failed to write trampoline data for {Function}", target.FunctionName);
            return null;
        }

        // Build and write trampoline code
        byte[] code = target.IsQpc
            ? BuildQpcTrampoline(dataAddr)
            : BuildScalarTrampoline(dataAddr);

        if (!WriteProcessMemory(hProcess, (IntPtr)codeAddr, code, code.Length, out _))
        {
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning("Failed to write trampoline code for {Function}", target.FunctionName);
            return null;
        }

        FlushInstructionCache(hProcess, (IntPtr)codeAddr, (UIntPtr)code.Length);

        // Read original IAT value
        var origBytes = new byte[8];
        if (!ReadProcessMemory(hProcess, (IntPtr)iatSlot, origBytes, 8, out _))
        {
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning("Failed to read original IAT value for {Function}", target.FunctionName);
            return null;
        }
        nuint originalIatValue = (nuint)BitConverter.ToUInt64(origBytes);

        // Patch IAT slot: suspend threads, change protection, write, restore
        var suspended = SuspendTargetThreads(processId);
        try
        {
            if (!VirtualProtectEx(hProcess, (IntPtr)iatSlot, (UIntPtr)8, PageReadWrite, out uint oldProtect))
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning("VirtualProtectEx failed for IAT slot 0x{Addr:X}", iatSlot);
                return null;
            }

            var patchBytes = BitConverter.GetBytes((ulong)codeAddr);
            if (!WriteProcessMemory(hProcess, (IntPtr)iatSlot, patchBytes, 8, out _))
            {
                VirtualProtectEx(hProcess, (IntPtr)iatSlot, (UIntPtr)8, oldProtect, out _);
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning("Failed to write IAT patch for {Function}", target.FunctionName);
                return null;
            }

            VirtualProtectEx(hProcess, (IntPtr)iatSlot, (UIntPtr)8, oldProtect, out _);
        }
        finally
        {
            ResumeTargetThreads(suspended);
        }

        return new IatPatchInfo(
            target.FunctionName,
            iatSlot,
            originalIatValue,
            codeAddr,
            multiplierAddr);
    }

    /// <summary>
    /// Walk the PE import directory of the main module to find the IAT slot
    /// for a specific function in a specific DLL.
    /// </summary>
    private nuint FindIatSlot(IntPtr hProcess, nuint moduleBase, string dllName, string functionName)
    {
        // Read DOS header
        var dosHeaderBytes = new byte[64]; // IMAGE_DOS_HEADER is 64 bytes
        if (!ReadProcessMemory(hProcess, (IntPtr)moduleBase, dosHeaderBytes, dosHeaderBytes.Length, out _))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Failed to read DOS header at 0x{Base:X}", moduleBase);
            return nuint.Zero;
        }

        ushort dosMagic = BitConverter.ToUInt16(dosHeaderBytes, 0);
        if (dosMagic != IMAGE_DOS_SIGNATURE)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Invalid DOS signature 0x{Magic:X4} at 0x{Base:X}", dosMagic, moduleBase);
            return nuint.Zero;
        }

        int e_lfanew = BitConverter.ToInt32(dosHeaderBytes, 60); // offset 60 = e_lfanew

        // Read NT signature (4 bytes)
        var sigBytes = new byte[4];
        if (!ReadProcessMemory(hProcess, (IntPtr)(moduleBase + (nuint)e_lfanew), sigBytes, 4, out _))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Failed to read NT signature");
            return nuint.Zero;
        }

        uint ntSig = BitConverter.ToUInt32(sigBytes);
        if (ntSig != IMAGE_NT_SIGNATURE)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Invalid NT signature 0x{Sig:X8}", ntSig);
            return nuint.Zero;
        }

        // Import directory is DataDirectory[1] in the optional header.
        // For x64 PE:
        //   NT headers start at e_lfanew
        //   Signature: 4 bytes
        //   FileHeader: 20 bytes
        //   OptionalHeader starts at e_lfanew + 24
        //   DataDirectory starts at OptionalHeader + 112 (for PE32+)
        //   DataDirectory[1] is at OptionalHeader + 112 + 8 = OptionalHeader + 120
        //   Each entry: 4 bytes VirtualAddress + 4 bytes Size
        nuint dataDirOffset = moduleBase + (nuint)e_lfanew + 24 + 120;
        var importDirBytes = new byte[8];
        if (!ReadProcessMemory(hProcess, (IntPtr)dataDirOffset, importDirBytes, 8, out _))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Failed to read import directory entry");
            return nuint.Zero;
        }

        uint importRva = BitConverter.ToUInt32(importDirBytes, 0);
        uint importSize = BitConverter.ToUInt32(importDirBytes, 4);

        if (importRva == 0 || importSize == 0)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("No import directory in module at 0x{Base:X}", moduleBase);
            return nuint.Zero;
        }

        // Walk IMAGE_IMPORT_DESCRIPTOR array (20 bytes each, null-terminated)
        nuint importTableAddr = moduleBase + importRva;
        var descriptorBytes = new byte[20]; // sizeof(IMAGE_IMPORT_DESCRIPTOR)
        var nameBuffer = new byte[256];

        for (int descIdx = 0; descIdx < 512; descIdx++) // safety cap
        {
            nuint descAddr = importTableAddr + (nuint)(descIdx * 20);
            if (!ReadProcessMemory(hProcess, (IntPtr)descAddr, descriptorBytes, 20, out _))
                break;

            uint originalFirstThunk = BitConverter.ToUInt32(descriptorBytes, 0);
            uint nameRva = BitConverter.ToUInt32(descriptorBytes, 12);
            uint firstThunk = BitConverter.ToUInt32(descriptorBytes, 16);

            // Null terminator check
            if (nameRva == 0 && firstThunk == 0)
                break;

            if (nameRva == 0)
                continue;

            // Read DLL name string
            if (!ReadProcessMemory(hProcess, (IntPtr)(moduleBase + nameRva), nameBuffer, nameBuffer.Length, out _))
                continue;

            string importedDll = ExtractNullTerminatedString(nameBuffer);
            if (!importedDll.Equals(dllName, StringComparison.OrdinalIgnoreCase))
                continue;

            // Found the matching DLL. Walk the INT (OriginalFirstThunk) to find the function.
            // Use OriginalFirstThunk if available, else fall back to FirstThunk
            uint intRva = originalFirstThunk != 0 ? originalFirstThunk : firstThunk;

            var thunkBytes = new byte[8]; // 8 bytes per entry on x64
            var hintNameBuffer = new byte[512];

            for (int thunkIdx = 0; thunkIdx < 4096; thunkIdx++) // safety cap
            {
                nuint thunkAddr = moduleBase + intRva + (nuint)(thunkIdx * 8);
                if (!ReadProcessMemory(hProcess, (IntPtr)thunkAddr, thunkBytes, 8, out _))
                    break;

                ulong thunkValue = BitConverter.ToUInt64(thunkBytes);
                if (thunkValue == 0)
                    break;

                // Check for ordinal import (high bit set)
                if ((thunkValue & 0x8000000000000000UL) != 0)
                    continue;

                // Read IMAGE_IMPORT_BY_NAME: 2-byte hint + null-terminated name
                nuint hintNameAddr = moduleBase + (nuint)(thunkValue & 0x7FFFFFFFFFFFFFFFUL);
                if (!ReadProcessMemory(hProcess, (IntPtr)hintNameAddr, hintNameBuffer, hintNameBuffer.Length, out _))
                    continue;

                // Skip 2-byte hint, read name
                string importedFunc = ExtractNullTerminatedString(hintNameBuffer, 2);

                if (importedFunc.Equals(functionName, StringComparison.Ordinal))
                {
                    // IAT slot address = base + FirstThunk RVA + thunkIdx * 8
                    nuint iatSlotAddr = moduleBase + firstThunk + (nuint)(thunkIdx * 8);
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug(
                            "Found IAT slot for {Dll}!{Func} at 0x{Addr:X}",
                            dllName, functionName, iatSlotAddr);
                    return iatSlotAddr;
                }
            }
        }

        return nuint.Zero;
    }

    // ─── Trampoline Code Generation ─────────────────────────────────

    /// <summary>
    /// Build trampoline for scalar timing functions (timeGetTime, GetTickCount64).
    /// These return a 64-bit counter in RAX.
    ///
    /// Algorithm: result = base + ((real() - base) * multiplier) >> 16
    ///
    /// Uses mov rbx,imm64 for absolute data addressing (no RIP-relative complexity).
    /// </summary>
    private static byte[] BuildScalarTrampoline(nuint dataAddr)
    {
        using var ms = new System.IO.MemoryStream(64);
        var w = new System.IO.BinaryWriter(ms);

        // push rbx                         ; 53
        w.Write((byte)0x53);
        // sub rsp, 0x20                    ; 48 83 EC 20 (shadow space)
        w.Write(new byte[] { 0x48, 0x83, 0xEC, 0x20 });
        // mov rbx, <dataAddr imm64>        ; 48 BB XX XX XX XX XX XX XX XX
        w.Write(new byte[] { 0x48, 0xBB });
        w.Write((ulong)dataAddr);
        // call qword [rbx]                 ; FF 13 (call [rbx+0] = real func)
        w.Write(new byte[] { 0xFF, 0x13 });
        // mov rcx, [rbx + 0x08]            ; 48 8B 4B 08 (base_value)
        w.Write(new byte[] { 0x48, 0x8B, 0x4B, 0x08 });
        // sub rax, rcx                     ; 48 29 C8
        w.Write(new byte[] { 0x48, 0x29, 0xC8 });
        // imul rax, [rbx + 0x10]           ; 48 0F AF 43 10 (multiplier)
        w.Write(new byte[] { 0x48, 0x0F, 0xAF, 0x43, 0x10 });
        // shr rax, 0x10                    ; 48 C1 E8 10
        w.Write(new byte[] { 0x48, 0xC1, 0xE8, 0x10 });
        // add rax, rcx                     ; 48 01 C8
        w.Write(new byte[] { 0x48, 0x01, 0xC8 });
        // add rsp, 0x20                    ; 48 83 C4 20
        w.Write(new byte[] { 0x48, 0x83, 0xC4, 0x20 });
        // pop rbx                          ; 5B
        w.Write((byte)0x5B);
        // ret                              ; C3
        w.Write((byte)0xC3);

        w.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// Build trampoline for QueryPerformanceCounter (takes LARGE_INTEGER* in RCX, returns BOOL).
    /// Calls real QPC, then scales the value at [RCX].
    ///
    /// Algorithm: *ptr = base + ((*ptr - base) * multiplier) >> 16
    /// </summary>
    private static byte[] BuildQpcTrampoline(nuint dataAddr)
    {
        using var ms = new System.IO.MemoryStream(64);
        var w = new System.IO.BinaryWriter(ms);

        // push rbx                         ; 53
        w.Write((byte)0x53);
        // push rdi                         ; 57
        w.Write((byte)0x57);
        // sub rsp, 0x28                    ; 48 83 EC 28 (shadow space + alignment)
        w.Write(new byte[] { 0x48, 0x83, 0xEC, 0x28 });
        // mov rdi, rcx                     ; 48 89 CF (save LARGE_INTEGER* pointer)
        w.Write(new byte[] { 0x48, 0x89, 0xCF });
        // mov rbx, <dataAddr imm64>        ; 48 BB XX XX XX XX XX XX XX XX
        w.Write(new byte[] { 0x48, 0xBB });
        w.Write((ulong)dataAddr);
        // mov rcx, rdi                     ; 48 89 F9 (restore RCX for the call)
        w.Write(new byte[] { 0x48, 0x89, 0xF9 });
        // call qword [rbx]                 ; FF 13 (call real QPC)
        w.Write(new byte[] { 0xFF, 0x13 });
        // mov rax, [rdi]                   ; 48 8B 07 (load counter value)
        w.Write(new byte[] { 0x48, 0x8B, 0x07 });
        // mov rcx, [rbx + 0x08]            ; 48 8B 4B 08 (base_value)
        w.Write(new byte[] { 0x48, 0x8B, 0x4B, 0x08 });
        // sub rax, rcx                     ; 48 29 C8 (delta)
        w.Write(new byte[] { 0x48, 0x29, 0xC8 });
        // imul rax, [rbx + 0x10]           ; 48 0F AF 43 10 (multiplier)
        w.Write(new byte[] { 0x48, 0x0F, 0xAF, 0x43, 0x10 });
        // shr rax, 0x10                    ; 48 C1 E8 10
        w.Write(new byte[] { 0x48, 0xC1, 0xE8, 0x10 });
        // add rax, rcx                     ; 48 01 C8
        w.Write(new byte[] { 0x48, 0x01, 0xC8 });
        // mov [rdi], rax                   ; 48 89 07 (write back scaled value)
        w.Write(new byte[] { 0x48, 0x89, 0x07 });
        // mov eax, 1                       ; B8 01 00 00 00 (return TRUE)
        w.Write(new byte[] { 0xB8, 0x01, 0x00, 0x00, 0x00 });
        // add rsp, 0x28                    ; 48 83 C4 28
        w.Write(new byte[] { 0x48, 0x83, 0xC4, 0x28 });
        // pop rdi                          ; 5F
        w.Write((byte)0x5F);
        // pop rbx                          ; 5B
        w.Write((byte)0x5B);
        // ret                              ; C3
        w.Write((byte)0xC3);

        w.Flush();
        return ms.ToArray();
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    /// <summary>Convert a double multiplier to 16.16 fixed-point representation.</summary>
    private static long ToFixedPoint(double multiplier) =>
        (long)(multiplier * 65536.0);

    /// <summary>Capture a base timing value by calling the real function locally.</summary>
    private static long CaptureBaseValue(TimingFunction target)
    {
        if (target.IsQpc)
        {
            QueryPerformanceCounter(out long qpcValue);
            return qpcValue;
        }

        if (target.FunctionName == "timeGetTime")
            return timeGetTime();

        // GetTickCount64
        return (long)GetTickCount64();
    }

    private static bool IsProcess64Bit(IntPtr hProcess)
    {
        // IsWow64Process2 available on Windows 10 1511+
        // If the process is WOW64, processMachine != IMAGE_FILE_MACHINE_UNKNOWN (0)
        // For native x64, processMachine == 0 (IMAGE_FILE_MACHINE_UNKNOWN)
        if (IsWow64Process2(hProcess, out ushort processMachine, out _))
        {
            // processMachine == 0 means native process (not running under WOW64)
            // processMachine == 0x014C (IMAGE_FILE_MACHINE_I386) means 32-bit on 64-bit
            return processMachine == 0;
        }

        // Fallback: IsWow64Process
        if (IsWow64Process(hProcess, out bool isWow64))
            return !isWow64;

        return false; // can't determine, assume not 64-bit for safety
    }

    /// <summary>Get the base address of the main module using CreateToolhelp32Snapshot.</summary>
    private nuint GetMainModuleBase(int processId)
    {
        var snapshot = CreateToolhelp32Snapshot(
            TH32CS_SNAPMODULE, (uint)processId);
        if (snapshot == IntPtr.Zero || snapshot == (IntPtr)(-1))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("CreateToolhelp32Snapshot(SNAPMODULE) failed for PID {Pid}", processId);
            return nuint.Zero;
        }

        try
        {
            var entry = new MODULEENTRY32W { dwSize = (uint)Marshal.SizeOf<MODULEENTRY32W>() };
            if (Module32FirstW(snapshot, ref entry))
            {
                return (nuint)(long)entry.modBaseAddr;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Module32FirstW returned false for PID {Pid}", processId);
            return nuint.Zero;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    /// <summary>Restore all IAT patches to their original values.</summary>
    private void RestoreAllPatches(IntPtr hProcess, int processId, List<IatPatchInfo> patches)
    {
        var suspended = SuspendTargetThreads(processId);
        try
        {
            foreach (var patch in patches)
            {
                var origBytes = BitConverter.GetBytes((ulong)patch.OriginalValue);
                if (VirtualProtectEx(hProcess, (IntPtr)patch.IatSlotAddress, (UIntPtr)8, PageReadWrite, out uint oldProtect))
                {
                    if (!WriteProcessMemory(hProcess, (IntPtr)patch.IatSlotAddress, origBytes, 8, out _))
                    {
                        if (_logger.IsEnabled(LogLevel.Warning))
                            _logger.LogWarning(
                                "Failed to restore IAT slot for {Function} at 0x{Addr:X}",
                                patch.FunctionName, patch.IatSlotAddress);
                    }
                    VirtualProtectEx(hProcess, (IntPtr)patch.IatSlotAddress, (UIntPtr)8, oldProtect, out _);
                }
            }
        }
        finally
        {
            ResumeTargetThreads(suspended);
        }
    }

    /// <summary>Extract a null-terminated ASCII string from a byte buffer.</summary>
    private static string ExtractNullTerminatedString(byte[] buffer, int offset = 0)
    {
        int end = offset;
        while (end < buffer.Length && buffer[end] != 0)
            end++;
        return System.Text.Encoding.ASCII.GetString(buffer, offset, end - offset);
    }

    // ─── Thread Suspension ──────────────────────────────────────────

    /// <summary>
    /// Suspend all threads in the target process.
    /// Follows the same pattern as WindowsCodeCaveEngine.SuspendTargetThreads.
    /// </summary>
    private List<IntPtr> SuspendTargetThreads(int processId)
    {
        var suspended = new List<IntPtr>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
        if (snapshot == IntPtr.Zero || snapshot == (IntPtr)(-1))
            return suspended;

        try
        {
            var entry = new THREADENTRY32 { dwSize = (uint)Marshal.SizeOf<THREADENTRY32>() };
            if (!Thread32First(snapshot, ref entry))
                return suspended;

            do
            {
                if (entry.th32OwnerProcessID != processId)
                    continue;

                var threadHandle = OpenThread(
                    0x0002 /* THREAD_SUSPEND_RESUME */,
                    false, entry.th32ThreadID);
                if (threadHandle == IntPtr.Zero)
                    continue;

                if (SuspendThread(threadHandle) != unchecked((uint)-1))
                {
                    suspended.Add(threadHandle);
                }
                else
                {
                    CloseHandle(threadHandle);
                }
            } while (Thread32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Suspended {Count} thread(s) in process {Pid}", suspended.Count, processId);
        return suspended;
    }

    /// <summary>Resume all previously suspended threads and close their handles.</summary>
    private static void ResumeTargetThreads(List<IntPtr> suspendedThreads)
    {
        foreach (var handle in suspendedThreads)
        {
            _ = ResumeThread(handle);
            _ = CloseHandle(handle);
        }
    }

    // ─── IDisposable ────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var kvp in _states)
        {
            try
            {
                RestoreAllPatches(kvp.Value.ProcessHandle, kvp.Key, kvp.Value.Patches);
                if (kvp.Value.CaveBase != IntPtr.Zero)
                    VirtualFreeEx(kvp.Value.ProcessHandle, kvp.Value.CaveBase, UIntPtr.Zero, MemRelease);
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning(ex, "Error during speed hack cleanup for PID {Pid}", kvp.Key);
            }
            finally
            {
                kvp.Value.Dispose();
            }
        }
        _states.Clear();
    }

    // ─── P/Invoke ───────────────────────────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(
        IntPtr processHandle, IntPtr baseAddress, UIntPtr size, uint allocationType, uint protection);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFreeEx(
        IntPtr processHandle, IntPtr baseAddress, UIntPtr size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtectEx(
        IntPtr processHandle, IntPtr baseAddress, UIntPtr size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        IntPtr processHandle, IntPtr baseAddress, [Out] byte[] buffer, int size, out int bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(
        IntPtr processHandle, IntPtr baseAddress, byte[] buffer, int size, out int bytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushInstructionCache(IntPtr processHandle, IntPtr baseAddress, UIntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true,
        BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(
        IntPtr processHandle, out ushort processMachine, out ushort nativeMachine);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process(IntPtr hProcess, out bool wow64Process);

    // Toolhelp32 — modules
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Module32FirstW(IntPtr hSnapshot, ref MODULEENTRY32W lpme);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Module32NextW(IntPtr hSnapshot, ref MODULEENTRY32W lpme);

    // Toolhelp32 — threads
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Thread32First(IntPtr hSnapshot, ref THREADENTRY32 lpte);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Thread32Next(IntPtr hSnapshot, ref THREADENTRY32 lpte);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint desiredAccess, bool inheritHandle, uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SuspendThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr hThread);

    // Timing functions (called locally to capture base values)
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryPerformanceCounter(out long lpPerformanceCount);

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    [DllImport("winmm.dll")]
    private static extern uint timeGetTime();

    // ─── Structs ────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MODULEENTRY32W
    {
        public uint dwSize;
        public uint th32ModuleID;
        public uint th32ProcessID;
        public uint GlbcntUsage;
        public uint ProccntUsage;
        public IntPtr modBaseAddr;
        public uint modBaseSize;
        public IntPtr hModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct THREADENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ThreadID;
        public uint th32OwnerProcessID;
        public int tpBasePri;
        public int tpDeltaPri;
        public uint dwFlags;
    }
}
