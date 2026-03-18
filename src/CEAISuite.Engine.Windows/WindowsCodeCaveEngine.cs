using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using CEAISuite.Engine.Abstractions;
using Iced.Intel;

namespace CEAISuite.Engine.Windows;

/// <summary>
/// Installs JMP-detour hooks ("code caves") that redirect execution through
/// allocated trampolines. No debugger attachment required — this is the stealthiest
/// form of execution monitoring available.
///
/// How it works:
/// 1. Disassemble instructions at target to find a safe steal length (≥14 bytes for x64 far JMP)
/// 2. Allocate executable memory (the "cave") via VirtualAllocEx
/// 3. Write trampoline into cave:
///    a. Increment hit counter (lock inc [counterAddress])
///    b. Optionally capture registers into a ring buffer
///    c. Execute stolen original bytes
///    d. JMP back to (target + stolen_length)
/// 4. Overwrite target with: FF 25 00000000 [absolute_addr] (14-byte far JMP to cave)
/// 5. Original bytes are preserved for clean removal
/// </summary>
public sealed class WindowsCodeCaveEngine : ICodeCaveEngine
{
    private const uint ProcessAllAccess = 0x001FFFFF;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageExecuteReadWrite = 0x40;

    // x64 far JMP: FF 25 00 00 00 00 [8-byte absolute address] = 14 bytes
    private const int FarJmpSize = 14;

    // Register snapshot ring buffer size per hook
    private const int MaxSnapshotsPerHook = 64;

    private readonly ConcurrentDictionary<string, CodeCaveState> _hooks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, IntPtr> _processHandles = new();
    private readonly object _gate = new();

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
            hook.LastKnownHitCount = (int)BitConverter.ToInt64(counterBuf);

            // Read register snapshots from the ring buffer if capture was enabled
            if (!hook.CaptureRegisters || hook.SnapshotBufferAddress == 0)
                return [];

            var results = new List<BreakpointHitEvent>();
            var hitCount = Math.Min(hook.LastKnownHitCount, MaxSnapshotsPerHook);
            var entriesToRead = Math.Min(hitCount, maxEntries);

            // Each snapshot is 7 registers × 8 bytes = 56 bytes
            const int snapshotSize = 56;
            var buffer = new byte[snapshotSize];

            for (int i = 0; i < entriesToRead; i++)
            {
                // Read from end of ring buffer backward (most recent first)
                var idx = ((hook.LastKnownHitCount - 1 - i) % MaxSnapshotsPerHook + MaxSnapshotsPerHook) % MaxSnapshotsPerHook;
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

        // Step 2: Determine safe steal length (must be ≥14 bytes, aligned to instruction boundary)
        // Use a simple heuristic: scan for instruction boundaries using common x64 patterns
        var stealLength = CalculateSafeStealLength(originalCode, FarJmpSize);

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

        // Step 4: Allocate executable memory for the cave
        var caveBase = VirtualAllocEx(hProcess, IntPtr.Zero, (UIntPtr)totalCaveSize,
            MemCommit | MemReserve, PageExecuteReadWrite);
        if (caveBase == IntPtr.Zero)
            throw new InvalidOperationException($"VirtualAllocEx failed for code cave: {Marshal.GetLastWin32Error()}");

        var caveAddress = (nuint)caveBase;
        var hitCounterAddr = caveAddress;
        var snapshotIndexAddr = caveAddress + (nuint)hitCounterSize;
        var snapshotBufferAddr = captureRegisters ? caveAddress + (nuint)(hitCounterSize + snapshotIndexSize) : (nuint)0;
        var codeStart = caveAddress + (nuint)dataRegionSize;

        // Step 5: Build the trampoline machine code
        var trampoline = BuildTrampoline(
            codeStart, address, (nuint)stealLength, stolenBytes,
            hitCounterAddr, snapshotIndexAddr, snapshotBufferAddr, captureRegisters);

        // Step 6: Write data region (zeroed) + trampoline into cave
        var caveData = new byte[totalCaveSize];
        Array.Copy(trampoline, 0, caveData, dataRegionSize, trampoline.Length);

        if (!WriteProcessMemory(hProcess, caveBase, caveData, caveData.Length, out _))
            throw new InvalidOperationException($"Cannot write code cave at 0x{caveAddress:X}.");

        FlushInstructionCache(hProcess, caveBase, (UIntPtr)caveData.Length);

        // Step 7: Write the JMP detour at the original address
        var detour = BuildFarJmp(codeStart);

        // NOP-pad remaining stolen bytes
        var fullPatch = new byte[stealLength];
        Array.Copy(detour, fullPatch, FarJmpSize);
        for (int i = FarJmpSize; i < stealLength; i++)
            fullPatch[i] = 0x90; // NOP

        // Ensure the target is writable
        if (!VirtualProtectEx(hProcess, (IntPtr)address, (UIntPtr)stealLength, PageExecuteReadWrite, out var oldProtect))
            throw new InvalidOperationException($"Cannot change protection at 0x{address:X}.");

        if (!WriteProcessMemory(hProcess, (IntPtr)address, fullPatch, fullPatch.Length, out _))
            throw new InvalidOperationException($"Cannot write JMP detour at 0x{address:X}.");

        FlushInstructionCache(hProcess, (IntPtr)address, (UIntPtr)stealLength);

        // Restore original protection
        VirtualProtectEx(hProcess, (IntPtr)address, (UIntPtr)stealLength, oldProtect, out _);

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

    private void RemoveHook(CodeCaveState hook)
    {
        if (!hook.IsActive) return;
        hook.IsActive = false;

        var hProcess = GetProcessHandle(hook.ProcessId);

        // Restore original bytes at the hook site
        VirtualProtectEx(hProcess, (IntPtr)hook.OriginalAddress, (UIntPtr)hook.StealLength,
            PageExecuteReadWrite, out var oldProtect);

        WriteProcessMemory(hProcess, (IntPtr)hook.OriginalAddress,
            hook.StolenBytes, hook.StolenBytes.Length, out _);

        FlushInstructionCache(hProcess, (IntPtr)hook.OriginalAddress, (UIntPtr)hook.StealLength);

        VirtualProtectEx(hProcess, (IntPtr)hook.OriginalAddress, (UIntPtr)hook.StealLength,
            oldProtect, out _);

        // Free the cave memory
        VirtualFreeEx(hProcess, (IntPtr)hook.CaveAddress, UIntPtr.Zero, MemRelease);

        _hooks.TryRemove(hook.Id, out _);
    }

    // ─── Machine Code Generation ─────────────────────────────────────

    /// <summary>
    /// Build x64 trampoline:
    ///   pushfq
    ///   push rax-rdi
    ///   lock inc qword [hitCounter]
    ///   (optional) snapshot registers to ring buffer
    ///   pop rdi-rax
    ///   popfq
    ///   execute stolen original bytes
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

            // Get snapshot index: mov rax, [snapshotIndexAddr]; mov rcx, rax
            code.AddRange(new byte[] { 0x48, 0xB8 });
            code.AddRange(BitConverter.GetBytes((ulong)snapshotIndexAddr));
            code.AddRange(new byte[] { 0x48, 0x8B, 0x00 }); // mov rax, [rax]
            code.AddRange(new byte[] { 0x48, 0x89, 0xC1 }); // mov rcx, rax

            // index = index % MaxSnapshotsPerHook (=64, so AND with 63)
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

            // Increment snapshot index: lock inc qword [snapshotIndexAddr]
            code.AddRange(new byte[] { 0x48, 0xB8 });
            code.AddRange(BitConverter.GetBytes((ulong)snapshotIndexAddr));
            code.AddRange(new byte[] { 0x48, 0x83, 0xC1, 0x01 }); // inc rcx (new index)
            code.AddRange(new byte[] { 0x48, 0x89, 0x08 }); // mov [rax], rcx
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

        if (!BlockEncoder.TryEncode(64, block, out var errorMessage, out _, BlockEncoderOptions.None))
        {
            System.Diagnostics.Debug.WriteLine(
                $"[CodeCave] RIP-relocation failed, using raw stolen bytes: {errorMessage}");
            return stolenBytes;
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
    /// Calculate the minimum number of bytes to steal that aligns to instruction boundaries.
    /// Uses a simple x64 instruction length decoder for common patterns.
    /// Falls back to the minimum required length if decoding is uncertain.
    /// </summary>
    internal static int CalculateSafeStealLength(byte[] code, int minLength)
    {
        int offset = 0;
        while (offset < minLength && offset < code.Length)
        {
            var instrLen = EstimateInstructionLength(code, offset);
            if (instrLen <= 0)
            {
                // Can't decode — just use minLength and hope for alignment
                return minLength;
            }
            offset += instrLen;
        }
        return Math.Max(offset, minLength);
    }

    /// <summary>
    /// Simple x64 instruction length estimator for common patterns found at function entries.
    /// Handles REX prefixes, ModR/M, SIB, and common immediate sizes.
    /// This is not a full decoder — it covers ~90% of real-world function prologues.
    /// </summary>
    internal static int EstimateInstructionLength(byte[] code, int offset)
    {
        if (offset >= code.Length) return -1;
        var b = code[offset];
        var pos = offset;

        // REX prefix (0x40-0x4F)
        bool hasRex = b >= 0x40 && b <= 0x4F;
        bool rexW = hasRex && (b & 0x08) != 0;
        if (hasRex)
        {
            pos++;
            if (pos >= code.Length) return -1;
            b = code[pos];
        }

        // Common instruction patterns
        switch (b)
        {
            // NOP
            case 0x90: return pos - offset + 1;

            // PUSH/POP reg (50-5F)
            case >= 0x50 and <= 0x5F: return pos - offset + 1;

            // MOV reg, imm32/64 (B8-BF)
            case >= 0xB8 and <= 0xBF: return pos - offset + 1 + (rexW ? 8 : 4);

            // RET
            case 0xC3: return pos - offset + 1;
            case 0xC2: return pos - offset + 3; // RET imm16

            // INT3
            case 0xCC: return pos - offset + 1;

            // SUB RSP, imm8 (83 EC xx)
            case 0x83:
                return pos - offset + 3;

            // SUB RSP, imm32 (81 EC xxxxxxxx)
            case 0x81:
                return pos - offset + 6;

            // MOV r/m, r or MOV r, r/m (89, 8B)
            case 0x89 or 0x8B:
            {
                pos++;
                if (pos >= code.Length) return -1;
                return pos - offset + ModRmLength(code, pos);
            }

            // LEA (8D)
            case 0x8D:
            {
                pos++;
                if (pos >= code.Length) return -1;
                return pos - offset + ModRmLength(code, pos);
            }

            // TEST r/m, r (85)
            case 0x85:
            {
                pos++;
                if (pos >= code.Length) return -1;
                return pos - offset + ModRmLength(code, pos);
            }

            // XOR r, r/m (33, 31)
            case 0x31 or 0x33:
            {
                pos++;
                if (pos >= code.Length) return -1;
                return pos - offset + ModRmLength(code, pos);
            }

            // Two-byte opcode (0F xx)
            case 0x0F:
            {
                pos++;
                if (pos >= code.Length) return -1;
                // Assume ModR/M follows for most 0F xx instructions
                pos++;
                if (pos >= code.Length) return -1;
                return pos - offset + ModRmLength(code, pos);
            }

            // Default: assume 1 byte if we can't decode
            default:
                return -1;
        }
    }

    /// <summary>Calculate extra bytes needed after ModR/M byte (SIB + displacement).</summary>
    private static int ModRmLength(byte[] code, int modrm_offset)
    {
        if (modrm_offset >= code.Length) return 1;
        var modrm = code[modrm_offset];
        var mod = (modrm >> 6) & 3;
        var rm = modrm & 7;

        var extra = 1; // the ModR/M byte itself

        if (mod == 3) return extra; // register-register, no displacement

        // SIB byte present when rm == 4 (except mod == 3)
        if (rm == 4) extra++;

        // RIP-relative when mod == 0 and rm == 5
        if (mod == 0 && rm == 5) extra += 4;
        else if (mod == 1) extra += 1; // disp8
        else if (mod == 2) extra += 4; // disp32
        else if (mod == 0 && rm == 4)
        {
            // Check SIB base for disp32
            if (modrm_offset + 1 < code.Length && (code[modrm_offset + 1] & 7) == 5)
                extra += 4;
        }

        return extra;
    }

    // ─── Process Handle Management ───────────────────────────────────

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
        public int LastKnownHitCount { get; set; }

        public CodeCaveHook ToDescriptor() =>
            new(Id, OriginalAddress, CaveAddress, StealLength, IsActive, LastKnownHitCount);
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
}
