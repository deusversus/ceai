using CEAISuite.Application;

namespace CEAISuite.Tests;

public class MemoryUtilsTests
{
    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(512, "512 bytes")]
    [InlineData(1023, "1023 bytes")]
    public void FormatBytes_Bytes_ReturnsCorrectFormat(long bytes, string expected)
    {
        Assert.Equal(expected, MemoryUtils.FormatBytes(bytes));
    }

    [Theory]
    [InlineData(1024, "1.0 KB")]
    [InlineData(2048, "2.0 KB")]
    [InlineData(1536, "1.5 KB")]
    public void FormatBytes_Kilobytes_ReturnsCorrectFormat(long bytes, string expected)
    {
        Assert.Equal(expected, MemoryUtils.FormatBytes(bytes));
    }

    [Theory]
    [InlineData(1_048_576, "1.0 MB")]
    [InlineData(2_621_440, "2.5 MB")]
    public void FormatBytes_Megabytes_ReturnsCorrectFormat(long bytes, string expected)
    {
        Assert.Equal(expected, MemoryUtils.FormatBytes(bytes));
    }

    [Theory]
    [InlineData(1_073_741_824, "1.0 GB")]
    [InlineData(2_147_483_648, "2.0 GB")]
    public void FormatBytes_Gigabytes_ReturnsCorrectFormat(long bytes, string expected)
    {
        Assert.Equal(expected, MemoryUtils.FormatBytes(bytes));
    }
}
