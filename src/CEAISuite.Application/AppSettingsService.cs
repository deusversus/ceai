using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CEAISuite.Application;

/// <summary>
/// Persisted settings. The API key is stored encrypted on disk via DPAPI;
/// the <see cref="OpenAiApiKey"/> property holds the *plaintext* at runtime,
/// while <see cref="EncryptedApiKey"/> holds the Base64-encoded ciphertext for serialization.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Runtime-only plaintext key (not serialized).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? OpenAiApiKey { get; set; }

    /// <summary>DPAPI-encrypted, Base64-encoded API key (written to disk).</summary>
    public string? EncryptedApiKey { get; set; }

    public string Model { get; set; } = "gpt-5.4";
    public int RefreshIntervalMs { get; set; } = 500;
    public bool ShowUnresolvedAsQuestionMarks { get; set; } = true;
    public bool MenuBarVisible { get; set; } = true;
}

public sealed class AppSettingsService
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CEAISuite");
    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    private AppSettings _settings = new();

    public AppSettings Settings => _settings;

    /// <summary>Event raised when settings change (after Save).</summary>
    public event Action? SettingsChanged;

    public void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                _settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new();
            }
        }
        catch { _settings = new(); }

        // Decrypt stored API key
        if (!string.IsNullOrWhiteSpace(_settings.EncryptedApiKey))
        {
            try
            {
                _settings.OpenAiApiKey = DecryptString(_settings.EncryptedApiKey);
            }
            catch { _settings.OpenAiApiKey = null; }
        }

        // Environment variable overrides stored key if present
        var envKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey) && string.IsNullOrWhiteSpace(_settings.OpenAiApiKey))
            _settings.OpenAiApiKey = envKey;

        var envModel = Environment.GetEnvironmentVariable("CEAI_MODEL");
        if (!string.IsNullOrWhiteSpace(envModel))
            _settings.Model = envModel;
    }

    public void Save()
    {
        // Encrypt API key before writing
        if (!string.IsNullOrWhiteSpace(_settings.OpenAiApiKey))
            _settings.EncryptedApiKey = EncryptString(_settings.OpenAiApiKey);
        else
            _settings.EncryptedApiKey = null;

        Directory.CreateDirectory(SettingsDir);
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings, options));
        SettingsChanged?.Invoke();
    }

    /// <summary>Encrypt a string using Windows DPAPI (CurrentUser scope).</summary>
    private static string EncryptString(string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    /// <summary>Decrypt a DPAPI-protected Base64 string.</summary>
    private static string DecryptString(string encrypted)
    {
        var bytes = Convert.FromBase64String(encrypted);
        var decrypted = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(decrypted);
    }
}
