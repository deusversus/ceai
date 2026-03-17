using System.Text.Json;

namespace CEAISuite.Application;

public sealed class AppSettings
{
    public string? OpenAiApiKey { get; set; }
    public string Model { get; set; } = "gpt-5.4";
    public int RefreshIntervalMs { get; set; } = 500;
    public bool ShowUnresolvedAsQuestionMarks { get; set; } = true;
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
        Directory.CreateDirectory(SettingsDir);
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings, options));
        SettingsChanged?.Invoke();
    }
}
