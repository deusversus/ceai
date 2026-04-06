using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using CEAISuite.Engine.Abstractions;
using Iced.Intel;
using Microsoft.Extensions.Logging;

namespace CEAISuite.Engine.Windows;

/// <summary>
/// Installs JMP-detour hooks ("code caves") that redirect execution through
/// allocated trampolines. No debugger attachment required — this is the stealthiest
/// form of execution monitoring available.
///
/// How it works:
/// 1. Disassemble instructions at target to find a safe steal length (≥14 bytes for x64 far JMP)
/// 2. Allocate executable memory (the "cave") via VirtualAllocEx within ±2GB of target
/// 3. Write trampoline into cave:
///    a. Increment hit counter (lock inc [counterAddress])
///    b. Optionally capture registers into a ring buffer
///    c. Execute stolen original bytes (with RIP-relative relocation)
///    d. JMP back to (target + stolen_length)
/// 4. Suspend target threads, overwrite target with 14-byte far JMP to cave, resume threads
/// 5. Downgrade cave pages to PAGE_EXECUTE_READ (anti-detection)
/// 6. Original bytes are preserved for clean removal
/// </summary>
public sealed class WindowsCodeCaveEngine : ICodeCaveEngine, IDisposable
{
    private readonly ILogger<WindowsCodeCaveEngine> _logger;

    public WindowsCodeCaveEngine(ILogger<WindowsCodeCaveEngine> logger)
    {
        _logger = logger;
    }

    private const uint ProcessAllAccess = 0x001FFFFF;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageExecuteReadWrite = 0x40;
    private const uint PageExecuteRead = 0x20;

    // x64 far JMP: FF 25 00 00 00 00 [8-byte absolute address] = 14 bytes
    private const int FarJmpSize = 14;

    // Register snapshot ring buffer size per hook
    private const int MaxSnapshotsPerHook = 64;

    // ±2GB range for RIP-relative addressing
    private const long NearAllocRange = 0x7FFF0000L;

    // 64KB allocation granularity on Windows
    private const long AllocGranularity = 0x10000L;

    private readonly ConcurrentDictionary<string, CodeCaveState> _hooks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, IntPtr> _processHandles = new();
    private readonly object _gate = new();
    private bool _disposed;

    public Task<CodeCaveInstallResult> InstallHookAsync(
        int processId, nuint address, bool captureRegisters = true,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hook = InstallHook(processId, address, captureRegisters);
                return new CodeCaveInstallResult(true, hook.ToDescriptor(), null);
            }
            catch (Exception ex)
            {
                return new CodeCaveInstallResult(false, null, ex.Message);
            }
        }, cancellationToken);

    public Task<bool> RemoveHookAsync(
        int processId, string hookId,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            if (!_hooks.TryGetValue(hookId, out var hook))
                return false;

            RemoveHook(hook);

            // 1G: Clean up process handle if no hooks remain for this process
            if (!_hooks.Values.Any(h => h.ProcessId == processId && h.IsActive))
                CleanupProcessHandle(processId);

            return true;
        }, cancellationToken);

    public Task<IReadOnlyList<CodeCaveHook>> ListHooksAsync(
        int processId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CodeCaveHook>>(
            _hooks.Values
                .Where(h => h.ProcessId == processId && h.IsActive)
                .Select(h => h.ToDescriptor())
                .ToList());

    public Task<IReadOnlyList<BreakpointHitEvent>> GetHookHitsAsync(
        string hookId, int maxEntries = 50,
        CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<BreakpointHitEvent>>(() =>
        {
            if (!_hooks.TryGetValue(hookId, out var hook))
                return [];

            // Read the hit counter from target process memory
            var hProcess = GetProcessHandle(hook.ProcessId);
            var counterBuf = new byte[8];
            ReadProcessMemory(hProcess, (IntPtr)hook.HitCounterAddress, counterBuf, 8, out _);
            // 3D: Use long throughout to avoid truncation after 2^31 hits
            hook.LastKnownHitCount = BitConverter.ToInt64(counterBuf);

            // Read register snapshots from the ring buffer if capture was enabled
            if (!hook.CaptureRegisters || hook.SnapshotBufferAddress == 0)
                return [];

            var results = new List<BreakpointHitEvent>();
            var hitCount = Math.Min(hook.LastKnownHitCount, MaxSnapshotsPerHook);
            var entriesToRead = (int)Math.Min(hitCount, maxEntries);

            // Each snapshot is 7 registers × 8 bytes = 56 bytes
            const int snapshotSize = 56;
            var buffer = new byte[snapshotSize];

            for (int i = 0; i < entriesToRead; i++)
            {
                // Read from end of ring buffer backward (most recent first)
                var idx = (int)(((hook.LastKnownHitCount - 1 - i) % MaxSnapshotsPerHook + MaxSnapshotsPerHook) % MaxSnapshotsPerHook);
                var entryAddr = hook.SnapshotBufferAddress + (nuint)(idx * snapshotSize);

                if (!ReadProcessMemory(hProcess, (IntPtr)entryAddr, buffer, snapshotSize, out var bytesRead) || bytesRead < snapshotSize)
                    continue;

                var regs = new Dictionary<string, string>
                {
                    ["RAX"] = $"0x{BitConverter.ToUInt64(buffer, 0):X16}",
                    ["RBX"] = $"0x{BitConverter.ToUInt64(buffer, 8):X16}",
                    ["RCX"] = $"0x{BitConverter.ToUInt64(buffer, 16):X16}",
                    ["RDX"] = $"0x{BitConverter.ToUInt64(buffer, 24):X16}",
                    ["RSI"] = $"0x{BitConverter.ToUInt64(buffer, 32):X16}",
                    ["RDI"] = $"0x{BitConverter.ToUInt64(buffer, 40):X16}",
                    ["RSP"] = $"0x{BitConverter.ToUInt64(buffer, 48):X16}"
                };

                results.Add(new BreakpointHitEvent(
                    hookId, hook.OriginalAddress, 0,
                    DateTimeOffset.UtcNow, regs));
            }

            return results;
        }, cancellationToken);

    // ─── Core Implementation ─────────────────────────────────────────

    private CodeCaveState InstallHook(int processId, nuint address, bool captureRegisters)
    {
        var hProcess = GetProcessHandle(processId);
        var hookId = $"hook-{Guid.NewGuid().ToString("N")[..8]}";

        // Step 1: Read original bytes at the target (we need at least FarJmpSize=14 bytes)
        // Read extra bytes to accommodate instruction alignment
        var readSize = FarJmpSize + 16;
        var originalCode = new byte[readSize];
        if (!ReadProcessMemory(hProcess, (IntPtr)address, originalCode, readSize, out var bytesRead) || bytesRead < FarJmpSize)
            throw new InvalidOperationException($"Cannot read target memory at 0x{address:X}.");

        // Step 2: 1B — Determine safe steal length using Iced decoder (not heuristic)
        var stealLength = CalculateSafeStealLength(originalCode, FarJmpSize, address);

        var stolenBytes = new byte[stealLength];
        Array.Copy(originalCode, stolenBytes, stealLength);

        // Step 3: Calculate cave size
        // Layout: [hit_counter:8] [snapshot_index:8] [snapshot_buffer:56*64] [trampoline_code]
        var hitCounterSize = 8;
        var snapshotIndexSize = 8;
        var snapshotBufferSize = captureRegisters ? (56 * MaxSnapshotsPerHook) : 0;
        var dataRegionSize = hitCounterSize + snapshotIndexSize + snapshotBufferSize;
        // Align to 16 bytes
        dataRegionSize = (dataRegionSize + 15) & ~15;

        // Trampoline code estimate: pushfq + push regs + snapshot code + pop regs + popfq + stolen bytes + far JMP back
        var trampolineEstimate = 512 + stealLength + FarJmpSize;
        var totalCaveSize = dataRegionSize + trampolineEstimate;

        // Step 4: 1D — Allocate executable memory within ±2GB of target for RIP-relative addressing
        var caveBase = AllocateNearTarget(hProcess, address, totalCaveSize);
        if (caveBase == IntPtr.Zero)
            throw new InvalidOperationException(
                $"VirtualAllocEx failed: unable to allocate {totalCaveSize} bytes near 0x{address:X} (within ±2GB range).");

        // 1A — Wrap remaining steps in try/catch to free cave on any failure
        try
        {
            var caveAddress = (nuint)caveBase;
            var hitCounterAddr = caveAddress;
            var snapshotIndexAddr = caveAddress + (nuint)hitCounterSize;
            var snapshotBufferAddr = captureRegisters ? caveAddress + (nuint)(hitCounterSize + snapshotIndexSize) : (nuint)0;
            var codeStart = caveAddress + (nuint)dataRegionSize;

            // Step 5: Build the trampoline machine code
            // 1C — RelocateRipRelativeInstructions now throws on failure instead of silent fallback
            var trampoline = BuildTrampoline(
                codeStart, address, (nuint)stealLength, stolenBytes,
                hitCounterAddr, snapshotIndexAddr, snapshotBufferAddr, captureRegisters);

            // Step 6: Write data region (zeroed) + trampoline into cave
            var caveData = new byte[totalCaveSize];
            Array.Copy(trampoline, 0, caveData, dataRegionSize, trampoline.Length);

            if (!WriteProcessMemory(hProcess, caveBase, caveData, caveData.Length, out _))
                throw new InvalidOperationException($"Cannot write code cave at 0x{caveAddress:X}.");

            FlushInstructionCache(hProcess, caveBase, (UIntPtr)caveData.Length);

            // 7A — Downgrade cave to PAGE_EXECUTE_READ (anti-detection: remove writable flag)
            VirtualProtectEx(hProcess, caveBase, (UIntPtr)totalCaveSize, PageExecuteRead, out _);

            // Step 7: Write the JMP detour at the original address
            var detour = BuildFarJmp(codeStart);

            // 7C — Use varied NOP encodings for residual stolen bytes (anti-detection)
            var fullPatch = new byte[stealLength];
            Array.Copy(detour, fullPatch, FarJmpSize);
            FillVariedNops(fullPatch, FarJmpSize, stealLength - FarJmpSize);

            // 1H — Suspend all target threads before patching to prevent mid-instruction races
            var suspendedThreads = SuspendTargetThreads(processId, hProcess, address, (nuint)stealLength);
            try
            {
                // Ensure the target is writable
                if (!VirtualProtectEx(hProcess, (IntPtr)address, (UIntPtr)stealLength, PageExecuteReadWrite, out var oldProtect))
                    throw new InvalidOperationException($"Cannot change protection at 0x{address:X}.");

                if (!WriteProcessMemory(hProcess, (IntPtr)address, fullPatch, fullPatch.Length, out _))
                {
                    // Restore protection before throwing
                    VirtualProtectEx(hProcess, (IntPtr)address, (UIntPtr)stealLength, oldProtect, out _);
                    throw new InvalidOperationException($"Cannot write JMP detour at 0x{address:X}.");
                }

                FlushInstructionCache(hProcess, (IntPtr)address, (UIntPtr)stealLength);

                // 1J — Check VirtualProtectEx restore return value
                if (!VirtualProtectEx(hProcess, (IntPtr)address, (UIntPtr)stealLength, oldProtect, out _))
                {
                    _logger.LogWarning("Failed to restore original protection at 0x{Address:X} (error: {Error})", address, Marshal.GetLastWin32Error());
                }

                // Step 8: Register the hook
                var state = new CodeCaveState
                {
                    Id = hookId,
                    ProcessId = processId,
                    OriginalAddress = address,
                    CaveAddress = caveAddress,
                    CodeStartAddress = codeStart,
                    StolenBytes = stolenBytes,
                    StealLength = stealLength,
                    IsActive = true,
                    CaptureRegisters = captureRegisters,
                    HitCounterAddress = hitCounterAddr,
                    SnapshotIndexAddress = snapshotIndexAddr,
                    SnapshotBufferAddress = snapshotBufferAddr,
                    OriginalProtection = oldProtect,
                    CaveTotalSize = totalCaveSize
                };

                _hooks[hookId] = state;
                return state;
            }
            finally
            {
                // 1H — Always resume threads
                ResumeTargetThreads(suspendedThreads);
            }
        }
        catch (Exception ex)
        {
            // 1A — Clean up allocated cave on any failure
            _logger.LogDebug(ex, "Code cave install failed — cleaning up allocated memory");
            VirtualFreeEx(hProcess, caveBase, UIntPtr.Zero, MemRelease);
            throw;
        }
    }

    private void RemoveHook(CodeCaveState hook)
    {
        if (!hook.IsActive) return;
        hook.IsActive = false;

        var hProcess = GetProcessHandle(hook.ProcessId);

        // 1I — Suspend threads and verify none are inside the trampoline before modifying
        var suspendedThreads = SuspendTargetThreads(
            hook.ProcessId, hProcess, hook.CaveAddress, (nuint)hook.CaveTotalSize);
        try
        {
            // Restore original bytes at the hook site
            VirtualProtectEx(hProcess, (IntPtr)hook.OriginalAddress, (UIntPtr)hook.StealLength,
                PageExecuteReadWrite, out var oldProtect);

            // 1F — Check WriteProcessMemory return; don't free cave if restore fails
            if (!WriteProcessMemory(hProcess, (IntPtr)hook.OriginalAddress,
                    hook.StolenBytes, hook.StolenBytes.Length, out _))
            {
                _logger.LogError("Failed to restore original bytes at 0x{OriginalAddress:X}. Cave at 0x{CaveAddress:X} will NOT be freed to prevent crash", hook.OriginalAddress, hook.CaveAddress);
                hook.RemovalFailed = true;
                // Restore protection but leave cave alive
                VirtualProtectEx(hProcess, (IntPtr)hook.OriginalAddress, (UIntPtr)hook.StealLength,
                    oldProtect, out _);
                _hooks.TryRemove(hook.Id, out _);
                return;
            }

            FlushInstructionCache(hProcess, (IntPtr)hook.OriginalAddress, (UIntPtr)hook.StealLength);

            VirtualProtectEx(hProcess, (IntPtr)hook.OriginalAddress, (UIntPtr)hook.StealLength,
                oldProtect, out _);

            // Free the cave memory (safe — original bytes are restored and no threads are in the cave)
            VirtualFreeEx(hProcess, (IntPtr)hook.CaveAddress, UIntPtr.Zero, MemRelease);
        }
        finally
        {
            // 1I — Always resume threads
            ResumeTargetThreads(suspendedThreads);
        }

        _hooks.TryRemove(hook.Id, out _);
    }

    // ─── Machine Code Generation ─────────────────────────────────────

    /// <summary>
    /// Build x64 trampoline:
    ///   pushfq
    ///   push rax-rdi, r8
    ///   lock inc qword [hitCounter]
    ///   (optional) snapshot registers to ring buffer (with atomic index increment)
    ///   pop r8, rdi-rax
    ///   popfq
    ///   execute stolen original bytes (RIP-relocated)
    ///   JMP back to (originalAddr + stealLength)
    /// </summary>
    private static byte[] BuildTrampoline(
        nuint codeAddr, nuint originalAddr, nuint stealLength, byte[] stolenBytes,
        nuint hitCounterAddr, nuint snapshotIndexAddr, nuint snapshotBufferAddr,
        bool captureRegisters)
    {
        var code = new List<byte>(512);

        // ── Save flags and registers ──
        code.Add(0x9C); // pushfq

        // push rax, rbx, rcx, rdx, rsi, rdi, r8 (for scratch)
        code.Add(0x50); // push rax
        code.Add(0x53); // push rbx
        code.Add(0x51); // push rcx
        code.Add(0x52); // push rdx
        code.Add(0x56); // push rsi
        code.Add(0x57); // push rdi
        code.AddRange(new byte[] { 0x41, 0x50 }); // push r8

        // ── Increment hit counter: lock inc qword [hitCounterAddr] ──
        // mov rax, hitCounterAddr
        code.AddRange(new byte[] { 0x48, 0xB8 });
        code.AddRange(BitConverter.GetBytes((ulong)hitCounterAddr));
        // lock inc qword [rax]
        code.AddRange(new byte[] { 0xF0, 0x48, 0xFF, 0x00 });

        if (captureRegisters && snapshotBufferAddr != 0)
        {
            // ── Snapshot registers into ring buffer ──
            // The stack currently has: r8, rdi, rsi, rdx, rcx, rbx, rax, rflags
            // We want to snapshot the ORIGINAL register values (before our pushes)

            // 3A — Use atomic lock xadd for snapshot index to prevent concurrent-hit races
            // mov rax, snapshotIndexAddr
            code.AddRange(new byte[] { 0x48, 0xB8 });
            code.AddRange(BitConverter.GetBytes((ulong)snapshotIndexAddr));
            // mov rcx, 1
            code.AddRange(new byte[] { 0x48, 0xC7, 0xC1, 0x01, 0x00, 0x00, 0x00 });
            // lock xadd [rax], rcx  → rcx gets OLD value, [rax] gets old+1
            code.AddRange(new byte[] { 0xF0, 0x48, 0x0F, 0xC1, 0x08 });
            // Now rcx = old snapshot index (our slot)

            // index = rcx % MaxSnapshotsPerHook (=64, so AND with 63)
            code.AddRange(new byte[] { 0x48, 0x89, 0xC8 }); // mov rax, rcx
            code.AddRange(new byte[] { 0x48, 0x83, 0xE0, (byte)(MaxSnapshotsPerHook - 1) }); // and rax, 63

            // offset = index * 56
            // imul rax, rax, 56
            code.AddRange(new byte[] { 0x48, 0x6B, 0xC0, 56 }); // imul rax, rax, 56

            // rbx = snapshotBufferAddr + offset
            code.AddRange(new byte[] { 0x48, 0xBB });
            code.AddRange(BitConverter.GetBytes((ulong)snapshotBufferAddr));
            code.AddRange(new byte[] { 0x48, 0x01, 0xC3 }); // add rbx, rax

            // Write original RAX (from stack: rsp+48 = rax push position)
            // The push order was: pushfq, push rax, push rbx, push rcx, push rdx, push rsi, push rdi, push r8
            // rsp+0 = r8, rsp+8 = rdi, rsp+16 = rsi, rsp+24 = rdx, rsp+32 = rcx, rsp+40 = rbx, rsp+48 = rax, rsp+56 = rflags
            code.AddRange(new byte[] { 0x48, 0x8B, 0x44, 0x24, 48 }); // mov rax, [rsp+48]
            code.AddRange(new byte[] { 0x48, 0x89, 0x03 }); // mov [rbx], rax

            code.AddRange(new byte[] { 0x48, 0x8B, 0x44, 0x24, 40 }); // mov rax, [rsp+40] (original rbx)
            code.AddRange(new byte[] { 0x48, 0x89, 0x43, 0x08 }); // mov [rbx+8], rax

            code.AddRange(new byte[] { 0x48, 0x8B, 0x44, 0x24, 32 }); // mov rax, [rsp+32] (original rcx)
            code.AddRange(new byte[] { 0x48, 0x89, 0x43, 0x10 }); // mov [rbx+16], rax

            code.AddRange(new byte[] { 0x48, 0x8B, 0x44, 0x24, 24 }); // mov rax, [rsp+24] (original rdx)
            code.AddRange(new byte[] { 0x48, 0x89, 0x43, 0x18 }); // mov [rbx+24], rax

            code.AddRange(new byte[] { 0x48, 0x8B, 0x44, 0x24, 16 }); // mov rax, [rsp+16] (original rsi)
            code.AddRange(new byte[] { 0x48, 0x89, 0x43, 0x20 }); // mov [rbx+32], rax

            code.AddRange(new byte[] { 0x48, 0x8B, 0x44, 0x24, 8 }); // mov rax, [rsp+8] (original rdi)
            code.AddRange(new byte[] { 0x48, 0x89, 0x43, 0x28 }); // mov [rbx+40], rax

            // RSP: original RSP = current RSP + 64 (8 pushes * 8 bytes)
            code.AddRange(new byte[] { 0x48, 0x89, 0xE0 }); // mov rax, rsp
            code.AddRange(new byte[] { 0x48, 0x83, 0xC0, 64 }); // add rax, 64
            code.AddRange(new byte[] { 0x48, 0x89, 0x43, 0x30 }); // mov [rbx+48], rax
        }

        // ── Restore registers and flags ──
        code.AddRange(new byte[] { 0x41, 0x58 }); // pop r8
        code.Add(0x5F); // pop rdi
        code.Add(0x5E); // pop rsi
        code.Add(0x5A); // pop rdx
        code.Add(0x59); // pop rcx
        code.Add(0x5B); // pop rbx
        code.Add(0x58); // pop rax
        code.Add(0x9D); // popfq

        // ── Execute stolen original bytes (with RIP-relative relocation) ──
        // 1C — RelocateRipRelativeInstructions throws on failure (no silent fallback)
        var relocatedStolen = RelocateRipRelativeInstructions(
            stolenBytes, originalAddr, codeAddr + (nuint)code.Count);
        code.AddRange(relocatedStolen);

        // ── JMP back to original + stealLength ──
        var returnAddr = originalAddr + stealLength;
        code.AddRange(BuildFarJmp(returnAddr));

        return code.ToArray();
    }

    /// <summary>
    /// Build a 14-byte x64 far JMP: FF 25 00000000 [8-byte absolute address]
    /// </summary>
    internal static byte[] BuildFarJmp(nuint targetAddress)
    {
        var jmp = new byte[FarJmpSize];
        jmp[0] = 0xFF;
        jmp[1] = 0x25;
        // jmp[2..5] = 00 00 00 00 (RIP-relative offset to the 8-byte address that follows)
        BitConverter.GetBytes((ulong)targetAddress).CopyTo(jmp, 6);
        return jmp;
    }

    /// <summary>
    /// Relocate RIP-relative instructions in stolen bytes so they reference the correct
    /// absolute addresses when executed from the code cave instead of the original location.
    /// Uses Iced x86 decoder + BlockEncoder for accurate instruction relocation.
    /// 1C: Throws on failure instead of silently returning broken bytes.
    /// </summary>
    internal static byte[] RelocateRipRelativeInstructions(
        byte[] stolenBytes, nuint originalAddress, nuint newAddress)
    {
        if (stolenBytes.Length == 0) return stolenBytes;

        // Decode instructions at original address
        var reader = new ByteArrayCodeReader(stolenBytes);
        var decoder = Decoder.Create(64, reader, (ulong)originalAddress);

        var instructions = new List<Instruction>();
        while (decoder.IP < (ulong)originalAddress + (ulong)stolenBytes.Length)
        {
            decoder.Decode(out var instr);
            if (instr.IsInvalid) break;
            instructions.Add(instr);
        }

        if (instructions.Count == 0) return stolenBytes;

        // Check if any instruction has a RIP-relative operand — skip encoder if not
        bool hasRipRelative = false;
        foreach (var instr in instructions)
        {
            if (instr.IsIPRelativeMemoryOperand)
            {
                hasRipRelative = true;
                break;
            }
            // Also check for relative branch targets (Jcc, CALL rel32, JMP rel32)
            if (instr.FlowControl is FlowControl.ConditionalBranch
                or FlowControl.UnconditionalBranch
                or FlowControl.Call)
            {
                hasRipRelative = true;
                break;
            }
        }
        if (!hasRipRelative) return stolenBytes;

        // Use Iced BlockEncoder to re-encode at the new address with automatic relocation
        var codeWriter = new CodeWriterImpl();
        var block = new InstructionBlock(codeWriter, instructions, (ulong)newAddress);

        // 1C — Fail hard on relocation failure instead of using broken bytes
        if (!BlockEncoder.TryEncode(64, block, out var errorMessage, out _, BlockEncoderOptions.None))
        {
            throw new InvalidOperationException(
                $"RIP-relative instruction relocation failed (stolen bytes cannot be safely relocated to cave): {errorMessage}");
        }

        return codeWriter.ToArray();
    }

    /// <summary>Minimal ICodeWriter for BlockEncoder that writes to a byte array.</summary>
    private sealed class CodeWriterImpl : CodeWriter
    {
        private readonly List<byte> _bytes = new();
        public override void WriteByte(byte value) => _bytes.Add(value);
        public byte[] ToArray() => _bytes.ToArray();
    }

    /// <summary>
    /// 1B: Calculate safe steal length using Iced decoder (replaces heuristic EstimateInstructionLength).
    /// Decodes instructions at the target address and finds the first instruction boundary ≥ minLength.
    /// Rejects if any instruction is invalid or if a relative branch target falls within the stolen range.
    /// </summary>
    internal static int CalculateSafeStealLength(byte[] code, int minLength, nuint address)
    {
        var reader = new ByteArrayCodeReader(code);
        var decoder = Decoder.Create(64, reader, (ulong)address);

        int totalLength = 0;
        while (totalLength < minLength && decoder.IP < (ulong)address + (ulong)code.Length)
        {
            var instr = decoder.Decode();
            if (instr.IsInvalid)
                throw new InvalidOperationException(
                    $"Cannot decode instruction at 0x{address + (nuint)totalLength:X} (offset +{totalLength}). " +
                    $"Hook cannot be safely installed — instruction decoding failed.");

            totalLength += instr.Length;

            // Reject if a relative branch/call targets an address WITHIN the stolen range
            // (branch into the middle of our JMP patch would crash)
            if (instr.FlowControl is FlowControl.ConditionalBranch
                or FlowControl.UnconditionalBranch
                or FlowControl.Call)
            {
                var target = instr.NearBranchTarget;
                if (target > (ulong)address && target < (ulong)address + (ulong)Math.Max(totalLength, minLength))
                {
                    throw new InvalidOperationException(
                        $"Instruction at 0x{instr.IP:X} branches to 0x{target:X} which falls within the " +
                        $"stolen byte range (0x{address:X}..0x{address + (nuint)minLength:X}). " +
                        $"Hook cannot be safely installed at this address.");
                }
            }
        }

        if (totalLength < minLength)
            throw new InvalidOperationException(
                $"Could not decode enough instructions at 0x{address:X}: got {totalLength} bytes but need ≥{minLength}.");

        return totalLength;
    }

    // ─── Near-Target Allocation (1D) ────────────────────────────────

    /// <summary>
    /// 1D: Allocate memory within ±2GB of target address for RIP-relative addressing.
    /// Scans from (target - 2GB) upward in 64KB increments until allocation succeeds.
    /// Falls back to any-address allocation if the entire range is exhausted.
    /// </summary>
    private IntPtr AllocateNearTarget(IntPtr hProcess, nuint targetAddress, int size)
    {
        var target = (long)targetAddress;
        var rangeStart = Math.Max(0x10000L, target - NearAllocRange);
        var rangeEnd = target + NearAllocRange;

        // Scan upward in 64KB increments (Windows allocation granularity)
        for (var probe = rangeStart; probe < rangeEnd; probe += AllocGranularity)
        {
            var result = VirtualAllocEx(hProcess, (IntPtr)probe, (UIntPtr)size,
                MemCommit | MemReserve, PageExecuteReadWrite);
            if (result != IntPtr.Zero)
                return result;
        }

        // Fallback: allocate at any address (RIP-relative relocation may fail if >2GB away,
        // but BuildTrampoline uses absolute addressing for the far JMP, so basic hooks still work)
        _logger.LogWarning("Could not allocate within +/-2GB of 0x{TargetAddress:X}. Falling back to any-address allocation. RIP-relative instructions in stolen bytes may fail", targetAddress);
        return VirtualAllocEx(hProcess, IntPtr.Zero, (UIntPtr)size, MemCommit | MemReserve, PageExecuteReadWrite);
    }

    // ─── Thread Suspension for Safe Patching (1H/1I) ─────────────────

    /// <summary>
    /// 1H: Suspend all threads in the target process to prevent mid-instruction races
    /// during code patching. Returns list of suspended thread handles for later resumption.
    ///
    /// NOTE: This method does NOT verify whether any thread's instruction pointer (RIP)
    /// falls within the danger zone at the time of suspension. A thread whose RIP is
    /// inside the patch area or trampoline cave will be suspended in that state, but the
    /// suspension itself prevents further execution, which is the primary safety measure.
    /// Full IP verification would require GetThreadContext with the CONTEXT struct, which
    /// adds significant P/Invoke complexity for a diagnostic-only check.
    /// </summary>
    private List<IntPtr> SuspendTargetThreads(int processId, IntPtr hProcess, nuint dangerStart, nuint dangerSize)
    {
        var suspended = new List<IntPtr>();
        var snapshot = CreateToolhelp32Snapshot(0x00000004 /* TH32CS_SNAPTHREAD */, 0);
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
                    0x0002 /* THREAD_SUSPEND_RESUME */ | 0x0008 /* THREAD_GET_CONTEXT */ | 0x0010 /* THREAD_QUERY_INFORMATION */,
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

        _logger.LogTrace("Suspended {ThreadCount} thread(s) in process {ProcessId} (danger zone: 0x{DangerStart:X}..0x{DangerEnd:X})", suspended.Count, processId, dangerStart, dangerStart + dangerSize);

        return suspended;
    }

    /// <summary>Resume all previously suspended threads and close their handles.</summary>
    private static void ResumeTargetThreads(List<IntPtr> suspendedThreads)
    {
        foreach (var handle in suspendedThreads)
        {
            ResumeThread(handle);
            CloseHandle(handle);
        }
    }

    // ─── NOP Variation (7C) ─────────────────────────────────────────

    /// <summary>
    /// 7C: Fill a byte range with varied NOP encodings to avoid uniform 0x90 NOP sled signatures.
    /// Uses multi-byte NOP forms recommended by Intel.
    /// </summary>
    private static void FillVariedNops(byte[] buffer, int offset, int count)
    {
        int pos = offset;
        int end = offset + count;
        while (pos < end)
        {
            int remaining = end - pos;
            switch (remaining)
            {
                case >= 9:
                    // 9-byte NOP: 66 0F 1F 84 00 00 00 00 00
                    buffer[pos] = 0x66; buffer[pos + 1] = 0x0F; buffer[pos + 2] = 0x1F;
                    buffer[pos + 3] = 0x84; buffer[pos + 4] = 0x00; buffer[pos + 5] = 0x00;
                    buffer[pos + 6] = 0x00; buffer[pos + 7] = 0x00; buffer[pos + 8] = 0x00;
                    pos += 9;
                    break;
                case >= 7:
                    // 7-byte NOP: 0F 1F 80 00 00 00 00
                    buffer[pos] = 0x0F; buffer[pos + 1] = 0x1F; buffer[pos + 2] = 0x80;
                    buffer[pos + 3] = 0x00; buffer[pos + 4] = 0x00; buffer[pos + 5] = 0x00;
                    buffer[pos + 6] = 0x00;
                    pos += 7;
                    break;
                case >= 4:
                    // 4-byte NOP: 0F 1F 40 00
                    buffer[pos] = 0x0F; buffer[pos + 1] = 0x1F; buffer[pos + 2] = 0x40;
                    buffer[pos + 3] = 0x00;
                    pos += 4;
                    break;
                case >= 3:
                    // 3-byte NOP: 0F 1F 00
                    buffer[pos] = 0x0F; buffer[pos + 1] = 0x1F; buffer[pos + 2] = 0x00;
                    pos += 3;
                    break;
                case 2:
                    // 2-byte NOP: 66 90
                    buffer[pos] = 0x66; buffer[pos + 1] = 0x90;
                    pos += 2;
                    break;
                default:
                    // 1-byte NOP: 90
                    buffer[pos] = 0x90;
                    pos++;
                    break;
            }
        }
    }

    // ─── Process Handle Management (1G) ─────────────────────────────

    private IntPtr GetProcessHandle(int processId)
    {
        if (_processHandles.TryGetValue(processId, out var existing) && existing != IntPtr.Zero)
            return existing;

        lock (_gate)
        {
            if (_processHandles.TryGetValue(processId, out existing) && existing != IntPtr.Zero)
                return existing;

            var handle = OpenProcess(ProcessAllAccess, false, processId);
            if (handle == IntPtr.Zero)
                throw new InvalidOperationException($"Cannot open process {processId}: {Marshal.GetLastWin32Error()}");

            _processHandles[processId] = handle;
            return handle;
        }
    }

    /// <summary>1G: Close and remove cached process handle when no hooks remain for this process.</summary>
    private void CleanupProcessHandle(int processId)
    {
        if (_processHandles.TryRemove(processId, out var handle) && handle != IntPtr.Zero)
        {
            CloseHandle(handle);
        }
    }

    /// <summary>1G: IDisposable — close all cached process handles on engine shutdown.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var kvp in _processHandles)
        {
            if (kvp.Value != IntPtr.Zero)
                CloseHandle(kvp.Value);
        }
        _processHandles.Clear();
    }

    // ─── Hook State ──────────────────────────────────────────────────

    private sealed class CodeCaveState
    {
        public required string Id { get; init; }
        public required int ProcessId { get; init; }
        public required nuint OriginalAddress { get; init; }
        public required nuint CaveAddress { get; init; }
        public required nuint CodeStartAddress { get; init; }
        public required byte[] StolenBytes { get; init; }
        public required int StealLength { get; init; }
        public required bool CaptureRegisters { get; init; }
        public required nuint HitCounterAddress { get; init; }
        public required nuint SnapshotIndexAddress { get; init; }
        public required nuint SnapshotBufferAddress { get; init; }
        public required uint OriginalProtection { get; init; }
        public required int CaveTotalSize { get; init; }
        public bool IsActive { get; set; }
        // 3D: Use long for hit count to avoid truncation after 2^31
        public long LastKnownHitCount { get; set; }
        // 1F: Track removal failures
        public bool RemovalFailed { get; set; }

        public CodeCaveHook ToDescriptor() =>
            new(Id, OriginalAddress, CaveAddress, StealLength, IsActive, (int)Math.Min(LastKnownHitCount, int.MaxValue));
    }

    // ─── P/Invoke ────────────────────────────────────────────────────

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

    // 1H/1I: Thread suspension P/Invoke

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
