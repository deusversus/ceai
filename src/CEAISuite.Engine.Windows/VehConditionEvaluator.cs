using System.Globalization;
using System.Text.RegularExpressions;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Engine.Windows;

/// <summary>
/// Evaluates breakpoint conditions against VEH hit events.
/// Supports register comparisons, hit count thresholds, and memory comparisons.
/// Conditions are evaluated host-side after the hit is recorded — the native agent
/// always records all hits; filtering happens here.
/// </summary>
internal static partial class VehConditionEvaluator
{
    /// <summary>
    /// Evaluate a condition against a hit event.
    /// Returns true if the hit passes the condition (should be yielded to caller).
    /// </summary>
    public static bool Evaluate(
        BreakpointCondition condition,
        VehHitEvent hit,
        int currentHitCount,
        Func<nuint, int, byte[]?>? readMemory = null)
    {
        return condition.Type switch
        {
            BreakpointConditionType.RegisterCompare => EvaluateRegisterCompare(condition.Expression, hit.Registers),
            BreakpointConditionType.HitCount => EvaluateHitCount(condition.Expression, currentHitCount),
            BreakpointConditionType.MemoryCompare => EvaluateMemoryCompare(condition.Expression, readMemory),
            _ => true // unknown condition type — pass through
        };
    }

    /// <summary>
    /// Evaluate a register comparison expression.
    /// Supported formats: "RAX == 0x100", "RCX > 0", "RDX != 5", "RSP &lt;= 0x7FFE0000"
    /// Register names: RAX, RBX, RCX, RDX, RSI, RDI, RSP, RBP, R8-R11
    /// Values: decimal or hex (0x prefix)
    /// Operators: ==, !=, &gt;, &lt;, &gt;=, &lt;=
    /// </summary>
    private static bool EvaluateRegisterCompare(string expression, RegisterSnapshot regs)
    {
        var match = RegisterCompareRegex().Match(expression);
        if (!match.Success)
            return true; // unparseable expression — pass through

        var regName = match.Groups[1].Value.ToUpperInvariant();
        var op = match.Groups[2].Value;
        var valueStr = match.Groups[3].Value;

        if (!TryParseValue(valueStr, out var comparand))
            return true;

        var regValue = GetRegisterValue(regName, regs);
        if (regValue is null)
            return true; // unknown register — pass through

        return op switch
        {
            "==" => regValue.Value == comparand,
            "!=" => regValue.Value != comparand,
            ">" => regValue.Value > comparand,
            "<" => regValue.Value < comparand,
            ">=" => regValue.Value >= comparand,
            "<=" => regValue.Value <= comparand,
            _ => true
        };
    }

    /// <summary>
    /// Evaluate a hit count condition.
    /// Expression is a threshold number: condition passes when currentHitCount reaches it.
    /// Formats: "100" (break on 100th hit), "== 50" (break on exactly 50th), "> 10" (break after 10th)
    /// </summary>
    private static bool EvaluateHitCount(string expression, int currentHitCount)
    {
        var trimmed = expression.Trim();

        // Simple number = "break when count reaches N"
        if (int.TryParse(trimmed, out var threshold))
            return currentHitCount >= threshold;

        // Operator + number
        var match = HitCountRegex().Match(trimmed);
        if (!match.Success)
            return true;

        var op = match.Groups[1].Value;
        if (!int.TryParse(match.Groups[2].Value, out var value))
            return true;

        return op switch
        {
            "==" => currentHitCount == value,
            "!=" => currentHitCount != value,
            ">" => currentHitCount > value,
            "<" => currentHitCount < value,
            ">=" => currentHitCount >= value,
            "<=" => currentHitCount <= value,
            "%" => value > 0 && currentHitCount % value == 0, // every Nth hit
            _ => true
        };
    }

    /// <summary>
    /// Evaluate a memory comparison.
    /// Expression format: "address == value" where address and value are hex.
    /// Example: "0x7FFE0000 == 0x01" (byte at address equals 1)
    /// Requires a readMemory callback to read process memory.
    /// </summary>
    private static bool EvaluateMemoryCompare(string expression, Func<nuint, int, byte[]?>? readMemory)
    {
        if (readMemory is null)
            return true; // no memory reader — pass through

        var match = MemoryCompareRegex().Match(expression);
        if (!match.Success)
            return true;

        if (!TryParseValue(match.Groups[1].Value, out var address))
            return true;
        var op = match.Groups[2].Value;
        if (!TryParseValue(match.Groups[3].Value, out var expected))
            return true;

        // Read up to 8 bytes from the address
        var bytes = readMemory((nuint)address, 8);
        if (bytes is null || bytes.Length == 0)
            return true; // read failed — pass through

        // Interpret as little-endian unsigned value
        ulong actual = 0;
        for (int i = 0; i < Math.Min(bytes.Length, 8); i++)
            actual |= (ulong)bytes[i] << (i * 8);

        return op switch
        {
            "==" => actual == expected,
            "!=" => actual != expected,
            ">" => actual > expected,
            "<" => actual < expected,
            ">=" => actual >= expected,
            "<=" => actual <= expected,
            _ => true
        };
    }

    private static ulong? GetRegisterValue(string name, RegisterSnapshot regs) => name switch
    {
        "RAX" or "EAX" => regs.Rax,
        "RBX" or "EBX" => regs.Rbx,
        "RCX" or "ECX" => regs.Rcx,
        "RDX" or "EDX" => regs.Rdx,
        "RSI" or "ESI" => regs.Rsi,
        "RDI" or "EDI" => regs.Rdi,
        "RSP" or "ESP" => regs.Rsp,
        "RBP" or "EBP" => regs.Rbp,
        "R8" => regs.R8,
        "R9" => regs.R9,
        "R10" => regs.R10,
        "R11" => regs.R11,
        _ => null
    };

    private static bool TryParseValue(string s, out ulong result)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || s.StartsWith("0X", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result);
        return ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    // Regex: register_name operator value
    // e.g., "RAX == 0x100", "RCX > 0", "R8 != 42"
    [GeneratedRegex(@"^\s*(R?[A-Z][A-Z0-9]{1,2})\s*(==|!=|>=|<=|>|<)\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex RegisterCompareRegex();

    // Regex: operator number (for hit count)
    // e.g., "== 50", "> 10", "% 100"
    [GeneratedRegex(@"^\s*(==|!=|>=|<=|>|<|%)\s*(\d+)\s*$")]
    private static partial Regex HitCountRegex();

    // Regex: address operator value (for memory compare)
    // e.g., "0x7FFE0000 == 0x01"
    [GeneratedRegex(@"^\s*(0x[0-9A-Fa-f]+|\d+)\s*(==|!=|>=|<=|>|<)\s*(0x[0-9A-Fa-f]+|\d+)\s*$")]
    private static partial Regex MemoryCompareRegex();
}
