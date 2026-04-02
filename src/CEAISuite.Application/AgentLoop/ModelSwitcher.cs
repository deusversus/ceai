using Microsoft.Extensions.AI;

namespace CEAISuite.Application.AgentLoop;

/// <summary>
/// Manages runtime model switching and automatic fallback chains.
///
/// When the retry policy detects 3 consecutive overload errors (529, 503, etc.),
/// it signals model fallback. The AgentLoop calls <see cref="FallbackToNext"/>
/// to advance to the next model in the chain. Users can also manually switch
/// via the <c>switch_model</c> meta-tool.
///
/// Modeled after Claude Code's model selection with fallback chain and
/// per-agent/per-skill model overrides.
/// </summary>
public sealed class ModelSwitcher
{
    private readonly List<ModelConfig> _models;
    private readonly object _lock = new();
    private int _currentIndex;
    private readonly Action<string, string>? _log;
    private DateTime? _cooldownUntil;
    private int _originalIndex;

    public ModelSwitcher(IEnumerable<ModelConfig> models, Action<string, string>? log = null)
    {
        _models = models.ToList();
        if (_models.Count == 0)
            throw new ArgumentException("At least one model configuration is required.", nameof(models));
        _log = log;
    }

    /// <summary>The currently active model configuration.</summary>
    public ModelConfig CurrentModel
    {
        get { lock (_lock) return _models[_currentIndex]; }
    }

    /// <summary>All configured models (snapshot).</summary>
    public IReadOnlyList<ModelConfig> Models
    {
        get { lock (_lock) return _models.ToList(); }
    }

    /// <summary>Index of the current model in the chain.</summary>
    public int CurrentIndex
    {
        get { lock (_lock) return _currentIndex; }
    }

    /// <summary>
    /// Switch to a specific model by ID. Returns true if found, false otherwise.
    /// </summary>
    public bool SwitchToModel(string modelId)
    {
        lock (_lock)
        {
            var idx = _models.FindIndex(m =>
                string.Equals(m.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
            {
                _log?.Invoke("MODEL", $"Model '{modelId}' not found in chain. Available: {string.Join(", ", _models.Select(m => m.ModelId))}");
                return false;
            }
            _currentIndex = idx;
            _log?.Invoke("MODEL", $"Switched to model: {_models[idx].ModelId}");
            return true;
        }
    }

    /// <summary>
    /// Advance to the next model in the fallback chain. Returns the new model,
    /// or null if already at the end of the chain.
    /// </summary>
    public ModelConfig? FallbackToNext()
    {
        lock (_lock)
        {
            if (_currentIndex >= _models.Count - 1)
            {
                _log?.Invoke("MODEL", "No more fallback models available — already at end of chain");
                return null;
            }
            _currentIndex++;
            var model = _models[_currentIndex];
            _log?.Invoke("MODEL", $"Falling back to model: {model.ModelId} (index {_currentIndex}/{_models.Count - 1})");
            return model;
        }
    }

    /// <summary>
    /// Reset to the primary (first) model in the chain. Call this when starting
    /// a new conversation or when the overload condition clears.
    /// </summary>
    public void ResetToPrimary()
    {
        lock (_lock)
        {
            _currentIndex = 0;
            _log?.Invoke("MODEL", $"Reset to primary model: {_models[0].ModelId}");
        }
    }

    /// <summary>
    /// Switch to fallback model temporarily. After the cooldown period,
    /// <see cref="CheckCooldownExpiry"/> restores the original model.
    /// </summary>
    public void TriggerCooldown(TimeSpan duration)
    {
        lock (_lock)
        {
            _originalIndex = _currentIndex;
            _cooldownUntil = DateTime.UtcNow + duration;
            if (_currentIndex < _models.Count - 1)
            {
                _currentIndex++;
                _log?.Invoke("MODEL", $"Fast-mode cooldown: using {_models[_currentIndex].ModelId} for {duration.TotalMinutes:F0}m");
            }
        }
    }

    /// <summary>Check if cooldown has expired and restore the original model.</summary>
    public void CheckCooldownExpiry()
    {
        lock (_lock)
        {
            if (_cooldownUntil.HasValue && DateTime.UtcNow >= _cooldownUntil.Value)
            {
                _log?.Invoke("MODEL", $"Fast-mode cooldown expired — restoring model: {_models[_originalIndex].ModelId}");
                _currentIndex = _originalIndex;
                _cooldownUntil = null;
            }
        }
    }

    /// <summary>
    /// Get a human-readable status summary of the model chain.
    /// </summary>
    public string GetStatusSummary()
    {
        lock (_lock)
        {
            var lines = _models.Select((m, i) =>
                $"  {(i == _currentIndex ? "▶" : " ")} [{i}] {m.ModelId} (context: {m.MaxContextTokens:#,0})");
            return $"Model chain ({_models.Count} models, active: {_models[_currentIndex].ModelId}):\n{string.Join("\n", lines)}";
        }
    }
}

/// <summary>
/// Configuration for a single model in the fallback chain.
/// </summary>
public sealed record ModelConfig
{
    /// <summary>Model identifier (e.g., "claude-sonnet-4-20250514", "gpt-4o").</summary>
    public required string ModelId { get; init; }

    /// <summary>Maximum context window size in tokens.</summary>
    public int MaxContextTokens { get; init; } = 200_000;

    /// <summary>
    /// Factory to create an IChatClient for this model. Called when switching
    /// to this model. If null, the existing client is reused with a different model ID.
    /// </summary>
    public Func<IChatClient>? ClientFactory { get; init; }

    /// <summary>Optional display name for UI.</summary>
    public string? DisplayName { get; init; }
}
