using System.Globalization;
using System.Runtime.InteropServices;
using CEAISuite.Engine.Abstractions;
using Iced.Intel;

namespace CEAISuite.Engine.Windows;

public sealed class WindowsDisassemblyEngine(ISymbolEngine? symbolEngine = null) : IDisassemblyEngine
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;

    public Task<DisassemblyResult> DisassembleAsync(
        int processId,
        nuint address,
        int maxInstructions = 20,
        CancellationToken cancellationToken = default) =>
        Task.Run<DisassemblyResult>(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
                if (handle == IntPtr.Zero)
                {
                    throw new InvalidOperationException($"Unable to open process {processId} for disassembly.");
                }

                try
                {
                    var bitness = DetectBitness(handle, processId);
                    var readSize = maxInstructions * 15; // max 15 bytes per x86 instruction
                    var buffer = new byte[readSize];

                    if (!ReadProcessMemory(handle, (IntPtr)address, buffer, readSize, out var bytesRead) || bytesRead <= 0)
                    {
                        throw new InvalidOperationException($"Unable to read memory at 0x{address:X}.");
                    }

                    var codeReader = new ByteArrayCodeReader(buffer, 0, bytesRead);
                    var decoder = Decoder.Create(bitness, codeReader, (ulong)address);
                    var formatter = new MasmFormatter();
                    var output = new StringOutput();
                    var instructions = new List<DisassembledInstruction>();
                    var totalBytes = 0;

                    var endRip = (ulong)address + (ulong)bytesRead;

                    // 5B: LIMITATION — Disassembly is a point-in-time snapshot. If the target uses
                    // self-modifying code (JIT, unpacking, runtime code generation), the bytes may
                    // change between read and display. Results may not reflect the current state.

                    while (decoder.IP < endRip && instructions.Count < maxInstructions)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var instr = decoder.Decode();

                        var instrBytes = buffer[totalBytes..(totalBytes + instr.Length)];
                        var hexBytes = string.Join(' ', instrBytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

                        string mnemonic;
                        string operands;

                        // 5A: Check for invalid instructions — output as "db 0xNN" instead
                        if (instr.IsInvalid)
                        {
                            mnemonic = "db";
                            operands = string.Join(", ", instrBytes.Select(b => $"0x{b:X2}"));
                        }
                        else
                        {
                            formatter.Format(instr, output);
                            var formatted = output.ToStringAndReset();
                            var spaceIdx = formatted.IndexOf(' ', StringComparison.Ordinal);
                            mnemonic = spaceIdx >= 0 ? formatted[..spaceIdx] : formatted;
                            operands = spaceIdx >= 0 ? formatted[(spaceIdx + 1)..].TrimStart() : "";
                        }

                        // Use combined resolution when available (single lock acquisition per instruction)
                        string? symbolName = null;
                        string? sourceFile = null;
                        int? sourceLine = null;
                        if (symbolEngine is WindowsSymbolEngine wse)
                        {
                            var (sym, line) = wse.ResolveAddressAndLine((nuint)instr.IP);
                            symbolName = sym?.DisplayName;
                            sourceFile = line?.FileName;
                            sourceLine = line?.LineNumber;
                        }
                        else if (symbolEngine is not null)
                        {
                            symbolName = symbolEngine.ResolveAddress((nuint)instr.IP)?.DisplayName;
                            var lineInfo = symbolEngine.ResolveSourceLine((nuint)instr.IP);
                            sourceFile = lineInfo?.FileName;
                            sourceLine = lineInfo?.LineNumber;
                        }

                        instructions.Add(new DisassembledInstruction(
                            (nuint)instr.IP,
                            hexBytes,
                            mnemonic,
                            operands,
                            instr.Length,
                            symbolName,
                            sourceFile,
                            sourceLine));

                        totalBytes += instr.Length;
                    }

                    return new DisassemblyResult(address, instructions, totalBytes);
                }
                finally
                {
                    CloseHandle(handle);
                }
            },
            cancellationToken);

    private static int DetectBitness(IntPtr processHandle, int processId)
    {
        if (IsWow64Process2(processHandle, out var processMachine, out _))
        {
            // If processMachine is not IMAGE_FILE_MACHINE_UNKNOWN, the process is running under WoW64 (32-bit)
            if (processMachine != 0)
            {
                return 32;
            }
        }

        return IntPtr.Size == 8 ? 64 : 32;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr processHandle, IntPtr baseAddress, [Out] byte[] buffer, int size, out int numberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
