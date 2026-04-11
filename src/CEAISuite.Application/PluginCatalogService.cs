using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CEAISuite.Application;

/// <summary>
/// Fetches community plugin catalog from GitHub Pages and downloads verified plugins.
/// </summary>
public sealed class PluginCatalogService : IDisposable
{
    /// <summary>A plugin entry from the community catalog.</summary>
    public sealed record CatalogEntry(
        string Name,
        string Version,
        string Description,
        string Author,
        string DownloadUrl,
        string? Checksum,
        long SizeBytes);

    private readonly HttpClient _http;
    private const string DefaultCatalogUrl = "https://deusversus.github.io/ceai/plugin-catalog.json";

    public string CatalogUrl { get; init; } = DefaultCatalogUrl;

    public PluginCatalogService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.Add("User-Agent", "CEAISuite-PluginCatalog");
    }

    /// <summary>
    /// Fetch the community plugin catalog. Returns empty list on network/parse errors.
    /// </summary>
    public async Task<IReadOnlyList<CatalogEntry>> FetchCatalogAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await _http.GetStringAsync(CatalogUrl, ct).ConfigureAwait(false);
            var entries = JsonSerializer.Deserialize<List<CatalogEntry>>(json, CatalogJsonContext.Default.ListCatalogEntry);
            return entries ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Download a plugin DLL and verify its SHA256 checksum if provided.
    /// Returns the destination file path.
    /// </summary>
    public async Task<string> DownloadAndVerifyAsync(
        CatalogEntry entry,
        string targetDirectory,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(targetDirectory))
            Directory.CreateDirectory(targetDirectory);

        var fileName = Path.GetFileName(new Uri(entry.DownloadUrl).LocalPath);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = $"{entry.Name}.dll";
        var destPath = Path.Combine(targetDirectory, fileName);

        using var response = await _http.GetAsync(entry.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? entry.SizeBytes;
        long downloaded = 0;

        await using var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);

        var buffer = new byte[8192];
        int bytesRead;
        while ((bytesRead = await contentStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
            downloaded += bytesRead;
            if (totalBytes > 0)
                progress?.Report((double)downloaded / totalBytes);
        }

        // Close the file before verification
        await fileStream.DisposeAsync().ConfigureAwait(false);

        // Verify checksum if provided
        if (entry.Checksum is not null)
        {
            await using var verifyStream = new FileStream(destPath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
            var hash = await SHA256.HashDataAsync(verifyStream, ct).ConfigureAwait(false);
            var actual = Convert.ToHexStringLower(hash);
            var expected = entry.Checksum.Trim().ToLowerInvariant();

            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                verifyStream.Close();
                File.Delete(destPath);
                throw new InvalidOperationException(
                    $"Checksum mismatch for {entry.Name}: expected {expected}, got {actual}");
            }
        }

        return destPath;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>JSON serialization context for AOT compatibility.</summary>
[JsonSerializable(typeof(List<PluginCatalogService.CatalogEntry>))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal sealed partial class CatalogJsonContext : JsonSerializerContext;
