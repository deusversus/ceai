using CEAISuite.Engine.Abstractions;
using CEAISuite.Engine.Windows;

namespace CEAISuite.Tests;

public class BreakpointModeResolutionTests
{
    [Theory]
    [InlineData(BreakpointType.HardwareExecute, BreakpointMode.Hardware)]
    [InlineData(BreakpointType.Software, BreakpointMode.Software)]
    [InlineData(BreakpointType.HardwareWrite, BreakpointMode.PageGuard)]
    [InlineData(BreakpointType.HardwareReadWrite, BreakpointMode.PageGuard)]
    public void ResolveAutoMode_SelectsLeastIntrusiveMode(BreakpointType type, BreakpointMode expected)
    {
        var resolved = WindowsBreakpointEngine.ResolveAutoMode(type);
        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void BreakpointMode_HasCorrectOrder()
    {
        // Modes are ordered from least to most intrusive
        Assert.True(BreakpointMode.Stealth < BreakpointMode.PageGuard);
        Assert.True(BreakpointMode.PageGuard < BreakpointMode.Hardware);
        Assert.True(BreakpointMode.Hardware < BreakpointMode.Software);
    }

    [Fact]
    public void BreakpointDescriptor_IncludesMode()
    {
        var bp = new BreakpointDescriptor(
            "bp-1", 0x1000, BreakpointType.HardwareWrite,
            BreakpointHitAction.LogAndContinue,
            IsEnabled: true, HitCount: 5, Mode: BreakpointMode.PageGuard);

        Assert.Equal(BreakpointMode.PageGuard, bp.Mode);
        Assert.Equal("bp-1", bp.Id);
        Assert.Equal(5, bp.HitCount);
    }
}

public class CodeCaveFarJmpTests
{
    [Fact]
    public void BuildFarJmp_ProducesCorrect14BytePattern()
    {
        var target = unchecked((nuint)0x00007FF6_12345678);
        var jmp = WindowsCodeCaveEngine.BuildFarJmp(target);

        Assert.Equal(14, jmp.Length);
        Assert.Equal(0xFF, jmp[0]);         // FF 25 = JMP [RIP+0]
        Assert.Equal(0x25, jmp[1]);
        Assert.Equal(0x00, jmp[2]);         // 00 00 00 00 RIP-relative offset
        Assert.Equal(0x00, jmp[3]);
        Assert.Equal(0x00, jmp[4]);
        Assert.Equal(0x00, jmp[5]);

        var embeddedAddr = BitConverter.ToUInt64(jmp, 6);
        Assert.Equal((ulong)target, embeddedAddr);
    }

    [Fact]
    public void BuildFarJmp_ZeroAddress_StillValid()
    {
        var jmp = WindowsCodeCaveEngine.BuildFarJmp(0);
        Assert.Equal(14, jmp.Length);
        Assert.Equal(0xFF, jmp[0]);
        Assert.Equal(0x25, jmp[1]);
        Assert.Equal(0UL, BitConverter.ToUInt64(jmp, 6));
    }
}

public class InstructionDecodingTests
{
    [Fact]
    public void CalculateSafeStealLength_AlignsToInstructionBoundary()
    {
        // Typical x64 function prologue that's long enough:
        // push rbp (1) + mov rbp,rsp (3) + sub rsp,0x20 (4) + push rbx (1) + push rsi (1) +
        // push rdi (1) + mov edi,ecx (2) + xor ebx,ebx (2) + nop (1) = 16 bytes
        byte[] prologue = [0x55, 0x48, 0x89, 0xE5, 0x48, 0x83, 0xEC, 0x20, 0x53, 0x56, 0x57, 0x89, 0xCF, 0x31, 0xDB, 0x90];
        var stealLen = WindowsCodeCaveEngine.CalculateSafeStealLength(prologue, 14, unchecked((nuint)0x140001000));
        Assert.True(stealLen >= 14, $"Steal length {stealLen} should be >= 14");
    }

    [Fact]
    public void CalculateSafeStealLength_SmallMinLength_AlignsUp()
    {
        // push rbp (1) + mov rbp,rsp (3) = 4 bytes — if min is 2, should align to instruction boundary
        byte[] prologue = [0x55, 0x48, 0x89, 0xE5];
        var stealLen = WindowsCodeCaveEngine.CalculateSafeStealLength(prologue, 2, unchecked((nuint)0x140001000));
        Assert.True(stealLen >= 2);
    }

    [Fact]
    public void CalculateSafeStealLength_InvalidInstruction_Throws()
    {
        // Invalid instruction bytes
        byte[] garbage = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
        // Iced may or may not decode 0xFF 0xFF as valid — test with known-bad pattern
        // 0x06 is PUSH ES which is invalid in 64-bit mode
        byte[] invalid64 = [0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06];
        Assert.ThrowsAny<InvalidOperationException>(() =>
            WindowsCodeCaveEngine.CalculateSafeStealLength(invalid64, 14, unchecked((nuint)0x140001000)));
    }

    [Fact]
    public void CalculateSafeStealLength_TooFewBytes_Throws()
    {
        // Only 2 bytes of valid instructions, need 14
        byte[] tooShort = [0x55, 0xC3]; // push rbp + ret = 2 bytes
        Assert.ThrowsAny<InvalidOperationException>(() =>
            WindowsCodeCaveEngine.CalculateSafeStealLength(tooShort, 14, unchecked((nuint)0x140001000)));
    }
}

public class CodeCaveHookContractTests
{
    [Fact]
    public void CodeCaveHook_Record_Properties()
    {
        var hook = new CodeCaveHook(
            "hook-1", unchecked((nuint)0x140001000), unchecked((nuint)0x7FF600000000), 14, true, 0);

        Assert.Equal("hook-1", hook.Id);
        Assert.Equal(unchecked((nuint)0x140001000), hook.OriginalAddress);
        Assert.Equal(unchecked((nuint)0x7FF600000000), hook.CaveAddress);
        Assert.Equal(14, hook.OriginalBytesLength);
        Assert.True(hook.IsActive);
        Assert.Equal(0, hook.HitCount);
    }

    [Fact]
    public void CodeCaveInstallResult_Success()
    {
        var hook = new CodeCaveHook("h1", 0x1000, 0x2000, 14, true, 0);
        var result = new CodeCaveInstallResult(true, hook, null);

        Assert.True(result.Success);
        Assert.NotNull(result.Hook);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void CodeCaveInstallResult_Failure()
    {
        var result = new CodeCaveInstallResult(false, null, "Unable to allocate memory");

        Assert.False(result.Success);
        Assert.Null(result.Hook);
        Assert.Equal("Unable to allocate memory", result.ErrorMessage);
    }
}

public class RipRelativeRelocationTests
{
    [Fact]
    public void EmptyBytes_ReturnsEmpty()
    {
        var result = WindowsCodeCaveEngine.RelocateRipRelativeInstructions(
            [], unchecked((nuint)0x140001000), unchecked((nuint)0x7FF600000000));
        Assert.Empty(result);
    }

    [Fact]
    public void NonRipRelative_ReturnsOriginalBytes()
    {
        // push rbp; mov rbp, rsp (common prologue — no RIP-relative)
        byte[] prologue = [0x55, 0x48, 0x89, 0xE5];
        var result = WindowsCodeCaveEngine.RelocateRipRelativeInstructions(
            prologue, unchecked((nuint)0x140001000), unchecked((nuint)0x7FF600000000));
        Assert.Equal(prologue, result);
    }

    [Fact]
    public void RipRelativeMov_GetsRelocated()
    {
        // mov rax, [rip+0x1000] at 0x140001000 → rip after instr = 0x140001007
        // Target = 0x140001007 + 0x1000 = 0x140002007
        // Encoded as: 48 8B 05 00100000
        byte[] movRipRel = [0x48, 0x8B, 0x05, 0x00, 0x10, 0x00, 0x00];
        var originalAddr = unchecked((nuint)0x140001000);
        // Use a new address within ±2GB so displacement still fits int32
        var newAddr = unchecked((nuint)0x140010000);

        var result = WindowsCodeCaveEngine.RelocateRipRelativeInstructions(
            movRipRel, originalAddr, newAddr);

        Assert.Equal(7, result.Length);
        Assert.Equal(0x48, result[0]);
        Assert.Equal(0x8B, result[1]);
        Assert.Equal(0x05, result[2]);

        // Original target: 0x140001007 + 0x1000 = 0x140002007
        // New RIP after: 0x140010007
        // Expected disp: 0x140002007 - 0x140010007 = -0xE000
        var newDisp = BitConverter.ToInt32(result, 3);
        int expectedDisp = (int)(0x140002007L - 0x140010007L);
        Assert.Equal(expectedDisp, newDisp);
    }

    [Fact]
    public void LeaRipRelative_GetsRelocated()
    {
        // lea rcx, [rip+0x500] at 0x140001000
        // Encoded as: 48 8D 0D 00050000
        byte[] leaRipRel = [0x48, 0x8D, 0x0D, 0x00, 0x05, 0x00, 0x00];
        var originalAddr = unchecked((nuint)0x140001000);
        // Put new address close enough for displacement to still fit in int32
        var newAddr = unchecked((nuint)0x140002000);

        var result = WindowsCodeCaveEngine.RelocateRipRelativeInstructions(
            leaRipRel, originalAddr, newAddr);

        Assert.Equal(7, result.Length);
        Assert.Equal(0x48, result[0]);
        Assert.Equal(0x8D, result[1]);
        Assert.Equal(0x0D, result[2]);

        // Original target: 0x140001007 + 0x500 = 0x140001507
        // New RIP after: 0x140002007
        // New disp: 0x140001507 - 0x140002007 = -0xB00 = FFFFF500
        var newDisp = BitConverter.ToInt32(result, 3);
        long expectedTarget = 0x140001000 + 7 + 0x500; // = 0x140001507
        long newRipAfter = 0x140002000 + 7; // = 0x140002007
        int expectedDisp = (int)(expectedTarget - newRipAfter);
        Assert.Equal(expectedDisp, newDisp);
    }

    [Fact]
    public void MixedInstructions_OnlyRelocatesRipRelative()
    {
        // push rbp (55) + mov rax,[rip+0x100] (48 8B 05 00010000) = 8 bytes total
        byte[] mixed = [0x55, 0x48, 0x8B, 0x05, 0x00, 0x01, 0x00, 0x00];
        var originalAddr = unchecked((nuint)0x140001000);
        var newAddr = unchecked((nuint)0x140005000);

        var result = WindowsCodeCaveEngine.RelocateRipRelativeInstructions(
            mixed, originalAddr, newAddr);

        // push rbp should be unchanged
        Assert.Equal(0x55, result[0]);
        // mov instruction should be relocated
        Assert.Equal(0x48, result[1]);
        Assert.Equal(0x8B, result[2]);
        Assert.Equal(0x05, result[3]);
        // Displacement should be adjusted
        var newDisp = BitConverter.ToInt32(result, 4);
        // Original target: 0x140001008 + 0x100 = 0x140001108
        // New RIP after mov: 0x140005008
        // Expected disp: 0x140001108 - 0x140005008 = -0x3F00
        int expectedDisp = (int)(0x140001108L - 0x140005008L);
        Assert.Equal(expectedDisp, newDisp);
    }
}
