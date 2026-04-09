using System.IO;
using CEAISuite.Application.AgentLoop;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for CeaiTaskScheduler: task lifecycle, cron matching, persistence, and cleanup.
/// </summary>
public class CeaiTaskSchedulerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _storagePath;

    public CeaiTaskSchedulerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ceai-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _storagePath = Path.Combine(_tempDir, "scheduled-tasks.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { /* best-effort cleanup */ }
    }

    private CeaiTaskScheduler CreateScheduler() => new(_storagePath);

    [Fact]
    public void AddTask_ValidCron_ReturnsId()
    {
        using var scheduler = CreateScheduler();
        var id = scheduler.AddTask("0 9 * * *", "do stuff", "Test task");
        Assert.NotNull(id);
        Assert.NotEmpty(id);
    }

    [Fact]
    public void AddTask_InvalidCron_Throws()
    {
        using var scheduler = CreateScheduler();
        Assert.Throws<ArgumentException>(() => scheduler.AddTask("invalid cron", "prompt", "desc"));
    }

    [Fact]
    public void AddTask_TaskAppearsInList()
    {
        using var scheduler = CreateScheduler();
        scheduler.AddTask("0 9 * * *", "prompt", "My task");
        Assert.Single(scheduler.Tasks);
        Assert.Equal("My task", scheduler.Tasks[0].Description);
    }

    [Fact]
    public void RemoveTask_ExistingId_ReturnsTrue()
    {
        using var scheduler = CreateScheduler();
        var id = scheduler.AddTask("0 9 * * *", "prompt", "desc");
        Assert.True(scheduler.RemoveTask(id));
        Assert.Empty(scheduler.Tasks);
    }

    [Fact]
    public void RemoveTask_UnknownId_ReturnsFalse()
    {
        using var scheduler = CreateScheduler();
        Assert.False(scheduler.RemoveTask("nonexistent"));
    }

    [Fact]
    public void SetEnabled_TogglesTaskState()
    {
        using var scheduler = CreateScheduler();
        var id = scheduler.AddTask("0 9 * * *", "prompt", "desc");
        Assert.True(scheduler.Tasks[0].Enabled);

        scheduler.SetEnabled(id, false);
        Assert.False(scheduler.Tasks[0].Enabled);

        scheduler.SetEnabled(id, true);
        Assert.True(scheduler.Tasks[0].Enabled);
    }

    [Fact]
    public void SetEnabled_UnknownId_ReturnsFalse()
    {
        using var scheduler = CreateScheduler();
        Assert.False(scheduler.SetEnabled("nonexistent", true));
    }

    [Fact]
    public void GetSummary_NoTasks_ReturnsEmptyMessage()
    {
        using var scheduler = CreateScheduler();
        var summary = scheduler.GetSummary();
        Assert.Contains("No scheduled tasks", summary);
    }

    [Fact]
    public void GetSummary_WithTasks_ReturnsList()
    {
        using var scheduler = CreateScheduler();
        scheduler.AddTask("0 9 * * *", "prompt", "Morning check");
        var summary = scheduler.GetSummary();
        Assert.Contains("Morning check", summary);
        Assert.Contains("0 9 * * *", summary);
    }

    [Fact]
    public void Start_LoadsTasks_FromPreviousSession()
    {
        // Create and save tasks
        using (var scheduler1 = CreateScheduler())
        {
            scheduler1.AddTask("*/5 * * * *", "prompt", "Recurring task");
        }

        // Load in a new instance
        using var scheduler2 = CreateScheduler();
        scheduler2.Start();
        Assert.Single(scheduler2.Tasks);
        Assert.Equal("Recurring task", scheduler2.Tasks[0].Description);
        scheduler2.Stop();
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var scheduler = CreateScheduler();
        scheduler.Start();
        scheduler.Dispose();
        // Double dispose should not throw
        scheduler.Dispose();
    }

    [Fact]
    public void AddTask_OneShot_SetsFlag()
    {
        using var scheduler = CreateScheduler();
        scheduler.AddTask("0 12 * * *", "once", "One-shot", isOneShot: true);
        Assert.True(scheduler.Tasks[0].IsOneShot);
    }

    [Fact]
    public void TaskDue_FiredForMatchingCron()
    {
        using var scheduler = CreateScheduler();
        var firedTasks = new List<ScheduledTask>();
        scheduler.TaskDue += t => firedTasks.Add(t);

        // Add a task that matches "every minute"
        scheduler.AddTask("* * * * *", "prompt", "Every minute");
        scheduler.Start();

        // Wait a bit for the tick (first tick is after 5 seconds)
        Thread.Sleep(7000);
        scheduler.Stop();

        Assert.NotEmpty(firedTasks);
        Assert.Equal("Every minute", firedTasks[0].Description);
    }
}

/// <summary>
/// Tests for the CronExpression parser.
/// </summary>
public class CronExpressionTests
{
    [Theory]
    [InlineData("0 9 * * *", true)]
    [InlineData("*/5 * * * *", true)]
    [InlineData("0 0 1 1 *", true)]
    [InlineData("0-30 9-17 * * 1-5", true)]
    [InlineData("invalid", false)]
    [InlineData("0 9 * *", false)] // only 4 fields
    [InlineData("0 9 * * * *", false)] // 6 fields
    [InlineData("", false)]
    public void IsValid_VariousExpressions(string expr, bool expected)
    {
        Assert.Equal(expected, CronExpression.IsValid(expr));
    }

    [Fact]
    public void Matches_ExactMinuteHour()
    {
        var time = new DateTime(2026, 4, 9, 9, 0, 0);
        Assert.True(CronExpression.Matches("0 9 * * *", time));
        Assert.False(CronExpression.Matches("30 9 * * *", time));
    }

    [Fact]
    public void Matches_Wildcard_AlwaysMatches()
    {
        var time = new DateTime(2026, 4, 9, 14, 37, 0);
        Assert.True(CronExpression.Matches("* * * * *", time));
    }

    [Fact]
    public void Matches_Range_WithinRange()
    {
        var time = new DateTime(2026, 4, 9, 10, 0, 0); // Wednesday = 3
        Assert.True(CronExpression.Matches("0 9-17 * * *", time)); // Hour 10 is in 9-17
        Assert.False(CronExpression.Matches("0 18-23 * * *", time)); // Hour 10 not in 18-23
    }

    [Fact]
    public void Matches_Step_EveryFiveMinutes()
    {
        var at0 = new DateTime(2026, 4, 9, 10, 0, 0);
        var at5 = new DateTime(2026, 4, 9, 10, 5, 0);
        var at7 = new DateTime(2026, 4, 9, 10, 7, 0);
        Assert.True(CronExpression.Matches("*/5 * * * *", at0));
        Assert.True(CronExpression.Matches("*/5 * * * *", at5));
        Assert.False(CronExpression.Matches("*/5 * * * *", at7));
    }

    [Fact]
    public void Matches_CommaList_MultipleValues()
    {
        var at0 = new DateTime(2026, 4, 9, 0, 0, 0);
        var at15 = new DateTime(2026, 4, 9, 0, 15, 0);
        var at30 = new DateTime(2026, 4, 9, 0, 30, 0);
        var at20 = new DateTime(2026, 4, 9, 0, 20, 0);
        Assert.True(CronExpression.Matches("0,15,30 * * * *", at0));
        Assert.True(CronExpression.Matches("0,15,30 * * * *", at15));
        Assert.True(CronExpression.Matches("0,15,30 * * * *", at30));
        Assert.False(CronExpression.Matches("0,15,30 * * * *", at20));
    }

    [Fact]
    public void Matches_DayOfWeek_Monday()
    {
        var monday = new DateTime(2026, 4, 6, 9, 0, 0); // Monday
        var tuesday = new DateTime(2026, 4, 7, 9, 0, 0); // Tuesday
        Assert.True(CronExpression.Matches("0 9 * * 1", monday));
        Assert.False(CronExpression.Matches("0 9 * * 1", tuesday));
    }

    [Fact]
    public void Matches_DayOfMonth()
    {
        var first = new DateTime(2026, 4, 1, 0, 0, 0);
        var second = new DateTime(2026, 4, 2, 0, 0, 0);
        Assert.True(CronExpression.Matches("0 0 1 * *", first));
        Assert.False(CronExpression.Matches("0 0 1 * *", second));
    }

    [Fact]
    public void Matches_Month_April()
    {
        var april = new DateTime(2026, 4, 1, 0, 0, 0);
        var may = new DateTime(2026, 5, 1, 0, 0, 0);
        Assert.True(CronExpression.Matches("0 0 1 4 *", april));
        Assert.False(CronExpression.Matches("0 0 1 4 *", may));
    }

    [Fact]
    public void Matches_RangeWithStep()
    {
        // 0-30/10 means 0,10,20,30
        var at10 = new DateTime(2026, 4, 9, 0, 10, 0);
        var at15 = new DateTime(2026, 4, 9, 0, 15, 0);
        Assert.True(CronExpression.Matches("0-30/10 * * * *", at10));
        Assert.False(CronExpression.Matches("0-30/10 * * * *", at15));
    }
}
