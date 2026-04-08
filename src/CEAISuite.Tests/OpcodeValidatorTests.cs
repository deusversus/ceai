using CEAISuite.Engine.Windows;

namespace CEAISuite.Tests;

public class OpcodeValidatorTests
{
    [Fact]
    public void SafeCode_NopMovJmpCallRet_PassesValidation()
    {
        // NOP, MOV EAX,EBX, JMP +0 (short), CALL +0, RET
        // 90                nop
        // 89 D8             mov eax, ebx
        // EB 00             jmp $+2
        // E8 00 00 00 00    call $+5
        // C3                ret
        byte[] safeBytes = [0x90, 0x89, 0xD8, 0xEB, 0x00, 0xE8, 0x00, 0x00, 0x00, 0x00, 0xC3];

        var result = OpcodeValidator.ValidateBytes(safeBytes, is64Bit: false);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void SafeCode64Bit_PassesValidation()
    {
        // 48 89 C3          mov rbx, rax
        // 90                nop
        // C3                ret
        byte[] safeBytes = [0x48, 0x89, 0xC3, 0x90, 0xC3];

        var result = OpcodeValidator.ValidateBytes(safeBytes, is64Bit: true);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void DangerousOpcode_Cli_IsBlocked()
    {
        // FA = CLI
        byte[] bytes = [0xFA];

        var result = OpcodeValidator.ValidateBytes(bytes, is64Bit: false);

        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
        Assert.Contains("cli", result.Errors[0]);
    }

    [Fact]
    public void DangerousOpcode_Sti_IsBlocked()
    {
        // FB = STI
        byte[] bytes = [0xFB];

        var result = OpcodeValidator.ValidateBytes(bytes, is64Bit: false);

        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
        Assert.Contains("sti", result.Errors[0]);
    }

    [Fact]
    public void DangerousOpcode_Hlt_IsBlocked()
    {
        // F4 = HLT
        byte[] bytes = [0xF4];

        var result = OpcodeValidator.ValidateBytes(bytes, is64Bit: false);

        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
        Assert.Contains("hlt", result.Errors[0]);
    }

    [Fact]
    public void DangerousOpcode_Sysenter_IsBlocked()
    {
        // 0F 34 = SYSENTER
        byte[] bytes = [0x0F, 0x34];

        var result = OpcodeValidator.ValidateBytes(bytes, is64Bit: false);

        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
        Assert.Contains("sysenter", result.Errors[0]);
    }

    [Fact]
    public void DangerousOpcode_In_IsBlocked()
    {
        // EC = IN AL, DX
        byte[] bytes = [0xEC];

        var result = OpcodeValidator.ValidateBytes(bytes, is64Bit: false);

        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
        Assert.Contains("Blocked instruction", result.Errors[0]);
        // Verify the mnemonic "in" appears (the error message contains ": in" or "insb" etc.)
        Assert.Matches(@"\bin\b", result.Errors[0]);
    }

    [Fact]
    public void DangerousOpcode_Out_IsBlocked()
    {
        // EE = OUT DX, AL
        byte[] bytes = [0xEE];

        var result = OpcodeValidator.ValidateBytes(bytes, is64Bit: false);

        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
        Assert.Contains("Blocked instruction", result.Errors[0]);
        Assert.Matches(@"\bout\b", result.Errors[0]);
    }

    [Fact]
    public void DangerousOpcode_IntTwoE_IsBlocked()
    {
        // CD 2E = INT 0x2E (syscall via interrupt)
        byte[] bytes = [0xCD, 0x2E];

        var result = OpcodeValidator.ValidateBytes(bytes, is64Bit: false);

        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
        Assert.Contains("int 0x2e", result.Errors[0]);
    }

    [Fact]
    public void SuspiciousOpcode_Int3_ProducesWarning()
    {
        // CC = INT3
        byte[] bytes = [0xCC];

        var result = OpcodeValidator.ValidateBytes(bytes, is64Bit: false);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Single(result.Warnings);
        Assert.Contains("int3", result.Warnings[0]);
    }

    [Fact]
    public void MixedCode_SafePlusDangerous_CatchesDangerous()
    {
        // 90 = NOP, FA = CLI, 90 = NOP
        byte[] bytes = [0x90, 0xFA, 0x90];

        var result = OpcodeValidator.ValidateBytes(bytes, is64Bit: false);

        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
        Assert.Contains("cli", result.Errors[0]);
    }

    [Fact]
    public void MultipleDangerousOpcodes_ReportsAll()
    {
        // FA = CLI, FB = STI
        byte[] bytes = [0xFA, 0xFB];

        var result = OpcodeValidator.ValidateBytes(bytes, is64Bit: false);

        Assert.False(result.IsValid);
        Assert.Equal(2, result.Errors.Count);
        Assert.Contains(result.Errors, e => e.Contains("cli", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("sti", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyBytes_ReturnsValid()
    {
        var result = OpcodeValidator.ValidateBytes([], is64Bit: false);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void NullBytes_ReturnsValid()
    {
        var result = OpcodeValidator.ValidateBytes(null!, is64Bit: false);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void TruncatedInstruction_HandledGracefully()
    {
        // 0F is an incomplete two-byte opcode prefix
        byte[] bytes = [0x0F];

        var result = OpcodeValidator.ValidateBytes(bytes, is64Bit: false);

        // Should not throw, should report as warning or be valid
        // The decoder will produce an invalid instruction for truncated bytes
        Assert.NotNull(result);
        // As long as it doesn't contain a blocked opcode, it should remain valid
        // (truncated bytes produce a warning about decode failure)
    }

    [Fact]
    public void DangerousOpcode_Wrmsr_IsBlocked()
    {
        // 0F 30 = WRMSR
        byte[] bytes = [0x0F, 0x30];

        var result = OpcodeValidator.ValidateBytes(bytes, is64Bit: false);

        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
        Assert.Contains("wrmsr", result.Errors[0]);
    }

    [Fact]
    public void DangerousOpcode_Rdmsr_IsBlocked()
    {
        // 0F 32 = RDMSR
        byte[] bytes = [0x0F, 0x32];

        var result = OpcodeValidator.ValidateBytes(bytes, is64Bit: false);

        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
        Assert.Contains("rdmsr", result.Errors[0]);
    }

    [Fact]
    public void DangerousOpcode_Invd_IsBlocked()
    {
        // 0F 08 = INVD
        byte[] bytes = [0x0F, 0x08];

        var result = OpcodeValidator.ValidateBytes(bytes, is64Bit: false);

        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
        Assert.Contains("invd", result.Errors[0]);
    }

    [Fact]
    public void SafeInt_NotTwoE_IsAllowed()
    {
        // CD 03 = INT 3 (via two-byte encoding, not INT 0x2E)
        // Note: this may decode as int3 or int 0x03 depending on the decoder
        // CD 21 = INT 0x21 (DOS interrupt, safe in user mode)
        byte[] bytes = [0xCD, 0x21];

        var result = OpcodeValidator.ValidateBytes(bytes, is64Bit: false);

        // INT 0x21 is not INT 0x2E so should not produce an error
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }
}
