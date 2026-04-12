using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using CEAISuite.Engine.Abstractions;
using Iced.Intel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CEAISuite.Engine.Windows;

/// <summary>
/// Inline-hooking speed hack engine for x64 Windows processes.
/// Intercepts timing APIs (timeGetTime, QueryPerformanceCounter, GetTickCount64) by:
/// 1. Resolving the real function address in the target (system DLLs share addresses)
/// 2. Saving the function prologue (14 bytes) and writing a JMP to our trampoline
/// 3. The trampoline calls the original via a "gateway" (stolen bytes + JMP back),
///    captures the result, and scales the delta with fixed-point arithmetic.
///
/// This catches ALL callers regardless of how they resolved the function address
/// (IAT, GetProcAddress, direct call, etc.), matching Cheat Engine's approach.
///
/// Cave layout per function (256 bytes):
///   [+0x00] Data section: real_func_addr(8) + base_value(8) + multiplier_fp(8) = 24 bytes
///   [+0x18] Gateway: stolen prologue bytes + JMP back to original+14 = ~28 bytes
///   [+0x40] Trampoline: scaling code that calls gateway = ~64 bytes
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

    // Inline hook: 14 bytes for `mov rax, imm64; jmp rax`
    private const int HookPrologueSize = 14;

    // Cave layout per function
    private const int DataSectionSize = 24;      // 3 x 8-byte slots
    private const int GatewayOffset = 0x18;      // after data
    private const int GatewayMaxSize = 40;       // stolen bytes + jmp back
    private const int TrampolineOffset = 0x40;   // after gateway
    private const int TrampolineMaxSize = 80;    // scaling code
    private const int PerFunctionCaveSize = 0xC0; // 192 bytes total (generous)

    // ─── Internal State ─────────────────────────────────────────────

    private sealed class ProcessSpeedHackState : IDisposable
    {
        public IntPtr ProcessHandle;
        public double Multiplier;
        public List<InlineHookInfo> Hooks = new();
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

    private sealed record InlineHookInfo(
        string FunctionName,
        nuint OriginalFunctionAddress,
        byte[] StolenBytes,
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
            var funcs = state.Hooks.Select(p => p.FunctionName).ToList();
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

        var hProcess = OpenProcess(ProcessAllAccess, false, processId);
        if (hProcess == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            return new SpeedHackResult(false, $"Failed to open process {processId} (Win32 error {err})");
        }

        try
        {
            if (!IsProcess64Bit(hProcess))
            {
                CloseHandle(hProcess);
                return new SpeedHackResult(false, "Speed hack only supports x64 processes");
            }

            var targets = new List<TimingFunction>();
            if (options.PatchTimeGetTime) targets.Add(AllTimingFunctions[0]);
            if (options.PatchQueryPerformanceCounter) targets.Add(AllTimingFunctions[1]);
            if (options.PatchGetTickCount64) targets.Add(AllTimingFunctions[2]);

            if (targets.Count == 0)
            {
                CloseHandle(hProcess);
                return new SpeedHackResult(false, "No timing functions selected for patching");
            }

            // Resolve first target to determine near-allocation anchor address
            var anchorDll = GetModuleHandleW(targets[0].DllName);
            var anchorAddr = anchorDll != IntPtr.Zero
                ? (nuint)(long)GetProcAddress(anchorDll, targets[0].FunctionName)
                : nuint.Zero;

            // Allocate cave within ±2GB of target for RIP-relative instruction relocation
            int totalCaveSize = targets.Count * PerFunctionCaveSize;
            var caveBase = AllocateNearTarget(hProcess, anchorAddr, totalCaveSize);
            if (caveBase == IntPtr.Zero)
            {
                CloseHandle(hProcess);
                return new SpeedHackResult(false, "Failed to allocate code cave in target process");
            }

            var patchedFunctions = new List<string>();
            var hooks = new List<InlineHookInfo>();
            long fixedPointMultiplier = ToFixedPoint(multiplier);

            for (int i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                var caveAddr = (nuint)(long)caveBase + (nuint)(i * PerFunctionCaveSize);

                try
                {
                    var hook = HookFunction(hProcess, processId, target, caveAddr, fixedPointMultiplier);
                    if (hook != null)
                    {
                        hooks.Add(hook);
                        patchedFunctions.Add(target.FunctionName);
                        if (_logger.IsEnabled(LogLevel.Information))
                            _logger.LogInformation(
                                "Hooked {Function} at 0x{Addr:X} -> trampoline at 0x{Trampoline:X}",
                                target.FunctionName, hook.OriginalFunctionAddress, hook.TrampolineAddress);
                    }
                    else
                    {
                        if (_logger.IsEnabled(LogLevel.Warning))
                            _logger.LogWarning("Skipped {Function}: could not resolve or hook", target.FunctionName);
                    }
                }
                catch (Exception ex)
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning(ex, "Failed to hook {Function}, skipping", target.FunctionName);
                }
            }

            if (hooks.Count == 0)
            {
                VirtualFreeEx(hProcess, caveBase, UIntPtr.Zero, MemRelease);
                CloseHandle(hProcess);
                return new SpeedHackResult(false, "No timing functions could be hooked");
            }

            var state = new ProcessSpeedHackState
            {
                ProcessHandle = hProcess,
                Multiplier = multiplier,
                Hooks = hooks,
                CaveBase = caveBase,
                CaveSize = totalCaveSize,
            };

            if (!_states.TryAdd(processId, state))
            {
                RestoreAllHooks(hProcess, processId, hooks);
                VirtualFreeEx(hProcess, caveBase, UIntPtr.Zero, MemRelease);
                CloseHandle(hProcess);
                return new SpeedHackResult(false, "Speed hack was applied by another thread concurrently");
            }

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation(
                    "Speed hack applied to process {ProcessId}: {Multiplier}x, {Count} function(s) hooked",
                    processId, multiplier, hooks.Count);

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
            RestoreAllHooks(state.ProcessHandle, processId, state.Hooks);

            // Brief delay before freeing the cave — any thread that entered the trampoline
            // just before the hook was removed will have exited via the safe JMP redirect
            // (written by RestoreAllHooks) within a few milliseconds.
            if (state.CaveBase != IntPtr.Zero)
            {
                Thread.Sleep(50);
                VirtualFreeEx(state.ProcessHandle, state.CaveBase, UIntPtr.Zero, MemRelease);
            }

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

        foreach (var hook in state.Hooks)
        {
            if (!WriteProcessMemory(
                    state.ProcessHandle, (IntPtr)hook.MultiplierDataAddress,
                    fpBytes, fpBytes.Length, out _))
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    var err = Marshal.GetLastWin32Error();
                    _logger.LogWarning(
                        "Failed to update multiplier for {Function} at 0x{Addr:X} (Win32 error {Error})",
                        hook.FunctionName, hook.MultiplierDataAddress, err);
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

    // ─── Inline Hooking ─────────────────────────────────────────────

    /// <summary>
    /// Hook a single timing function using inline JMP detour.
    /// 1. Resolve real function address (system DLLs share addresses across processes)
    /// 2. Read and save the first 14 bytes of the function (stolen prologue)
    /// 3. Write a "gateway" in the cave: stolen bytes + JMP back to original+14
    /// 4. Write the scaling trampoline that calls the gateway
    /// 5. Overwrite the function prologue with: mov rax, trampoline; jmp rax
    /// </summary>
    private InlineHookInfo? HookFunction(
        IntPtr hProcess, int processId,
        TimingFunction target, nuint caveAddr, long fixedPointMultiplier)
    {
        // Resolve the real function address. System DLLs are at the same base in all processes.
        var dllHandle = GetModuleHandleW(target.DllName);
        if (dllHandle == IntPtr.Zero)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("GetModuleHandleW({Dll}) returned null", target.DllName);
            return null;
        }

        var realFuncAddr = (nuint)(long)GetProcAddress(dllHandle, target.FunctionName);
        if (realFuncAddr == nuint.Zero)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("GetProcAddress({Dll}, {Func}) returned null", target.DllName, target.FunctionName);
            return null;
        }

        // Follow forwarding stubs: many kernel32 functions are just `jmp [rip+disp32]`
        // pointing to the real implementation in kernelbase or ntdll.
        // We must hook the actual function body, not the stub.
        realFuncAddr = FollowForwardingStubs(hProcess, realFuncAddr, target.FunctionName);

        // Read enough bytes to decode instruction boundaries (32 bytes is generous for 14-byte minimum)
        const int readSize = 32;
        var prologueBuffer = new byte[readSize];
        if (!ReadProcessMemory(hProcess, (IntPtr)realFuncAddr, prologueBuffer, readSize, out _))
        {
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning("Failed to read prologue of {Function} at 0x{Addr:X}", target.FunctionName, realFuncAddr);
            return null;
        }

        // Use Iced decoder to find safe steal length at instruction boundaries
        int stealLength;
        try
        {
            stealLength = WindowsCodeCaveEngine.CalculateSafeStealLength(prologueBuffer, HookPrologueSize, realFuncAddr);
        }
        catch (InvalidOperationException ex)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning("Cannot hook {Function}: {Message}", target.FunctionName, ex.Message);
            return null;
        }

        // Extract exactly the bytes we'll steal (at instruction boundary)
        var stolenBytes = new byte[stealLength];
        Array.Copy(prologueBuffer, stolenBytes, stealLength);

        // Capture base timing value before hooking
        long baseValue = CaptureBaseValue(target);

        // ── Write cave sections ──

        nuint dataAddr = caveAddr;                              // [+0x00] data
        nuint gatewayAddr = caveAddr + GatewayOffset;           // [+0x18] gateway
        nuint trampolineAddr = caveAddr + TrampolineOffset;     // [+0x40] trampoline
        nuint multiplierAddr = caveAddr + 2 * 8;                // [+0x10] multiplier slot

        // Write data section: real_func_addr, base_value, multiplier
        var dataBytes = new byte[DataSectionSize];
        BitConverter.GetBytes((long)realFuncAddr).CopyTo(dataBytes, 0);
        BitConverter.GetBytes(baseValue).CopyTo(dataBytes, 8);
        BitConverter.GetBytes(fixedPointMultiplier).CopyTo(dataBytes, 16);

        if (!WriteProcessMemory(hProcess, (IntPtr)dataAddr, dataBytes, dataBytes.Length, out _))
        {
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning("Failed to write data section for {Function}", target.FunctionName);
            return null;
        }

        // Relocate RIP-relative instructions in stolen bytes to the gateway address
        byte[] relocatedStolen;
        try
        {
            relocatedStolen = WindowsCodeCaveEngine.RelocateRipRelativeInstructions(
                stolenBytes, realFuncAddr, gatewayAddr);
        }
        catch (InvalidOperationException ex)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning("RIP relocation failed for {Function}: {Message}", target.FunctionName, ex.Message);
            return null;
        }

        // Write gateway: relocated stolen bytes + absolute JMP back to original + stealLength
        var gateway = BuildGateway(relocatedStolen, realFuncAddr + (nuint)stealLength);
        if (!WriteProcessMemory(hProcess, (IntPtr)gatewayAddr, gateway, gateway.Length, out _))
        {
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning("Failed to write gateway for {Function}", target.FunctionName);
            return null;
        }

        // Write trampoline: calls gateway, scales result
        byte[] trampoline = target.IsQpc
            ? BuildQpcTrampoline(dataAddr, gatewayAddr)
            : BuildScalarTrampoline(dataAddr, gatewayAddr);

        if (!WriteProcessMemory(hProcess, (IntPtr)trampolineAddr, trampoline, trampoline.Length, out _))
        {
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning("Failed to write trampoline for {Function}", target.FunctionName);
            return null;
        }

        FlushInstructionCache(hProcess, (IntPtr)caveAddr, (UIntPtr)PerFunctionCaveSize);

        // ── Install the hook: overwrite function prologue with JMP to trampoline ──
        // We overwrite exactly stealLength bytes (>= 14), padding with NOPs
        var jmpBytes = BuildAbsoluteJmp(trampolineAddr, stealLength);

        var suspended = SuspendTargetThreads(processId);
        try
        {
            if (!VirtualProtectEx(hProcess, (IntPtr)realFuncAddr, (UIntPtr)stealLength, PageExecuteReadWrite, out uint oldProtect))
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning("VirtualProtectEx failed for {Function} at 0x{Addr:X}", target.FunctionName, realFuncAddr);
                return null;
            }

            if (!WriteProcessMemory(hProcess, (IntPtr)realFuncAddr, jmpBytes, jmpBytes.Length, out _))
            {
                VirtualProtectEx(hProcess, (IntPtr)realFuncAddr, (UIntPtr)stealLength, oldProtect, out _);
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning("Failed to write JMP hook for {Function}", target.FunctionName);
                return null;
            }

            VirtualProtectEx(hProcess, (IntPtr)realFuncAddr, (UIntPtr)stealLength, oldProtect, out _);
            FlushInstructionCache(hProcess, (IntPtr)realFuncAddr, (UIntPtr)stealLength);
        }
        finally
        {
            ResumeTargetThreads(suspended);
        }

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Hooked {Function}: stole {StealLen} bytes, gateway at 0x{Gateway:X}",
                target.FunctionName, stealLength, gatewayAddr);

        return new InlineHookInfo(
            target.FunctionName,
            realFuncAddr,
            stolenBytes,  // Original bytes for restoration
            trampolineAddr,
            multiplierAddr);
    }

    // ─── Code Generation ────────────────────────────────────────────

    /// <summary>
    /// Build the "gateway" — stolen prologue bytes followed by an absolute JMP
    /// back to original_function + 14. This lets the trampoline call the original
    /// function as if it were never hooked.
    /// </summary>
    private static byte[] BuildGateway(byte[] stolenBytes, nuint returnAddr)
    {
        using var ms = new System.IO.MemoryStream(GatewayMaxSize);
        var w = new System.IO.BinaryWriter(ms);

        // Write stolen prologue bytes
        w.Write(stolenBytes);

        // JMP to returnAddr (original + 14):  mov rax, imm64; jmp rax
        w.Write(new byte[] { 0x48, 0xB8 }); // mov rax,
        w.Write((ulong)returnAddr);
        w.Write(new byte[] { 0xFF, 0xE0 }); // jmp rax

        w.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// Build an absolute JMP: mov rax, imm64; jmp rax (12 bytes core, padded with NOPs to totalLength).
    /// totalLength must be >= 12 (HookPrologueSize is 14 but instruction boundary may be larger).
    /// </summary>
    private static byte[] BuildAbsoluteJmp(nuint targetAddr, int totalLength = HookPrologueSize)
    {
        var bytes = new byte[totalLength];
        // Fill with NOPs first
        Array.Fill(bytes, (byte)0x90);
        // mov rax, imm64 (10 bytes)
        bytes[0] = 0x48; bytes[1] = 0xB8;
        BitConverter.GetBytes((ulong)targetAddr).CopyTo(bytes, 2);
        // jmp rax (2 bytes)
        bytes[10] = 0xFF; bytes[11] = 0xE0;
        // Remaining bytes are already NOPs
        return bytes;
    }

    /// <summary>
    /// Trampoline for scalar timing functions (timeGetTime, GetTickCount64).
    /// Calls gateway to get real result in RAX, then scales:
    ///   result = base + ((real - base) * multiplier) >> 16
    /// </summary>
    private static byte[] BuildScalarTrampoline(nuint dataAddr, nuint gatewayAddr)
    {
        using var ms = new System.IO.MemoryStream(TrampolineMaxSize);
        var w = new System.IO.BinaryWriter(ms);

        // push rbx
        w.Write((byte)0x53);
        // sub rsp, 0x20  (shadow space)
        w.Write(new byte[] { 0x48, 0x83, 0xEC, 0x20 });
        // mov rbx, dataAddr
        w.Write(new byte[] { 0x48, 0xBB });
        w.Write((ulong)dataAddr);
        // mov rax, gatewayAddr
        w.Write(new byte[] { 0x48, 0xB8 });
        w.Write((ulong)gatewayAddr);
        // call rax  (call gateway → returns real result in RAX)
        w.Write(new byte[] { 0xFF, 0xD0 });
        // mov rcx, [rbx + 0x08]  (base_value)
        w.Write(new byte[] { 0x48, 0x8B, 0x4B, 0x08 });
        // sub rax, rcx  (delta = real - base)
        w.Write(new byte[] { 0x48, 0x29, 0xC8 });
        // imul rax, [rbx + 0x10]  (delta * multiplier_fp)
        w.Write(new byte[] { 0x48, 0x0F, 0xAF, 0x43, 0x10 });
        // sar rax, 0x10  (arithmetic shift right by 16 for signed deltas)
        w.Write(new byte[] { 0x48, 0xC1, 0xF8, 0x10 });
        // add rax, rcx  (result = base + scaled_delta)
        w.Write(new byte[] { 0x48, 0x01, 0xC8 });
        // add rsp, 0x20
        w.Write(new byte[] { 0x48, 0x83, 0xC4, 0x20 });
        // pop rbx
        w.Write((byte)0x5B);
        // ret
        w.Write((byte)0xC3);

        w.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// Trampoline for QueryPerformanceCounter (takes LARGE_INTEGER* in RCX, returns BOOL).
    /// Calls gateway, then scales the value at [RCX]:
    ///   *ptr = base + ((*ptr - base) * multiplier) >> 16
    /// </summary>
    private static byte[] BuildQpcTrampoline(nuint dataAddr, nuint gatewayAddr)
    {
        using var ms = new System.IO.MemoryStream(TrampolineMaxSize);
        var w = new System.IO.BinaryWriter(ms);

        // push rbx
        w.Write((byte)0x53);
        // push rdi
        w.Write((byte)0x57);
        // sub rsp, 0x28  (shadow space + alignment)
        w.Write(new byte[] { 0x48, 0x83, 0xEC, 0x28 });
        // mov rdi, rcx  (save LARGE_INTEGER* pointer)
        w.Write(new byte[] { 0x48, 0x89, 0xCF });
        // mov rbx, dataAddr
        w.Write(new byte[] { 0x48, 0xBB });
        w.Write((ulong)dataAddr);
        // mov rcx, rdi  (restore RCX for the call)
        w.Write(new byte[] { 0x48, 0x89, 0xF9 });
        // mov rax, gatewayAddr
        w.Write(new byte[] { 0x48, 0xB8 });
        w.Write((ulong)gatewayAddr);
        // call rax  (call gateway → real QPC fills [rdi])
        w.Write(new byte[] { 0xFF, 0xD0 });
        // mov rax, [rdi]  (load counter value)
        w.Write(new byte[] { 0x48, 0x8B, 0x07 });
        // mov rcx, [rbx + 0x08]  (base_value)
        w.Write(new byte[] { 0x48, 0x8B, 0x4B, 0x08 });
        // sub rax, rcx  (delta)
        w.Write(new byte[] { 0x48, 0x29, 0xC8 });
        // imul rax, [rbx + 0x10]  (multiplier)
        w.Write(new byte[] { 0x48, 0x0F, 0xAF, 0x43, 0x10 });
        // sar rax, 0x10  (arithmetic shift right)
        w.Write(new byte[] { 0x48, 0xC1, 0xF8, 0x10 });
        // add rax, rcx
        w.Write(new byte[] { 0x48, 0x01, 0xC8 });
        // mov [rdi], rax  (write back scaled value)
        w.Write(new byte[] { 0x48, 0x89, 0x07 });
        // mov eax, 1  (return TRUE)
        w.Write(new byte[] { 0xB8, 0x01, 0x00, 0x00, 0x00 });
        // add rsp, 0x28
        w.Write(new byte[] { 0x48, 0x83, 0xC4, 0x28 });
        // pop rdi
        w.Write((byte)0x5F);
        // pop rbx
        w.Write((byte)0x5B);
        // ret
        w.Write((byte)0xC3);

        w.Flush();
        return ms.ToArray();
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Follow forwarding stubs (jmp [rip+disp32]) to the real function body.
    /// Many kernel32 functions are just one-instruction stubs that forward to
    /// kernelbase.dll or ntdll.dll. We need to hook the actual implementation,
    /// not the forwarding stub.
    ///
    /// Patterns detected:
    ///   48 FF 25 XX XX XX XX  = REX.W jmp qword [rip + disp32]  (7 bytes)
    ///   FF 25 XX XX XX XX     = jmp qword [rip + disp32]         (6 bytes)
    /// </summary>
    private nuint FollowForwardingStubs(IntPtr hProcess, nuint funcAddr, string funcName)
    {
        // Read first 7 bytes to check for forwarding stub
        var buf = new byte[7];
        if (!ReadProcessMemory(hProcess, (IntPtr)funcAddr, buf, buf.Length, out _))
            return funcAddr;

        nuint targetPtrAddr;
        int instrLen;

        if (buf[0] == 0x48 && buf[1] == 0xFF && buf[2] == 0x25)
        {
            // REX.W jmp qword [rip + disp32] — 7 bytes
            int disp32 = BitConverter.ToInt32(buf, 3);
            instrLen = 7;
            targetPtrAddr = (nuint)((long)funcAddr + instrLen + disp32);
        }
        else if (buf[0] == 0xFF && buf[1] == 0x25)
        {
            // jmp qword [rip + disp32] — 6 bytes
            int disp32 = BitConverter.ToInt32(buf, 2);
            instrLen = 6;
            targetPtrAddr = (nuint)((long)funcAddr + instrLen + disp32);
        }
        else
        {
            // Not a forwarding stub — hook here
            return funcAddr;
        }

        // Read the 8-byte pointer at the target location
        var ptrBuf = new byte[8];
        if (!ReadProcessMemory(hProcess, (IntPtr)targetPtrAddr, ptrBuf, 8, out _))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Could not read forwarding target pointer for {Func}", funcName);
            return funcAddr;
        }

        var realTarget = (nuint)BitConverter.ToUInt64(ptrBuf);
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "Followed forwarding stub for {Func}: 0x{Stub:X} -> 0x{Real:X}",
                funcName, funcAddr, realTarget);

        // Recurse once in case of double-forwarding (kernel32 -> kernelbase -> ntdll)
        return FollowForwardingStubs(hProcess, realTarget, funcName);
    }

    private static long ToFixedPoint(double multiplier) =>
        (long)(multiplier * 65536.0);

    // ±2GB range for RIP-relative addressing
    private const long NearAllocRange = 0x7FFF0000L;
    private const long AllocGranularity = 0x10000L; // 64KB Windows allocation granularity

    /// <summary>
    /// Allocate memory within ±2GB of target address for RIP-relative instruction relocation.
    /// Falls back to any-address allocation if the entire range is exhausted.
    /// </summary>
    private IntPtr AllocateNearTarget(IntPtr hProcess, nuint targetAddress, int size)
    {
        if (targetAddress == nuint.Zero)
            return VirtualAllocEx(hProcess, IntPtr.Zero, (UIntPtr)size, MemCommit | MemReserve, PageExecuteReadWrite);

        var target = (long)targetAddress;
        var rangeStart = Math.Max(0x10000L, target - NearAllocRange);
        var rangeEnd = target + NearAllocRange;

        for (var probe = rangeStart; probe < rangeEnd; probe += AllocGranularity)
        {
            var result = VirtualAllocEx(hProcess, checked((IntPtr)probe), checked((UIntPtr)size),
                MemCommit | MemReserve, PageExecuteReadWrite);
            if (result != IntPtr.Zero)
                return result;
        }

        if (_logger.IsEnabled(LogLevel.Warning))
            _logger.LogWarning("Could not allocate within ±2GB of 0x{Target:X}, falling back to any-address", targetAddress);
        return VirtualAllocEx(hProcess, IntPtr.Zero, (UIntPtr)size, MemCommit | MemReserve, PageExecuteReadWrite);
    }

    private static long CaptureBaseValue(TimingFunction target)
    {
        if (target.IsQpc)
        {
            QueryPerformanceCounter(out long qpcValue);
            return qpcValue;
        }

        if (target.FunctionName == "timeGetTime")
            return timeGetTime();

        return (long)GetTickCount64();
    }

    private static bool IsProcess64Bit(IntPtr hProcess)
    {
        if (IsWow64Process2(hProcess, out ushort processMachine, out _))
            return processMachine == 0;
        if (IsWow64Process(hProcess, out bool isWow64))
            return !isWow64;
        return false;
    }

    /// <summary>Restore all inline hooks by writing back the stolen prologue bytes.</summary>
    /// <remarks>
    /// We do NOT suspend threads during removal. The suspend/resume cycle was causing
    /// game input threads to hang permanently (suspend count mismatch or RIP landing
    /// in freed cave memory on resume). Instead:
    ///
    /// 1. Patch each trampoline to JMP directly to the original function (safe redirect)
    /// 2. Restore the original prologue bytes via WriteProcessMemory (atomic for small aligned writes)
    /// 3. Flush instruction cache
    ///
    /// Between steps 1 and 2, any thread calling the hooked function hits the trampoline
    /// which now just JMPs to the original — completely safe. After step 2, all future
    /// calls go directly to the restored original. No thread suspension needed.
    /// </remarks>
    private void RestoreAllHooks(IntPtr hProcess, int processId, List<InlineHookInfo> hooks)
    {
        foreach (var hook in hooks)
        {
            // Step 1: Redirect the trampoline to the original function.
            // Any in-flight call through the hook now safely reaches the original.
            var safeJmp = BuildAbsoluteJmp(hook.OriginalFunctionAddress);
            WriteProcessMemory(hProcess, (IntPtr)hook.TrampolineAddress, safeJmp, safeJmp.Length, out _);
            FlushInstructionCache(hProcess, (IntPtr)hook.TrampolineAddress, (UIntPtr)safeJmp.Length);
        }

        // Brief pause to let any thread that just entered the trampoline exit through the safe redirect
        Thread.Sleep(50);

        // Step 2: Restore original prologue bytes
        foreach (var hook in hooks)
        {
            var len = (UIntPtr)hook.StolenBytes.Length;
            if (VirtualProtectEx(hProcess, (IntPtr)hook.OriginalFunctionAddress,
                    len, PageExecuteReadWrite, out uint oldProtect))
            {
                if (!WriteProcessMemory(hProcess, (IntPtr)hook.OriginalFunctionAddress,
                        hook.StolenBytes, hook.StolenBytes.Length, out _))
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning("Failed to restore {Function} at 0x{Addr:X}",
                            hook.FunctionName, hook.OriginalFunctionAddress);
                }
                VirtualProtectEx(hProcess, (IntPtr)hook.OriginalFunctionAddress,
                    len, oldProtect, out _);
                FlushInstructionCache(hProcess, (IntPtr)hook.OriginalFunctionAddress, len);
            }
        }
    }

    // ─── Thread Suspension ──────────────────────────────────────────

    private static List<IntPtr> SuspendTargetThreads(int processId)
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

                var threadHandle = OpenThread(0x0002, false, entry.th32ThreadID);
                if (threadHandle == IntPtr.Zero)
                    continue;

                if (SuspendThread(threadHandle) != unchecked((uint)-1))
                    suspended.Add(threadHandle);
                else
                    CloseHandle(threadHandle);
            } while (Thread32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return suspended;
    }

    private static void ResumeTargetThreads(List<IntPtr> suspendedThreads)
    {
        foreach (var handle in suspendedThreads)
        {
            // ResumeThread returns the previous suspend count.
            // We called SuspendThread exactly once, so we call ResumeThread exactly once.
            // If SuspendThread incremented from 0→1, ResumeThread decrements 1→0 (running).
            // If SuspendThread incremented from N→N+1 (already suspended), ResumeThread
            // decrements N+1→N (still suspended, same as before we touched it). This is correct.
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
                RestoreAllHooks(kvp.Value.ProcessHandle, kvp.Key, kvp.Value.Hooks);
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

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
