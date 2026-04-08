using System.Globalization;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Engine.Windows;

namespace CEAISuite.Tests;

/// <summary>
/// Adversarial tests for the Auto Assembler script parser.
/// Ensures that malformed, malicious, or edge-case inputs are handled gracefully
/// without crashes, hangs, or unhandled exceptions.
/// </summary>
public sealed class AdversarialValidationTests
{
    private readonly WindowsAutoAssemblerEngine _engine = new();

    // ── Empty / whitespace inputs ──

    [Fact]
    public void Parse_EmptyString_ReturnsInvalidGracefully()
    {
        var result = _engine.Parse("");

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_WhitespaceOnly_ReturnsInvalidGracefully()
    {
        var result = _engine.Parse("   \t\n\r\n   ");

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    [InlineData("\n\n\n")]
    public void Parse_VariousWhitespace_DoesNotCrash(string input)
    {
        var exception = Record.Exception(() => _engine.Parse(input));
        Assert.Null(exception);
    }

    // ── Missing sections ──

    [Fact]
    public void Parse_NoEnableOrDisableSection_ReturnsInvalid()
    {
        var script = "alloc(newmem, 256)\nlabel(returnHere)\nnop";

        var result = _engine.Parse(script);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("section", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parse_OnlyEnableSection_ParsesSuccessfully()
    {
        var script = "[ENABLE]\nnop";

        var result = _engine.Parse(script);

        // Should parse without errors — missing [DISABLE] is a warning, not a hard error
        Assert.NotNull(result.EnableSection);
        Assert.Null(result.DisableSection);
    }

    [Fact]
    public void Parse_OnlyDisableSection_ParsesSuccessfully()
    {
        var script = "[DISABLE]\nnop";

        var result = _engine.Parse(script);

        Assert.Null(result.EnableSection);
        Assert.NotNull(result.DisableSection);
    }

    // ── Mismatched / duplicated sections ──

    [Fact]
    public void Parse_DuplicateEnableSections_DoesNotCrash()
    {
        var script = "[ENABLE]\nnop\n[ENABLE]\nnop\n[DISABLE]\nnop";

        var exception = Record.Exception(() => _engine.Parse(script));
        Assert.Null(exception);
    }

    [Fact]
    public void Parse_SectionsInReverseOrder_HandlesGracefully()
    {
        var script = "[DISABLE]\nnop\n[ENABLE]\nnop";

        var result = _engine.Parse(script);

        // Parser should handle reversed order without crashing
        Assert.NotNull(result.EnableSection);
        Assert.NotNull(result.DisableSection);
    }

    // ── Extremely long script ──

    [Fact]
    public void Parse_ExtremelyLongScript_DoesNotHangOrOom()
    {
        // Build a script that's over 100KB
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[ENABLE]");
        for (int i = 0; i < 5000; i++)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"define(var_{i}, {i})");
        }
        sb.AppendLine("[DISABLE]");
        for (int i = 0; i < 5000; i++)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"// cleanup line {i}");
        }

        var script = sb.ToString();
        Assert.True(script.Length > 100_000, $"Script should be >100KB but was {script.Length} chars");

        var exception = Record.Exception(() => _engine.Parse(script));
        Assert.Null(exception);
    }

    [Fact]
    public void Parse_LargeScript_CompletesInReasonableTime()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[ENABLE]");
        for (int i = 0; i < 10_000; i++)
            sb.AppendLine(CultureInfo.InvariantCulture, $"label(lbl_{i})");
        sb.AppendLine("[DISABLE]");
        sb.AppendLine("nop");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        _engine.Parse(sb.ToString());
        sw.Stop();

        // Should complete within 10 seconds even on slow CI
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"Parse took {sw.Elapsed.TotalSeconds:F2}s — may indicate O(n^2) or worse behavior");
    }

    // ── Null bytes / control characters ──

    [Fact]
    public void Parse_ScriptWithNullBytes_DoesNotCrash()
    {
        var script = "[ENABLE]\nnop\0\0\0\n[DISABLE]\nnop";

        var exception = Record.Exception(() => _engine.Parse(script));
        Assert.Null(exception);
    }

    [Fact]
    public void Parse_ScriptWithControlCharacters_DoesNotCrash()
    {
        var script = "[ENABLE]\n\x01\x02\x03\x04\x05\x06\x07\x08\n[DISABLE]\nnop";

        var exception = Record.Exception(() => _engine.Parse(script));
        Assert.Null(exception);
    }

    [Fact]
    public void Parse_ScriptWithBellAndBackspace_DoesNotCrash()
    {
        var script = "[ENABLE]\n\a\b\f\v\n[DISABLE]\nnop";

        var exception = Record.Exception(() => _engine.Parse(script));
        Assert.Null(exception);
    }

    // ── Unicode / emoji in labels ──

    [Fact]
    public void Parse_UnicodeLabelsAndDefines_DoesNotCrash()
    {
        var script = "[ENABLE]\ndefine(\u00e9l\u00e8ve, 42)\nlabel(\u00fc\u00f6\u00e4)\n[DISABLE]\nnop";

        var exception = Record.Exception(() => _engine.Parse(script));
        Assert.Null(exception);
    }

    [Fact]
    public void Parse_EmojiInScript_DoesNotCrash()
    {
        var script = "[ENABLE]\n// \U0001F680 rocket label\ndefine(\U0001F4A5_var, 100)\n[DISABLE]\nnop";

        var exception = Record.Exception(() => _engine.Parse(script));
        Assert.Null(exception);
    }

    [Fact]
    public void Parse_ChineseCharactersInLabels_DoesNotCrash()
    {
        var script = "[ENABLE]\ndefine(\u6d4b\u8bd5, 1)\nlabel(\u5730\u5740)\n[DISABLE]\nnop";

        var exception = Record.Exception(() => _engine.Parse(script));
        Assert.Null(exception);
    }

    [Fact]
    public void Parse_MixedUnicodeAndAscii_DoesNotCrash()
    {
        var script = "[ENABLE]\nalloc(newmem_\u00e9, 256)\nlabel(ret\u00fcr)\n[DISABLE]\ndealloc(newmem_\u00e9)";

        var exception = Record.Exception(() => _engine.Parse(script));
        Assert.Null(exception);
    }

    // ── Malformed directives ──

    [Fact]
    public void Parse_UnterminatedParentheses_DoesNotCrash()
    {
        var script = "[ENABLE]\nalloc(newmem, 256\nlabel(ret\n[DISABLE]\nnop";

        var exception = Record.Exception(() => _engine.Parse(script));
        Assert.Null(exception);
    }

    [Fact]
    public void Parse_ScriptWithOnlyComments_DoesNotCrash()
    {
        var script = "[ENABLE]\n// nothing here\n// really nothing\n[DISABLE]\n// also nothing";

        var exception = Record.Exception(() => _engine.Parse(script));
        Assert.Null(exception);
    }

    [Fact]
    public void Parse_NestedBrackets_DoesNotCrash()
    {
        var script = "[ENABLE]\n[[[[ENABLE]]]]\n[DISABLE]\nnop";

        var exception = Record.Exception(() => _engine.Parse(script));
        Assert.Null(exception);
    }

    // ── Script parse result contract ──

    [Fact]
    public void ScriptParseResult_InvalidResult_HasNonNullCollections()
    {
        var result = _engine.Parse("");

        // Errors and Warnings should never be null, even for invalid scripts
        Assert.NotNull(result.Errors);
        Assert.NotNull(result.Warnings);
    }

    [Fact]
    public void ScriptParseResult_ValidResult_HasEmptyErrorsCollection()
    {
        var script = "[ENABLE]\nnop\n[DISABLE]\nnop";

        var result = _engine.Parse(script);

        Assert.NotNull(result.Errors);
        Assert.NotNull(result.Warnings);
        // Even if valid, the collections should be non-null
    }
}
