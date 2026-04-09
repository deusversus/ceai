using CEAISuite.Application;

namespace CEAISuite.Tests;

public class ToolResultStoreTests
{
    private readonly ToolResultStore _store = new();

    [Fact]
    public void Store_ReturnsUniqueHandles()
    {
        var h1 = _store.Store("Disassemble", "result1");
        var h2 = _store.Store("HexDump", "result2");

        Assert.NotEqual(h1, h2);
        Assert.StartsWith("tr_", h1);
        Assert.StartsWith("tr_", h2);
        Assert.Equal(2, _store.Count);
    }

    [Fact]
    public void Store_IncrementingIds()
    {
        var id1 = _store.Store("t", "a");
        var id2 = _store.Store("t", "b");
        Assert.Equal("tr_0001", id1);
        Assert.Equal("tr_0002", id2);
    }

    [Fact]
    public void Retrieve_ReturnsCorrectSlice()
    {
        var fullText = new string('A', 1000) + new string('B', 1000);
        var handle = _store.Store("TestTool", fullText);

        var page = _store.Retrieve(handle, 0, 500);
        Assert.NotNull(page);
        Assert.StartsWith(new string('A', 500), page);
        Assert.Contains("more chars remaining", page);
    }

    [Fact]
    public void Retrieve_LastPage_NoRemainingNotice()
    {
        var handle = _store.Store("TestTool", "Hello, world!");

        var page = _store.Retrieve(handle, 0, 100);
        Assert.Equal("Hello, world!", page);
    }

    [Fact]
    public void Retrieve_WithOffset_SkipsCorrectly()
    {
        var handle = _store.Store("TestTool", "AAAAABBBBB");

        var page = _store.Retrieve(handle, 5, 100);
        Assert.Equal("BBBBB", page);
    }

    [Fact]
    public void Retrieve_InvalidHandle_ReturnsNull()
    {
        Assert.Null(_store.Retrieve("tr_9999", 0, 100));
    }

    [Fact]
    public void Retrieve_OffsetPastEnd_ReturnsNotice()
    {
        var handle = _store.Store("TestTool", "short");

        var page = _store.Retrieve(handle, 999, 100);
        Assert.Contains("past end", page);
    }

    [Fact]
    public void Retrieve_NegativeOffset_ClampsToZero()
    {
        var handle = _store.Store("tool", "data");
        var result = _store.Retrieve(handle, -5, 1000);
        Assert.Equal("data", result);
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        _store.Store("A", "data1");
        _store.Store("B", "data2");
        Assert.Equal(2, _store.Count);

        _store.Clear();
        Assert.Equal(0, _store.Count);
    }

    [Fact]
    public void Clear_ResetsIdSequence()
    {
        _store.Store("t", "x");
        _store.Store("t", "y");
        _store.Clear();
        var id = _store.Store("t", "z");
        Assert.Equal("tr_0001", id);
    }

    [Fact]
    public void ListAll_ReturnsMetadataOrderedByTime()
    {
        _store.Store("First", "aaa");
        _store.Store("Second", "bbb");

        var items = _store.ListAll();
        Assert.Equal(2, items.Count);
        Assert.Equal("Second", items[0].ToolName);
        Assert.Equal("First", items[1].ToolName);
    }

    [Fact]
    public void GetInfo_ReturnsMetadata()
    {
        var handle = _store.Store("Disassemble", "line1\nline2\nline3");

        var info = _store.GetInfo(handle);
        Assert.NotNull(info);
        Assert.Equal("Disassemble", info.ToolName);
        Assert.Equal(3, info.TotalLines);
        Assert.Equal(17, info.TotalChars);
    }

    [Fact]
    public void GetInfo_InvalidHandle_ReturnsNull()
    {
        Assert.Null(_store.GetInfo("nope"));
    }

    [Fact]
    public void Remove_DeletesSingleEntry()
    {
        var h1 = _store.Store("A", "data1");
        var h2 = _store.Store("B", "data2");

        Assert.True(_store.Remove(h1));
        Assert.Equal(1, _store.Count);
        Assert.Null(_store.Retrieve(h1, 0, 100));
        Assert.NotNull(_store.Retrieve(h2, 0, 100));
    }

    [Fact]
    public void Remove_NonExistent_ReturnsFalse()
    {
        Assert.False(_store.Remove("nope"));
    }

    [Fact]
    public void Count_ReflectsStoreState()
    {
        Assert.Equal(0, _store.Count);
        _store.Store("t", "a");
        Assert.Equal(1, _store.Count);
        _store.Store("t", "b");
        Assert.Equal(2, _store.Count);
    }
}
