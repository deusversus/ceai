using System.Text.Json;
using CEAISuite.Application;

namespace CEAISuite.Tests;

public class NuintJsonConverterTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new NuintJsonConverter() }
    };

    [Fact]
    public void Write_SerializesAsHexString()
    {
        var json = JsonSerializer.Serialize((nuint)0x400000, Options);
        Assert.Equal("\"0x400000\"", json);
    }

    [Fact]
    public void Read_HexWithPrefix_ParsesCorrectly()
    {
        var value = JsonSerializer.Deserialize<nuint>("\"0x400000\"", Options);
        Assert.Equal((nuint)0x400000, value);
    }

    [Fact]
    public void Read_HexWithoutPrefix_ParsesCorrectly()
    {
        var value = JsonSerializer.Deserialize<nuint>("\"400000\"", Options);
        Assert.Equal((nuint)0x400000, value);
    }

    [Fact]
    public void Read_NullString_ReturnsZero()
    {
        var value = JsonSerializer.Deserialize<nuint>("null", Options);
        Assert.Equal((nuint)0, value);
    }

    [Fact]
    public void Read_CaseInsensitivePrefix()
    {
        var value = JsonSerializer.Deserialize<nuint>("\"0XFF\"", Options);
        Assert.Equal((nuint)0xFF, value);
    }

    [Fact]
    public void Read_WhitespaceAroundHex_Trims()
    {
        var value = JsonSerializer.Deserialize<nuint>("\"  0xABC  \"", Options);
        Assert.Equal((nuint)0xABC, value);
    }

    [Fact]
    public void RoundTrip_PreservesValue()
    {
        nuint original = (nuint)0xDEADBEEF;
        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<nuint>(json, Options);
        Assert.Equal(original, deserialized);
    }

    [Fact]
    public void Write_ZeroValue_SerializesAsHex()
    {
        var json = JsonSerializer.Serialize((nuint)0, Options);
        Assert.Equal("\"0x0\"", json);
    }
}
