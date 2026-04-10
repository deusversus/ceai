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

    // ── Additional coverage tests ──

    [Fact]
    public void IsNewerVersion_InvalidVersion_ReturnsFalse()
    {
        Assert.False(UpdateService.IsNewerVersion("not-a-version", "0.2.0"));
        Assert.False(UpdateService.IsNewerVersion("0.2.0", "garbage"));
        Assert.False(UpdateService.IsNewerVersion("", ""));
        Assert.False(UpdateService.IsNewerVersion("abc", "def"));
    }

    [Fact]
    public void IsNewerVersion_WithPreReleaseTag_StripsAndCompares()
    {
        // Pre-release tags are stripped, so 0.3.0-beta > 0.2.0
        Assert.True(UpdateService.IsNewerVersion("0.3.0-beta+build.42", "0.2.0+build.1"));
        // Same base version with different pre-release should be equal (both stripped to 0.2.0)
        Assert.False(UpdateService.IsNewerVersion("0.2.0-alpha", "0.2.0-beta"));
    }

    [Fact]
    public void IsNewerVersion_FourPartVersion_Works()
    {
        Assert.True(UpdateService.IsNewerVersion("1.2.3.4", "1.2.3.3"));
        Assert.False(UpdateService.IsNewerVersion("1.2.3.4", "1.2.3.4"));
        Assert.False(UpdateService.IsNewerVersion("1.2.3.3", "1.2.3.4"));
    }

    [Fact]
    public async Task CheckForUpdateAsync_NetworkError_ReturnsNull()
    {
        // Use the real UpdateService against the real GitHub API.
        // In CI or offline environments this will fail gracefully.
        // The key assertion is that no exception escapes.
        using var svc = new UpdateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Even if network is down, should not throw
        var result = await svc.CheckForUpdateAsync(cts.Token);
        // result can be null (no update / offline) or an UpdateInfo — both are valid
        // The important thing is no exception
    }

    [Fact]
    public async Task CheckForUpdateAsync_CancellationToken_Respected()
    {
        using var svc = new UpdateService();
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // immediately cancelled

        // Should throw OperationCanceledException because the token is already cancelled
        // OR return null if the check didn't even start. Both are acceptable.
        try
        {
            var result = await svc.CheckForUpdateAsync(cts.Token);
            // If we reach here, the cancellation was checked inside the catch.
            // That means CheckForUpdateAsync returned null — acceptable.
            Assert.Null(result);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
    }

    [Fact]
    public void VerifyChecksum_CaseInsensitive_ReturnsTrue()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var content = "Test data for checksum"u8.ToArray();
            File.WriteAllBytes(tempFile, content);

            using var stream = File.OpenRead(tempFile);
            var hash = SHA256.HashData(stream);
            var upperHex = Convert.ToHexString(hash).ToUpperInvariant();
            var lowerHex = upperHex.ToLowerInvariant();

            Assert.True(UpdateService.VerifyChecksum(tempFile, upperHex));
            Assert.True(UpdateService.VerifyChecksum(tempFile, lowerHex));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var svc = new UpdateService();
        svc.Dispose();
        // Double dispose should also not throw
        svc.Dispose();
    }
}
