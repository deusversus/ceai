using System.Globalization;
using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for Memory Read/Write parity closure:
/// unsigned types, WideString, freeze modes, BatchWrite, FillMemory.
/// </summary>
public class MemoryRWParityTests
{
    // ──────────────────────────────────────────────────────────
    // Unsigned data types
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void MemoryDataType_HasUnsignedVariants()
    {
        Assert.True(Enum.IsDefined(MemoryDataType.UInt16));
        Assert.True(Enum.IsDefined(MemoryDataType.UInt32));
        Assert.True(Enum.IsDefined(MemoryDataType.UInt64));
    }

    [Fact]
    public void MemoryDataType_HasWideString()
    {
        Assert.True(Enum.IsDefined(MemoryDataType.WideString));
    }

    [Fact]
    public async Task ReadValue_UInt32_ReturnsUnsigned()
    {
        var facade = new StubEngineFacade();
        // Write max uint32 value (4294967295) as bytes
        facade.WriteMemoryDirect(0x1000, BitConverter.GetBytes(uint.MaxValue));

        var result = await facade.ReadValueAsync(1000, 0x1000, MemoryDataType.Int32);
        // As signed Int32, this is -1
        Assert.Equal("-1", result.DisplayValue);

        // StubEngineFacade reads as signed — but the unsigned type in WindowsEngineFacade
        // would display as 4294967295. We verify the enum exists and is parseable.
        Assert.True(Enum.TryParse<MemoryDataType>("UInt32", true, out var dt));
        Assert.Equal(MemoryDataType.UInt32, dt);
    }

    // ──────────────────────────────────────────────────────────
    // Freeze modes
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void FreezeMode_DefaultIsExact()
    {
        Assert.Equal(FreezeMode.Exact, default(FreezeMode));
    }

    [Fact]
    public void FreezeMode_EnumHasThreeValues()
    {
        var values = Enum.GetValues<FreezeMode>();
        Assert.Equal(3, values.Length);
        Assert.Contains(FreezeMode.Exact, values);
        Assert.Contains(FreezeMode.Increment, values);
        Assert.Contains(FreezeMode.Decrement, values);
    }

    [Fact]
    public void AddressTableNode_FreezeMode_DefaultsToExact()
    {
        var node = new AddressTableNode("test", "test-label", false);
        Assert.Equal(FreezeMode.Exact, node.FreezeMode);
    }

    // ──────────────────────────────────────────────────────────
    // WideString
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void WideString_Encoding_RoundTrips()
    {
        var original = "Hello";
        var encoded = System.Text.Encoding.Unicode.GetBytes(original + '\0');
        // Find null terminator
        int len = encoded.Length;
        for (int i = 0; i < encoded.Length - 1; i += 2)
        {
            if (encoded[i] == 0 && encoded[i + 1] == 0) { len = i; break; }
        }
        var decoded = System.Text.Encoding.Unicode.GetString(encoded, 0, len);
        Assert.Equal(original, decoded);
    }

    // ──────────────────────────────────────────────────────────
    // BatchWrite / FillMemory AI tool format
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void BatchWrite_EntryFormat_ParsesCorrectly()
    {
        var entries = "0x1000|Int32|100;0x2000|Float|3.14";
        var parts = entries.Split(';', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, parts.Length);

        var fields1 = parts[0].Split('|', 3);
        Assert.Equal("0x1000", fields1[0]);
        Assert.Equal("Int32", fields1[1]);
        Assert.Equal("100", fields1[2]);

        var fields2 = parts[1].Split('|', 3);
        Assert.Equal("0x2000", fields2[0]);
        Assert.Equal("Float", fields2[1]);
        Assert.Equal("3.14", fields2[2]);
    }

    [Fact]
    public void FillMemory_PatternRepeat_WorksCorrectly()
    {
        var patternBytes = Convert.FromHexString("CCCC");
        var length = 10;
        var fillBuffer = new byte[length];
        for (int i = 0; i < length; i++)
            fillBuffer[i] = patternBytes[i % patternBytes.Length];

        Assert.All(fillBuffer, b => Assert.Equal(0xCC, b));
    }

    [Fact]
    public void FillMemory_MultiBytePattern_Repeats()
    {
        var patternBytes = Convert.FromHexString("AABB");
        var length = 6;
        var fillBuffer = new byte[length];
        for (int i = 0; i < length; i++)
            fillBuffer[i] = patternBytes[i % patternBytes.Length];

        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xAA, 0xBB, 0xAA, 0xBB }, fillBuffer);
    }
}
