using CEAISuite.Engine.Abstractions;
using CEAISuite.Engine.Windows;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for the VEH condition evaluator — register comparisons,
/// hit count thresholds, and memory comparisons.
/// </summary>
public class VehConditionEvaluatorTests
{
    private static readonly RegisterSnapshot TestRegs = new(
        Rax: 0x100, Rbx: 0x200, Rcx: 0, Rdx: 0xFFFFFFFF,
        Rsi: 0, Rdi: 0, Rsp: 0x7FFE0000, Rbp: 0,
        R8: 42, R9: 0, R10: 0, R11: 0);

    private static readonly VehHitEvent TestHit = new(
        (nuint)0x400000, 1234, VehBreakpointType.Execute, (nuint)0x1,
        TestRegs, Environment.TickCount64);

    // ── Register Compare ──

    [Theory]
    [InlineData("RAX == 0x100", true)]
    [InlineData("RAX == 0x200", false)]
    [InlineData("RAX != 0x200", true)]
    [InlineData("RAX != 0x100", false)]
    [InlineData("RAX > 0xFF", true)]
    [InlineData("RAX > 0x100", false)]
    [InlineData("RAX < 0x200", true)]
    [InlineData("RAX >= 0x100", true)]
    [InlineData("RAX <= 0x100", true)]
    [InlineData("RBX == 512", true)]       // decimal
    [InlineData("RCX == 0", true)]
    [InlineData("RDX == 0xFFFFFFFF", true)]
    [InlineData("RSP == 0x7FFE0000", true)]
    [InlineData("R8 == 42", true)]
    [InlineData("R8 > 100", false)]
    public void RegisterCompare_EvaluatesCorrectly(string expression, bool expected)
    {
        var condition = new BreakpointCondition(expression, BreakpointConditionType.RegisterCompare);
        var result = VehConditionEvaluator.Evaluate(condition, TestHit, 1);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("EAX == 0x100", true)]     // 32-bit alias
    [InlineData("ECX == 0", true)]
    public void RegisterCompare_Supports32BitAliases(string expression, bool expected)
    {
        var condition = new BreakpointCondition(expression, BreakpointConditionType.RegisterCompare);
        var result = VehConditionEvaluator.Evaluate(condition, TestHit, 1);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void RegisterCompare_UnknownRegister_PassesThrough()
    {
        var condition = new BreakpointCondition("XYZ == 0", BreakpointConditionType.RegisterCompare);
        var result = VehConditionEvaluator.Evaluate(condition, TestHit, 1);
        Assert.True(result); // unknown register → pass through
    }

    [Fact]
    public void RegisterCompare_MalformedExpression_PassesThrough()
    {
        var condition = new BreakpointCondition("not a valid expression", BreakpointConditionType.RegisterCompare);
        var result = VehConditionEvaluator.Evaluate(condition, TestHit, 1);
        Assert.True(result); // unparseable → pass through
    }

    // ── Hit Count ──

    [Theory]
    [InlineData("10", 10, true)]    // threshold met
    [InlineData("10", 9, false)]    // threshold not met
    [InlineData("10", 15, true)]    // above threshold
    [InlineData("== 5", 5, true)]
    [InlineData("== 5", 6, false)]
    [InlineData("> 10", 11, true)]
    [InlineData("> 10", 10, false)]
    [InlineData("< 5", 3, true)]
    [InlineData("< 5", 5, false)]
    [InlineData("% 10", 20, true)]  // every 10th hit
    [InlineData("% 10", 21, false)]
    public void HitCount_EvaluatesCorrectly(string expression, int currentCount, bool expected)
    {
        var condition = new BreakpointCondition(expression, BreakpointConditionType.HitCount);
        var result = VehConditionEvaluator.Evaluate(condition, TestHit, currentCount);
        Assert.Equal(expected, result);
    }

    // ── Memory Compare ──

    [Fact]
    public void MemoryCompare_MatchingValue_ReturnsTrue()
    {
        var condition = new BreakpointCondition("0x1000 == 0x42", BreakpointConditionType.MemoryCompare);
        byte[] ReadMem(nuint addr, int size) => [0x42, 0, 0, 0, 0, 0, 0, 0];
        var result = VehConditionEvaluator.Evaluate(condition, TestHit, 1, ReadMem);
        Assert.True(result);
    }

    [Fact]
    public void MemoryCompare_NonMatchingValue_ReturnsFalse()
    {
        var condition = new BreakpointCondition("0x1000 == 0x42", BreakpointConditionType.MemoryCompare);
        byte[] ReadMem(nuint addr, int size) => [0x43, 0, 0, 0, 0, 0, 0, 0];
        var result = VehConditionEvaluator.Evaluate(condition, TestHit, 1, ReadMem);
        Assert.False(result);
    }

    [Fact]
    public void MemoryCompare_NoReadCallback_PassesThrough()
    {
        var condition = new BreakpointCondition("0x1000 == 0x42", BreakpointConditionType.MemoryCompare);
        var result = VehConditionEvaluator.Evaluate(condition, TestHit, 1, readMemory: null);
        Assert.True(result); // no reader → pass through
    }

    [Fact]
    public void MemoryCompare_ReadFails_PassesThrough()
    {
        var condition = new BreakpointCondition("0x1000 == 0x42", BreakpointConditionType.MemoryCompare);
        byte[]? ReadMem(nuint addr, int size) => null;
        var result = VehConditionEvaluator.Evaluate(condition, TestHit, 1, ReadMem);
        Assert.True(result); // read failure → pass through
    }

    // ── Edge Cases ──

    [Fact]
    public void HitCount_ModuloZero_ReturnsFalse()
    {
        // % 0 should not divide by zero — short-circuit via value > 0 check
        var condition = new BreakpointCondition("% 0", BreakpointConditionType.HitCount);
        var result = VehConditionEvaluator.Evaluate(condition, TestHit, 10);
        Assert.False(result);
    }

    [Theory]
    [InlineData("rax == 0x100", true)]      // lowercase
    [InlineData("Rax == 0x100", true)]      // mixed case
    [InlineData("eax == 0x100", true)]      // lowercase 32-bit alias
    public void RegisterCompare_CaseInsensitive(string expression, bool expected)
    {
        var condition = new BreakpointCondition(expression, BreakpointConditionType.RegisterCompare);
        var result = VehConditionEvaluator.Evaluate(condition, TestHit, 1);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void RegisterCompare_32BitAlias_MasksTo32Bits()
    {
        // RAX = 0x100, but if it were 0x100000100, EAX should be 0x100
        var regsHigh = new RegisterSnapshot(
            Rax: 0x100000100, Rbx: 0, Rcx: 0, Rdx: 0,
            Rsi: 0, Rdi: 0, Rsp: 0, Rbp: 0,
            R8: 0, R9: 0, R10: 0, R11: 0);
        var hitHigh = new VehHitEvent(
            (nuint)0x400000, 1234, VehBreakpointType.Execute, (nuint)0x1,
            regsHigh, Environment.TickCount64);

        // EAX should see only low 32 bits = 0x100
        var condEax = new BreakpointCondition("EAX == 0x100", BreakpointConditionType.RegisterCompare);
        Assert.True(VehConditionEvaluator.Evaluate(condEax, hitHigh, 1));

        // RAX should see full 64-bit value
        var condRax = new BreakpointCondition("RAX == 0x100", BreakpointConditionType.RegisterCompare);
        Assert.False(VehConditionEvaluator.Evaluate(condRax, hitHigh, 1));
    }

    [Fact]
    public void RegisterCompare_NegativeDecimal_PassesThrough()
    {
        // ulong.TryParse rejects negative values — condition should pass through
        var condition = new BreakpointCondition("RAX == -1", BreakpointConditionType.RegisterCompare);
        var result = VehConditionEvaluator.Evaluate(condition, TestHit, 1);
        Assert.True(result); // unparseable value → pass through
    }
}
