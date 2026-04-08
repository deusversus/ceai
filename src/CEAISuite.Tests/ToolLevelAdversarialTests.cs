using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

/// <summary>
/// Adversarial tests for tool-level entry points (ValidateScript, ValidateScriptDeep,
/// CreateScriptEntry) using AiToolFunctions with stub dependencies.
/// Verifies that adversarial script content does not crash tools or produce
/// misleading results.
/// </summary>
public class ToolLevelAdversarialTests
{
    private static (AiToolFunctions tools, AddressTableService addressTable, StubAutoAssemblerEngine assembler)
        CreateTools()
    {
        var engineFacade = new StubEngineFacade();
        var sessionRepo = new StubSessionRepository();
        var dashboardService = new WorkspaceDashboardService(engineFacade, sessionRepo);
        var scanEngine = new StubScanEngine();
        var scanService = new ScanService(scanEngine);
        var disasmEngine = new StubDisassemblyEngine();
        var disassemblyService = new DisassemblyService(disasmEngine);
        var scriptGenService = new ScriptGenerationService();
        var addressTableService = new AddressTableService(engineFacade);
        var assembler = new StubAutoAssemblerEngine();

        var tools = new AiToolFunctions(
            engineFacade,
            dashboardService,
            scanService,
            addressTableService,
            disassemblyService,
            scriptGenService,
            autoAssemblerEngine: assembler);

        return (tools, addressTableService, assembler);
    }

    private static AddressTableNode AddScriptNode(
        AddressTableService addressTable,
        string id,
        string label,
        string script)
    {
        var node = new AddressTableNode(id, label, false)
        {
            AssemblerScript = script,
            Address = "(script)"
        };
        addressTable.Roots.Add(node);
        return node;
    }

    // ── A. ValidateScript with adversarial inputs ──

    [Fact]
    public async Task ValidateScript_EmptyScript_ReturnsValidationResult()
    {
        var (tools, addressTable, _) = CreateTools();
        AddScriptNode(addressTable, "s1", "Empty Script", "");

        var result = await tools.ValidateScript("s1");

        Assert.NotNull(result);
        // Stub returns valid by default, so result should mention validity
        Assert.Contains("valid", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateScript_ScriptWithNullBytes_NoCrash()
    {
        var (tools, addressTable, _) = CreateTools();
        var scriptWithNulls = "[ENABLE]\0\0alloc(mem,64)\0\n[DISABLE]\0dealloc(mem)";
        AddScriptNode(addressTable, "s2", "Null Byte Script", scriptWithNulls);

        var result = await tools.ValidateScript("s2");

        Assert.NotNull(result);
        // Should not crash — stub always returns valid
        Assert.DoesNotContain("Exception", result);
    }

    [Fact]
    public async Task ValidateScript_VeryLargeScript_NoCrash()
    {
        var (tools, addressTable, _) = CreateTools();
        // Generate a 100KB+ script
        var largeScript = "[ENABLE]\n" + new string('A', 100_000) + "\n[DISABLE]\ndealloc(mem)";
        AddScriptNode(addressTable, "s3", "Large Script", largeScript);

        var result = await tools.ValidateScript("s3");

        Assert.NotNull(result);
    }

    [Fact]
    public async Task ValidateScript_UnicodeAndEmoji_NoCrash()
    {
        var (tools, addressTable, _) = CreateTools();
        var emojiScript = "[ENABLE]\n// 修改生命值 ❤️ 🎮\nalloc(newmem,64)\n[DISABLE]\ndealloc(newmem)";
        AddScriptNode(addressTable, "s4", "Emoji Script 🎮", emojiScript);

        var result = await tools.ValidateScript("s4");

        Assert.NotNull(result);
        Assert.Contains("valid", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateScript_ScriptWithControlCharacters_NoCrash()
    {
        var (tools, addressTable, _) = CreateTools();
        var controlScript = "[ENABLE]\r\n\talloc(mem,64)\x01\x02\x03\n[DISABLE]\ndealloc(mem)";
        AddScriptNode(addressTable, "s5", "Control Chars", controlScript);

        var result = await tools.ValidateScript("s5");

        Assert.NotNull(result);
    }

    // ── B. ValidateScript with missing node ──

    [Fact]
    public async Task ValidateScript_NonexistentNodeId_ReturnsNotFoundMessage()
    {
        var (tools, addressTable, _) = CreateTools();
        // Add one script so there are available scripts to list
        AddScriptNode(addressTable, "s1", "Real Script", "[ENABLE]\n[DISABLE]");

        var result = await tools.ValidateScript("nonexistent-id");

        Assert.Contains("not found", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateScript_NonScriptNode_ReturnsNotScriptMessage()
    {
        var (tools, addressTable, _) = CreateTools();
        // Add a value entry (not a script)
        var valueNode = new AddressTableNode("v1", "Health", false)
        {
            Address = "00FF1234",
            DataType = MemoryDataType.Int32,
            CurrentValue = "100"
        };
        addressTable.Roots.Add(valueNode);

        var result = await tools.ValidateScript("v1");

        Assert.Contains("not a script", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateScript_EmptyAddressTable_ReturnsNotFound()
    {
        var (tools, _, _) = CreateTools();

        var result = await tools.ValidateScript("any-id");

        Assert.Contains("not found", result, StringComparison.OrdinalIgnoreCase);
    }

    // ── C. ValidateScript when assembler reports errors ──

    [Fact]
    public async Task ValidateScript_WithParseErrors_ReturnsErrorDetails()
    {
        var (tools, addressTable, assembler) = CreateTools();
        assembler.NextParseResult = new ScriptParseResult(
            false,
            ["Syntax error at line 3", "Unknown directive 'allocx'"],
            ["Consider using 'alloc' instead"],
            null,
            null);

        AddScriptNode(addressTable, "s-err", "Bad Script", "[ENABLE]\nallocx(mem,64)");

        var result = await tools.ValidateScript("s-err");

        Assert.Contains("issues", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Syntax error", result);
        Assert.Contains("Unknown directive", result);
    }

    // ── D. CreateScriptEntry with adversarial content ──

    [Fact]
    public async Task CreateScriptEntry_NullBytesInScript_CreatesOrRejectsGracefully()
    {
        var (tools, addressTable, _) = CreateTools();
        var nullScript = "[ENABLE]\0\0\n[DISABLE]\0";

        var result = await tools.CreateScriptEntry("Null Script", nullScript);

        Assert.NotNull(result);
        // Should either create successfully or give a clear error
        Assert.True(
            result.Contains("created", StringComparison.OrdinalIgnoreCase) ||
            result.Contains("failed", StringComparison.OrdinalIgnoreCase),
            "Result should indicate creation or failure");
    }

    [Fact]
    public async Task CreateScriptEntry_ControlCharsInLabel_CreatesGracefully()
    {
        var (tools, addressTable, _) = CreateTools();
        var weirdLabel = "Script\x01\x02\x03With\tTabs\nNewlines";
        var script = "[ENABLE]\nalloc(mem,64)\n[DISABLE]\ndealloc(mem)";

        var result = await tools.CreateScriptEntry(weirdLabel, script);

        Assert.NotNull(result);
        Assert.Contains("created", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateScriptEntry_ExtremelyLongScript_CreatesGracefully()
    {
        var (tools, addressTable, _) = CreateTools();
        var longScript = "[ENABLE]\n" + new string('X', 200_000) + "\n[DISABLE]\ndealloc(mem)";

        var result = await tools.CreateScriptEntry("Long Script", longScript);

        Assert.NotNull(result);
        Assert.Contains("created", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("200", result); // Should mention the char count
    }

    [Fact]
    public async Task CreateScriptEntry_EmptyScript_CreatesOrRejectsGracefully()
    {
        var (tools, addressTable, _) = CreateTools();

        var result = await tools.CreateScriptEntry("Empty Script", "");

        Assert.NotNull(result);
    }

    [Fact]
    public async Task CreateScriptEntry_HtmlInjectionInLabel_StoredAsPlainText()
    {
        var (tools, addressTable, _) = CreateTools();
        var label = "<script>alert('xss')</script>";
        var script = "[ENABLE]\nalloc(mem,64)\n[DISABLE]\ndealloc(mem)";

        var result = await tools.CreateScriptEntry(label, script);

        Assert.Contains("created", result, StringComparison.OrdinalIgnoreCase);

        // Verify the node was added and label is stored as plain text
        var node = addressTable.Roots.FirstOrDefault(n => n.Label == label);
        Assert.NotNull(node);
        Assert.Equal(label, node.Label);
    }

    [Fact]
    public async Task CreateScriptEntry_SqlInjectionInLabel_StoredAsPlainText()
    {
        var (tools, addressTable, _) = CreateTools();
        var label = "'; DROP TABLE entries; --";
        var script = "[ENABLE]\nalloc(mem,64)\n[DISABLE]\ndealloc(mem)";

        var result = await tools.CreateScriptEntry(label, script);

        Assert.Contains("created", result, StringComparison.OrdinalIgnoreCase);
        var node = addressTable.Roots.FirstOrDefault(n => n.Label == label);
        Assert.NotNull(node);
    }

    [Fact]
    public async Task CreateScriptEntry_ValidationFails_NotCreated()
    {
        var (tools, addressTable, assembler) = CreateTools();
        assembler.NextParseResult = new ScriptParseResult(
            false,
            ["Catastrophic parse failure"],
            [],
            null,
            null);

        var result = await tools.CreateScriptEntry("Bad Script", "garbage content");

        Assert.Contains("failed", result, StringComparison.OrdinalIgnoreCase);
        // Node should NOT have been added to the address table
        Assert.DoesNotContain(addressTable.Roots, n => n.Label == "Bad Script");
    }

    [Fact]
    public async Task CreateScriptEntry_InvalidParentGroupId_ReturnsError()
    {
        var (tools, addressTable, _) = CreateTools();

        var result = await tools.CreateScriptEntry(
            "Orphan Script",
            "[ENABLE]\n[DISABLE]",
            parentGroupId: "nonexistent-group");

        Assert.Contains("not found", result, StringComparison.OrdinalIgnoreCase);
    }

    // ── E. ValidateScript with special characters in node ID ──

    [Fact]
    public async Task ValidateScript_NodeIdWithSpecialChars_HandledGracefully()
    {
        var (tools, addressTable, _) = CreateTools();
        AddScriptNode(addressTable, "ct-99", "Normal Script", "[ENABLE]\n[DISABLE]");

        // Try looking up with various adversarial ID strings
        var result1 = await tools.ValidateScript("../../../etc/passwd");
        Assert.Contains("not found", result1, StringComparison.OrdinalIgnoreCase);

        var result2 = await tools.ValidateScript("<script>alert(1)</script>");
        Assert.Contains("not found", result2, StringComparison.OrdinalIgnoreCase);

        var result3 = await tools.ValidateScript("'; DROP TABLE--");
        Assert.Contains("not found", result3, StringComparison.OrdinalIgnoreCase);
    }

    // ── F. ListScripts with adversarial node labels ──

    [Fact]
    public async Task ListScripts_AdversarialLabels_NoCrash()
    {
        var (tools, addressTable, _) = CreateTools();
        AddScriptNode(addressTable, "s1", "<script>alert(1)</script>", "[ENABLE]\n[DISABLE]");
        AddScriptNode(addressTable, "s2", "'; DROP TABLE--", "[ENABLE]\n[DISABLE]");
        AddScriptNode(addressTable, "s3", "\0\0NullLabel\0", "[ENABLE]\n[DISABLE]");
        AddScriptNode(addressTable, "s4", new string('A', 10_000), "[ENABLE]\n[DISABLE]");

        var result = await tools.ListScripts();

        Assert.NotNull(result);
        Assert.Contains("4 scripts", result);
    }

    // ── G. ViewScript with adversarial content ──

    [Fact]
    public async Task ViewScript_ScriptContainingMaliciousContent_DisplayedAsPlainText()
    {
        var (tools, addressTable, _) = CreateTools();
        var maliciousScript = "[ENABLE]\n<script>document.cookie</script>\nalloc(mem,64)\n[DISABLE]\ndealloc(mem)";
        AddScriptNode(addressTable, "s-malicious", "View Test", maliciousScript);

        var result = await tools.ViewScript("s-malicious");

        Assert.NotNull(result);
        Assert.Contains("<script>document.cookie</script>", result);
        Assert.Contains("View Test", result);
    }
}
