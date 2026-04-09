using CEAISuite.Application;
using CEAISuite.Desktop.Services;

namespace CEAISuite.Tests;

public sealed class AddressExpressionParserTests
{
    [Fact]
    public void PlainHexWithPrefix_Succeeds()
    {
        Assert.True(AddressExpressionParser.TryParse("0x00400000", null, out var addr));
        Assert.Equal(0x00400000UL, addr);
    }

    [Fact]
    public void PlainHexWithoutPrefix_Succeeds()
    {
        Assert.True(AddressExpressionParser.TryParse("1A3F0", null, out var addr));
        Assert.Equal(0x1A3F0UL, addr);
    }

    [Fact]
    public void PlainHexUpperCase_Succeeds()
    {
        Assert.True(AddressExpressionParser.TryParse("0xDEADBEEF", null, out var addr));
        Assert.Equal(0xDEADBEEFUL, addr);
    }

    [Fact]
    public void ModulePlusOffset_WithMatchingModule_ReturnsSum()
    {
        var modules = new List<ModuleOverview>
        {
            new("game.exe", "0x00400000", "0x100000")
        };

        Assert.True(AddressExpressionParser.TryParse("game.exe+0x1A3F0", modules, out var addr));
        Assert.Equal(0x00400000UL + 0x1A3F0UL, addr);
    }

    [Fact]
    public void ModulePlusOffset_CaseInsensitive()
    {
        var modules = new List<ModuleOverview>
        {
            new("Game.EXE", "0x00400000", "0x100000")
        };

        Assert.True(AddressExpressionParser.TryParse("game.exe+0x100", modules, out var addr));
        Assert.Equal(0x00400100UL, addr);
    }

    [Fact]
    public void ModulePlusOffset_ModuleNotFound_FallsThrough()
    {
        var modules = new List<ModuleOverview>
        {
            new("other.dll", "0x10000000", "0x50000")
        };

        // "game.exe+0x1A3F0" can't be parsed as plain hex, so it should fail
        Assert.False(AddressExpressionParser.TryParse("game.exe+0x1A3F0", modules, out _));
    }

    [Fact]
    public void EmptyInput_ReturnsFalse()
    {
        Assert.False(AddressExpressionParser.TryParse("", null, out _));
        Assert.False(AddressExpressionParser.TryParse("   ", null, out _));
    }

    [Fact]
    public void NullExpression_ReturnsFalse()
    {
        Assert.False(AddressExpressionParser.TryParse(null!, null, out _));
    }

    [Fact]
    public void GarbageInput_ReturnsFalse()
    {
        Assert.False(AddressExpressionParser.TryParse("hello world", null, out _));
        Assert.False(AddressExpressionParser.TryParse("xyz+abc", null, out _));
    }

    [Fact]
    public void NullModuleList_FallsBackToPlainHex()
    {
        Assert.True(AddressExpressionParser.TryParse("0xABCD", null, out var addr));
        Assert.Equal(0xABCDUL, addr);
    }

    // ── Additional coverage (Phase 6) ──

    [Fact]
    public void PlainHexLowerCase_Succeeds()
    {
        Assert.True(AddressExpressionParser.TryParse("0xdeadbeef", null, out var addr));
        Assert.Equal(0xDEADBEEFUL, addr);
    }

    [Fact]
    public void ModulePlusOffset_WithoutHexPrefix_Succeeds()
    {
        var modules = new List<ModuleOverview>
        {
            new("game.exe", "0x00400000", "0x100000")
        };

        Assert.True(AddressExpressionParser.TryParse("game.exe+1A3F0", modules, out var addr));
        Assert.Equal(0x00400000UL + 0x1A3F0UL, addr);
    }

    [Fact]
    public void ModulePlusOffset_EmptyModuleList_FallsThrough()
    {
        var modules = new List<ModuleOverview>();
        Assert.False(AddressExpressionParser.TryParse("game.exe+0x100", modules, out _));
    }

    [Fact]
    public void LargeAddress_64Bit_Succeeds()
    {
        Assert.True(AddressExpressionParser.TryParse("0x7FF000000000", null, out var addr));
        Assert.Equal(0x7FF000000000UL, addr);
    }

    [Fact]
    public void ZeroAddress_Succeeds()
    {
        Assert.True(AddressExpressionParser.TryParse("0x0", null, out var addr));
        Assert.Equal(0UL, addr);
    }

    [Fact]
    public void MaxAddress_Succeeds()
    {
        Assert.True(AddressExpressionParser.TryParse("0xFFFFFFFFFFFFFFFF", null, out var addr));
        Assert.Equal(0xFFFFFFFFFFFFFFFFUL, addr);
    }

    [Fact]
    public void ModulePlusOffset_MultipleModules_FindsCorrectOne()
    {
        var modules = new List<ModuleOverview>
        {
            new("kernel32.dll", "0x76000000", "0x100000"),
            new("game.exe", "0x00400000", "0x80000"),
            new("ntdll.dll", "0x77000000", "0x200000"),
        };

        Assert.True(AddressExpressionParser.TryParse("ntdll.dll+0x5000", modules, out var addr));
        Assert.Equal(0x77000000UL + 0x5000UL, addr);
    }

    [Fact]
    public void PlusSignOnly_ReturnsFalse()
    {
        Assert.False(AddressExpressionParser.TryParse("+", null, out _));
    }

    [Fact]
    public void TrailingPlus_ReturnsFalse()
    {
        Assert.False(AddressExpressionParser.TryParse("game.exe+", null, out _));
    }

    [Theory]
    [InlineData("   0x400000   ")]
    [InlineData("\t0x400000\t")]
    public void WhitespacePadded_StillParses(string input)
    {
        Assert.True(AddressExpressionParser.TryParse(input, null, out var addr));
        Assert.Equal(0x400000UL, addr);
    }
}
