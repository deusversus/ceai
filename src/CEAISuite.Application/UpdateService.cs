using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace CEAISuite.Application;

public sealed class UpdateService : IDisposable
{
    public sealed record UpdateInfo(
        string Version,
        string DownloadUrl,
        long SizeBytes,
        string? Checksum,
        string ReleaseNotes);

    private readonly HttpClient _http = new();
    private const string GitHubApiUrl = "https://api.github.com/repos/deusversus/ceai/releases/latest";

    public UpdateService()
    {
        _http.DefaultRequestHeaders.Add("User-Agent", "CEAISuite-Updater");
        _http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
    }

    /// <summary>
    /// Checks GitHub for a newer release. Returns <see langword="null"/> if current or
    /// if the network is unavailable.
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync(GitHubApiUrl, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var release = await response.Content.ReadFromJsonAsync(GitHubReleaseJsonContext.Default.GitHubRelease, ct);
            if (release is null)
                return null;

            var remoteVersion = release.TagName?.TrimStart('v') ?? "";
            var localVersion = GetCurrentVersion();

            if (!IsNewerVersion(remoteVersion, localVersion))
                return null;

            // Find a .zip asset (convention: CEAISuite-*.zip)
            var asset = release.Assets?.FirstOrDefault(a =>
                a.Name?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true);
            if (asset is null)
                return null;

            // Try to find a checksum in the release body (format: SHA256: <hex>)
            string? checksum = null;
            if (!string.IsNullOrWhiteSpace(release.Body))
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    release.Body, @"SHA256:\s*([0-9a-fA-F]{64})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success)
                    checksum = match.Groups[1].Value;
            }

            return new UpdateInfo(
                remoteVersion,
                asset.BrowserDownloadUrl ?? "",
                asset.Size,
                checksum,
                release.Body ?? "");
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Network error, DNS failure, etc. — silently return null
            return null;
        }
    }

    /// <summary>
    /// Downloads the update zip to a temp folder. Reports progress as 0.0..1.0.
    /// Returns the path to the downloaded file.
    /// </summary>
    public async Task<string> DownloadUpdateAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "CEAISuite-update");
        Directory.CreateDirectory(tempDir);

        var fileName = Path.GetFileName(new Uri(update.DownloadUrl).AbsolutePath);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "CEAISuite-update.zip";
        var filePath = Path.Combine(tempDir, fileName);

        using var response = await _http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? update.SizeBytes;

        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        long bytesRead = 0;
        int read;
        while ((read = await contentStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
            bytesRead += read;
            if (totalBytes > 0)
                progress?.Report((double)bytesRead / totalBytes);
        }

        progress?.Report(1.0);
        return filePath;
    }

    /// <summary>
    /// Verifies the SHA256 checksum of a file. Returns true if it matches.
    /// Returns false if <paramref name="expectedChecksum"/> is null or empty (unverified downloads are rejected).
    /// </summary>
    public static bool VerifyChecksum(string filePath, string? expectedChecksum)
    {
        if (string.IsNullOrWhiteSpace(expectedChecksum))
            return false;

        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        var hex = Convert.ToHexString(hash);

        return string.Equals(hex, expectedChecksum, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts the update zip and launches a batch script to replace files and restart.
    /// Calls <see cref="Environment.Exit(int)"/> after starting the updater script.
    /// </summary>
    public static void ApplyUpdate(string zipPath)
    {
        var stagingDir = Path.Combine(Path.GetDirectoryName(zipPath)!, "staging");
        if (Directory.Exists(stagingDir))
            Directory.Delete(stagingDir, true);

        ZipFile.ExtractToDirectory(zipPath, stagingDir);

        var appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var exePath = Path.Combine(appDir, "CEAISuite.Desktop.exe");
        var scriptPath = Path.Combine(Path.GetDirectoryName(zipPath)!, "updater.cmd");

        // Write the updater batch script
        var script = $"""
            @echo off
            timeout /t 2 /nobreak >nul
            xcopy /s /y "{stagingDir}\*" "{appDir}\*"
            rmdir /s /q "{stagingDir}"
            start "" "{exePath}"
            del "%~f0"
            """;
        File.WriteAllText(scriptPath, script);

        // Launch the script detached
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });

        Environment.Exit(0);
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Returns the current assembly's informational version.</summary>
    public static string GetCurrentVersion()
    {
        return typeof(UpdateService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0";
    }

    /// <summary>
    /// Returns true if <paramref name="remote"/> is a higher semver than <paramref name="local"/>.
    /// Handles optional 'v' prefix and strips any '+metadata' suffix.
    /// </summary>
    public static bool IsNewerVersion(string remote, string local)
    {
        static Version? Parse(string v)
        {
            v = v.TrimStart('v');
            // Strip pre-release and build metadata (e.g., 1.0.0-alpha+build123)
            var plusIdx = v.IndexOf('+');
            if (plusIdx >= 0) v = v[..plusIdx];
            var dashIdx = v.IndexOf('-');
            if (dashIdx >= 0) v = v[..dashIdx];

            return Version.TryParse(v, out var result) ? result : null;
        }

        var r = Parse(remote);
        var l = Parse(local);
        if (r is null || l is null)
            return false;

        return r > l;
    }

    // ── GitHub API response model ──

    internal sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    internal sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}

[JsonSerializable(typeof(UpdateService.GitHubRelease))]
internal partial class GitHubReleaseJsonContext : JsonSerializerContext;
