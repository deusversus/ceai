using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Tests;

public class ArtifactServicesTests
{
    private static List<AddressTableEntry> CreateSampleEntries() => new()
    {
        new AddressTableEntry("addr-001", "Health", "0x1000", MemoryDataType.Int32, "100", null, "Player HP", true, "999"),
        new AddressTableEntry("addr-002", "Mana", "0x1004", MemoryDataType.Float, "50.5", null, null, true, "100"),
        new AddressTableEntry("addr-003", "Gold", "0x1008", MemoryDataType.Int32, "500", null, null, false, null),
    };

    // ── ScriptGenerationService: GenerateTrainerScript ──

    [Fact]
    public void GenerateTrainerScript_ContainsProcessName()
    {
        var entries = CreateSampleEntries();
        var script = ScriptGenerationService.GenerateTrainerScript(entries, "TestGame.exe");

        Assert.Contains("TestGame", script);
        Assert.Contains("Target: TestGame.exe", script);
    }

    [Fact]
    public void GenerateTrainerScript_IncludesLockedEntriesOnly()
    {
        var entries = CreateSampleEntries();
        var script = ScriptGenerationService.GenerateTrainerScript(entries, "TestGame.exe");

        // Health and Mana are locked, Gold is not
        Assert.Contains("Health", script);
        Assert.Contains("Mana", script);
        Assert.DoesNotContain("Gold", script);
    }

    [Fact]
    public void GenerateTrainerScript_ContainsWin32Api()
    {
        var entries = CreateSampleEntries();
        var script = ScriptGenerationService.GenerateTrainerScript(entries, "TestGame.exe");

        Assert.Contains("OpenProcess", script);
        Assert.Contains("WriteProcessMemory", script);
        Assert.Contains("CloseHandle", script);
    }

    [Fact]
    public void GenerateTrainerScript_EmptyEntries_StillGeneratesStructure()
    {
        var script = ScriptGenerationService.GenerateTrainerScript(
            new List<AddressTableEntry>(), "TestGame.exe");

        Assert.Contains("Trainer", script);
        Assert.Contains("Apply", script);
    }

    // ── ScriptGenerationService: GenerateAutoAssemblerScript ──

    [Fact]
    public void GenerateAutoAssemblerScript_ContainsEnableDisableSections()
    {
        var entries = CreateSampleEntries();
        var script = ScriptGenerationService.GenerateAutoAssemblerScript(entries, "TestGame.exe");

        Assert.Contains("[ENABLE]", script);
        Assert.Contains("[DISABLE]", script);
    }

    [Fact]
    public void GenerateAutoAssemblerScript_ContainsAllocAndDealloc()
    {
        var entries = CreateSampleEntries();
        var script = ScriptGenerationService.GenerateAutoAssemblerScript(entries, "TestGame.exe");

        Assert.Contains("alloc(", script);
        Assert.Contains("dealloc(", script);
    }

    [Fact]
    public void GenerateAutoAssemblerScript_FloatEntry_CastsToFloat()
    {
        var entries = CreateSampleEntries();
        var script = ScriptGenerationService.GenerateAutoAssemblerScript(entries, "TestGame.exe");

        Assert.Contains("(float)", script);
    }

    // ── ScriptGenerationService: GenerateLuaScript ──

    [Fact]
    public void GenerateLuaScript_ContainsLuaComments()
    {
        var entries = CreateSampleEntries();
        var script = ScriptGenerationService.GenerateLuaScript(entries, "TestGame.exe");

        Assert.Contains("-- CE AI Suite", script);
        Assert.Contains("-- Target: TestGame.exe", script);
    }

    [Fact]
    public void GenerateLuaScript_UsesCorrectWriteFunctions()
    {
        var entries = CreateSampleEntries();
        var script = ScriptGenerationService.GenerateLuaScript(entries, "TestGame.exe");

        // Int32 entry should use writeInteger
        Assert.Contains("writeInteger", script);
        // Float entry should use writeFloat
        Assert.Contains("writeFloat", script);
    }

    [Fact]
    public void GenerateLuaScript_ContainsProcessAttachment()
    {
        var entries = CreateSampleEntries();
        var script = ScriptGenerationService.GenerateLuaScript(entries, "TestGame.exe");

        Assert.Contains("getProcessIDFromProcessName", script);
        Assert.Contains("openProcess", script);
    }

    // ── ScriptGenerationService: SummarizeInvestigation ──

    [Fact]
    public void SummarizeInvestigation_ContainsTargetInfo()
    {
        var entries = CreateSampleEntries();
        var summary = ScriptGenerationService.SummarizeInvestigation(
            "TestGame.exe", 1234, entries, null, null);

        Assert.Contains("TestGame.exe", summary);
        Assert.Contains("1234", summary);
    }

    [Fact]
    public void SummarizeInvestigation_ListsAddressEntries()
    {
        var entries = CreateSampleEntries();
        var summary = ScriptGenerationService.SummarizeInvestigation(
            "TestGame.exe", 1234, entries, null, null);

        Assert.Contains("Health", summary);
        Assert.Contains("0x1000", summary);
        Assert.Contains("[LOCKED]", summary);
        Assert.Contains("3 entries tracked", summary);
    }

    [Fact]
    public void SummarizeInvestigation_NoEntries_ShowsNoEntriesMessage()
    {
        var summary = ScriptGenerationService.SummarizeInvestigation(
            "TestGame.exe", 1234, new List<AddressTableEntry>(), null, null);

        Assert.Contains("No entries recorded", summary);
    }

    [Fact]
    public void SummarizeInvestigation_NoScanResults_ShowsNoResultsMessage()
    {
        var summary = ScriptGenerationService.SummarizeInvestigation(
            "TestGame.exe", 1234, new List<AddressTableEntry>(), null, null);

        Assert.Contains("No scan results", summary);
    }

    // ── AddressTableExportService: ExportToJson / ImportFromJson ──

    [Fact]
    public void ExportToJson_ProducesValidJson()
    {
        var entries = CreateSampleEntries();
        var json = AddressTableExportService.ExportToJson(entries);

        Assert.NotNull(json);
        Assert.StartsWith("[", json.TrimStart());
        Assert.Contains("Health", json);
        Assert.Contains("0x1000", json);
    }

    [Fact]
    public void ImportFromJson_RoundTrips()
    {
        var original = CreateSampleEntries();
        var json = AddressTableExportService.ExportToJson(original);
        var imported = AddressTableExportService.ImportFromJson(json);

        Assert.Equal(3, imported.Count);
        Assert.Equal("Health", imported[0].Label);
        Assert.Equal("0x1000", imported[0].Address);
        Assert.Equal(MemoryDataType.Int32, imported[0].DataType);
        Assert.Equal("100", imported[0].CurrentValue);
        Assert.True(imported[0].IsLocked);
        Assert.Equal("999", imported[0].LockedValue);
    }

    [Fact]
    public void ImportFromJson_PreservesNotes()
    {
        var original = CreateSampleEntries();
        var json = AddressTableExportService.ExportToJson(original);
        var imported = AddressTableExportService.ImportFromJson(json);

        Assert.Equal("Player HP", imported[0].Notes);
    }

    [Fact]
    public void ImportFromJson_GeneratesUniqueIds()
    {
        var original = CreateSampleEntries();
        var json = AddressTableExportService.ExportToJson(original);
        var imported = AddressTableExportService.ImportFromJson(json);

        // Each imported entry gets a new unique ID
        var ids = imported.Select(e => e.Id).ToHashSet();
        Assert.Equal(3, ids.Count);
    }

    [Fact]
    public void ExportToJson_EmptyList_ReturnsEmptyArray()
    {
        var json = AddressTableExportService.ExportToJson(new List<AddressTableEntry>());
        Assert.Equal("[]", json.Trim());
    }

    [Fact]
    public void ImportFromJson_EmptyArray_ReturnsEmptyList()
    {
        var imported = AddressTableExportService.ImportFromJson("[]");
        Assert.Empty(imported);
    }

    [Fact]
    public void ExportToJson_ContainsDataTypeAsString()
    {
        var entries = CreateSampleEntries();
        var json = AddressTableExportService.ExportToJson(entries);

        Assert.Contains("\"Int32\"", json);
        Assert.Contains("\"Float\"", json);
    }
}
