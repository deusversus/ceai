using System.Globalization;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Engine.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace CEAISuite.Tests;

/// <summary>
/// Integration tests for <see cref="WindowsEngineFacade"/> against the real
/// CEAISuite.Tests.Harness process. Each test launches its own harness to
/// avoid cross-test contamination.
/// </summary>
[Trait("Category", "Integration")]
public class EngineWindowsFacadeIntegrationTests
{
    private static WindowsEngineFacade CreateFacade() =>
        new(NullLogger<WindowsEngineFacade>.Instance);

    [Fact]
    public async Task ListProcessesAsync_ReturnsHarnessProcess()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);
        var facade = CreateFacade();

        var processes = await facade.ListProcessesAsync(TestContext.Current.CancellationToken);

        Assert.Contains(processes, p => p.Id == harness.ProcessId);
        var harnessProc = processes.First(p => p.Id == harness.ProcessId);
        Assert.Contains("CEAISuite.Tests.Harness", harnessProc.Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AttachAsync_HarnessProcess_ReturnsAttachment()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);
        var facade = CreateFacade();

        var attachment = await facade.AttachAsync(harness.ProcessId, TestContext.Current.CancellationToken);

        Assert.Equal(harness.ProcessId, attachment.ProcessId);
        Assert.True(attachment.Modules.Count > 0, "Attached process should have at least one module");
        Assert.True(facade.IsAttached, "Facade should report IsAttached after AttachAsync");
    }

    [Fact]
    public async Task AttachAsync_CachesAttachment_ReturnsSameResult()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);
        var facade = CreateFacade();

        var first = await facade.AttachAsync(harness.ProcessId, TestContext.Current.CancellationToken);
        var second = await facade.AttachAsync(harness.ProcessId, TestContext.Current.CancellationToken);

        // Cached attachment returns the exact same object reference
        Assert.Same(first, second);
    }

    [Fact]
    public async Task ReadMemoryAsync_AllocatedRegion_ReturnsDE()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);
        var facade = CreateFacade();
        await facade.AttachAsync(harness.ProcessId, TestContext.Current.CancellationToken);

        // ALLOC returns "ALLOC_OK:<hex_address>"
        var allocResp = await harness.SendCommandAsync("ALLOC 256");
        Assert.NotNull(allocResp);
        Assert.StartsWith("ALLOC_OK:", allocResp);
        var address = nuint.Parse(allocResp.Split(':')[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        var result = await facade.ReadMemoryAsync(harness.ProcessId, address, 256, TestContext.Current.CancellationToken);

        Assert.Equal(harness.ProcessId, result.ProcessId);
        Assert.Equal(address, result.Address);
        Assert.Equal(256, result.Bytes.Count);
        // Harness fills allocated memory with 0xDE
        Assert.All(result.Bytes.ToArray(), b => Assert.Equal(0xDE, b));
    }

    [Fact]
    public async Task ReadValueAsync_Int32_ReturnsCorrectValue()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);
        var facade = CreateFacade();
        await facade.AttachAsync(harness.ProcessId, TestContext.Current.CancellationToken);

        var allocResp = await harness.SendCommandAsync("ALLOC 64");
        Assert.NotNull(allocResp);
        var address = nuint.Parse(allocResp!.Split(':')[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        // Write a known Int32 value
        const int testValue = 1234567;
        var writeResult = await facade.WriteValueAsync(
            harness.ProcessId, address, MemoryDataType.Int32, testValue.ToString(CultureInfo.InvariantCulture),
            TestContext.Current.CancellationToken);
        Assert.Equal(4, writeResult.BytesWritten);

        // Read it back as typed value
        var readResult = await facade.ReadValueAsync(
            harness.ProcessId, address, MemoryDataType.Int32, TestContext.Current.CancellationToken);

        Assert.Equal(MemoryDataType.Int32, readResult.DataType);
        Assert.Equal(testValue.ToString(CultureInfo.InvariantCulture), readResult.DisplayValue);
    }

    [Fact]
    public async Task WriteValueAsync_Int32_WritesAndReadBack()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);
        var facade = CreateFacade();
        await facade.AttachAsync(harness.ProcessId, TestContext.Current.CancellationToken);

        var allocResp = await harness.SendCommandAsync("ALLOC 64");
        Assert.NotNull(allocResp);
        var address = nuint.Parse(allocResp!.Split(':')[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        const int value = -42;
        var writeResult = await facade.WriteValueAsync(
            harness.ProcessId, address, MemoryDataType.Int32, value.ToString(CultureInfo.InvariantCulture),
            TestContext.Current.CancellationToken);
        Assert.Equal(4, writeResult.BytesWritten);

        // Read raw bytes and verify
        var readResult = await facade.ReadMemoryAsync(harness.ProcessId, address, 4, TestContext.Current.CancellationToken);
        var readValue = BitConverter.ToInt32(readResult.Bytes.ToArray(), 0);
        Assert.Equal(value, readValue);
    }

    [Fact]
    public async Task WriteBytesAsync_CustomPattern_ReadBackVerifies()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);
        var facade = CreateFacade();
        await facade.AttachAsync(harness.ProcessId, TestContext.Current.CancellationToken);

        var allocResp = await harness.SendCommandAsync("ALLOC 64");
        Assert.NotNull(allocResp);
        var address = nuint.Parse(allocResp!.Split(':')[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        var pattern = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE, 0x00, 0x11, 0x22, 0x33 };
        var bytesWritten = await facade.WriteBytesAsync(
            harness.ProcessId, address, pattern, TestContext.Current.CancellationToken);
        Assert.Equal(pattern.Length, bytesWritten);

        var readResult = await facade.ReadMemoryAsync(harness.ProcessId, address, pattern.Length, TestContext.Current.CancellationToken);
        Assert.Equal(pattern, readResult.Bytes.ToArray());
    }

    [Fact]
    public async Task Detach_ClearsState()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);
        var facade = CreateFacade();
        await facade.AttachAsync(harness.ProcessId, TestContext.Current.CancellationToken);

        Assert.True(facade.IsAttached);

        facade.Detach();

        Assert.False(facade.IsAttached);
        Assert.Null(facade.AttachedProcessId);
    }
}
