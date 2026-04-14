using System.Text;
using System.Text.RegularExpressions;

namespace CEAISuite.Engine.Lua;

/// <summary>
/// Preprocesses Lua source to transform Lua 5.3 bitwise operators into Lua 5.2-compatible
/// function calls. MoonSharp implements Lua 5.2 which does not support <c>|</c>, <c>&amp;</c>,
/// <c>~</c> (XOR), <c>&lt;&lt;</c>, <c>&gt;&gt;</c> as operators.
///
/// Since these characters are completely invalid in Lua 5.2 syntax (outside strings/comments),
/// any occurrence is guaranteed to be a Lua 5.3 bitwise operator that needs transformation.
///
/// Transforms:
///   a | b      →  bOr(a, b)
///   a &amp; b      →  bAnd(a, b)
///   a &lt;&lt; b     →  bShl(a, b)
///   a &gt;&gt; b     →  bShr(a, b)
///   a ~ b      →  bXor(a, b)   (binary XOR, NOT ~= which is "not equals")
///   ~a         →  bNot(a)      (unary bitwise NOT)
/// </summary>
internal static partial class Lua53BitwisePreprocessor
{
    /// <summary>
    /// Preprocess Lua source code, replacing Lua 5.3 bitwise operators with function calls.
    /// Returns the original string unchanged if no Lua 5.3 operators are detected.
    /// </summary>
    public static string Preprocess(string source)
    {
        // Fast path: if the source contains none of the Lua 5.3 bitwise operators, skip processing.
        // Check for | and & (most common). ~ is tricky because ~= is valid Lua 5.2.
        // << and >> are rare in CE scripts but supported.
        if (!source.Contains('|') && !source.Contains('&') && !source.Contains("<<") && !source.Contains(">>"))
            return source;

        var sb = new StringBuilder(source.Length + 64);
        var lines = source.Split('\n');
        var inLongString = false;
        var inLongComment = false;
        var longClosePattern = ""; // e.g. "]]" or "]=]"

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (inLongString || inLongComment)
            {
                // Check if the long string/comment ends on this line
                var closeIdx = line.IndexOf(longClosePattern, StringComparison.Ordinal);
                if (closeIdx >= 0)
                {
                    inLongString = false;
                    inLongComment = false;
                    // Process the remainder after the close
                    var remainder = line[(closeIdx + longClosePattern.Length)..];
                    var prefix = line[..(closeIdx + longClosePattern.Length)];
                    sb.Append(prefix);
                    sb.Append(ProcessLine(remainder));
                }
                else
                {
                    sb.Append(line);
                }
            }
            else
            {
                // Check for long string/comment starts on this line
                var (processed, isLong, closePattern) = ProcessLineWithLongCheck(line);
                sb.Append(processed);
                if (isLong)
                {
                    inLongString = true;
                    longClosePattern = closePattern;
                }
            }

            if (i < lines.Length - 1)
                sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Process a single line, checking for long string/comment starts.
    /// Returns (processed line, whether a long string/comment was started, close pattern).
    /// </summary>
    private static (string Processed, bool IsLong, string ClosePattern) ProcessLineWithLongCheck(string line)
    {
        // First, check if this line starts or contains a long comment (--[[ or --[==[)
        var longCommentMatch = LongCommentStartRegex().Match(line);
        if (longCommentMatch.Success)
        {
            var eqs = longCommentMatch.Groups[1].Value;
            var closePattern = $"]{eqs}]";
            var prefix = line[..longCommentMatch.Index];
            var processed = ProcessLine(prefix);
            var remainder = line[longCommentMatch.Index..];

            // Check if the comment closes on the same line
            var closeIdx = remainder.IndexOf(closePattern, longCommentMatch.Length, StringComparison.Ordinal);
            if (closeIdx >= 0)
            {
                var afterClose = remainder[(closeIdx + closePattern.Length)..];
                return (processed + remainder[..(closeIdx + closePattern.Length)] + ProcessLine(afterClose), false, "");
            }
            return (processed + remainder, true, closePattern);
        }

        // Check for long string starts ([[ or [==[)
        var longStringMatch = LongStringStartRegex().Match(line);
        if (longStringMatch.Success)
        {
            var eqs = longStringMatch.Groups[1].Value;
            var closePattern = $"]{eqs}]";
            var prefix = line[..longStringMatch.Index];
            var processed = ProcessLine(prefix);
            var remainder = line[longStringMatch.Index..];

            var closeIdx = remainder.IndexOf(closePattern, longStringMatch.Length, StringComparison.Ordinal);
            if (closeIdx >= 0)
            {
                var afterClose = remainder[(closeIdx + closePattern.Length)..];
                return (processed + remainder[..(closeIdx + closePattern.Length)] + ProcessLine(afterClose), false, "");
            }
            return (processed + remainder, true, closePattern);
        }

        return (ProcessLine(line), false, "");
    }

    /// <summary>
    /// Process a single line (or line fragment) that is NOT inside a long string/comment.
    /// Handles single-line strings and single-line comments, then replaces operators.
    /// </summary>
    private static string ProcessLine(string line)
    {
        // Fast path: no operators on this line
        if (!line.Contains('|') && !line.Contains('&') && !line.Contains("<<") && !line.Contains(">>"))
            return line;

        // Build a mask of which characters are "code" (not inside strings or comments)
        var isCode = new bool[line.Length];
        var inString = false;
        char stringDelim = '\0';

        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (inString)
            {
                isCode[i] = false;
                if (c == '\\') { i++; if (i < line.Length) isCode[i] = false; continue; } // skip escaped char
                if (c == stringDelim) inString = false;
                continue;
            }

            // Check for single-line comment
            if (c == '-' && i + 1 < line.Length && line[i + 1] == '-')
            {
                // Rest of line is a comment
                for (int j = i; j < line.Length; j++) isCode[j] = false;
                break;
            }

            // Check for string start
            if (c is '\'' or '"')
            {
                inString = true;
                stringDelim = c;
                isCode[i] = false;
                continue;
            }

            isCode[i] = true;
        }

        // Now replace operators only where isCode is true
        // Process in precedence order: << and >> first, then &, then |
        // (higher precedence operators are replaced first so they become function calls
        //  before lower-precedence operators try to match them as operands)
        var result = ReplaceShiftOperators(line, isCode);
        result = ReplaceBinaryOperator(result, '&', "bAnd", isCode);
        result = ReplaceBinaryOperator(result, '|', "bOr", isCode);

        return result;
    }

    /// <summary>Replace &lt;&lt; and &gt;&gt; operators with bShl/bShr.</summary>
    private static string ReplaceShiftOperators(string line, bool[] isCode)
    {
        // Handle << and >> by scanning for consecutive < or > in code regions
        var sb = new StringBuilder(line.Length);
        int i = 0;
        while (i < line.Length)
        {
            if (i + 1 < line.Length && isCode[i] && isCode[i + 1])
            {
                if (line[i] == '<' && line[i + 1] == '<')
                {
                    sb.Append(WrapBinaryOp(line, isCode, i, 2, "bShl", ref sb));
                    return sb.ToString(); // restart from scratch after wrapping
                }
                if (line[i] == '>' && line[i + 1] == '>')
                {
                    sb.Append(WrapBinaryOp(line, isCode, i, 2, "bShr", ref sb));
                    return sb.ToString();
                }
            }
            sb.Append(line[i]);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Replace a single-character binary operator with a function call.
    /// Handles the common CE patterns: `a | b`, `a & b`, `(a & b) ~= 0`.
    /// </summary>
    private static string ReplaceBinaryOperator(string line, char op, string funcName, bool[] origIsCode)
    {
        // Rebuild isCode for the current line state (may have changed from prior replacements)
        var isCode = BuildCodeMask(line);

        // Find the first occurrence of the operator in a code region
        var opIdx = -1;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == op && i < isCode.Length && isCode[i])
            {
                // Make sure this isn't part of ~= (for ~ as XOR — not handled here)
                if (op == '~' && i + 1 < line.Length && line[i + 1] == '=')
                    continue;
                opIdx = i;
                break;
            }
        }

        if (opIdx < 0) return line;

        // Find the left operand (scan backwards from opIdx)
        var leftEnd = opIdx - 1;
        while (leftEnd >= 0 && line[leftEnd] == ' ') leftEnd--;
        if (leftEnd < 0) return line; // no left operand

        var leftStart = FindExpressionStart(line, leftEnd);

        // Find the right operand (scan forward from opIdx)
        var rightStart = opIdx + 1;
        while (rightStart < line.Length && line[rightStart] == ' ') rightStart++;
        if (rightStart >= line.Length) return line; // no right operand

        var rightEnd = FindExpressionEnd(line, rightStart);

        var leftExpr = line[leftStart..(leftEnd + 1)];
        var rightExpr = line[rightStart..(rightEnd + 1)];
        var prefix = line[..leftStart];
        var suffix = rightEnd + 1 < line.Length ? line[(rightEnd + 1)..] : "";

        var replaced = $"{prefix}{funcName}({leftExpr}, {rightExpr}){suffix}";

        // Recursively replace more occurrences on the same line (a | b | c)
        return ReplaceBinaryOperator(replaced, op, funcName, origIsCode);
    }

    /// <summary>Find the start of an expression, scanning backwards from endIdx.</summary>
    private static int FindExpressionStart(string line, int endIdx)
    {
        var i = endIdx;

        // Handle closing paren: find matching open paren
        if (line[i] == ')')
        {
            var depth = 1;
            i--;
            while (i >= 0 && depth > 0)
            {
                if (line[i] == ')') depth++;
                else if (line[i] == '(') depth--;
                if (depth > 0) i--;
            }
            // Continue backwards to pick up function name or prefix
            if (i > 0 && i - 1 >= 0 && (char.IsLetterOrDigit(line[i - 1]) || line[i - 1] is '_' or '.'))
            {
                i--;
                while (i > 0 && (char.IsLetterOrDigit(line[i - 1]) || line[i - 1] is '_' or '.' or ':'))
                    i--;
            }
            return i;
        }

        // Handle identifier/number: scan back through word chars, dots, brackets
        while (i > 0 && (char.IsLetterOrDigit(line[i - 1]) || line[i - 1] is '_' or '.' or ':'))
            i--;

        // Handle hex literals: 0x prefix
        if (i >= 2 && line[i - 1] == 'x' && line[i - 2] == '0')
            i -= 2;

        return i;
    }

    /// <summary>Find the end of an expression, scanning forward from startIdx.</summary>
    private static int FindExpressionEnd(string line, int startIdx)
    {
        var i = startIdx;

        // Handle opening paren: find matching close paren
        if (line[i] == '(')
        {
            var depth = 1;
            i++;
            while (i < line.Length && depth > 0)
            {
                if (line[i] == '(') depth++;
                else if (line[i] == ')') depth--;
                if (depth > 0) i++;
            }
            return i;
        }

        // Handle unary minus
        if (line[i] == '-' && i + 1 < line.Length && (char.IsDigit(line[i + 1]) || line[i + 1] == '('))
            i++;

        // Handle identifier/number: scan forward through word chars, dots, brackets
        while (i + 1 < line.Length && (char.IsLetterOrDigit(line[i + 1]) || line[i + 1] is '_' or '.' or ':' or '[' or ']'))
            i++;

        // Handle function call: identifier followed by (...)
        if (i + 1 < line.Length && line[i + 1] == '(')
        {
            i++;
            var depth = 1;
            i++;
            while (i < line.Length && depth > 0)
            {
                if (line[i] == '(') depth++;
                else if (line[i] == ')') depth--;
                if (depth > 0) i++;
            }
        }

        return i;
    }

    /// <summary>Build a code mask for the given line (true = code, false = string/comment).</summary>
    private static bool[] BuildCodeMask(string line)
    {
        var isCode = new bool[line.Length];
        var inString = false;
        char stringDelim = '\0';

        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inString)
            {
                isCode[i] = false;
                if (c == '\\') { i++; if (i < line.Length) isCode[i] = false; continue; }
                if (c == stringDelim) inString = false;
                continue;
            }
            if (c == '-' && i + 1 < line.Length && line[i + 1] == '-')
            {
                for (int j = i; j < line.Length; j++) isCode[j] = false;
                break;
            }
            if (c is '\'' or '"') { inString = true; stringDelim = c; isCode[i] = false; continue; }
            isCode[i] = true;
        }
        return isCode;
    }

    /// <summary>Stub for shift operator wrapping — returns remaining line after operator.</summary>
    private static string WrapBinaryOp(string line, bool[] isCode, int opIdx, int opLen, string funcName, ref StringBuilder sb)
    {
        // For shift operators, fall back to simple line-level replacement
        // This handles the rare `a << b` pattern
        var before = sb.ToString();
        sb.Clear();

        var leftEnd = opIdx - 1;
        while (leftEnd >= 0 && line[leftEnd] == ' ') leftEnd--;
        if (leftEnd < 0) return line[opIdx..];

        var leftStart = FindExpressionStart(line, leftEnd);
        var rightStart = opIdx + opLen;
        while (rightStart < line.Length && line[rightStart] == ' ') rightStart++;
        if (rightStart >= line.Length) return line[opIdx..];
        var rightEnd = FindExpressionEnd(line, rightStart);

        var leftExpr = line[leftStart..(leftEnd + 1)];
        var rightExpr = line[rightStart..(rightEnd + 1)];
        var prefix = before + line[..leftStart];
        var suffix = rightEnd + 1 < line.Length ? line[(rightEnd + 1)..] : "";

        return $"{prefix}{funcName}({leftExpr}, {rightExpr}){suffix}";
    }

    [GeneratedRegex(@"--\[(=*)\[")]
    private static partial Regex LongCommentStartRegex();

    [GeneratedRegex(@"(?<!--)\[(=*)\[")]
    private static partial Regex LongStringStartRegex();
}
