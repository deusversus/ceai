using CEAISuite.Engine.Abstractions;
using CEAISuite.Engine.Lua;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for CE-compatible address list / memory record Lua bindings.
/// </summary>
public sealed class LuaAddressListBindingsTests : IDisposable
{
    private readonly StubAddressListProvider _provider = new();
    private readonly MoonSharpLuaEngine _engine;

    public LuaAddressListBindingsTests()
    {
        _provider.AddCannedRecord(new LuaMemoryRecord("addr-1", "Health", "0x1000", "Int32", "100", false, false));
        _provider.AddCannedRecord(new LuaMemoryRecord("addr-2", "Mana", "0x1004", "Float", "75.5", true, false));
        _provider.AddCannedRecord(new LuaMemoryRecord("addr-3", "Gold", "0x1008", "Int32", "9999", false, false));

        _engine = new MoonSharpLuaEngine(
            addressListProvider: _provider,
            executionTimeout: TimeSpan.FromSeconds(5));
    }

    public void Dispose() => _engine.Dispose();

    [Fact]
    public async Task GetAddressList_ReturnsTable()
    {
        var result = await _engine.ExecuteAsync("""
            local al = getAddressList()
            return al.getCount()
            """);

        Assert.True(result.Success, result.Error);
        Assert.Equal("3", result.ReturnValue);
    }

    [Fact]
    public async Task AddressList_GetMemoryRecord_ByIndex()
    {
        var result = await _engine.ExecuteAsync("""
            local al = getAddressList()
            local mr = al.getMemoryRecord(0)
            return mr.Description
            """);

        Assert.True(result.Success, result.Error);
        Assert.Equal("Health", result.ReturnValue);
    }

    [Fact]
    public async Task AddressList_GetMemoryRecordByDescription()
    {
        var result = await _engine.ExecuteAsync("""
            local al = getAddressList()
            local mr = al.getMemoryRecordByDescription("Mana")
            return mr.Value
            """);

        Assert.True(result.Success, result.Error);
        Assert.Equal("75.5", result.ReturnValue);
    }

    [Fact]
    public async Task AddressList_GetMemoryRecordByID()
    {
        var result = await _engine.ExecuteAsync("""
            local al = getAddressList()
            local mr = al.getMemoryRecordByID("addr-3")
            return mr.Description
            """);

        Assert.True(result.Success, result.Error);
        Assert.Equal("Gold", result.ReturnValue);
    }

    [Fact]
    public async Task Standalone_AddresslistGetCount()
    {
        var result = await _engine.ExecuteAsync("return addresslist_getCount()");
        Assert.True(result.Success, result.Error);
        Assert.Equal("3", result.ReturnValue);
    }

    [Fact]
    public async Task Standalone_AddresslistGetMemoryRecord()
    {
        var result = await _engine.ExecuteAsync("""
            local mr = addresslist_getMemoryRecord(1)
            return mr.Description
            """);

        Assert.True(result.Success, result.Error);
        Assert.Equal("Mana", result.ReturnValue);
    }

    [Fact]
    public async Task MemoryRecord_GetValue()
    {
        var result = await _engine.ExecuteAsync("""
            local mr = addresslist_getMemoryRecord(0)
            return memoryrecord_getValue(mr)
            """);

        Assert.True(result.Success, result.Error);
        Assert.Equal("100", result.ReturnValue);
    }

    [Fact]
    public async Task MemoryRecord_SetValue()
    {
        var result = await _engine.ExecuteAsync("""
            local mr = addresslist_getMemoryRecord(0)
            memoryrecord_setValue(mr, "200")
            return memoryrecord_getValue(mr)
            """);

        Assert.True(result.Success, result.Error);
        Assert.Equal("200", result.ReturnValue);
    }

    [Fact]
    public async Task MemoryRecord_GetAddress()
    {
        var result = await _engine.ExecuteAsync("""
            local mr = addresslist_getMemoryRecord(0)
            return memoryrecord_getAddress(mr)
            """);

        Assert.True(result.Success, result.Error);
        Assert.Equal("0x1000", result.ReturnValue);
    }

    [Fact]
    public async Task MemoryRecord_SetActive_Freeze()
    {
        var result = await _engine.ExecuteAsync("""
            local mr = addresslist_getMemoryRecord(0)
            memoryrecord_setActive(mr, true)
            return memoryrecord_getActive(mr)
            """);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task MemoryRecord_SetDescription()
    {
        var result = await _engine.ExecuteAsync("""
            local mr = addresslist_getMemoryRecord(0)
            memoryrecord_setDescription(mr, "Player HP")
            return memoryrecord_getDescription(mr)
            """);

        Assert.True(result.Success, result.Error);
        Assert.Equal("Player HP", result.ReturnValue);
    }

    [Fact]
    public async Task MemoryRecord_MethodStyle_GetValue()
    {
        var result = await _engine.ExecuteAsync("""
            local mr = addresslist_getMemoryRecord(2)
            return mr.getValue()
            """);

        Assert.True(result.Success, result.Error);
        Assert.Equal("9999", result.ReturnValue);
    }

    [Fact]
    public async Task MemoryRecord_MethodStyle_SetValue()
    {
        var result = await _engine.ExecuteAsync("""
            local mr = addresslist_getMemoryRecord(2)
            mr.setValue("12345")
            return mr.getValue()
            """);

        Assert.True(result.Success, result.Error);
        Assert.Equal("12345", result.ReturnValue);
    }

    [Fact]
    public async Task GetMemoryRecord_OutOfRange_ReturnsNil()
    {
        var result = await _engine.ExecuteAsync("""
            local mr = addresslist_getMemoryRecord(99)
            return mr == nil
            """);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task GetMemoryRecordByDescription_NotFound_ReturnsNil()
    {
        var result = await _engine.ExecuteAsync("""
            local mr = addresslist_getMemoryRecordByDescription("nonexistent")
            return mr == nil
            """);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }
}
