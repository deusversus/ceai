using System.IO;
using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Tests;

public class CrashRecoveryTests : IDisposable
{
    private readonly AddressTableExportService _exportService = new();
    private readonly string _tempDir;

    public CrashRecoveryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ceai-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string RecoveryPath => Path.Combine(_tempDir, "recovery.json");

    private static List<AddressTableNode> CreateSampleRoots()
    {
        var group = new AddressTableNode("group-1", "Player Stats", true);
        var child1 = new AddressTableNode("addr-001", "Health", false)
        {
            Address = "0x400100",
            DataType = MemoryDataType.Int32,
            CurrentValue = "100",
            IsLocked = true,
            LockedValue = "100",
            ShowAsHex = false,
            ShowAsSigned = true,
            UserColor = "#FF4444",
            Notes = "Player HP"
        };
        child1.Parent = group;
        group.Children.Add(child1);

        var child2 = new AddressTableNode("addr-002", "Mana", false)
        {
            Address = "0x400200",
            DataType = MemoryDataType.Float,
            CurrentValue = "50.5",
            IsPointer = true,
            PointerOffsets = new List<long> { 0x10, 0x20 }
        };
        child2.Parent = group;
        group.Children.Add(child2);

        var scriptNode = new AddressTableNode("addr-003", "Infinite HP Script", false)
        {
            Address = "(script)",
            AssemblerScript = "[ENABLE]\nnop\n[DISABLE]\ndb 90"
        };

        return new List<AddressTableNode> { group, scriptNode };
    }

    [Fact]
    public async Task ExportRecovery_ValidRoots_WritesFile()
    {
        var roots = CreateSampleRoots();

        await _exportService.ExportRecoveryAsync(roots, RecoveryPath);

        Assert.True(File.Exists(RecoveryPath));
        var content = await File.ReadAllTextAsync(RecoveryPath);
        Assert.Contains("Health", content);
        Assert.Contains("Player Stats", content);
    }

    [Fact]
    public async Task ImportRecovery_ValidFile_ReturnsNodes()
    {
        var roots = CreateSampleRoots();
        await _exportService.ExportRecoveryAsync(roots, RecoveryPath);

        var result = await _exportService.ImportRecoveryAsync(RecoveryPath);

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.True(result[0].IsGroup);
        Assert.Equal("Player Stats", result[0].Label);
        Assert.Equal(2, result[0].Children.Count);
    }

    [Fact]
    public async Task ImportRecovery_MissingFile_ReturnsNull()
    {
        var result = await _exportService.ImportRecoveryAsync(
            Path.Combine(_tempDir, "does-not-exist.json"));

        Assert.Null(result);
    }

    [Fact]
    public async Task ImportRecovery_CorruptFile_ReturnsNull()
    {
        await File.WriteAllTextAsync(RecoveryPath, "{{{{not valid json!@#$");

        var result = await _exportService.ImportRecoveryAsync(RecoveryPath);

        Assert.Null(result);
    }

    [Fact]
    public async Task ExportImportRecovery_RoundTrip_PreservesEntries()
    {
        var roots = CreateSampleRoots();
        await _exportService.ExportRecoveryAsync(roots, RecoveryPath);

        var restored = await _exportService.ImportRecoveryAsync(RecoveryPath);

        Assert.NotNull(restored);

        // Group structure
        var group = restored[0];
        Assert.True(group.IsGroup);
        Assert.Equal("group-1", group.Id);
        Assert.Equal("Player Stats", group.Label);
        Assert.Equal(2, group.Children.Count);

        // First child: Health
        var health = group.Children[0];
        Assert.Equal("addr-001", health.Id);
        Assert.Equal("Health", health.Label);
        Assert.Equal("0x400100", health.Address);
        Assert.Equal(MemoryDataType.Int32, health.DataType);
        Assert.Equal("100", health.CurrentValue);
        Assert.True(health.IsLocked);
        Assert.Equal("100", health.LockedValue);
        Assert.True(health.ShowAsSigned);
        Assert.False(health.ShowAsHex);
        Assert.Equal("#FF4444", health.UserColor);
        Assert.Equal("Player HP", health.Notes);
        Assert.Equal(group, health.Parent); // parent reference restored

        // Second child: Mana (pointer)
        var mana = group.Children[1];
        Assert.Equal("addr-002", mana.Id);
        Assert.Equal("Mana", mana.Label);
        Assert.Equal(MemoryDataType.Float, mana.DataType);
        Assert.Equal("50.5", mana.CurrentValue);
        Assert.True(mana.IsPointer);
        Assert.Equal(new List<long> { 0x10, 0x20 }, mana.PointerOffsets);

        // Script node
        var script = restored[1];
        Assert.Equal("addr-003", script.Id);
        Assert.Equal("Infinite HP Script", script.Label);
        Assert.NotNull(script.AssemblerScript);
        Assert.Contains("[ENABLE]", script.AssemblerScript);
    }
}
