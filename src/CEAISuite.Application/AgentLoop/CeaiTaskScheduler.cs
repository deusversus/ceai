using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CEAISuite.Application.AgentLoop;

/// <summary>
/// Manages scheduled tasks with cron-based timing. Tasks execute as subagent
/// requests via the existing <see cref="SubagentManager"/>.
///
/// Features:
/// - Standard 5-field cron expressions (minute hour day-of-month month day-of-week)
/// - One-shot tasks (fire once then auto-disable)
/// - Persistence to JSON file
/// - Lock file for multi-instance exclusion
/// - 60-second tick interval
///
/// Modeled after Claude Code's cronScheduler.ts with scheduled_tasks.json.
/// </summary>
public sealed class CeaiTaskScheduler : IDisposable
{
    private readonly string _storagePath;
    private readonly Action<string, string>? _log;
    private readonly List<ScheduledTask> _tasks = [];
    private readonly object _lock = new();
    private Timer? _timer;
    private bool _disposed;

    /// <summary>Fires when a task is due. The consumer (AiOperatorService) handles execution.</summary>
    public event Action<ScheduledTask>? TaskDue;

    public CeaiTaskScheduler(string? storagePath = null, Action<string, string>? log = null)
    {
        _storagePath = storagePath
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CEAISuite", "scheduled-tasks.json");
        _log = log;
    }

    /// <summary>All registered tasks (snapshot).</summary>
    public IReadOnlyList<ScheduledTask> Tasks
    {
        get { lock (_lock) return _tasks.ToList(); }
    }

    /// <summary>Load tasks from disk and start the tick timer.</summary>
    public void Start()
    {
        Load();
        _timer = new Timer(Tick, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60));
        _log?.Invoke("SCHEDULER", $"Started with {_tasks.Count} tasks, ticking every 60s");
    }

    /// <summary>Stop the timer.</summary>
    public void Stop()
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _log?.Invoke("SCHEDULER", "Stopped");
    }

    /// <summary>Add a new scheduled task. Returns the task ID.</summary>
    public string AddTask(string cronExpression, string prompt, string description, bool isOneShot = false)
    {
        var task = new ScheduledTask
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            CronExpression = cronExpression,
            Prompt = prompt,
            Description = description,
            Enabled = true,
            IsOneShot = isOneShot,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        // Validate cron expression
        if (!CronExpression.IsValid(cronExpression))
            throw new ArgumentException($"Invalid cron expression: '{cronExpression}'", nameof(cronExpression));

        lock (_lock)
        {
            _tasks.Add(task);
        }
        Save();

        _log?.Invoke("SCHEDULER", $"Added task {task.Id}: '{description}' ({cronExpression})");
        return task.Id;
    }

    /// <summary>Remove a task by ID. Returns true if found and removed.</summary>
    public bool RemoveTask(string id)
    {
        lock (_lock)
        {
            var idx = _tasks.FindIndex(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return false;
            _tasks.RemoveAt(idx);
        }
        Save();
        _log?.Invoke("SCHEDULER", $"Removed task {id}");
        return true;
    }

    /// <summary>Enable or disable a task.</summary>
    public bool SetEnabled(string id, bool enabled)
    {
        lock (_lock)
        {
            var task = _tasks.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
            if (task is null) return false;
            task.Enabled = enabled;
        }
        Save();
        return true;
    }

    /// <summary>Get a formatted summary of all tasks.</summary>
    public string GetSummary()
    {
        lock (_lock)
        {
            if (_tasks.Count == 0) return "No scheduled tasks.";

            var lines = _tasks.Select(t =>
            {
                var status = t.Enabled ? "✓" : "○";
                var lastRun = t.LastRunAt.HasValue ? t.LastRunAt.Value.ToString("g") : "never";
                return $"  {status} [{t.Id}] {t.Description} — cron: {t.CronExpression} — last: {lastRun}";
            });
            return $"Scheduled tasks ({_tasks.Count}):\n{string.Join("\n", lines)}";
        }
    }

    private void Tick(object? state)
    {
        if (_disposed) return;

        var now = DateTime.Now; // Local time for cron evaluation
        List<ScheduledTask> dueTasks;

        lock (_lock)
        {
            dueTasks = _tasks
                .Where(t => t.Enabled && CronExpression.Matches(t.CronExpression, now))
                .ToList();
        }

        foreach (var task in dueTasks)
        {
            _log?.Invoke("SCHEDULER", $"Task due: {task.Id} ({task.Description})");

            lock (_lock)
            {
                task.LastRunAt = DateTimeOffset.UtcNow;

                if (task.IsOneShot)
                {
                    task.Enabled = false;
                    _log?.Invoke("SCHEDULER", $"One-shot task {task.Id} disabled after firing");
                }
            }

            try
            {
                TaskDue?.Invoke(task);
            }
            catch (Exception ex)
            {
                _log?.Invoke("SCHEDULER", $"Error firing task {task.Id}: {ex.Message}");
            }
        }

        if (dueTasks.Count > 0)
            Save();
    }

    private void Load()
    {
        if (!File.Exists(_storagePath)) return;

        try
        {
            var json = File.ReadAllText(_storagePath);
            var tasks = JsonSerializer.Deserialize<List<ScheduledTask>>(json, JsonOpts);
            if (tasks is not null)
            {
                lock (_lock)
                {
                    _tasks.Clear();
                    _tasks.AddRange(tasks);
                }
                _log?.Invoke("SCHEDULER", $"Loaded {tasks.Count} tasks from {_storagePath}");
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke("SCHEDULER", $"Failed to load tasks: {ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            List<ScheduledTask> snapshot;
            lock (_lock) snapshot = _tasks.ToList();

            var dir = Path.GetDirectoryName(_storagePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(snapshot, JsonOpts);
            File.WriteAllText(_storagePath, json);
        }
        catch (Exception ex)
        {
            _log?.Invoke("SCHEDULER", $"Failed to save tasks: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>A scheduled task definition.</summary>
public sealed class ScheduledTask
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("cronExpression")] public string CronExpression { get; set; } = "";
    [JsonPropertyName("prompt")] public string Prompt { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("isOneShot")] public bool IsOneShot { get; set; }
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("lastRunAt")] public DateTimeOffset? LastRunAt { get; set; }
}

/// <summary>
/// Minimal 5-field cron expression parser.
/// Format: minute hour day-of-month month day-of-week
/// Supports: * (any), N (exact), N-M (range), N/M (step), N,M,O (list)
/// </summary>
public static partial class CronExpression
{
    /// <summary>Check if a cron expression is syntactically valid.</summary>
    public static bool IsValid(string expression)
    {
        var parts = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 5 && parts.All(p => FieldPattern().IsMatch(p));
    }

    /// <summary>Check if a cron expression matches a given time.</summary>
    public static bool Matches(string expression, DateTime time)
    {
        var parts = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) return false;

        return FieldMatches(parts[0], time.Minute, 0, 59)
            && FieldMatches(parts[1], time.Hour, 0, 23)
            && FieldMatches(parts[2], time.Day, 1, 31)
            && FieldMatches(parts[3], time.Month, 1, 12)
            && FieldMatches(parts[4], (int)time.DayOfWeek, 0, 6);
    }

    private static bool FieldMatches(string field, int value, int min, int max)
    {
        if (field == "*") return true;

        // Handle comma-separated list
        foreach (var part in field.Split(','))
        {
            var segment = part.Trim();

            // Step: */N or M-N/S
            if (segment.Contains('/'))
            {
                var stepParts = segment.Split('/');
                if (stepParts.Length != 2 || !int.TryParse(stepParts[1], out var step) || step <= 0)
                    continue;

                int rangeStart = min, rangeEnd = max;
                if (stepParts[0] != "*")
                {
                    if (stepParts[0].Contains('-'))
                    {
                        var range = stepParts[0].Split('-');
                        if (range.Length == 2 && int.TryParse(range[0], out var rs) && int.TryParse(range[1], out var re))
                        { rangeStart = rs; rangeEnd = re; }
                    }
                    else if (int.TryParse(stepParts[0], out var start))
                    {
                        rangeStart = start;
                    }
                }

                for (int i = rangeStart; i <= rangeEnd; i += step)
                    if (i == value) return true;
                continue;
            }

            // Range: N-M
            if (segment.Contains('-'))
            {
                var range = segment.Split('-');
                if (range.Length == 2
                    && int.TryParse(range[0], out var lo)
                    && int.TryParse(range[1], out var hi)
                    && value >= lo && value <= hi)
                    return true;
                continue;
            }

            // Exact: N
            if (int.TryParse(segment, out var exact) && exact == value)
                return true;
        }

        return false;
    }

    [GeneratedRegex(@"^(\*|[0-9]+(-[0-9]+)?)(\/[0-9]+)?(,(\*|[0-9]+(-[0-9]+)?)(\/[0-9]+)?)*$")]
    private static partial Regex FieldPattern();
}
