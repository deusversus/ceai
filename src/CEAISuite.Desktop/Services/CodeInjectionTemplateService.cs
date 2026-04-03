namespace CEAISuite.Desktop.Services;

/// <summary>
/// Provides code injection templates for common memory patching operations.
/// </summary>
public sealed class CodeInjectionTemplateService
{
    /// <summary>Generate a NOP sled of the specified length (x86 opcode 0x90).</summary>
    public byte[] NopSelection(int length)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, (byte)0x90);
        return bytes;
    }

    /// <summary>
    /// Generate a JMP hook from source to target.
    /// x86 (5 bytes): E9 [rel32]
    /// x64 (14 bytes): FF 25 00 00 00 00 [abs64]
    /// </summary>
    public byte[] InsertJmpHook(ulong source, ulong target, bool is64Bit)
    {
        if (!is64Bit)
        {
            // x86: E9 relative32
            var rel = (int)(target - source - 5);
            var bytes = new byte[5];
            bytes[0] = 0xE9;
            BitConverter.TryWriteBytes(bytes.AsSpan(1), rel);
            return bytes;
        }
        else
        {
            // x64: FF 25 00 00 00 00 [8-byte absolute target]
            var bytes = new byte[14];
            bytes[0] = 0xFF;
            bytes[1] = 0x25;
            // bytes[2..5] = 00 00 00 00 (RIP-relative offset to immediately following qword)
            BitConverter.TryWriteBytes(bytes.AsSpan(6), target);
            return bytes;
        }
    }
}
