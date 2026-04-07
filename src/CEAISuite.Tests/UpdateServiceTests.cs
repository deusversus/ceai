using System.IO;
using System.Security.Cryptography;
using System.Text;
using CEAISuite.Application;

namespace CEAISuite.Tests;

public class UpdateServiceTests
{
    [Fact]
    public void IsNewerVersion_Higher_ReturnsTrue()
    {
        Assert.True(UpdateService.IsNewerVersion("0.3.0", "0.2.0"));
        Assert.True(UpdateService.IsNewerVersion("1.0.0", "0.9.9"));
        Assert.True(UpdateService.IsNewerVersion("0.2.1", "0.2.0"));
    }

    [Fact]
    public void IsNewerVersion_Same_ReturnsFalse()
    {
        Assert.False(UpdateService.IsNewerVersion("0.2.0", "0.2.0"));
        Assert.False(UpdateService.IsNewerVersion("1.0.0", "1.0.0"));
    }

    [Fact]
    public void IsNewerVersion_Lower_ReturnsFalse()
    {
        Assert.False(UpdateService.IsNewerVersion("0.1.0", "0.2.0"));
        Assert.False(UpdateService.IsNewerVersion("0.2.0", "1.0.0"));
    }

    [Fact]
    public void IsNewerVersion_WithVPrefix_Parses()
    {
        Assert.True(UpdateService.IsNewerVersion("v0.3.0", "0.2.0"));
        Assert.True(UpdateService.IsNewerVersion("v1.0.0", "v0.9.0"));
        Assert.False(UpdateService.IsNewerVersion("v0.2.0", "v0.2.0"));
    }

    [Fact]
    public void IsNewerVersion_WithBuildMetadata_StripsAndCompares()
    {
        Assert.True(UpdateService.IsNewerVersion("0.3.0+build.123", "0.2.0"));
        Assert.True(UpdateService.IsNewerVersion("0.3.0-alpha", "0.2.0"));
        Assert.False(UpdateService.IsNewerVersion("0.2.0+build.456", "0.2.0"));
    }

    [Fact]
    public void GetCurrentVersion_ReturnsNonEmpty()
    {
        var version = UpdateService.GetCurrentVersion();
        Assert.False(string.IsNullOrWhiteSpace(version));
    }

    [Fact]
    public void VerifyChecksum_CorrectHash_ReturnsTrue()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var content = "Hello, World!"u8.ToArray();
            File.WriteAllBytes(tempFile, content);

            using var stream = File.OpenRead(tempFile);
            var hash = SHA256.HashData(stream);
            var expectedHex = Convert.ToHexString(hash);

            Assert.True(UpdateService.VerifyChecksum(tempFile, expectedHex));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void VerifyChecksum_WrongHash_ReturnsFalse()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, "Some content"u8.ToArray());
            var wrongHash = new string('A', 64);

            Assert.False(UpdateService.VerifyChecksum(tempFile, wrongHash));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void VerifyChecksum_NullOrEmptyExpected_ReturnsFalse()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, "Any content"u8.ToArray());

            Assert.False(UpdateService.VerifyChecksum(tempFile, null));
            Assert.False(UpdateService.VerifyChecksum(tempFile, ""));
            Assert.False(UpdateService.VerifyChecksum(tempFile, "  "));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
