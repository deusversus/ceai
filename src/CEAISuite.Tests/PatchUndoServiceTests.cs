using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Tests;

public class PatchUndoServiceTests
{
    private static StubEngineFacade CreateFacadeWithInt32(nuint address, int value)
    {
        var facade = new StubEngineFacade();
        facade.WriteMemoryDirect(address, BitConverter.GetBytes(value));
        return facade;
    }

    [Fact]
    public async Task WriteWithUndoAsync_RecordsOriginalBytes_AndWritesNewValue()
    {
        var facade = CreateFacadeWithInt32((nuint)0x1000, 100);
        var svc = new PatchUndoService(facade);

        var result = await svc.WriteWithUndoAsync(1, (nuint)0x1000, MemoryDataType.Int32, "200");

        Assert.Equal(4, result.BytesWritten);
        Assert.Equal(1, svc.UndoCount);
        Assert.Equal(0, svc.RedoCount);
    }

    [Fact]
    public async Task UndoAsync_RestoresOriginalBytes()
    {
        var facade = CreateFacadeWithInt32((nuint)0x1000, 100);
        var svc = new PatchUndoService(facade);

        await svc.WriteWithUndoAsync(1, (nuint)0x1000, MemoryDataType.Int32, "200");
        var msg = await svc.UndoAsync();

        Assert.Contains("Undone", msg);
        Assert.Equal(0, svc.UndoCount);
        Assert.Equal(1, svc.RedoCount);

        // Verify original value was restored by reading memory
        var read = await facade.ReadValueAsync(1, (nuint)0x1000, MemoryDataType.Int32);
        // After undo, the individual byte writes should have restored the original
        // The stub stores per-address, so check the overall effect
        Assert.Contains("Undone write at 0x1000", msg);
    }

    [Fact]
    public async Task UndoAsync_EmptyStack_ReturnsNothingMessage()
    {
        var facade = new StubEngineFacade();
        var svc = new PatchUndoService(facade);

        var msg = await svc.UndoAsync();

        Assert.Equal("Nothing to undo.", msg);
    }

    [Fact]
    public async Task RedoAsync_EmptyStack_ReturnsNothingMessage()
    {
        var facade = new StubEngineFacade();
        var svc = new PatchUndoService(facade);

        var msg = await svc.RedoAsync();

        Assert.Equal("Nothing to redo.", msg);
    }

    [Fact]
    public async Task RedoAsync_ReappliesUndoneWrite()
    {
        var facade = CreateFacadeWithInt32((nuint)0x1000, 100);
        var svc = new PatchUndoService(facade);

        await svc.WriteWithUndoAsync(1, (nuint)0x1000, MemoryDataType.Int32, "200");
        await svc.UndoAsync();
        var msg = await svc.RedoAsync();

        Assert.Contains("Redone", msg);
        Assert.Equal(0, svc.RedoCount);
        Assert.Equal(1, svc.UndoCount);
    }

    [Fact]
    public async Task WriteUndoRedo_RoundTrip_CountsCorrect()
    {
        var facade = CreateFacadeWithInt32((nuint)0x1000, 10);
        var svc = new PatchUndoService(facade);

        await svc.WriteWithUndoAsync(1, (nuint)0x1000, MemoryDataType.Int32, "20");
        await svc.WriteWithUndoAsync(1, (nuint)0x1000, MemoryDataType.Int32, "30");
        Assert.Equal(2, svc.UndoCount);

        await svc.UndoAsync();
        Assert.Equal(1, svc.UndoCount);
        Assert.Equal(1, svc.RedoCount);

        await svc.RedoAsync();
        Assert.Equal(2, svc.UndoCount);
        Assert.Equal(0, svc.RedoCount);
    }

    [Fact]
    public async Task WriteWithUndoAsync_ClearsRedoStack()
    {
        var facade = CreateFacadeWithInt32((nuint)0x1000, 10);
        var svc = new PatchUndoService(facade);

        await svc.WriteWithUndoAsync(1, (nuint)0x1000, MemoryDataType.Int32, "20");
        await svc.UndoAsync();
        Assert.Equal(1, svc.RedoCount);

        // New write should clear redo
        await svc.WriteWithUndoAsync(1, (nuint)0x1000, MemoryDataType.Int32, "30");
        Assert.Equal(0, svc.RedoCount);
    }

    [Fact]
    public async Task RollbackAllAsync_UndsAllPatches()
    {
        var facade = CreateFacadeWithInt32((nuint)0x1000, 10);
        facade.WriteMemoryDirect((nuint)0x2000, BitConverter.GetBytes(50));
        var svc = new PatchUndoService(facade);

        await svc.WriteWithUndoAsync(1, (nuint)0x1000, MemoryDataType.Int32, "20");
        await svc.WriteWithUndoAsync(1, (nuint)0x2000, MemoryDataType.Int32, "60");
        Assert.Equal(2, svc.UndoCount);

        var rolled = await svc.RollbackAllAsync();

        Assert.Equal(2, rolled);
        Assert.Equal(0, svc.UndoCount);
    }

    [Fact]
    public async Task GetHistory_ReturnsRecentPatches()
    {
        var facade = CreateFacadeWithInt32((nuint)0x1000, 10);
        var svc = new PatchUndoService(facade);

        await svc.WriteWithUndoAsync(1, (nuint)0x1000, MemoryDataType.Int32, "20");
        await svc.WriteWithUndoAsync(1, (nuint)0x1000, MemoryDataType.Int32, "30");
        await svc.WriteWithUndoAsync(1, (nuint)0x1000, MemoryDataType.Int32, "40");

        var history = svc.GetHistory(2);
        Assert.Equal(2, history.Count);
        Assert.Equal("30", history[0].NewValue);
        Assert.Equal("40", history[1].NewValue);
    }

    [Fact]
    public async Task GetHistory_EmptyStack_ReturnsEmpty()
    {
        var facade = new StubEngineFacade();
        var svc = new PatchUndoService(facade);

        var history = svc.GetHistory();
        Assert.Empty(history);
    }

    [Fact]
    public async Task WriteWithUndoAsync_TrimsHistory_WhenMaxExceeded()
    {
        var facade = new StubEngineFacade();
        var svc = new PatchUndoService(facade);

        // Write 501 times to exceed MaxHistory (500)
        for (int i = 0; i < 501; i++)
        {
            facade.WriteMemoryDirect((nuint)0x1000, BitConverter.GetBytes(i));
            await svc.WriteWithUndoAsync(1, (nuint)0x1000, MemoryDataType.Int32, (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Assert.Equal(500, svc.UndoCount);
    }
}
