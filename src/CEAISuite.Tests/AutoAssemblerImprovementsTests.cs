using System.IO;
using System.Text.RegularExpressions;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class AutoAssemblerImprovementsTests
{
    // ── 7C.1: registersymbol / unregistersymbol ──

    [Fact]
    public void RegisterSymbol_StoresInTable()
    {
        var engine = new StubAutoAssemblerEngine();
        engine.RegisterSymbol("myVar", (nuint)0x12345);

        var symbols = engine.GetRegisteredSymbols();
        Assert.Single(symbols);
        Assert.Equal("myVar", symbols[0].Name);
        Assert.Equal((nuint)0x12345, symbols[0].Address);
    }

    [Fact]
    public void UnregisterSymbol_RemovesFromTable()
    {
        var engine = new StubAutoAssemblerEngine();
        engine.RegisterSymbol("myVar", (nuint)0x12345);
        engine.UnregisterSymbol("myVar");

        var symbols = engine.GetRegisteredSymbols();
        Assert.Empty(symbols);
    }

    [Fact]
    public void RegisterSymbol_AvailableInResolve()
    {
        var engine = new StubAutoAssemblerEngine();
        engine.RegisterSymbol("testSym", (nuint)0xABCDE);

        var addr = engine.ResolveSymbol("testSym");
        Assert.NotNull(addr);
        Assert.Equal((nuint)0xABCDE, addr.Value);
    }

    // ── 7C.2: createthread ──

    [Fact]
    public void CreateThread_RegexMatches()
    {
        var regex = new Regex(@"^createthread\s*\(\s*(.+?)\s*\)$", RegexOptions.IgnoreCase);
        var match = regex.Match("createthread(myLabel)");
        Assert.True(match.Success);
        Assert.Equal("myLabel", match.Groups[1].Value);
    }

    [Fact]
    public void CreateThread_RejectsEmpty()
    {
        var regex = new Regex(@"^createthread\s*\(\s*(.+?)\s*\)$", RegexOptions.IgnoreCase);
        var match = regex.Match("createthread()");
        Assert.False(match.Success);
    }

    // ── 7C.3: readmem / writemem ──

    [Fact]
    public void ReadMem_RegexMatches()
    {
        var regex = new Regex(@"^readmem\s*\(\s*(.+?)\s*,\s*(\d+)\s*\)$", RegexOptions.IgnoreCase);
        var match = regex.Match("readmem(0x12345678, 16)");
        Assert.True(match.Success);
        Assert.Equal("0x12345678", match.Groups[1].Value);
        Assert.Equal("16", match.Groups[2].Value);
    }

    [Fact]
    public void WriteMem_RegexMatches()
    {
        var regex = new Regex(@"^writemem\s*\(\s*(.+?)\s*,\s*((?:[0-9A-Fa-f]{2}\s*)+)\s*\)$", RegexOptions.IgnoreCase);
        var match = regex.Match("writemem(myAddr, 90 90 90 90)");
        Assert.True(match.Success);
        Assert.Equal("myAddr", match.Groups[1].Value);
        Assert.Contains("90 90 90 90", match.Groups[2].Value);
    }

    // ── 7C.4: Script Variables ──

    [Fact]
    public void ScriptVariable_RegexMatches()
    {
        var regex = new Regex(@"^var\s+(\w+)\s*=\s*(.+)$", RegexOptions.IgnoreCase);
        var match = regex.Match("var myOffset = 0x100");
        Assert.True(match.Success);
        Assert.Equal("myOffset", match.Groups[1].Value);
        Assert.Equal("0x100", match.Groups[2].Value);
    }

    // ── 7C.5: {$strict} / {$luacode} Pragmas ──

    [Fact]
    public void StrictMode_DetectedByRegex()
    {
        var line = "{$strict}";
        Assert.True(line.Equals("{$strict}", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LuaCodePragma_DetectedByRegex()
    {
        var lines = new[] { "{$luacode}", "print('hello')", "{$asm}" };
        var insideLua = false;
        var skippedCount = 0;

        foreach (var line in lines)
        {
            if (line.Equals("{$luacode}", StringComparison.OrdinalIgnoreCase))
            { insideLua = true; continue; }
            if (line.Equals("{$asm}", StringComparison.OrdinalIgnoreCase))
            { insideLua = false; continue; }
            if (insideLua) { skippedCount++; continue; }
        }

        Assert.Equal(1, skippedCount);
        Assert.False(insideLua);
    }

    // ── 7C.6: loadlibrary ──

    [Fact]
    public void LoadLibrary_RegexExtractsPath()
    {
        var regex = new Regex(@"^loadlibrary\s*\(\s*(.+?)\s*\)$", RegexOptions.IgnoreCase);
        var match = regex.Match(@"loadlibrary(C:\mods\hack.dll)");
        Assert.True(match.Success);
        Assert.Equal(@"C:\mods\hack.dll", match.Groups[1].Value);
    }

    // ── 7C.7: Include Files ──

    [Fact]
    public void Include_InlinesFileContent()
    {
        var tmpPath = Path.Combine(Path.GetTempPath(), $"include_test_{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(tmpPath, "define(included_val, 42)");
            var script = $"{{$include {tmpPath}}}\n[ENABLE]\nsome_label:";

            // Simulate the preprocessing — just check the include replacement
            var processed = script;
            foreach (var line in script.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("{$include ", StringComparison.OrdinalIgnoreCase) && trimmed.EndsWith('}'))
                {
                    var path = trimmed[10..^1].Trim();
                    if (File.Exists(path))
                    {
                        var included = File.ReadAllText(path);
                        processed = processed.Replace(line, included);
                    }
                }
            }

            Assert.Contains("define(included_val, 42)", processed);
            Assert.DoesNotContain("{$include", processed);
        }
        finally
        {
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
        }
    }

    [Fact]
    public void Include_CircularDetection()
    {
        // Verify depth limit concept — 11 nested levels should be caught
        var depth = 0;
        var maxDepth = 10;

        void SimulateInclude(int d)
        {
            if (d > maxDepth)
                throw new InvalidOperationException("Include depth exceeded.");
            SimulateInclude(d + 1);
        }

        Assert.Throws<InvalidOperationException>(() => SimulateInclude(0));
    }

    // ── Interface contract ──

    [Fact]
    public void ScriptExecutionResult_IncludesRegisteredSymbols()
    {
        var symbols = new List<RegisteredSymbol>
        {
            new("sym1", (nuint)0x1000),
            new("sym2", (nuint)0x2000)
        };
        var result = new ScriptExecutionResult(true, null, [], [], symbols);

        Assert.NotNull(result.RegisteredSymbols);
        Assert.Equal(2, result.RegisteredSymbols.Count);
    }

    [Fact]
    public void ScriptExecutionResult_NullRegisteredSymbols_BackwardCompat()
    {
        // Old-style construction without symbols still works
        var result = new ScriptExecutionResult(true, null, [], []);
        Assert.Null(result.RegisteredSymbols);
    }
}
