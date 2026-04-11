using System.IO;
using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

/// <summary>
/// Stress tests that dispatch many concurrent tool calls via Task.WhenAll,
/// verifying no deadlocks, state corruption, or unhandled exceptions.
/// All tests use stubs (no live processes). Tools may return error strings
/// (e.g. "Process 1000 is no longer running") — that is expected.
/// The point is thread safety: no hangs, no NullReferenceException, no corruption.
/// </summary>
[Trait("Category", "Stress")]
public class ConcurrentToolStressTests
{
    private static (AiToolFunctions tools, AddressTableService addressTable) CreateTools()
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
            engineFacade, dashboardService, scanService, addressTableService,
            disassemblyService, scriptGenService, autoAssemblerEngine: assembler);

        return (tools, addressTableService);
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

    [Fact]
    public async Task ConcurrentReads_NoCorruption()
    {
        var (tools, _) = CreateTools();

        var tasks = Enumerable.Range(0, 20)
            .Select(_ => tools.ReadMemory(1000, "0x1000", "Int32"))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // All 20 calls completed without throwing
        Assert.Equal(20, results.Length);
        Assert.All(results, r => Assert.NotNull(r));
    }

    [Fact]
    public async Task ConcurrentListProcesses_AllComplete()
    {
        var (tools, _) = CreateTools();

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => tools.ListProcesses())
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(10, results.Length);
        Assert.All(results, r => Assert.NotNull(r));
    }

    [Fact]
    public async Task ConcurrentCheatTableLoads_NoCorruption()
    {
        const string minimalCt =
            """
            <CheatTable CheatEngineTableVersion="45">
              <CheatEntries>
                <CheatEntry>
                  <ID>1</ID>
                  <Description>"Test"</Description>
                  <VariableType>4 Bytes</VariableType>
                  <Address>0x1000</Address>
                </CheatEntry>
              </CheatEntries>
            </CheatTable>
            """;

        var tempFiles = new List<string>();
        try
        {
            // Create 5 temp CT files with slightly different content
            for (int i = 0; i < 5; i++)
            {
                var path = Path.Combine(Path.GetTempPath(), $"stress_test_{i}_{Guid.NewGuid():N}.CT");
                var ct = minimalCt.Replace("0x1000", $"0x{0x1000 + i:X}");
                File.WriteAllText(path, ct);
                tempFiles.Add(path);
            }

            // Each call gets its own AiToolFunctions to avoid address table conflicts
            var tasks = tempFiles.Select(path =>
            {
                var (t, _) = CreateTools();
                return t.LoadCheatTable(path);
            }).ToArray();

            var results = await Task.WhenAll(tasks);

            Assert.Equal(5, results.Length);
            Assert.All(results, r => Assert.NotNull(r));
        }
        finally
        {
            foreach (var f in tempFiles)
            {
                try { File.Delete(f); } catch { /* best effort cleanup */ }
            }
        }
    }

    [Fact]
    public async Task ConcurrentScriptOperations_NoRace()
    {
        var (tools, addressTable) = CreateTools();

        // Create 5 script entries sequentially
        for (int i = 1; i <= 5; i++)
        {
            AddScriptNode(addressTable, $"ct-{i}", $"Script {i}",
                $"[ENABLE]\nalloc(mem{i},64)\n[DISABLE]\ndealloc(mem{i})");
        }

        // 10 concurrent calls: mix of ListScripts and ViewScript
        var tasks = Enumerable.Range(0, 10)
            .Select(i => i % 2 == 0
                ? tools.ListScripts()
                : tools.ViewScript("ct-1"))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(10, results.Length);
        Assert.All(results, r => Assert.NotNull(r));
    }

    [Fact]
    public async Task ConcurrentAddressTableOperations_NoStateCorruption()
    {
        var (tools, _) = CreateTools();

        var tasks = Enumerable.Range(0, 10)
            .Select(i => i % 2 == 0
                ? tools.ListAddressTable()
                : tools.SummarizeCheatTable())
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(10, results.Length);
        Assert.All(results, r => Assert.NotNull(r));
    }

    [Fact]
    public async Task HighVolumeToolCalls_CompletesInReasonableTime()
    {
        var (tools, _) = CreateTools();

        var sw = System.Diagnostics.Stopwatch.StartNew();

        var tasks = Enumerable.Range(0, 50)
            .Select(i => (i % 3) switch
            {
                0 => tools.ListProcesses(),
                1 => tools.ListAddressTable(),
                _ => tools.ListScripts()
            })
            .ToArray();

        var results = await Task.WhenAll(tasks);
        sw.Stop();

        Assert.Equal(50, results.Length);
        Assert.All(results, r => Assert.NotNull(r));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"50 tool calls took {sw.Elapsed.TotalSeconds:F2}s, expected < 5s");
    }

    [Fact]
    public async Task ConcurrentRefreshAndList_NoDeadlock()
    {
        var (tools, _) = CreateTools();

        var tasks = Enumerable.Range(0, 10)
            .Select(i => i % 2 == 0
                ? tools.ListAddressTable()
                : tools.RefreshAddressTable(1000))
            .ToArray();

        // All should complete — RefreshAddressTable will fail gracefully
        // since PID 1000 isn't attached, but must not deadlock
        var results = await Task.WhenAll(tasks);

        Assert.Equal(10, results.Length);
        Assert.All(results, r => Assert.NotNull(r));
    }
}
