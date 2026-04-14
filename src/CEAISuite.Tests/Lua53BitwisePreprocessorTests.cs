using CEAISuite.Engine.Lua;
using Xunit;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for the Lua 5.3 → 5.2 bitwise operator preprocessor.
/// Verifies that | and &amp; operators are correctly transformed into bOr/bAnd function calls.
/// </summary>
public sealed class Lua53BitwisePreprocessorTests
{
    [Fact]
    public void SimpleOr_Transformed()
    {
        var result = Lua53BitwisePreprocessor.Preprocess("value = value | cb.value");
        Assert.Equal("value = bOr(value, cb.value)", result);
    }

    [Fact]
    public void SimpleAnd_Transformed()
    {
        var result = Lua53BitwisePreprocessor.Preprocess("result = a & b");
        Assert.Equal("result = bAnd(a, b)", result);
    }

    [Fact]
    public void AndWithParensAndNotEquals_BDFFHD_Pattern()
    {
        // From BDFFHD "Unlock Jobs": cb.Checked = (value &amp; cb.value) ~= 0
        var result = Lua53BitwisePreprocessor.Preprocess("cb.Checked = (value & cb.value) ~= 0");
        Assert.Contains("bAnd(value, cb.value)", result);
        Assert.Contains("~= 0", result); // ~= must be preserved (not-equals, not XOR)
    }

    [Fact]
    public void ChainedOr_TransformedNested()
    {
        // a | b | c should become bOr(bOr(a, b), c) or bOr(a, bOr(b, c))
        var result = Lua53BitwisePreprocessor.Preprocess("x = a | b | c");
        Assert.Contains("bOr", result);
        Assert.DoesNotContain("|", result);
    }

    [Fact]
    public void NoOperators_Unchanged()
    {
        var source = "local x = 42\nprint(x)";
        var result = Lua53BitwisePreprocessor.Preprocess(source);
        Assert.Equal(source, result);
    }

    [Fact]
    public void OperatorInsideString_NotTransformed()
    {
        var result = Lua53BitwisePreprocessor.Preprocess("local s = \"a | b & c\"");
        Assert.Equal("local s = \"a | b & c\"", result);
    }

    [Fact]
    public void OperatorInsideComment_NotTransformed()
    {
        var result = Lua53BitwisePreprocessor.Preprocess("-- a | b & c");
        Assert.Equal("-- a | b & c", result);
    }

    [Fact]
    public void MixedAndOr_CorrectPrecedence()
    {
        // & has higher precedence than |, so it should be replaced first
        var result = Lua53BitwisePreprocessor.Preprocess("x = a | b & c");
        // b & c should become bAnd(b, c) first, then a | bAnd(b, c) becomes bOr(a, bAnd(b, c))
        Assert.Contains("bAnd(b, c)", result);
        Assert.Contains("bOr", result);
        Assert.DoesNotContain("|", result);
        Assert.DoesNotContain("&", result);
    }

    [Fact]
    public void BDFFHD_WriteClasses_Pattern()
    {
        // The actual pattern from the "Unlock Jobs" script writeClasses function
        var source = @"  if cb.Checked then
      value = value | cb.value
    end";
        var result = Lua53BitwisePreprocessor.Preprocess(source);
        Assert.Contains("bOr(value, cb.value)", result);
        Assert.DoesNotContain("|", result);
    }

    [Fact]
    public void BDFFHD_ReadClasses_Pattern()
    {
        // The actual pattern from the "Unlock Jobs" script readClasses function
        var source = "    cb.Checked = (value & cb.value) ~= 0";
        var result = Lua53BitwisePreprocessor.Preprocess(source);
        Assert.Contains("bAnd", result);
        Assert.Contains("~= 0", result); // ~= preserved
    }

    [Fact]
    public void MultiLine_OnlyCodeLinesTransformed()
    {
        var source = "local x = a | b\n-- comment with |\nlocal y = c & d";
        var result = Lua53BitwisePreprocessor.Preprocess(source);
        Assert.Contains("bOr(a, b)", result);
        Assert.Contains("-- comment with |", result); // comment preserved
        Assert.Contains("bAnd(c, d)", result);
    }
}
