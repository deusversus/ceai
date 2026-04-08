using System.Globalization;
using CEAISuite.Engine.Abstractions;
using Iced.Intel;

namespace CEAISuite.Engine.Windows;

/// <summary>
/// Validates assembled bytes for dangerous opcodes before they are written to process memory.
/// Uses the Iced disassembler to decode bytes and check each instruction's mnemonic against
/// a deny-list of privileged/dangerous instructions.
/// </summary>
internal static class OpcodeValidator
{
    /// <summary>Opcodes that are blocked outright (return an error).</summary>
    private static readonly HashSet<Mnemonic> BlockedMnemonics =
    [
        Mnemonic.Sysenter,
        Mnemonic.Sysexit,
        Mnemonic.Syscall,
        Mnemonic.Sysret,
        Mnemonic.Cli,
        Mnemonic.Sti,
        Mnemonic.Hlt,
        Mnemonic.Invd,
        Mnemonic.Wrmsr,
        Mnemonic.Rdmsr,
        Mnemonic.In,
        Mnemonic.Insb,
        Mnemonic.Insw,
        Mnemonic.Insd,
        Mnemonic.Out,
        Mnemonic.Outsb,
        Mnemonic.Outsw,
        Mnemonic.Outsd,
    ];

    /// <summary>Opcodes that produce a warning (suspicious but not necessarily dangerous).</summary>
    private static readonly HashSet<Mnemonic> WarnMnemonics =
    [
        Mnemonic.Int3,
    ];

    /// <summary>
    /// Disassembles <paramref name="assembledBytes"/> and checks each instruction against
    /// the blocked/warned opcode lists. Returns an <see cref="OpcodeValidationResult"/>.
    /// </summary>
    /// <param name="assembledBytes">The machine code bytes to validate.</param>
    /// <param name="is64Bit">Whether the target process is 64-bit.</param>
    /// <returns>Validation result containing any errors and warnings found.</returns>
    internal static OpcodeValidationResult ValidateBytes(byte[] assembledBytes, bool is64Bit)
    {
        if (assembledBytes is null || assembledBytes.Length == 0)
            return OpcodeValidationResult.Valid;

        var errors = new List<string>();
        var warnings = new List<string>();

        var bitness = is64Bit ? 64 : 32;
        var codeReader = new ByteArrayCodeReader(assembledBytes);
        var decoder = Decoder.Create(bitness, codeReader);

        var offset = 0;
        while (offset < assembledBytes.Length)
        {
            var instr = decoder.Decode();

            if (instr.IsInvalid)
            {
                // Could not decode — report a warning and skip one byte.
                warnings.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "Unable to decode instruction at offset 0x{0:X}: byte 0x{1:X2}",
                    offset, assembledBytes[offset]));
                break; // Cannot continue decoding after an invalid instruction
            }

            // Check for int 0x2e specifically (syscall via interrupt on Windows)
            if (instr.Mnemonic == Mnemonic.Int && instr.Op0Kind == OpKind.Immediate8 && instr.Immediate8 == 0x2E)
            {
                errors.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "Blocked instruction at offset 0x{0:X}: int 0x2e (syscall via interrupt)",
                    offset));
            }
            else if (BlockedMnemonics.Contains(instr.Mnemonic))
            {
                errors.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "Blocked instruction at offset 0x{0:X}: {1}",
                    offset, instr.Mnemonic.ToString().ToLowerInvariant()));
            }
            else if (WarnMnemonics.Contains(instr.Mnemonic))
            {
                warnings.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "Suspicious instruction at offset 0x{0:X}: {1}",
                    offset, instr.Mnemonic.ToString().ToLowerInvariant()));
            }

            offset += instr.Length;
        }

        var isValid = errors.Count == 0;
        return new OpcodeValidationResult(isValid, warnings, errors);
    }
}
