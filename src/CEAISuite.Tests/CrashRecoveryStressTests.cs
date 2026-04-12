using System.IO;
using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Tests;

[Trait("Category", "Stabilization")]
public class CrashRecoveryStressTests : IDisposable
{
    private readonly AddressTableExportService _exportService = new();
    private readonly string _tempDir;

    public CrashRecoveryStressTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ceai-recovery-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static AddressTableService CreateTableWithEntries(int count)
    {
        var engine = new StubEngineFacade();
        var service = new AddressTableService(engine);
        for (int i = 0; i < count; i++)
            service.AddEntry($"0x{(0x1000 + i * 4):X}", MemoryDataType.Int32, i.ToString(System.Globalization.CultureInfo.InvariantCulture), $"Entry_{i}");
        return service;
    }

    [Fact]
    public async Task RecoveryRoundTrip_100Entries_AllPreserved()
    {
        var path = Path.Combine(_tempDir, "100entries.json");
        try
        {
            var service = CreateTableWithEntries(100);
            var roots = service.Roots.ToList();

            await _exportService.ExportRecoveryAsync(roots, path);

            var restored = await _exportService.ImportRecoveryAsync(path);

            Assert.NotNull(restored);
            Assert.Equal(100, restored.Count);

            for (int i = 0; i < 100; i++)
            {
                Assert.Equal($"Entry_{i}", restored[i].Label);
                Assert.Equal($"0x{(0x1000 + i * 4):X}", restored[i].Address);
                Assert.Equal(MemoryDataType.Int32, restored[i].DataType);
            }
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task RecoveryRoundTrip_WithScripts_ScriptsPreserved()
    {
        var path = Path.Combine(_tempDir, "scripts.json");
        try
        {
            var service = CreateTableWithEntries(3);

            // Add a node with an assembler script directly to Roots
            var scriptNode = new AddressTableNode($"addr-script-1", "Infinite HP Script", false)
            {
                Address = "(script)",
                AssemblerScript = "[ENABLE]\nnop\n[DISABLE]\ndb 90"
            };
            service.Roots.Add(scriptNode);

            var scriptNode2 = new AddressTableNode($"addr-script-2", "Speed Boost", false)
            {
                Address = "(script)",
                AssemblerScript = "[ENABLE]\nmov eax, 1\n[DISABLE]\nmov eax, 0"
            };
            service.Roots.Add(scriptNode2);

            var roots = service.Roots.ToList();
            await _exportService.ExportRecoveryAsync(roots, path);

            var restored = await _exportService.ImportRecoveryAsync(path);

            Assert.NotNull(restored);
            Assert.Equal(5, restored.Count); // 3 entries + 2 scripts

            var restoredScript1 = restored.First(n => n.Label == "Infinite HP Script");
            Assert.NotNull(restoredScript1.AssemblerScript);
            Assert.Contains("[ENABLE]", restoredScript1.AssemblerScript);
            Assert.Contains("nop", restoredScript1.AssemblerScript);
            Assert.Contains("[DISABLE]", restoredScript1.AssemblerScript);

            var restoredScript2 = restored.First(n => n.Label == "Speed Boost");
            Assert.NotNull(restoredScript2.AssemblerScript);
            Assert.Contains("mov eax, 1", restoredScript2.AssemblerScript);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task CorruptRecoveryFile_GracefulFallback()
    {
        var path = Path.Combine(_tempDir, "corrupt.json");
        try
        {
            await File.WriteAllTextAsync(path, "{{{{garbage not json}}}}");

            var result = await _exportService.ImportRecoveryAsync(path);

            Assert.Null(result);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task ConcurrentExport_NoCorruption()
    {
        var service = CreateTableWithEntries(50);
        var roots = service.Roots.ToList();
        var paths = Enumerable.Range(0, 5)
            .Select(i => Path.Combine(_tempDir, $"concurrent_{i}.json"))
            .ToArray();

        try
        {
            await Task.WhenAll(
                Enumerable.Range(0, 5).Select(i =>
                    _exportService.ExportRecoveryAsync(roots, paths[i])));

            for (int i = 0; i < 5; i++)
            {
                Assert.True(File.Exists(paths[i]), $"File {i} should exist");
                var restored = await _exportService.ImportRecoveryAsync(paths[i]);
                Assert.NotNull(restored);
                Assert.Equal(50, restored.Count);
            }
        }
        finally
        {
            foreach (var p in paths)
                try { File.Delete(p); } catch { }
        }
    }

    [Fact]
    public async Task LargeTable_500Entries_RoundTrips()
    {
        var path = Path.Combine(_tempDir, "500entries.json");
        try
        {
            var service = CreateTableWithEntries(500);
            var roots = service.Roots.ToList();

            await _exportService.ExportRecoveryAsync(roots, path);

            var restored = await _exportService.ImportRecoveryAsync(path);

            Assert.NotNull(restored);
            Assert.Equal(500, restored.Count);

            // Spot-check first, middle, and last entries
            Assert.Equal("Entry_0", restored[0].Label);
            Assert.Equal($"0x{0x1000:X}", restored[0].Address);

            Assert.Equal("Entry_249", restored[249].Label);
            Assert.Equal($"0x{(0x1000 + 249 * 4):X}", restored[249].Address);

            Assert.Equal("Entry_499", restored[499].Label);
            Assert.Equal($"0x{(0x1000 + 499 * 4):X}", restored[499].Address);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
