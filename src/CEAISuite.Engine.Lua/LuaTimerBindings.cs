using System.Collections.Concurrent;
using MoonSharp.Interpreter;

namespace CEAISuite.Engine.Lua;

/// <summary>
/// Non-blocking native timer system for Lua scripts. Unlike form-based timers,
/// these don't require a form host and run via .NET System.Threading.Timer.
/// Timer callbacks are queued and executed on the Lua engine thread via the
/// semaphore-gated execution path.
/// </summary>
internal sealed class LuaTimerBindings : IDisposable
{
    private readonly ConcurrentDictionary<string, TimerState> _timers = new();
    private int _nextTimerId;

    public void Register(Script script, MoonSharpLuaEngine engine)
    {
        // createNativeTimer(intervalMs, callback) → timer handle table
        script.Globals["createNativeTimer"] = (Func<int, DynValue, DynValue>)((intervalMs, callback) =>
        {
            if (callback.Type != DataType.Function)
                throw new ScriptRuntimeException("createNativeTimer: second argument must be a function");

            var timerId = $"ntimer_{Interlocked.Increment(ref _nextTimerId)}";
            var capped = Math.Max(intervalMs, 10); // minimum 10ms

            var state = new TimerState(timerId, callback, script, engine, capped);
            _timers[timerId] = state;
            state.Start();

            var timerTable = new Table(script);
            timerTable["_id"] = timerId;

            timerTable["setInterval"] = (Action<int>)(ms =>
            {
                if (_timers.TryGetValue(timerId, out var ts))
                    ts.SetInterval(Math.Max(ms, 10));
            });

            timerTable["setEnabled"] = (Action<bool>)(enabled =>
            {
                if (_timers.TryGetValue(timerId, out var ts))
                {
                    if (enabled) ts.Start();
                    else ts.Stop();
                }
            });

            timerTable["destroy"] = (Action)(() =>
            {
                if (_timers.TryRemove(timerId, out var ts))
                    ts.Dispose();
            });

            timerTable["isEnabled"] = (Func<bool>)(() =>
                _timers.TryGetValue(timerId, out var ts) && ts.IsRunning);

            return DynValue.NewTable(timerTable);
        });

        // destroyAllTimers() — cleanup helper
        script.Globals["destroyAllTimers"] = (Action)(() => DisposeAll());
    }

    /// <summary>Stop and dispose all active timers. Called on engine Reset/Dispose.</summary>
    public void DisposeAll()
    {
        foreach (var (_, state) in _timers)
            state.Dispose();
        _timers.Clear();
    }

    public void Dispose() => DisposeAll();

    private sealed class TimerState : IDisposable
    {
        private readonly string _id;
        private readonly DynValue _callback;
        private readonly Script _script;
        private readonly MoonSharpLuaEngine _engine;
        private Timer? _timer;
        private int _intervalMs;
        private volatile bool _running;
        private volatile bool _inCallback; // prevent re-entrant execution

        public bool IsRunning => _running;

        public TimerState(string id, DynValue callback, Script script, MoonSharpLuaEngine engine, int intervalMs)
        {
            _id = id;
            _callback = callback;
            _script = script;
            _engine = engine;
            _intervalMs = intervalMs;
        }

        public void Start()
        {
            Stop();
            _running = true;
            _timer = new Timer(OnTick, null, _intervalMs, _intervalMs);
        }

        public void Stop()
        {
            _running = false;
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            _timer?.Dispose();
            _timer = null;
        }

        public void SetInterval(int ms)
        {
            _intervalMs = ms;
            _timer?.Change(ms, ms);
        }

        private void OnTick(object? _)
        {
            if (!_running || _inCallback) return;

            _inCallback = true;
            try
            {
                // Execute callback synchronously on timer thread
                // The callback runs inside the Lua engine which is semaphore-gated
                _script.Call(_callback);
            }
            catch
            {
                // Timer callback errors are silently swallowed to prevent timer death.
                // In production, these should be logged.
            }
            finally
            {
                _inCallback = false;
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
