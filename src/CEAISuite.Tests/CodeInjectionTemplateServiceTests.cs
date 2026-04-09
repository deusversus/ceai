using CEAISuite.Desktop.Services;

namespace CEAISuite.Tests;

public sealed class CodeInjectionTemplateServiceTests
{
    // ── NopSelection ──

    [Fact]
    public void NopSelection_ReturnsCorrectLength()
    {
        var nops = CodeInjectionTemplateService.NopSelection(5);
        Assert.Equal(5, nops.Length);
    }

    [Fact]
    public void NopSelection_AllBytesAre0x90()
    {
        var nops = CodeInjectionTemplateService.NopSelection(10);
        Assert.All(nops, b => Assert.Equal(0x90, b));
    }

    [Fact]
    public void NopSelection_ZeroLength_ReturnsEmpty()
    {
        var nops = CodeInjectionTemplateService.NopSelection(0);
        Assert.Empty(nops);
    }

    [Fact]
    public void NopSelection_SingleByte_Returns0x90()
    {
        var nops = CodeInjectionTemplateService.NopSelection(1);
        Assert.Single(nops);
        Assert.Equal(0x90, nops[0]);
    }

    // ── InsertJmpHook x86 ──

    [Fact]
    public void InsertJmpHook_X86_Returns5Bytes()
    {
        var bytes = CodeInjectionTemplateService.InsertJmpHook(0x400000, 0x500000, is64Bit: false);
        Assert.Equal(5, bytes.Length);
    }

    [Fact]
    public void InsertJmpHook_X86_StartsWithE9()
    {
        var bytes = CodeInjectionTemplateService.InsertJmpHook(0x400000, 0x500000, is64Bit: false);
        Assert.Equal(0xE9, bytes[0]);
    }

    [Fact]
    public void InsertJmpHook_X86_CorrectRelativeOffset()
    {
        ulong source = 0x400000;
        ulong target = 0x500000;
        var bytes = CodeInjectionTemplateService.InsertJmpHook(source, target, is64Bit: false);

        // Relative offset = target - source - 5
        int expected = (int)(target - source - 5);
        int actual = BitConverter.ToInt32(bytes, 1);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void InsertJmpHook_X86_NegativeOffset_Works()
    {
        // Jump backwards
        ulong source = 0x500000;
        ulong target = 0x400000;
        var bytes = CodeInjectionTemplateService.InsertJmpHook(source, target, is64Bit: false);

        Assert.Equal(0xE9, bytes[0]);
        int expected = (int)(target - source - 5);
        int actual = BitConverter.ToInt32(bytes, 1);
        Assert.Equal(expected, actual);
        Assert.True(actual < 0); // Negative offset for backward jump
    }

    // ── InsertJmpHook x64 ──

    [Fact]
    public void InsertJmpHook_X64_Returns14Bytes()
    {
        var bytes = CodeInjectionTemplateService.InsertJmpHook(0x400000, 0x7FF000000000, is64Bit: true);
        Assert.Equal(14, bytes.Length);
    }

    [Fact]
    public void InsertJmpHook_X64_StartsWithFF25()
    {
        var bytes = CodeInjectionTemplateService.InsertJmpHook(0x400000, 0x7FF000000000, is64Bit: true);
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0x25, bytes[1]);
    }

    [Fact]
    public void InsertJmpHook_X64_RipRelativeOffsetIsZero()
    {
        var bytes = CodeInjectionTemplateService.InsertJmpHook(0x400000, 0x7FF000000000, is64Bit: true);
        // bytes[2..5] should be 0x00000000 (RIP-relative offset = 0 means target is immediately after)
        Assert.Equal(0, BitConverter.ToInt32(bytes, 2));
    }

    [Fact]
    public void InsertJmpHook_X64_AbsoluteTargetStoredAt6()
    {
        ulong target = 0x7FF0DEADBEEF;
        var bytes = CodeInjectionTemplateService.InsertJmpHook(0x400000, target, is64Bit: true);
        ulong stored = BitConverter.ToUInt64(bytes, 6);
        Assert.Equal(target, stored);
    }

    [Fact]
    public void InsertJmpHook_X64_SameSourceAndTarget()
    {
        var bytes = CodeInjectionTemplateService.InsertJmpHook(0x400000, 0x400000, is64Bit: true);
        Assert.Equal(14, bytes.Length);
        Assert.Equal(0x400000UL, BitConverter.ToUInt64(bytes, 6));
    }
}
