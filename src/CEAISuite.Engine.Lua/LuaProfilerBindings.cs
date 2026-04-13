using System.Collections.Concurrent;
using System.Diagnostics;
using MoonSharp.Interpreter;

namespace CEAISuite.Engine.Lua;

/// <summary>
/// Script profiler for Lua — tracks function call counts and cumulative time.
/// Exposed as profiler.start(), profiler.stop(), profiler.report(), profiler.reset().
/// Uses Stopwatch for timing and tracks by source location.
/// </summary>
internal sealed class LuaProfilerBindings
{
    private readonly ConcurrentDictionary<string, ProfileEntry> _entries = new();
    private readonly Stopwatch _totalStopwatch = new();
    private bool _active;

    public void Register(Script script)
    {
        var profilerTable = new Table(script);

        profilerTable["start"] = (Action)(() =>
        {
            _active = true;
            _totalStopwatch.Restart();
        });

        profilerTable["stop"] = (Action)(() =>
        {
            _active = false;
            _totalStopwatch.Stop();
        });

        profilerTable["reset"] = (Action)(() =>
        {
            _entries.Clear();
            _totalStopwatch.Reset();
            _active = false;
        });

        profilerTable["report"] = (Func<DynValue>)(() =>
        {
            if (_entries.IsEmpty)
                return DynValue.NewString("No profiler data. Call profiler.start() before executing code.");

            var lines = new List<string>
            {
                $"=== Lua Script Profile ({_totalStopwatch.Elapsed.TotalMilliseconds:F1}ms total) ===",
                $"{"Location",-40} {"Calls",8} {"Total ms",10} {"Avg ms",8}"
            };

            foreach (var (location, entry) in _entries.OrderByDescending(e => e.Value.TotalMs))
            {
                var avg = entry.CallCount > 0 ? entry.TotalMs / entry.CallCount : 0;
                lines.Add($"{Truncate(location, 40),-40} {entry.CallCount,8} {entry.TotalMs,10:F2} {avg,8:F3}");
            }

            return DynValue.NewString(string.Join("\n", lines));
        });

        profilerTable["getEntries"] = (Func<DynValue>)(() =>
        {
            var table = new Table(script);
            int idx = 1;
            foreach (var (location, entry) in _entries.OrderByDescending(e => e.Value.TotalMs))
            {
                var row = new Table(script);
                row["location"] = location;
                row["calls"] = (double)entry.CallCount;
                row["totalMs"] = entry.TotalMs;
                row["avgMs"] = entry.CallCount > 0 ? entry.TotalMs / entry.CallCount : 0;
                table[idx++] = DynValue.NewTable(row);
            }
            return DynValue.NewTable(table);
        });

        script.Globals["profiler"] = DynValue.NewTable(profilerTable);
    }

    /// <summary>Record a function call. Called by the instruction debugger hook.</summary>
    internal void RecordCall(string location, double elapsedMs)
    {
        if (!_active) return;

        _entries.AddOrUpdate(location,
            _ => new ProfileEntry { CallCount = 1, TotalMs = elapsedMs },
            (_, existing) =>
            {
                Interlocked.Increment(ref existing.CallCount);
                // Atomic add for double via Interlocked.Exchange CAS loop
                double initial, computed;
                do
                {
                    initial = existing.TotalMs;
                    computed = initial + elapsedMs;
                } while (Interlocked.CompareExchange(ref existing.TotalMs, computed, initial) != initial);
                return existing;
            });
    }

    internal bool IsActive => _active;

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..(maxLen - 3)] + "...";

    private sealed class ProfileEntry
    {
        public long CallCount;
        public double TotalMs;
    }
}
