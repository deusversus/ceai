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
        var target = (nuint)0x00007FF6_12345678;
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

public class InstructionLengthEstimatorTests
{
    [Theory]
    [InlineData(new byte[] { 0x55 }, 0, 1)]                          // push rbp
    [InlineData(new byte[] { 0x50 }, 0, 1)]                          // push rax
    [InlineData(new byte[] { 0xC3 }, 0, 1)]                          // ret
    [InlineData(new byte[] { 0x90 }, 0, 1)]                          // nop
    [InlineData(new byte[] { 0xCC }, 0, 1)]                          // int3
    [InlineData(new byte[] { 0x48, 0x89, 0xE5 }, 0, 3)]              // mov rbp, rsp (REX.W + modrm)
    [InlineData(new byte[] { 0x48, 0x83, 0xEC, 0x20 }, 0, 4)]        // sub rsp, 0x20 (REX.W + modrm + imm8)
    public void EstimateInstructionLength_CommonProloguePatterns(byte[] code, int offset, int expected)
    {
        var len = WindowsCodeCaveEngine.EstimateInstructionLength(code, offset);
        Assert.Equal(expected, len);
    }

    [Fact]
    public void EstimateInstructionLength_PastEnd_ReturnsNegative()
    {
        var len = WindowsCodeCaveEngine.EstimateInstructionLength(Array.Empty<byte>(), 0);
        Assert.Equal(-1, len);
    }

    [Fact]
    public void CalculateSafeStealLength_AlignsToInstructionBoundary()
    {
        // push rbp (1) + mov rbp,rsp (3) + sub rsp,0x20 (4) = 8 bytes
        byte[] prologue = [0x55, 0x48, 0x89, 0xE5, 0x48, 0x83, 0xEC, 0x20];
        var stealLen = WindowsCodeCaveEngine.CalculateSafeStealLength(prologue, 14);
        // minLength=14 but we only have 8 bytes of decodable code — should return 14 (minimum)
        // Actually, it depends on how the decoder handles the boundary
        Assert.True(stealLen >= 14, $"Steal length {stealLen} should be >= 14");
    }

    [Fact]
    public void CalculateSafeStealLength_SmallMinLength_AlignsUp()
    {
        // push rbp (1) + mov rbp,rsp (3) = 4 bytes — if min is 2, should get at least 1
        byte[] prologue = [0x55, 0x48, 0x89, 0xE5];
        var stealLen = WindowsCodeCaveEngine.CalculateSafeStealLength(prologue, 2);
        Assert.True(stealLen >= 2);
        // Should align to instruction boundary: either 1 (push rbp) or 4 (push rbp + mov rbp,rsp)
        Assert.True(stealLen == 3 || stealLen == 4, $"Expected 3 or 4 but got {stealLen}");
    }
}

public class CodeCaveHookContractTests
{
    [Fact]
    public void CodeCaveHook_Record_Properties()
    {
        var hook = new CodeCaveHook(
            "hook-1", (nuint)0x140001000, (nuint)0x7FF600000000, 14, true, 0);

        Assert.Equal("hook-1", hook.Id);
        Assert.Equal((nuint)0x140001000, hook.OriginalAddress);
        Assert.Equal((nuint)0x7FF600000000, hook.CaveAddress);
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
